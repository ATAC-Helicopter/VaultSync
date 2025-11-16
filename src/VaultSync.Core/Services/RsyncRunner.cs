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

        public async Task<int> SyncAsync(Project project, string destination, bool dryRun, CancellationToken ct)
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
                FileName = "rsync",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // trailing slash on source for rsync semantics (copy contents)
            var src = project.RootPath.EndsWith(Path.DirectorySeparatorChar)
                ? project.RootPath
                : project.RootPath + Path.DirectorySeparatorChar;

            // common flags: archive, delete extra files at dest, verbose modestly
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add("--delete");
            psi.ArgumentList.Add("--human-readable");

            // macOS: avoid metadata noise; on Linux it's harmless
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                psi.ArgumentList.Add("--force");

            if (dryRun) psi.ArgumentList.Add("--dry-run");

            // Apply exclude-from file if we generated one
            if (tempExcludeFile != null)
            {
                psi.ArgumentList.Add($"--exclude-from={tempExcludeFile}");
            }

            psi.ArgumentList.Add(src);
            psi.ArgumentList.Add(destination);

            using var proc = Process.Start(psi)!;

            // Drain outputs to avoid deadlocks
            var stdOut = proc.StandardOutput.ReadToEndAsync();
            var stdErr = proc.StandardError.ReadToEndAsync();

            await proc.WaitForExitAsync(ct);

            // Optional: you can log outputs here if you want
            _ = await stdOut;
            _ = await stdErr;

            // Cleanup temp exclude file
            if (tempExcludeFile != null && File.Exists(tempExcludeFile))
            {
                try { File.Delete(tempExcludeFile); } catch { /* ignore */ }
            }

            return proc.ExitCode;
        }
    }
}