
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services
{
    public interface ISyncRunner
    {
        /// <summary>
        /// Mirrors <paramref name="project.RootPath"/> to <paramref name="destination"/>.
        /// Returns process exit code; 0 means success.
        /// </summary>
        Task<int> SyncAsync(Project project, string destination, bool dryRun, CancellationToken ct);
        string Name { get; }
    }
}