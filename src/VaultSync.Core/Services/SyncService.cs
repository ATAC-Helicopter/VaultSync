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
                // Prefer robocopy if present; otherwise fail with an actionable setup error.
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
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            char sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
            foreach (string dir in path.Split(sep, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"{tool}.exe" : tool);
                    if (File.Exists(candidate)) return true;
                }
                catch { /* ignore */ }
            }
            return false;
        }
    }
}
