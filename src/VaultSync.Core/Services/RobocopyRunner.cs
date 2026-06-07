using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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
        private static readonly Dictionary<string, (DateTime LastWriteUtc, Dictionary<string, string> Map)> s_presetIndexCache
            = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object s_presetIndexLock = new();

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
            (List<string> excludeFiles, List<string> excludeDirs) = LoadExcludes(project);

            // Normalize and enable long paths
            string src = NormalizeWinPath(project.RootPath);
            string dst = NormalizeWinPath(destination);

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
            int threadCount = _isNetworkDestination
                ? Math.Min(32, Math.Max(4, Environment.ProcessorCount))
                : Math.Min(128, Math.Max(8, Environment.ProcessorCount * 2));
            psi.ArgumentList.Add($"/MT:{threadCount}");
            if (maxBandwidthMbps is > 0)
            {
                int ipg = TransferPolicy.ToRobocopyIpgMilliseconds(maxBandwidthMbps.Value, threadCount);
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
            DateTime lastLog = DateTime.UtcNow;
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

                    string line = e.Data.Trim();

                    if (progressCallback is not null)
                    {
                        // First, try to parse a percentage from this line.
                        double? percent = TryParsePercent(line);
                        if (percent is double p)
                        {
                            lastPercent = p;
                            // Use the last-seen file path as the "current file" label if we have one.
                            string fileLabel = currentFile ?? string.Empty;
                            string etaText = line;
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
                                    int lastSpace = line.LastIndexOf(' ');
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

                using CancellationTokenRegistration reg = ct.Register(() =>
                {
                    try
                    {
                        if (!proc.HasExited)
                            proc.Kill(entireProcessTree: true);
                    }
                    catch { /* ignore */ }
                });

                int exit = await tcs.Task.ConfigureAwait(false);

                // Robocopy 0..7 are success (changes/no changes). Normalize to 0.
                int normalized = exit <= 7 ? 0 : exit;

                if (normalized != 0)
                {
                    // Emit stdout/stderr for diagnostics (trim to avoid flooding logs).
                    string outText = TrimLog(stdout);
                    string errText = TrimLog(stderr);

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
            }
        }

        private static void AddRobocopyExcludes(ProcessStartInfo psi, IReadOnlyList<string> files, IReadOnlyList<string> dirs)
        {
            // robocopy accepts: /XF file1 file2 ...  and /XD dir1 dir2 ...
            if (files.Count > 0)
            {
                psi.ArgumentList.Add("/XF");
                foreach (string f in files)
                    psi.ArgumentList.Add(f);
            }

            if (dirs.Count > 0)
            {
                psi.ArgumentList.Add("/XD");
                foreach (string d in dirs)
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
                string? presetPath = ResolvePresetFile(project.Preset);
                if (!string.IsNullOrWhiteSpace(presetPath))
                {
                    MergeIgnoreFile(presetPath!, files, dirs);
                }
            }

            // 2) Project-local .vaultsyncignore (always allowed to override/add rules)
            string localIgnore = Path.Combine(project.RootPath, ".vaultsyncignore");
            MergeIgnoreFile(localIgnore, files, dirs);

            return (files, dirs);
        }

        private static void MergeIgnoreFile(string path, List<string> files, List<string> dirs)
        {
            if (!File.Exists(path))
                return;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();

                // Skip comments / empty
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                // Handle ** wildcard (common in gitignore-style). Robocopy doesn't support it.
                // If the pattern looks like "bin/**" treat it as a dir exclude "bin".
                if (line.Contains("/**") || line.Contains("\\**"))
                {
                    string d = line.Replace("/**", string.Empty)
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
                    string d = line.TrimEnd('/');
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
            string? envDir = Environment.GetEnvironmentVariable("VAULTSYNC_PRESETS_DIR");
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                string? candidate = ResolvePresetFileInDirectory(envDir, presetName);
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate;
            }

            // 2) App-installed presets folder: <app>/presets
            string appDir = Path.Combine(AppContext.BaseDirectory, "presets");
            string? appPreset = ResolvePresetFileInDirectory(appDir, presetName);
            if (!string.IsNullOrWhiteSpace(appPreset))
                return appPreset;

            // 3) Dev tree: walk up to find src/presets
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                string candidateDir = Path.Combine(dir, "src", "presets");
                string? candidate = ResolvePresetFileInDirectory(candidateDir, presetName);
                if (!string.IsNullOrWhiteSpace(candidate))
                    return candidate;

                string? parent = Directory.GetParent(dir)?.FullName;
                if (parent is null)
                    break;

                dir = parent;
            }

            // 4) User presets: ~/.vaultsync/presets
            string home       = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string userDir = Path.Combine(home, ".vaultsync", "presets");
            string? userPreset = ResolvePresetFileInDirectory(userDir, presetName);
            if (!string.IsNullOrWhiteSpace(userPreset))
                return userPreset;

            return null;
        }

        private static string? ResolvePresetFileInDirectory(string? directory, string presetName)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return null;

            string direct = Path.Combine(directory, $"{presetName}.vaultsyncignore");
            if (File.Exists(direct))
                return direct;

            string? mapped = ResolvePresetFileFromIndex(directory, presetName);
            if (!string.IsNullOrWhiteSpace(mapped))
                return mapped;

            return null;
        }

        private static string? ResolvePresetFileFromIndex(string directory, string presetName)
        {
            try
            {
                string indexPath = Path.Combine(directory, "presets.index.json");
                if (!File.Exists(indexPath))
                    return null;

                Dictionary<string, string> map;
                lock (s_presetIndexLock)
                {
                    DateTime lastWrite = File.GetLastWriteTimeUtc(indexPath);
                    if (!s_presetIndexCache.TryGetValue(indexPath, out (DateTime LastWriteUtc, Dictionary<string, string> Map) cached) || cached.LastWriteUtc != lastWrite)
                    {
                        string json = File.ReadAllText(indexPath);
                        PresetIndex? index = JsonSerializer.Deserialize<PresetIndex>(json);
                        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                        if (index?.Presets != null)
                        {
                            foreach (PresetInfo preset in index.Presets)
                            {
                                if (string.IsNullOrWhiteSpace(preset.Id) || string.IsNullOrWhiteSpace(preset.File))
                                    continue;

                                parsed[preset.Id] = preset.File;
                            }
                        }

                        cached = (lastWrite, parsed);
                        s_presetIndexCache[indexPath] = cached;
                    }

                    map = cached.Map;
                }

                if (!map.TryGetValue(presetName, out string? fileName) || string.IsNullOrWhiteSpace(fileName))
                    return null;

                string candidate = Path.Combine(directory, fileName);
                return File.Exists(candidate) ? candidate : null;
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeRobocopyGlob(string pattern)
        {
            // Convert forward slashes to backslashes; keep wildcards as-is.
            // Robocopy accepts wildcards in names, e.g. *.tmp or obj\*.
            string p = pattern.Replace('/', '\\');

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
            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              .Replace('/', '\\');

            // If already long-path or UNC, leave as-is.
            if (trimmed.StartsWith(@"\\?\") || trimmed.StartsWith(@"\\"))
                return trimmed;

            // UNC share (e.g. \\server\share) -> long UNC form \\?\UNC\server\share
            if (trimmed.StartsWith(@"\\"))
            {
                string withoutLeadingSlashes = trimmed.TrimStart('\\');
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
            int idx = line.IndexOf('%');
            if (idx <= 0)
                return null;

            int end = idx - 1;
            int start = end;
            bool hasDigit = false;

            while (start >= 0)
            {
                char ch = line[start];
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

            string numberSpan = line[start..(end + 1)];
            if (double.TryParse(numberSpan, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
            {
                if (value >= 0 && value <= 100)
                    return value;
            }

            return null;
        }

        private sealed class PresetIndex
        {
            public List<PresetInfo> Presets { get; set; } = new();
        }

        private sealed class PresetInfo
        {
            public string Id { get; set; } = string.Empty;
            public string File { get; set; } = string.Empty;
        }

        private static string TrimLog(StringBuilder sb, int maxChars = 4000)
        {
            string text = sb.ToString();
            if (text.Length <= maxChars)
                return text;

            // Keep the tail, since robocopy typically prints errors at the end.
            return text[^maxChars..];
        }
    }
}
