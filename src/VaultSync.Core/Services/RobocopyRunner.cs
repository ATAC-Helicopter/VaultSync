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

        public async Task<int> SyncAsync(Project project, string destination, bool dryRun, CancellationToken ct)
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
                FileName = "robocopy",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
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
            psi.ArgumentList.Add("/MT"); // default threads (~8). Could be /MT:16 if desired.

            // Apply exclusions (preset + local)
            AddRobocopyExcludes(psi, excludeFiles, excludeDirs);

            if (dryRun)
            {
                // List only; reduce noise but show intent
                psi.ArgumentList.Add("/L");
                psi.ArgumentList.Add("/NS");  // no size
                psi.ArgumentList.Add("/NC");  // no class
                // You can comment the next two to show files/dirs in dry-run
                psi.ArgumentList.Add("/NFL"); // no file list
                psi.ArgumentList.Add("/NDL"); // no dir list
                psi.ArgumentList.Add("/NJH"); // no job header
                psi.ArgumentList.Add("/NJS"); // no job summary
            }
            else
            {
                // Quieter normal runs
                psi.ArgumentList.Add("/NFL"); // no file list
                psi.ArgumentList.Add("/NDL"); // no dir list
                psi.ArgumentList.Add("/NJH"); // no header
                psi.ArgumentList.Add("/NJS"); // no summary
            }

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnData(object? _, DataReceivedEventArgs e)
            {
                if (e.Data != null) stdout.AppendLine(e.Data);
            }
            void OnErr(object? _, DataReceivedEventArgs e)
            {
                if (e.Data != null) stderr.AppendLine(e.Data);
            }
            void OnExit(object? _, EventArgs __)
            {
                tcs.TrySetResult(proc.ExitCode);
            }

            proc.OutputDataReceived += OnData;
            proc.ErrorDataReceived += OnErr;
            proc.Exited += OnExit;

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
                return exit <= 7 ? 0 : exit;
            }
            finally
            {
                proc.OutputDataReceived -= OnData;
                proc.ErrorDataReceived -= OnErr;
                proc.Exited -= OnExit;

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

            // 1) Preset file under ~/.vaultsync/presets/<preset>.vaultsyncignore
            if (!string.IsNullOrWhiteSpace(project.Preset))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var presetPath = Path.Combine(home, ".vaultsync", "presets", $"{project.Preset}.vaultsyncignore");
                MergeIgnoreFile(presetPath, files, dirs);
            }

            // 2) Project-local .vaultsyncignore
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

        private static string NormalizeRobocopyGlob(string pattern)
        {
            // Convert forward slashes to backslashes; keep wildcards as-is.
            // Robocopy accepts wildcards in names, e.g. *.tmp or obj\*.
            var p = pattern.Replace('/', '\\');

            // Avoid leading ".\" which can confuse in some contexts
            if (p.StartsWith(@".\")) p = p.Substring(2);

            return p;
        }

        private static string NormalizeWinPath(string path)
        {
            // Trim trailing separators, convert to backslashes,
            // and add \\?\ long-path prefix if not present.
            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              .Replace('/', '\\');

            if (trimmed.StartsWith(@"\\?\") || trimmed.StartsWith(@"\\"))
                return trimmed; // already long/UNC

            return @"\\?\" + trimmed;
        }
    }
}