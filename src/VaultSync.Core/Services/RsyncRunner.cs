using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services
{
    public sealed class RsyncRunner : ISyncRunner
    {
        public string Name => "rsync";

        public async Task<int> SyncAsync(Project project, string destination, bool dryRun, CancellationToken ct)
        {
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

            // Ignore file patterns from preset (handled by SnapshotService when hashing)
            // For rsync we can still add a default filter to skip .git etc. optional.
            // Not mandatory because snapshot logic already filters for verify/history.

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

            return proc.ExitCode;
        }
    }
}