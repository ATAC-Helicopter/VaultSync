using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services
{
    public sealed class SyncService
    {
        private readonly ISyncRunner _runner;

        public SyncService() : this(ChooseRunner()) { }

        public SyncService(ISyncRunner runner)
        {
            _runner = runner;
        }

        public Task<int> SyncAsync(Project project, string destination, bool dryRun, CancellationToken ct)
            => _runner.SyncAsync(project, destination, dryRun, ct);

        public string RunnerName => _runner.Name;

        private static ISyncRunner ChooseRunner()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Prefer robocopy if present; otherwise fall back to .NET copy loop (todo) or error.
                if (IsOnPath("robocopy")) return new RobocopyRunner();

                throw new InvalidOperationException("robocopy not found on PATH. Install it (comes with Windows) or add to PATH.");
            }

            // macOS/Linux: rsync
            if (IsOnPath("rsync")) return new RsyncRunner();

            throw new InvalidOperationException("rsync not found on PATH. Please install rsync.");
        }

        private static bool IsOnPath(string tool)
        {
            // simple PATH probe
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
            foreach (var dir in path.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{tool}.exe" : tool);
                    if (File.Exists(candidate)) return true;
                }
                catch { /* ignore */ }
            }
            return false;
        }
    }
}