using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services
{
    /// <summary>
    /// Windows fallback using robocopy. Interprets robocopy's exit codes:
    /// 0..7 are success; 8+ are failures.
    /// Respects presets and project-local .vaultsyncignore via /XD and /XF.
    /// </summary>
    public sealed class RobocopyRunner : ISyncRunner
    {
        public string Name => "robocopy";
        private readonly bool _isNetworkDestination;

        public RobocopyRunner(bool isNetworkDestination = false)
        {
            _isNetworkDestination = isNetworkDestination;
        }

        // ISyncRunner base signature (no progress)
        public Task<int> SyncAsync(Project project, string destination, bool dryRun, CancellationToken ct)
            => SyncAsync(project, destination, dryRun, progressCallback: null, maxBandwidthMbps: null, ct);

        /// <summary>
        /// Runs robocopy to mirror project.RootPath into destination, optionally reporting progress.
        /// </summary>
        public async Task<int> SyncAsync(
            Project project,
            string destination,
            bool dryRun,
            Action<double, string, string>? progressCallback,
            int? maxBandwidthMbps = null,
            CancellationToken ct = default)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                throw new PlatformNotSupportedException("Robocopy is Windows-only.");

            // Load exclusions from preset + project-local ignore
            var (excludeFiles, excludeDirs) = LoadExcludes(project);

            // Normalize and enable long paths
            var src = NormalizeWinPath(project.RootPath);
            var dst = NormalizeWinPath(destination);

            Directory.CreateDirectory(destination); // ensure exists; robocopy handles \\?\

            var psi = new ProcessStartInfo
            {
                FileName               = "robocopy",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                WindowStyle            = ProcessWindowStyle.Hidden
            };

            // Required: source + dest first
            psi.ArgumentList.Add(src);
            psi.ArgumentList.Add(dst);

            // Mirror tree; copy file data/attrs/timestamps; copy directory timestamps
            psi.ArgumentList.Add("/MIR");
            psi.ArgumentList.Add("/COPY:DAT");
            psi.ArgumentList.Add("/DCOPY:T");

            // Keep it fast and predictable
            psi.ArgumentList.Add("/R:1");
            psi.ArgumentList.Add("/W:1");
            var threadCount = _isNetworkDestination
                ? Math.Min(32, Math.Max(4, Environment.ProcessorCount))
                : Math.Min(128, Math.Max(8, Environment.ProcessorCount * 2));
            psi.ArgumentList.Add($"/MT:{threadCount}");
            if (maxBandwidthMbps is > 0)
            {
                var ipg = TransferPolicy.ToRobocopyIpgMilliseconds(maxBandwidthMbps.Value, threadCount);
                if (ipg > 0)
                    psi.ArgumentList.Add($"/IPG:{ipg}");
            }
            if (_isNetworkDestination)
            {
                // Network share tuning: restartable + tolerate time granularity, avoid cache thrash.
                psi.ArgumentList.Add("/Z");
                psi.ArgumentList.Add("/FFT");
                psi.ArgumentList.Add("/J");
            }
            // Apply exclusions (preset + local)
            AddRobocopyExcludes(psi, excludeFiles, excludeDirs);

            if (dryRun)
            {
                // List only; reduce noise but show intent
                psi.ArgumentList.Add("/L");
                psi.ArgumentList.Add("/NS");  // no size
                psi.ArgumentList.Add("/NC");  // no class
                psi.ArgumentList.Add("/NFL"); // no file list
                psi.ArgumentList.Add("/NDL"); // no dir list
                psi.ArgumentList.Add("/NJH"); // no job header
                psi.ArgumentList.Add("/NJS"); // no job summary
            }
            else
            {
                // For progress parsing we keep file lines, but remove header/summary noise.
                psi.ArgumentList.Add("/NJH"); // no header
                psi.ArgumentList.Add("/NJS"); // no summary
                psi.ArgumentList.Add("/ETA"); // enable percent + ETA lines
                psi.ArgumentList.Add("/FP");  // include full paths in file lines
            }

            string? currentFile = null;
            double? lastPercent = null;
            var lastLog = DateTime.UtcNow;
            var logInterval = TimeSpan.FromSeconds(5);
            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var tcs    = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnData(object? _, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                {
                    stdout.AppendLine(e.Data);

                    var line = e.Data.Trim();

                    if (progressCallback is not null)
                    {
                        // First, try to parse a percentage from this line.
                        var percent = TryParsePercent(line);
                        if (percent is double p)
                        {
                            lastPercent = p;
                            // Use the last-seen file path as the "current file" label if we have one.
                            var fileLabel = currentFile ?? string.Empty;
                            var etaText = line;
                            progressCallback(p, fileLabel, etaText);

                            if ((DateTime.UtcNow - lastLog) >= logInterval)
                            {
                                Console.WriteLine($"[RobocopyRunner] {line}");
                                lastLog = DateTime.UtcNow;
                            }
                        }
                        else
                        {
                            // If there's no %, this might be a file line; try to extract a path-like tail.
                            if (!string.IsNullOrWhiteSpace(line) &&
                                !line.StartsWith("   Total", StringComparison.OrdinalIgnoreCase) &&
                                !line.StartsWith("   New Dir", StringComparison.OrdinalIgnoreCase) &&
                                !line.StartsWith("   New File", StringComparison.OrdinalIgnoreCase) &&
                                !line.StartsWith("Total", StringComparison.OrdinalIgnoreCase))
                            {
                                // Heuristic: if the line contains a slash or backslash, treat the trailing token as the path.
                                if (line.Contains('\\') || line.Contains('/'))
                                {
                                    var lastSpace = line.LastIndexOf(' ');
                                    if (lastSpace >= 0 && lastSpace < line.Length - 1)
                                        currentFile = line[(lastSpace + 1)..];
                                    else
                                        currentFile = line;
                                }
                                else
                                {
                                    // As a fallback, keep the whole line.
                                    currentFile = line;
                                }

                                progressCallback(lastPercent ?? 0, currentFile ?? string.Empty, string.Empty);

                                if ((DateTime.UtcNow - lastLog) >= logInterval)
                                {
                                    Console.WriteLine($"[RobocopyRunner] {currentFile}");
                                    lastLog = DateTime.UtcNow;
                                }
                            }
                        }
                    }
                }
            }

            void OnErr(object? _, DataReceivedEventArgs e)
            {
                if (e.Data != null)
                    stderr.AppendLine(e.Data);
            }

            void OnExit(object? _, EventArgs __)
            {
                tcs.TrySetResult(proc.ExitCode);
            }

            proc.OutputDataReceived += OnData;
            proc.ErrorDataReceived += OnErr;
            proc.Exited            += OnExit;

            try
            {
                if (!proc.Start())
                    throw new InvalidOperationException("Failed to start robocopy.");

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                using var reg = ct.Register(() =>
                {
                    try
                    {
                        if (!proc.HasExited)
                            proc.Kill(entireProcessTree: true);
                    }
                    catch { /* ignore */ }
                });

                var exit = await tcs.Task.ConfigureAwait(false);

                // Robocopy 0..7 are success (changes/no changes). Normalize to 0.
                var normalized = exit <= 7 ? 0 : exit;

                if (normalized != 0)
                {
                    // Emit stdout/stderr for diagnostics (trim to avoid flooding logs).
                    var outText = TrimLog(stdout);
                    var errText = TrimLog(stderr);

                    Console.WriteLine($"[RobocopyRunner] robocopy failed (exit={exit}, normalized={normalized}) src='{src}' dst='{dst}'.");
                    if (outText.Length > 0)
                        Console.WriteLine($"[RobocopyRunner][stdout]\n{outText}");
                    if (errText.Length > 0)
                        Console.WriteLine($"[RobocopyRunner][stderr]\n{errText}");
                }

                return normalized;
            }
            finally
            {
                proc.OutputDataReceived -= OnData;
                proc.ErrorDataReceived -= OnErr;
                proc.Exited            -= OnExit;

                // Optional: log stdout/stderr if exit > 7
                // _log.Debug(stdout.ToString());
                // _log.Error(stderr.ToString());
            }
        }

        private static void AddRobocopyExcludes(ProcessStartInfo psi, IReadOnlyList<string> files, IReadOnlyList<string> dirs)
        {
            // robocopy accepts: /XF file1 file2 ...  and /XD dir1 dir2 ...
            if (files.Count > 0)
            {
                psi.ArgumentList.Add("/XF");
                foreach (var f in files)
                    psi.ArgumentList.Add(f);
            }

            if (dirs.Count > 0)
            {
                psi.ArgumentList.Add("/XD");
                foreach (var d in dirs)
                    psi.ArgumentList.Add(d);
            }
        }

        private static (List<string> files, List<string> dirs) LoadExcludes(Project project)
        {
            var files = new List<string>();
            var dirs  = new List<string>();

            // 1) Preset file resolved using the same rules as the new preset system
            //    (env override, app presets/, src/presets/, user ~/.vaultsync/presets).
            if (!string.IsNullOrWhiteSpace(project.Preset))
            {
                var presetPath = ResolvePresetFile(project.Preset);
                if (!string.IsNullOrWhiteSpace(presetPath))
                {
                    MergeIgnoreFile(presetPath!, files, dirs);
                }
            }

            // 2) Project-local .vaultsyncignore (always allowed to override/add rules)
            var localIgnore = Path.Combine(project.RootPath, ".vaultsyncignore");
            MergeIgnoreFile(localIgnore, files, dirs);

            return (files, dirs);
        }

        private static void MergeIgnoreFile(string path, List<string> files, List<string> dirs)
        {
            if (!File.Exists(path))
                return;

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();

                // Skip comments / empty
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                // Handle ** wildcard (common in gitignore-style). Robocopy doesn't support it.
                // If the pattern looks like "bin/**" treat it as a dir exclude "bin".
                if (line.Contains("/**") || line.Contains("\\**"))
                {
                    var d = line.Replace("/**", string.Empty)
                                .Replace("\\**", string.Empty)
                                .TrimEnd('/');

                    if (!string.IsNullOrWhiteSpace(d))
                        dirs.Add(NormalizeRobocopyGlob(d));

                    continue;
                }

                // Very simple parsing:
                // - trailing slash => treat as directory pattern
                // - everything else => file/glob pattern
                // - strip leading "./"
                if (line.StartsWith("./")) line = line.Substring(2);

                if (line.EndsWith("/"))
                {
                    // robocopy /XD likes bare dir names or relative paths; leave as-is
                    var d = line.TrimEnd('/');
                    if (!string.IsNullOrWhiteSpace(d))
                        dirs.Add(NormalizeRobocopyGlob(d));
                }
                else
                {
                    files.Add(NormalizeRobocopyGlob(line));
                }
            }
        }

        private static string? ResolvePresetFile(string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName))
                return null;

            // 1) Environment override
            var envDir = Environment.GetEnvironmentVariable("VAULTSYNC_PRESETS_DIR");
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                var candidate = Path.Combine(envDir, $"{presetName}.vaultsyncignore");
                if (File.Exists(candidate))
                    return candidate;
            }

            // 2) App-installed presets folder: <app>/presets
            var appPreset = Path.Combine(AppContext.BaseDirectory, "presets", $"{presetName}.vaultsyncignore");
            if (File.Exists(appPreset))
                return appPreset;

            // 3) Dev tree: walk up to find src/presets
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                var candidateDir = Path.Combine(dir, "src", "presets");
                var candidate    = Path.Combine(candidateDir, $"{presetName}.vaultsyncignore");
                if (File.Exists(candidate))
                    return candidate;

                var parent = Directory.GetParent(dir)?.FullName;
                if (parent is null)
                    break;

                dir = parent;
            }

            // 4) User presets: ~/.vaultsync/presets
            var home       = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userPreset = Path.Combine(home, ".vaultsync", "presets", $"{presetName}.vaultsyncignore");
            if (File.Exists(userPreset))
                return userPreset;

            return null;
        }

        private static string NormalizeRobocopyGlob(string pattern)
        {
            // Convert forward slashes to backslashes; keep wildcards as-is.
            // Robocopy accepts wildcards in names, e.g. *.tmp or obj\*.
            var p = pattern.Replace('/', '\\');

            // Robocopy does not understand "**" (recursive glob). Downgrade to single star.
            while (p.Contains("**"))
                p = p.Replace("**", "*");

            // Avoid leading ".\" which can confuse in some contexts
            if (p.StartsWith(@".\")) p = p.Substring(2);

            return p;
        }

        private static string NormalizeWinPath(string path)
        {
            // Trim trailing separators, convert to backslashes.
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              .Replace('/', '\\');

            // If already long-path or UNC, leave as-is.
            if (trimmed.StartsWith(@"\\?\") || trimmed.StartsWith(@"\\"))
                return trimmed;

            // UNC share (e.g. \\server\share) -> long UNC form \\?\UNC\server\share
            if (trimmed.StartsWith(@"\\"))
            {
                var withoutLeadingSlashes = trimmed.TrimStart('\\');
                return @"\\?\UNC\" + withoutLeadingSlashes;
            }

            // Drive-letter path: only add \\?\ if path is long enough to need it.
            // Robocopy can error 53 on mapped drives when always using \\?\.
            const int maxPath = 240; // conservative threshold
            if (trimmed.Length >= maxPath)
                return @"\\?\" + trimmed;

            return trimmed;
        }

        private static double? TryParsePercent(string line)
        {
            // Robocopy sometimes prints progress-like lines containing a percentage.
            // We use a simple heuristic: find the first '%' and read preceding digits.
            var idx = line.IndexOf('%');
            if (idx <= 0)
                return null;

            var end = idx - 1;
            var start = end;
            var hasDigit = false;

            while (start >= 0)
            {
                var ch = line[start];
                if (char.IsDigit(ch))
                {
                    hasDigit = true;
                    start--;
                    continue;
                }

                if ((ch == '.' || ch == ',') && hasDigit)
                {
                    start--;
                    continue;
                }

                break;
            }

            start++;

            if (!hasDigit || start > end)
                return null;

            var numberSpan = line[start..(end + 1)];
            if (double.TryParse(numberSpan, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                if (value >= 0 && value <= 100)
                    return value;
            }

            return null;
        }

        private static string TrimLog(StringBuilder sb, int maxChars = 4000)
        {
            var text = sb.ToString();
            if (text.Length <= maxChars)
                return text;

            // Keep the tail, since robocopy typically prints errors at the end.
            return text[^maxChars..];
        }
    }
}
