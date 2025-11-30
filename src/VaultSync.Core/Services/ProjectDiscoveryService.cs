using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Config;

namespace VaultSync.Core.Services
{
    /// <summary>
    /// Lightweight representation of a discovered project.
    /// For now we only expose what the UI/CLI need to list and select.
    /// Later we can enrich this with snapshot stats from the DB.
    /// </summary>
    public sealed record DiscoveredProject(
        string Name,
        string Path,
        DateTime? LastSnapshotTime,
        long? LastSnapshotSizeBytes);

    public interface IProjectDiscoveryService
    {
        /// <summary>
        /// Discover projects based on the configured ProjectsRoot.
        /// This is intentionally fast and side-effect free.
        /// </summary>
        Task<IReadOnlyList<DiscoveredProject>> DiscoverAsync(
            AppConfig config,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Default implementation: treat each subdirectory in the configured
    /// ProjectsRoot (or OS default) as a "project".
    /// Later we can tighten this to only include folders with VaultSync metadata.
    /// </summary>
    public sealed class ProjectDiscoveryService : IProjectDiscoveryService
    {
        public Task<IReadOnlyList<DiscoveredProject>> DiscoverAsync(
            AppConfig config,
            CancellationToken cancellationToken = default)
        {
            var root = string.IsNullOrWhiteSpace(config.ProjectsRoot)
                ? GetDefaultRoot()
                : config.ProjectsRoot;

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return Task.FromResult<IReadOnlyList<DiscoveredProject>>(
                    Array.Empty<DiscoveredProject>());
            }

            var projects = Directory
                .EnumerateDirectories(root)
                .Select(path =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var name = Path.GetFileName(
                        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

                    // Snapshot info will come from the DB later; keep null for now.
                    return new DiscoveredProject(
                        Name: name,
                        Path: path,
                        LastSnapshotTime: null,
                        LastSnapshotSizeBytes: null);
                })
                .ToList()
                .AsReadOnly();

            return Task.FromResult<IReadOnlyList<DiscoveredProject>>(projects);
        }

        private static string GetDefaultRoot()
        {
#if WINDOWS
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(docs, "Projects");
#else
            var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            return Path.Combine(home, "Projects");
#endif
        }
    }
}