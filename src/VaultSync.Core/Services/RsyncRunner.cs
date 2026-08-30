using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services; // For FilterService

namespace VaultSync.Core.Services
{
    public sealed class RsyncRunner : ISyncRunner
    {
        private static readonly ConcurrentDictionary<string, RsyncCapabilities> s_capabilityCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly bool _useWholeFile;
        private readonly string _rsyncPath;

        public RsyncRunner(bool useWholeFile = true, string? rsyncPath = null)
        {
            _useWholeFile = useWholeFile;
            _rsyncPath = string.IsNullOrWhiteSpace(rsyncPath) ? "rsync" : rsyncPath;
        }

        public string Name => "rsync";

        public Task<int> SyncAsync(Project project, string destination, bool dryRun, CancellationToken ct)
            => SyncAsync(project, destination, dryRun, progressCallback: null, linkDest: null, ct: ct);

        public async Task<int> SyncAsync(
            Project project,
            string destination,
            bool dryRun,
            Action<double, string, string>? progressCallback,
            string? linkDest = null,
            int? maxBandwidthKbps = null,
            CancellationToken ct = default)
        {
            BackupSafetyService.EnsureSafeBackupRoot(project, destination);

            var filter = FilterService.FromPresetAndLocal(project.RootPath, project.Preset);
            string? tempExcludeFile = null;

            if (filter.HasRules)
            {
                tempExcludeFile = Path.Combine(Path.GetTempPath(), $"vaultsync_exclude_{Guid.NewGuid():N}.txt");
                IReadOnlyList<string> patterns = filter.RawPatterns ?? Array.Empty<string>();
                File.WriteAllLines(tempExcludeFile, patterns.Where(p => !string.IsNullOrWhiteSpace(p)));
            }

            var psi = new ProcessStartInfo
            {
                FileName               = _rsyncPath,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };
            ConfigureMacLibraryPath(psi, _rsyncPath);

            if (OperatingSystem.IsWindows())
            {
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
            }

            // trailing slash on source for rsync semantics (copy contents)
            string src = project.RootPath.EndsWith(Path.DirectorySeparatorChar)
                ? project.RootPath
                : project.RootPath + Path.DirectorySeparatorChar;

            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add("--delete");
            psi.ArgumentList.Add("--human-readable");
            psi.ArgumentList.Add("--no-compress");
            if (_useWholeFile)
                psi.ArgumentList.Add("--whole-file");

            if (maxBandwidthKbps is > 0)
                psi.ArgumentList.Add($"--bwlimit={maxBandwidthKbps.Value}");

            psi.ArgumentList.Add("--progress");

            if (GetCapabilities(_rsyncPath).SupportsInfoProgress2)
            {
                psi.ArgumentList.Add("--info=progress2");
            }

            if (!string.IsNullOrWhiteSpace(linkDest))
            {
                string linkPath = OperatingSystem.IsWindows() ? ToRsyncPath(linkDest) : linkDest;
                psi.ArgumentList.Add($"--link-dest={linkPath}");
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                psi.ArgumentList.Add("--force");

            if (dryRun)
                psi.ArgumentList.Add("--dry-run");

            if (OperatingSystem.IsWindows())
            {
                if (tempExcludeFile != null)
                {
                    psi.ArgumentList.Add($"--exclude-from={ToRsyncPath(tempExcludeFile)}");
                }

                psi.ArgumentList.Add(ToRsyncPath(src));
                psi.ArgumentList.Add(ToRsyncPath(destination));
            }
            else
            {
                if (tempExcludeFile != null)
                {
                    psi.ArgumentList.Add($"--exclude-from={tempExcludeFile}");
                }

                psi.ArgumentList.Add(src);
                psi.ArgumentList.Add(destination);
            }

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = false };

            string? currentFile = null;

            void HandleProgressLine(string? data)
            {
                if (ct.IsCancellationRequested)
                    return;
                if (string.IsNullOrWhiteSpace(data) || progressCallback is null)
                    return;

                string line = data.Trim();

                // rsync typically prints the file path on one line, then a separate line
                // with the numeric progress (bytes, percent, speed, ETA).
                // We treat lines without '%' as potential file names and remember them.
                if (!line.Contains('%'))
                {
                    if (!line.StartsWith("sending incremental file list", StringComparison.OrdinalIgnoreCase) &&
                        !line.StartsWith("sent ", StringComparison.OrdinalIgnoreCase) &&
                        !line.StartsWith("total size is ", StringComparison.OrdinalIgnoreCase) &&
                        !line.StartsWith("receiving incremental file list", StringComparison.OrdinalIgnoreCase))
                    {
                        currentFile = line;
                    }

                    return;
                }

                double? percent = TryParsePercent(line);
                if (percent is double p)
                {
                    // Use the last-seen file path as the "current file" for this progress tick.
                    string fileLabel = currentFile ?? string.Empty;
                    progressCallback(p, fileLabel, string.Empty);
                }
            }

            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;

                HandleProgressLine(e.Data);
            };

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;

                HandleProgressLine(e.Data);
            };

            if (!proc.Start())
                throw new InvalidOperationException("Failed to start rsync process.");

            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            try
            {
                await proc.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[RsyncRunner] Cancellation requested; stopping rsync process.");
                try { proc.CancelErrorRead(); } catch { }
                try { proc.CancelOutputRead(); } catch { }
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            finally
            {
                if (tempExcludeFile != null && File.Exists(tempExcludeFile))
                {
                    try { File.Delete(tempExcludeFile); } catch { }
                }
            }

            return proc.ExitCode;
        }

        private sealed record RsyncCapabilities(Version? Version, bool SupportsInfoProgress2);

        private static RsyncCapabilities GetCapabilities(string rsyncPath)
        {
            if (s_capabilityCache.TryGetValue(rsyncPath, out RsyncCapabilities? cached))
                return cached;

            RsyncCapabilities detected = DetectCapabilities(rsyncPath);
            s_capabilityCache[rsyncPath] = detected;
            return detected;
        }

        private static RsyncCapabilities DetectCapabilities(string rsyncPath)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = rsyncPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                ConfigureMacLibraryPath(psi, rsyncPath);
                psi.ArgumentList.Add("--version");

                using var proc = Process.Start(psi);
                if (proc is null)
                    return new RsyncCapabilities(null, false);

                if (!proc.WaitForExit(2000))
                {
                    try
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Capability detection is best effort. A failed timeout
                        // cleanup must not prevent the guarded fallback path.
                    }
                    return new RsyncCapabilities(null, false);
                }

                string output = proc.StandardOutput.ReadToEnd();
                Version? version = ParseVersion(output);
                bool supportsProgress2 = version is not null && version >= new Version(3, 1, 0);
                return new RsyncCapabilities(version, supportsProgress2);
            }
            catch
            {
                return new RsyncCapabilities(null, false);
            }
        }

        private static Version? ParseVersion(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0)
                return null;

            // Expected: "rsync  version 3.4.1  protocol version 32"
            string[] tokens = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string? versionToken = tokens.FirstOrDefault(t => t.Any(char.IsDigit) && t.Contains('.'));
            return Version.TryParse(versionToken, out Version? parsed) ? parsed : null;
        }

        private static double? TryParsePercent(string line)
        {
            // Very simple heuristic: find the first '%' and read the preceding digits.
            int idx = line.IndexOf('%');
            if (idx <= 0)
                return null;

            // Walk backwards to gather digits
            int end = idx - 1;
            int start = end;
            while (start >= 0 && char.IsDigit(line[start]))
                start--;

            start++;

            if (start > end)
                return null;

            string numberSpan = line[start..(end + 1)];
            if (double.TryParse(numberSpan, out double value))
            {
                if (value >= 0 && value <= 100)
                    return value;
            }

            return null;
        }

        private static string ToRsyncPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            string normalized = path;
            if (normalized.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "\\" + normalized.Substring(7);
            }
            else if (normalized.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(4);
            }

            if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return "//" + normalized.TrimStart('\\').Replace('\\', '/');
            }

            string full = Path.GetFullPath(normalized);
            if (full.Length >= 2 && full[1] == ':')
            {
                char drive = char.ToLowerInvariant(full[0]);
                string rest = full.Substring(2).TrimStart('\\').Replace('\\', '/');
                return $"/cygdrive/{drive}/{rest}";
            }

            return full.Replace('\\', '/');
        }

        private static void ConfigureMacLibraryPath(ProcessStartInfo psi, string rsyncPath)
        {
            if (!OperatingSystem.IsMacOS())
                return;

            if (string.IsNullOrWhiteSpace(rsyncPath))
                return;

            string? directory = Path.GetDirectoryName(rsyncPath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            string libDir = Path.GetFullPath(Path.Combine(directory, "..", "lib"));
            if (!Directory.Exists(libDir))
                return;

            string existing = psi.Environment.TryGetValue("DYLD_LIBRARY_PATH", out string? current)
                ? current ?? string.Empty
                : string.Empty;
            psi.Environment["DYLD_LIBRARY_PATH"] = PrependPathEntry(existing, libDir);

            string fallback = psi.Environment.TryGetValue("DYLD_FALLBACK_LIBRARY_PATH", out string? fallbackCurrent)
                ? fallbackCurrent ?? string.Empty
                : string.Empty;
            psi.Environment["DYLD_FALLBACK_LIBRARY_PATH"] = PrependPathEntry(fallback, libDir);
        }

        private static string PrependPathEntry(string existing, string entry)
        {
            if (string.IsNullOrWhiteSpace(existing))
                return entry;

            char separator = ':';
            string[] parts = existing.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(p => string.Equals(p, entry, StringComparison.Ordinal)))
                return existing;

            return $"{entry}{separator}{existing}";
        }
    }
}
