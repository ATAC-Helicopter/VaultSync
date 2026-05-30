using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;

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

        private async Task<DestinationResolution> PrepareDestinationForAutoBackupAsync(BackupDestination dest, AppConfig cfg)
        {
            DestinationResolution first = await PrepareDestinationAsync(dest, cfg).ConfigureAwait(false);
            if (first.IsSuccess)
                return first;

            string display = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path ?? string.Empty : dest.Alias!;
            DiagnosticsLogger.Record($"[AutoBackup] Destination '{display}' unavailable on first prepare; probing wake path before retry. Message='{first.Message}'");

            DestinationTestResult probe = await Task.Run(() => TryTestDestination(dest, cfg)).ConfigureAwait(false);
            UpdateDestinationProbeSummary(dest, probe);

            await Task.Delay(AutoBackupDestinationWakeDelay).ConfigureAwait(false);

            DestinationResolution second = await PrepareDestinationAsync(dest, cfg).ConfigureAwait(false);
            DiagnosticsLogger.Record(
                $"[AutoBackup] Destination '{display}' prepare retry complete. Success={second.IsSuccess}; Message='{second.Message}'");
            return second;
        }

        private async Task WarmAutoBackupDestinationsAsync(AppConfig cfg, IReadOnlyCollection<BackupDestination> destinations)
        {
            if (destinations.Count == 0)
                return;

            bool anyUnreachable = false;
            foreach (BackupDestination dest in destinations)
            {
                string display = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path ?? string.Empty : dest.Alias!;
                try
                {
                    DiagnosticsLogger.Record($"[AutoBackup] Warm-up probe start: '{display}'.");
                    DestinationTestResult result = await Task.Run(() => TryTestDestination(dest, cfg)).ConfigureAwait(false);
                    UpdateDestinationProbeSummary(dest, result);
                    if (!result.Reachable)
                    {
                        anyUnreachable = true;
                        DiagnosticsLogger.Record($"[AutoBackup] Warm-up probe did not reach '{display}': {result.Message}");
                    }
                }
                catch (Exception ex)
                {
                    anyUnreachable = true;
                    DiagnosticsLogger.Record($"[AutoBackup] Warm-up probe failed for '{display}': {ex.GetType().Name} - {ex.Message}");
                }
            }

            if (anyUnreachable)
            {
                DiagnosticsLogger.Record($"[AutoBackup] Waiting {AutoBackupDestinationWakeDelay.TotalSeconds:0}s before retrying warmed destinations.");
                await Task.Delay(AutoBackupDestinationWakeDelay).ConfigureAwait(false);
            }
        }

        private BackupAllPreparationResult PrepareBackupAll()
        {
            AppConfig cfg = _configStore.GetSnapshot();
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
