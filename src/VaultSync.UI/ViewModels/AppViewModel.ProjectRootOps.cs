using System;
using System.IO;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private void ReconcileBlankProjectRootsOnStartup()
        {
            try
            {
                var cfg = AppConfigStore.GetSnapshot();
                var projectsRoot = cfg.ProjectsRoot?.Trim();
                if (string.IsNullOrWhiteSpace(projectsRoot))
                {
                    DiagnosticsLogger.Record("Startup project-root reconciliation skipped: ProjectsRoot is empty.");
                    return;
                }

                if (!Directory.Exists(projectsRoot))
                {
                    DiagnosticsLogger.Record($"Startup project-root reconciliation skipped: ProjectsRoot '{projectsRoot}' does not exist.");
                    return;
                }

                var repaired = 0;
                foreach (var project in _repo.GetAllProjects())
                {
                    if (!string.IsNullOrWhiteSpace(project.RootPath))
                        continue;

                    var candidate = Path.Combine(projectsRoot, project.Name);
                    if (!Directory.Exists(candidate))
                        continue;

                    if (TryUpdateProjectRootPath(project, candidate))
                    {
                        repaired++;
                        DiagnosticsLogger.Record($"Startup project-root reconciliation repaired '{project.Name}' -> '{candidate}'.");
                    }
                }

                if (repaired == 0)
                    return;

                Dispatcher.UIThread.Post(() =>
                {
                    _ = _projectsViewModel.RefreshAsync(forceDiscovery: false);

                    if (_dashboardViewModel is not null)
                    {
                        _ = _dashboardViewModel.RefreshAsync(force: true);
                    }
                });
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Startup project-root reconciliation failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private bool TryUpdateProjectRootPath(Project project, string newPath)
        {
            if (project.Id > 0)
                return _repo.UpdateProjectPath(project.Id, newPath, out _);

            return _repo.UpdateProjectPath(project.Name, newPath, out _);
        }
    }
}
