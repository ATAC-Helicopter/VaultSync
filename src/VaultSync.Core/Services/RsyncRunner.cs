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

        // ISyncRunner implementation (without progress callback)
        public Task<int> SyncAsync(Project project, string destination, bool dryRun, CancellationToken ct)
            => SyncAsync(project, destination, dryRun, progressCallback: null, linkDest: null, ct);

        /// <summary>
        /// Run rsync to mirror project.RootPath into destination, optionally reporting progress.
        /// </summary>
        public async Task<int> SyncAsync(
            Project project,
            string destination,
            bool dryRun,
            Action<double, string, string>? progressCallback,
            string? linkDest = null,
            CancellationToken ct = default)
        {
            // Build ignore filter file based on project's preset and local .vaultsyncignore
            var filter = FilterService.FromPresetAndLocal(project.RootPath, project.Preset);
            string? tempExcludeFile = null;

            if (filter.HasRules)
            {
                tempExcludeFile = Path.Combine(Path.GetTempPath(), $"vaultsync_exclude_{Guid.NewGuid():N}.txt");
                var patterns = filter.RawPatterns ?? Array.Empty<string>();
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
            var src = project.RootPath.EndsWith(Path.DirectorySeparatorChar)
                ? project.RootPath
                : project.RootPath + Path.DirectorySeparatorChar;

            // common flags: archive, delete extra files at dest
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add("--delete");
            psi.ArgumentList.Add("--human-readable");

            // Fast LAN / local copy optimizations: do not compress, optionally send whole files.
            psi.ArgumentList.Add("--no-compress"); // avoid wasting CPU on compression over LAN / local FS
            if (_useWholeFile)
                psi.ArgumentList.Add("--whole-file");  // skip delta algorithm, faster for LAN and mounted shares

            // Make rsync actually print progress lines with percentages.
            psi.ArgumentList.Add("--progress");

            // progress2 gives us aggregate stats; only available on newer rsync builds.
            if (GetCapabilities(_rsyncPath).SupportsInfoProgress2)
            {
                psi.ArgumentList.Add("--info=progress2");
            }

            if (!string.IsNullOrWhiteSpace(linkDest))
            {
                var linkPath = OperatingSystem.IsWindows() ? ToRsyncPath(linkDest) : linkDest;
                psi.ArgumentList.Add($"--link-dest={linkPath}");
            }

            // macOS: avoid metadata noise; on Linux it's harmless
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                psi.ArgumentList.Add("--force");

            if (dryRun)
                psi.ArgumentList.Add("--dry-run");

            // Apply exclude-from file if we generated one
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

            // Helper to parse progress from any rsync output line (stdout or stderr)
            void HandleProgressLine(string? data)
            {
                if (ct.IsCancellationRequested)
                    return;
                if (string.IsNullOrWhiteSpace(data) || progressCallback is null)
                    return;

                var line = data.Trim();

                // rsync typically prints the file path on one line, then a separate line
                // with the numeric progress (bytes, percent, speed, ETA).
                // We treat lines without '%' as potential file names and remember them.
                if (!line.Contains('%'))
                {
                    // Heuristic: ignore generic status lines, keep likely paths.
                    if (!line.StartsWith("sending incremental file list", StringComparison.OrdinalIgnoreCase) &&
                        !line.StartsWith("sent ", StringComparison.OrdinalIgnoreCase) &&
                        !line.StartsWith("total size is ", StringComparison.OrdinalIgnoreCase) &&
                        !line.StartsWith("receiving incremental file list", StringComparison.OrdinalIgnoreCase))
                    {
                        currentFile = line;
                    }

                    return;
                }

                var percent = TryParsePercent(line);
                if (percent is double p)
                {
                    // Use the last-seen file path as the "current file" for this progress tick.
                    var fileLabel = currentFile ?? string.Empty;
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
                try { proc.CancelErrorRead(); } catch { /* ignore */ }
                try { proc.CancelOutputRead(); } catch { /* ignore */ }
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                throw;
            }
            finally
            {
                // Cleanup temp exclude file
                if (tempExcludeFile != null && File.Exists(tempExcludeFile))
                {
                    try { File.Delete(tempExcludeFile); } catch { /* ignore */ }
                }
            }

            return proc.ExitCode;
        }

        private sealed record RsyncCapabilities(Version? Version, bool SupportsInfoProgress2);

        private static RsyncCapabilities GetCapabilities(string rsyncPath)
        {
            if (s_capabilityCache.TryGetValue(rsyncPath, out var cached))
                return cached;

            var detected = DetectCapabilities(rsyncPath);
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
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    return new RsyncCapabilities(null, false);
                }

                var output = proc.StandardOutput.ReadToEnd();
                var version = ParseVersion(output);
                var supportsProgress2 = version is not null && version >= new Version(3, 1, 0);
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

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0)
                return null;

            // Expected: "rsync  version 3.4.1  protocol version 32"
            var tokens = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var versionToken = tokens.FirstOrDefault(t => t.Any(char.IsDigit) && t.Contains('.'));
            return Version.TryParse(versionToken, out var parsed) ? parsed : null;
        }

        private static double? TryParsePercent(string line)
        {
            // Very simple heuristic: find the first '%' and read the preceding digits.
            var idx = line.IndexOf('%');
            if (idx <= 0)
                return null;

            // Walk backwards to gather digits
            var end = idx - 1;
            var start = end;
            while (start >= 0 && char.IsDigit(line[start]))
                start--;

            start++;

            if (start > end)
                return null;

            var numberSpan = line[start..(end + 1)];
            if (double.TryParse(numberSpan, out var value))
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

            var normalized = path;
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

            var full = Path.GetFullPath(normalized);
            if (full.Length >= 2 && full[1] == ':')
            {
                var drive = char.ToLowerInvariant(full[0]);
                var rest = full.Substring(2).TrimStart('\\').Replace('\\', '/');
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

            var directory = Path.GetDirectoryName(rsyncPath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            var libDir = Path.GetFullPath(Path.Combine(directory, "..", "lib"));
            if (!Directory.Exists(libDir))
                return;

            var existing = psi.Environment.TryGetValue("DYLD_LIBRARY_PATH", out var current)
                ? current ?? string.Empty
                : string.Empty;
            psi.Environment["DYLD_LIBRARY_PATH"] = PrependPathEntry(existing, libDir);

            var fallback = psi.Environment.TryGetValue("DYLD_FALLBACK_LIBRARY_PATH", out var fallbackCurrent)
                ? fallbackCurrent ?? string.Empty
                : string.Empty;
            psi.Environment["DYLD_FALLBACK_LIBRARY_PATH"] = PrependPathEntry(fallback, libDir);
        }

        private static string PrependPathEntry(string existing, string entry)
        {
            if (string.IsNullOrWhiteSpace(existing))
                return entry;

            var separator = ':';
            var parts = existing.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(p => string.Equals(p, entry, StringComparison.Ordinal)))
                return existing;

            return $"{entry}{separator}{existing}";
        }
    }
}
