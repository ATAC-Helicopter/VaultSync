using System;
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
        public string Name => "rsync";

        // ISyncRunner implementation (without progress callback)
        public Task<int> SyncAsync(Project project, string destination, bool dryRun, CancellationToken ct)
            => SyncAsync(project, destination, dryRun, progressCallback: null, ct);

        /// <summary>
        /// Run rsync to mirror project.RootPath into destination, optionally reporting progress.
        /// </summary>
        public async Task<int> SyncAsync(
            Project project,
            string destination,
            bool dryRun,
            Action<double, string, string>? progressCallback,
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
                FileName               = "rsync",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            // trailing slash on source for rsync semantics (copy contents)
            var src = project.RootPath.EndsWith(Path.DirectorySeparatorChar)
                ? project.RootPath
                : project.RootPath + Path.DirectorySeparatorChar;

            // common flags: archive, delete extra files at dest
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add("--delete");
            psi.ArgumentList.Add("--human-readable");

            // Make rsync actually print progress lines with percentages.
            psi.ArgumentList.Add("--progress");

            // progress2 gives us aggregate stats; combined with --progress we should see % in output.
            psi.ArgumentList.Add("--info=progress2");

            // macOS: avoid metadata noise; on Linux it's harmless
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                psi.ArgumentList.Add("--force");

            if (dryRun)
                psi.ArgumentList.Add("--dry-run");

            // Apply exclude-from file if we generated one
            if (tempExcludeFile != null)
            {
                psi.ArgumentList.Add($"--exclude-from={tempExcludeFile}");
            }

            psi.ArgumentList.Add(src);
            psi.ArgumentList.Add(destination);

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = false };

            string? currentFile = null;

            // Helper to parse progress from any rsync output line (stdout or stderr)
            void HandleProgressLine(string? data)
            {
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
    }
}