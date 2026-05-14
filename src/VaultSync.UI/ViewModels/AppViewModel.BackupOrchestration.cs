using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private void UpdateAggregateBackupAllUi(
            ConcurrentDictionary<int, double> progressPerProject,
            ref DateTime lastAggregateUiUpdateUtc,
            string currentFile,
            string etaText)
        {
            if (progressPerProject.IsEmpty)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    BackupsViewModel.BackupProgress = 0;
                    BackupsViewModel.BackupCurrentFile = AppViewModel.L("Backups.Status.Preparing", "Preparing backup...");
                    BackupsViewModel.BackupEtaText = string.Empty;
                    BackupsViewModel.BusyMessage = AppViewModel.L("Backups.Busy.All", "Backing up all projects...");
                });
                return;
            }

            double avg = progressPerProject.Values.DefaultIfEmpty(0).Average();
            DateTime now = DateTime.UtcNow;
            if (avg < 100 && (now - lastAggregateUiUpdateUtc) < TimeSpan.FromMilliseconds(200))
            {
                return;
            }

            lastAggregateUiUpdateUtc = now;

            string label;
            if (!string.IsNullOrWhiteSpace(currentFile))
            {
                label = currentFile;
            }
            else if (avg <= 0.1)
            {
                label = AppViewModel.L("Backups.Status.Preparing", "Preparing backup...");
            }
            else if (avg < 100)
            {
                label = AppViewModel.L("Backups.Status.RunningMultiple", "Running backups...");
            }
            else
            {
                label = AppViewModel.L("Backups.Status.AllCompleted", "All backups completed");
            }

            Dispatcher.UIThread.Post(() =>
            {
                BackupsViewModel.BackupProgress = avg;
                BackupsViewModel.BackupCurrentFile = label;
                BackupsViewModel.BackupEtaText = etaText;
                BackupsViewModel.BusyMessage = AppViewModel.L("Backups.Busy.All", "Backing up all projects...");
            });
        }

        private DestinationResolution PrepareDestination(BackupDestination dest, AppConfig cfg)
        {
            NetworkCredentialProfile? profile = cfg.Network.Credentials?
                .FirstOrDefault(c =>
                    string.Equals(c.Name, dest.CredentialName ?? string.Empty, StringComparison.OrdinalIgnoreCase));

            DestinationResolution resolution = _networkMountService.PrepareDestination(dest, profile);
            RuntimeLog.WriteVerbose($"[Backup] Destination resolved: alias='{dest.Alias ?? dest.Path}', path='{dest.Path}', effective='{resolution.EffectivePath}', success={resolution.IsSuccess}, mountedByUs={resolution.MountedByUs}");
            UpdateDestinationProbeSummary(dest, new DestinationTestResult(
                resolution.IsSuccess,
                resolution.IsSuccess,
                resolution.EffectivePath,
                resolution.Message));

            if (resolution.IsSuccess && !string.IsNullOrWhiteSpace(resolution.EffectivePath))
                PruneMissingBackupsFromPreparedDestination(dest, resolution.EffectivePath, cfg);

            return resolution;
        }

        private Task<DestinationResolution> PrepareDestinationAsync(BackupDestination dest, AppConfig cfg)
        {
            return Task.Run(() => PrepareDestination(dest, cfg));
        }

        private BackupAllPreparationResult PrepareBackupAll()
        {
            AppConfig cfg = AppConfigStore.GetSnapshot();
            System.Collections.Generic.List<BackupDestination> destinations = AppViewModel.GetAllDestinations(cfg);
            if (destinations.Count == 0)
            {
                return BackupAllPreparationResult.Failure("no_destination");
            }

            return BackupAllPreparationResult.Success(cfg);
        }

        private sealed record BackupAllPreparationResult(bool IsReady, string? FailureCode, AppConfig? Config)
        {
            public static BackupAllPreparationResult Failure(string reason) => new(false, reason, null);
            public static BackupAllPreparationResult Success(AppConfig cfg) => new(true, null, cfg);
        }

        private void ResolveBackupRoots(
            Project project,
            string configuredBackupRoot,
            out string effectiveBackupRoot,
            out string? preferredFinalBackupRoot)
        {
            BackupSafetyService.EnsureSafeBackupRoot(project, configuredBackupRoot);

            effectiveBackupRoot = configuredBackupRoot;
            preferredFinalBackupRoot = null;

            if (_settingsViewModel?.PreferExternalDrives == true && IsNetworkPath(configuredBackupRoot))
            {
                if (Directory.Exists(configuredBackupRoot))
                {
                    TryMigrateTempBackups(project, configuredBackupRoot);
                }
                else
                {
                    var tempRoot = BackupSafetyService.GetOfflineStagingRoot(project);
                    Directory.CreateDirectory(tempRoot);
                    BackupSafetyService.EnsureSafeBackupRoot(project, tempRoot);

                    effectiveBackupRoot = tempRoot;
                    preferredFinalBackupRoot = configuredBackupRoot;
                    EnsureNasMonitorStarted();
                    RuntimeLog.WriteVerbose($"[Backup] Network destination unavailable for project '{project.Name}'. Using safe offline staging root '{tempRoot}'.");
                }
            }
        }

        private static void TryMigrateTempBackups(Project project, string targetRoot)
        {
            TryMigrateTempBackupsFromRoot(BackupSafetyService.GetOfflineStagingRoot(project), targetRoot);
            TryMigrateTempBackupsFromRoot(BackupSafetyService.GetLegacyProjectTempRoot(project), targetRoot);
        }

        private static void TryMigrateTempBackupsFromRoot(string tempRoot, string targetRoot)
        {
            if (!Directory.Exists(tempRoot))
                return;

            Directory.CreateDirectory(targetRoot);

            foreach (string dir in Directory.EnumerateDirectories(tempRoot))
            {
                string dest = Path.Combine(targetRoot, Path.GetFileName(dir));

                try
                {
                    if (!Directory.Exists(dest))
                        Directory.Move(dir, dest);
                }
                catch
                {
                }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(tempRoot).Any())
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }

        private static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            return path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"//", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
        }
    }
}
