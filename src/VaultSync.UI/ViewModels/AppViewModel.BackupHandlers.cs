using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.UI.Notifications;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private const string TelemetryBackupSingleSkipped = "backup_single_skipped";
        private const string TelemetryBackupAllSkipped = "backup_all_skipped";
        private const string TelemetryReason = "reason";
        private const string TelemetryProject = "project";
        private const string TelemetryProjectRoot = "projectRoot";
        private const string TelemetryDestinationPath = "destinationPath";
        private const string TelemetryUseArchiveMode = "useArchiveMode";
        private const string TelemetryDurationSeconds = "durationSeconds";
        private const string BackupsStatusPreparingKey = "Backups.Status.Preparing";
        private const string BackupsStatusPreparingFallback = "Preparing backup...";

        private void OnBackupProjectRequested(ProjectBackupItem? item)
        {
            AppViewModel.RunDetached(() => OnBackupProjectRequestedAsync(item), nameof(OnBackupProjectRequestedAsync));
        }

        private void OnBackupGroupRequested(IReadOnlyList<int>? projectIds)
        {
            AppViewModel.RunDetached(() => OnBackupGroupRequestedAsync(projectIds), nameof(OnBackupGroupRequestedAsync));
        }

        private async Task OnBackupGroupRequestedAsync(IReadOnlyList<int>? projectIds)
        {
            if (projectIds is null || projectIds.Count == 0)
                return;

            var uniqueIds = projectIds
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            if (uniqueIds.Count == 0)
                return;

            foreach (int projectId in uniqueIds)
            {
                Project? project;
                try
                {
                    project = _repo.GetProjectById(projectId);
                }
                catch
                {
                    continue;
                }

                if (project is null)
                    continue;

                var item = new ProjectBackupItem
                {
                    Id = project.Id.ToString(CultureInfo.InvariantCulture),
                    Name = project.Name
                };
                await OnBackupProjectRequestedAsync(item).ConfigureAwait(false);
            }
        }

        private async Task OnBackupProjectRequestedAsync(ProjectBackupItem? item)
        {
            bool trayRun = _trayInitiatedBackup;
            _trayInitiatedBackup = false;
            DateTime start = DateTime.UtcNow;
            bool inFlightAdded = false;

            if (ShouldPauseBackupsForBattery(out string? pauseReason))
            {
                BackupsViewModel.BackupCurrentFile = pauseReason;
                BackupsViewModel.BusyMessage = pauseReason;
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.BatteryPaused"],
                    NotificationSeverity.Warning);
                Telemetry.Log(TelemetryBackupSingleSkipped, b => b
                    .WithCode(TelemetryReason, "battery"));
                return;
            }

            if (item is null)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.NoProject"],
                    NotificationSeverity.Warning);
                Telemetry.Log(TelemetryBackupSingleSkipped, b => b
                    .WithCode(TelemetryReason, "no_project"));
                return;
            }

            if (!int.TryParse(item.Id, out int projectId))
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.InvalidProjectId"],
                    NotificationSeverity.Error);
                Telemetry.Log(TelemetryBackupSingleSkipped, b => b
                    .WithCode(TelemetryReason, "invalid_project_id"));
                return;
            }

            if (Volatile.Read(ref _backupAllInProgress) == 1)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.AlreadyRunning"],
                    NotificationSeverity.Info);
                Telemetry.Log(TelemetryBackupSingleSkipped, b => b
                    .WithCode(TelemetryReason, "backup_all_running")
                    .WithHashedString(TelemetryProject, item.Name));
                return;
            }

            if (BackupsViewModel.IsBusy && Volatile.Read(ref _manualBackupInFlightCount) == 0)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.AlreadyRunning"],
                    NotificationSeverity.Info);
                Telemetry.Log(TelemetryBackupSingleSkipped, b => b
                    .WithCode(TelemetryReason, "busy"));
                return;
            }

            BackupProjectPreparation preparation = await Task.Run(() => CreateManualBackupPreparation(projectId));

            if (preparation.Destinations.Count == 0)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.NoDestination"],
                    NotificationSeverity.Warning);
                Telemetry.Log(TelemetryBackupSingleSkipped, b => b
                    .WithCode(TelemetryReason, "no_destination"));
                return; // later: show error in UI
            }

            if (preparation.Project is null)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.ProjectNotFound"],
                    NotificationSeverity.Error);
                Telemetry.Log(TelemetryBackupSingleSkipped, b => b
                    .WithCode(TelemetryReason, "project_not_found"));
                return;
            }

            AppConfig cfg = preparation.Config;
            List<BackupDestination> destinations = preparation.Destinations;
            Project project = preparation.Project;
            LogBackupPolicyTransitionIfChanged(cfg, "manual-backup-start");
            string activePolicyText = GetBackupPolicyChipTextForConfig(cfg);
            if (!string.IsNullOrWhiteSpace(preparation.DestinationWarning))
            {
                BackupsViewModel.ShowNotification(preparation.DestinationWarning, "Warning");
                Telemetry.Log("backup_single_destination_fallback", b => b
                    .WithCode(TelemetryReason, preparation.DestinationWarningCode ?? "preferred_destination_fallback")
                    .WithHashedString(TelemetryProject, project.Name));
            }
            if (!TryResolveProjectRoot(project, cfg, out Project? resolvedProject, out string? rootError))
            {
                MaybeNotifyProjectRootMissing(project, rootError);
                Telemetry.Log(TelemetryBackupSingleSkipped, b => b
                    .WithCode(TelemetryReason, "project_root_missing")
                    .WithHashedString(TelemetryProject, project.Name)
                    .WithHashedString(TelemetryProjectRoot, project.RootPath));
                return;
            }
            project = resolvedProject;
            if (cfg.Backups.PromptRestoreAfterImport && project.NeedsRestore)
            {
                MaybeNotifyRestoreRecommended(project);
                Telemetry.Log("backup_single_advisory", b => b
                    .WithCode(TelemetryReason, "restore_recommended")
                    .WithHashedString(TelemetryProject, project.Name));
            }

            if (!_manualBackupInFlight.TryAdd(projectId, 0))
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.AlreadyRunning"],
                    NotificationSeverity.Info);
                Telemetry.Log(TelemetryBackupSingleSkipped, b => b
                    .WithCode(TelemetryReason, "duplicate")
                    .WithHashedString(TelemetryProject, item.Name));
                return;
            }
            inFlightAdded = true;
            int manualCount = Interlocked.Increment(ref _manualBackupInFlightCount);
            bool isFirstManual = manualCount == 1;
            int maxSnapshotsToKeep = cfg.Backups.MaxSnapshotsPerProject;
            bool useArchiveMode = _settingsViewModel.UseBackupCompression;
            Telemetry.Log("backup_single_start", b => b
                .WithHashedString(TelemetryProject, project.Name)
                .WithHashedString(TelemetryProjectRoot, project.RootPath)
                .WithCount("destinations", destinations.Count)
                .WithFlag(TelemetryUseArchiveMode, useArchiveMode));

            if (isFirstManual)
            {
                // Reset progress state
                BackupsViewModel.BackupProgress = 0;
                BackupsViewModel.BackupCurrentFile = _localizationService["Backups.Notification.Preparing"];
                BackupsViewModel.BackupEtaText = string.Empty;

                // Reset per-project cards and add this project
                BackupsViewModel.ClearActiveBackups();
            }
            BackupsViewModel.UpdateActiveBackup(
                project.Id.ToString(),
                project.Name,
                0,
                AppViewModel.L(BackupsStatusPreparingKey, BackupsStatusPreparingFallback),
                string.Empty,
                policyText: activePolicyText);
            if (isFirstManual)
            {
                bool allowToggle = cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 };
                List<BackupDestination> overviewDestinations = AppViewModel.GetAllDestinations(cfg);
                BackupsViewModel.ResetDestinationStatuses(overviewDestinations, allowToggle);
                RefreshDestinationStatusOverview();
            }

            BackupsViewModel.IsBusy = true;
            BackupsViewModel.BusyMessage = isFirstManual
                ? Lf("Backups.Busy.Single", "Backing up {0}...", project.Name)
                : AppViewModel.L("Backups.Busy.All", "Backing up all projects...");
            if (trayRun && ShouldShowBackupWidget)
            {
                _backupWidgetService?.ShowForTrayBackup();
            }

            try
            {
                int? sharedSnapshotId = null;
                bool metadataWritten = false;
                bool cancelled = false;
                string? metadataRoot = null;
                int? metadataBackupId = null;
                int attempts = 0;
                int succeeded = 0;
                int failed = 0;
                int unreachable = 0;
                int driveBlocked = 0;

                foreach (BackupDestination dest in destinations)
                {
                    string destId = DestinationStatusItem.GetId(dest);
                    DestinationResolution resolution = await PrepareDestinationAsync(dest, cfg);
                    if (!resolution.IsSuccess)
                    {
                        BackupsViewModel.UpdateDestinationStatus(destId, resolution.Message, BackupsViewModel.SeverityStatus.Error);
                    }

                    if (!resolution.IsSuccess)
                    {
                        unreachable++;
                        Telemetry.Log("backup_single_destination_unreachable", b => b
                            .WithHashedString(TelemetryProject, project.Name)
                            .WithHashedString(TelemetryDestinationPath, dest.Path)
                            .WithHashedString("destinationAlias", dest.Alias ?? string.Empty));
                        continue;
                    }

                    DriveHealthDecision driveDecision = await EvaluateDriveHealthAsync(project.RootPath, resolution.EffectivePath);
                    if (!string.IsNullOrWhiteSpace(driveDecision.Message))
                    {
                        ShowDriveHealthNotification(driveDecision.Message, driveDecision.Severity);
                    }
                    if (driveDecision.Block)
                    {
                        driveBlocked++;
                        BackupsViewModel.UpdateDestinationStatus(destId, driveDecision.Message, BackupsViewModel.SeverityStatus.Warning);
                        _networkMountService.Cleanup(resolution);
                        continue;
                    }

                    string labelPrefix = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path;
                    int? archiveUploadBufferBytes = await EnsureArchiveUploadBufferAsync(
                        dest,
                        cfg,
                        resolution.EffectivePath,
                        useArchiveMode,
                        CancellationToken.None);
                    int retryMaxAttempts = Math.Clamp(dest.RetryMaxAttempts, 1, 10);
                    int retryBaseDelaySeconds = Math.Clamp(dest.RetryBackoffSeconds, 1, 300);

                    try
                    {
                        _ = TryShowPreflightEstimateAsync(project, resolution.EffectivePath, labelPrefix, useArchiveMode, cfg);
                        bool destinationSucceeded = false;
                        bool noChangesDetected = false;
                        for (int attemptIndex = 1; attemptIndex <= retryMaxAttempts; attemptIndex++)
                        {
                            try
                            {
                                var (Result, Elapsed) = await Task.Run(async () =>
                                {
                                    attempts++;
                                    bool isRemoteDestination = IsRemoteDestinationPath(resolution.EffectivePath)
                                        || IsRemoteDestinationPath(dest.Path);
                                    bool allowParallelUpload = cfg.Backups.EnableParallelArchiveUpload;
                                    bool preferParallelUpload = allowParallelUpload && isRemoteDestination;
                                    if (!allowParallelUpload)
                                    {
                                        RuntimeLog.WriteVerbose($"[BackupService] Parallel archive upload disabled by user settings for '{labelPrefix}'.");
                                    }
                                    var sw = Stopwatch.StartNew();
                                    BackupService.BackupRunResult result = await _backupService.RunBackupAsync(
                                        project,
                                        resolution.EffectivePath,
                                        isAuto: false,
                                        progressCallback: (percent, currentFile, etaText) =>
                                        {
                                            if (_backupCancelRequested.ContainsKey(project.Id))
                                                return;

                                            if (!ShouldUpdateBackupUi(project.Id, percent, etaText))
                                                return;

                                            bool isFinalizing = !string.IsNullOrWhiteSpace(etaText) &&
                                                               etaText.Contains("Finalizing", StringComparison.OrdinalIgnoreCase);
                                            string label = BuildBackupProgressLabel(etaText, currentFile, percent);
                                            if (!string.IsNullOrWhiteSpace(labelPrefix))
                                                label = $"[{labelPrefix}] {label}";

                                            BackupsViewModel.UpdateActiveBackup(
                                                project.Id.ToString(),
                                                project.Name,
                                                percent,
                                                label,
                                                etaText,
                                                allowCancel: !isFinalizing,
                                                destinationLabel: labelPrefix,
                                                policyText: activePolicyText);
                                            LogBackupProgress(project.Id, project.Name, percent, label, etaText);

                                            Dispatcher.UIThread.Post(() =>
                                            {
                                                BackupsViewModel.BackupProgress = percent;
                                                BackupsViewModel.BackupCurrentFile = label;
                                                BackupsViewModel.BackupEtaText = etaText;
                                            });
                                        },
                                        useArchiveMode: useArchiveMode,
                                        fullSnapshotHash: _settingsViewModel.UseFullSnapshotHash,
                                        maxSnapshotsToKeep: maxSnapshotsToKeep,
                                        minimumFreeSpacePercent: _settingsViewModel.MinimumFreeSpacePercent,
                                        preferredFinalBackupRoot: null,
                                        reuseSnapshotId: metadataWritten ? sharedSnapshotId : null,
                                        writeMetadata: !metadataWritten,
                                        destinationPath: resolution.EffectivePath,
                                        destinationAlias: labelPrefix,
                                        useRsyncDelta: _settingsViewModel.UseRsyncDelta,
                                        useIncrementalBackups: _settingsViewModel.UseIncrementalBackups,
                                        archiveUploadBufferBytes: archiveUploadBufferBytes,
                                        preferRunnerProgressOnly: isRemoteDestination,
                                        preferParallelArchiveUpload: preferParallelUpload,
                                        useScanCache: _settingsViewModel.EnableScanCache,
                                        aggressiveScanCache: _settingsViewModel.AggressiveScanCache,
                                        enableCheckpointedRetry: dest.EnableCheckpointResume
                                    );
                                    sw.Stop();

                                    if (!metadataWritten && result.BackupId > 0)
                                    {
                                        metadataWritten = true;
                                        metadataRoot = resolution.EffectivePath;
                                        metadataBackupId = result.BackupId;

                                        if (!sharedSnapshotId.HasValue && result.BackupId > 0)
                                        {
                                            Backup? created = _repo.GetBackupById(result.BackupId);
                                            sharedSnapshotId = created?.SnapshotId ?? sharedSnapshotId;
                                        }
                                    }

                                    return (Result: result, sw.Elapsed);
                                });

                                if (Result.SkippedForNoChanges)
                                {
                                    Telemetry.Log(TelemetryBackupSingleSkipped, b => b
                                        .WithCode(TelemetryReason, "no_changes")
                                        .WithHashedString(TelemetryProject, project.Name)
                                        .WithHashedString(TelemetryDestinationPath, dest.Path ?? string.Empty));
                                    noChangesDetected = true;
                                    destinationSucceeded = true;
                                    break;
                                }

                                if (Result.Cancelled)
                                {
                                    cancelled = true;
                                    BackupsViewModel.UpdateActiveBackup(
                                        project.Id.ToString(),
                                        project.Name,
                                        100,
                                        AppViewModel.L("Backups.Status.Cancelled", "Cancelled"),
                                        string.Empty,
                                        allowCancel: false,
                                        policyText: activePolicyText);
                                    Telemetry.Log("backup_single_cancelled", b => b
                                        .WithHashedString(TelemetryProject, project.Name)
                                        .WithHashedString(TelemetryDestinationPath, dest.Path ?? string.Empty)
                                        .WithFlag(TelemetryUseArchiveMode, useArchiveMode));
                                    destinationSucceeded = true;
                                    break;
                                }

                                if (Result.BackupId > 0)
                                {
                                    BackupsViewModel.UpdateActiveBackup(
                                        project.Id.ToString(),
                                        project.Name,
                                        100,
                                        AppViewModel.L("Backups.Status.Completed", "Completed"),
                                        string.Empty,
                                        policyText: activePolicyText);
                                    succeeded++;
                                    RecordBackupThroughput(Result.BackupId, Elapsed, useArchiveMode);
                                    TryExportMetadataForBackup(cfg, dest, resolution.EffectivePath, Result.BackupId);
                                    destinationSucceeded = true;
                                    break;
                                }

                                failed++;
                            }
                            catch (Exception ex) when (attemptIndex < retryMaxAttempts)
                            {
                                int delaySeconds = Math.Min(300, retryBaseDelaySeconds * (1 << Math.Max(0, attemptIndex - 1)));
                                failed++;
                                BackupsViewModel.UpdateDestinationStatus(
                                    destId,
                                    Lf(
                                        "Backups.Destinations.Retrying",
                                        "Retrying destination in {0}s (attempt {1}/{2})",
                                        delaySeconds,
                                        attemptIndex + 1,
                                        retryMaxAttempts),
                                    BackupsViewModel.SeverityStatus.Warning);
                                Telemetry.Log("backup_single_destination_retry", b => b
                                    .WithHashedString(TelemetryProject, project.Name)
                                    .WithHashedString(TelemetryDestinationPath, dest.Path)
                                    .WithHashedString("destinationAlias", dest.Alias ?? string.Empty)
                                    .WithCount("attempt", attemptIndex + 1)
                                    .WithCount("maxAttempts", retryMaxAttempts)
                                    .WithException(ex));
                                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), CancellationToken.None);
                            }
                        }

                        if (!destinationSucceeded)
                        {
                            BackupsViewModel.UpdateDestinationStatus(
                                destId,
                                Lf("Backups.Destinations.RetryExhausted", "Failed after {0} attempts", retryMaxAttempts),
                                BackupsViewModel.SeverityStatus.Error);
                        }
                        if (noChangesDetected)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Telemetry.Log("backup_single_cancelled", b => b
                            .WithHashedString(TelemetryProject, project.Name)
                            .WithHashedString(TelemetryDestinationPath, dest.Path)
                            .WithFlag(TelemetryUseArchiveMode, useArchiveMode));
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Telemetry.Log("backup_single_failure", b => b
                            .WithHashedString(TelemetryProject, project.Name)
                            .WithHashedString(TelemetryProjectRoot, project.RootPath)
                            .WithHashedString(TelemetryDestinationPath, dest.Path)
                            .WithHashedString("destinationAlias", dest.Alias ?? string.Empty)
                            .WithFlag(TelemetryUseArchiveMode, useArchiveMode)
                            .WithException(ex));
                    }
                    finally
                    {
                        _networkMountService.Cleanup(resolution);
                    }
                }

                if (cancelled && !metadataWritten)
                {
                    return;
                }

                if (!metadataWritten)
                {
                    throw new InvalidOperationException("No destinations completed successfully.");
                }

                // --- After backup: optional verification / post-hash ---
                AppConfig cfgAfter = await Task.Run(_configStore.Load);
                if (metadataRoot is not null)
                {
                    Backup? latest = metadataBackupId.HasValue
                        ? _repo.GetBackupById(metadataBackupId.Value)
                        : _repo.GetLatestBackupForProject(project.Id);

                    if (latest != null)
                    {
                        if (AppViewModel.ShouldRunVerification(project, isAutoRun: false, cfgAfter.Backups.VerifyAfterCreate))
                        {
                            StartVerificationAsync(project, latest, metadataRoot, "backup_single_verify_failed");
                        }
                        else
                        {
                            StartPostBackupHashingAsync(project, latest.SnapshotId);
                        }
                    }
                }

                ReloadBackupsVmData();
                await DashboardViewModel.RefreshAsync();

                // Refresh Projects view so the newly created snapshot appears immediately.
                await _projectsViewModel.RefreshAsync();

                Telemetry.Log("backup_single_success", b => b
                    .WithHashedString(TelemetryProject, project.Name)
                    .WithHashedString(TelemetryProjectRoot, project.RootPath)
                    .WithCount("destinations", destinations.Count)
                    .WithCount("attempts", attempts)
                    .WithCount("succeeded", succeeded)
                    .WithCount("failed", failed)
                    .WithCount("destinationsUnreachable", unreachable)
                    .WithCount("driveBlocked", driveBlocked)
                    .WithFlag(TelemetryUseArchiveMode, useArchiveMode)
                    .WithNumber(TelemetryDurationSeconds, (DateTime.UtcNow - start).TotalSeconds));

                // Notify success if enabled in settings and globally
                if (NotificationsEnabled && _settingsViewModel.NotifyOnBackupSuccess)
                {
                    string msg = Lf("Backups.Notification.Success", "Backup for '{0}' completed successfully.", project.Name);
                    string title = AppViewModel.L("Backups.Notification.SuccessTitle", "Backup completed");

                    _notificationService.ShowInfo(
                        title,
                        msg,
                        NotificationKind.Backup);

                    BackupsViewModel.ShowNotification(
                        Lf("Backups.Notification.Success", "Backup for '{0}' completed successfully.", project.Name),
                        "Info");

                    // Toast only when not already on the Backups page.
                    if (!IsOnBackupsPage)
                    {
                        GlobalNotificationCenter.Instance.Show(
                            msg,
                            NotificationSeverity.Info,
                            title);
                    }

                    if (ShouldRaiseSystemNotification)
                    {
                        GlobalNotificationCenter.Instance.ShowSystem(
                            msg,
                            NotificationSeverity.Info,
                            title);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // User cancelled the backup; keep UI tidy without surfacing an error toast.
                BackupsViewModel.BackupCurrentFile = AppViewModel.L("Backups.Notification.Cancelled", "Backup cancelled.");
                BackupsViewModel.BackupEtaText = string.Empty;
                Telemetry.Log("backup_single_cancelled", b => b
                    .WithHashedString(TelemetryProject, project?.Name ?? string.Empty)
                    .WithNumber(TelemetryDurationSeconds, (DateTime.UtcNow - start).TotalSeconds));
            }
            catch (Exception ex)
            {

                // Detect the low-disk-space condition thrown by BackupService
                bool isLowDisk =
                    ex is InvalidOperationException ioe &&
                    ioe.Message.Contains("does not have enough free space", StringComparison.OrdinalIgnoreCase);

                if (isLowDisk)
                {
                    // Low disk space: treat as a skipped backup with a clear warning,
                    // honoring the notifications settings.
                    Telemetry.Log("backup_single_low_disk", b => b
                        .WithHashedString(TelemetryProject, project.Name)
                        .WithHashedString(TelemetryProjectRoot, project.RootPath)
                        .WithNumber(TelemetryDurationSeconds, (DateTime.UtcNow - start).TotalSeconds));

                    if (NotificationsEnabled && _settingsViewModel.NotifyOnLowDiskSpace)
                    {
                        string title = AppViewModel.L("Backups.Notification.LowDiskTitle", "Low disk space");
                        string singleMessage = Lf(
                            "Backups.Notification.LowDiskMessage",
                            "Backup for '{0}' was skipped due to low disk space on the backup target.",
                            project.Name);

                        if (_lowDiskWarningShown.TryAdd(project.Id, 0))
                        {
                            QueueGroupedBackupProjectNotification(
                                "backup-low-disk",
                                project.Name,
                                NotificationSeverity.Warning,
                                title,
                                names => names.Count == 1
                                    ? singleMessage
                                    : Lf(
                                        "Backups.Notification.LowDiskMultiple",
                                        "Backups for {0} projects were skipped due to low disk space on the backup target: {1}.",
                                        names.Count,
                                        FormatGroupedProjectNames(names)));
                        }
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        BackupsViewModel.BackupCurrentFile = AppViewModel.L("Backups.Status.LowDisk", "Backup skipped: low disk space.");
                        BackupsViewModel.BackupEtaText =
                            string.IsNullOrWhiteSpace(BackupsViewModel.BackupEtaText)
                                ? ex.Message
                                : BackupsViewModel.BackupEtaText + " - " + AppViewModel.L("Backups.Status.LowDiskSuffix", "Low disk space");
                    });
                }
                else
                {
                    // Generic backup failure path
                    Telemetry.Log("backup_single_failure", b => b
                        .WithHashedString(TelemetryProject, project.Name)
                        .WithHashedString(TelemetryProjectRoot, project.RootPath)
                        .WithFlag(TelemetryUseArchiveMode, useArchiveMode)
                        .WithException(ex)
                        .WithNumber(TelemetryDurationSeconds, (DateTime.UtcNow - start).TotalSeconds));

                    if (NotificationsEnabled)
                    {
                        string msg = Lf("Backups.Notification.FailureMessage", "Backup failed for '{0}'. Check logs for details.", project.Name);
                        string title = AppViewModel.L("Backups.Notification.FailureTitle", "Backup failed");
                        string actionLabel = AppViewModel.L("Logs.CopySnippet", "Copy log snippet");
                        System.Windows.Input.ICommand actionCommand = CreateCopyLogSnippetCommand(
                            Lf("Logs.Snippet.BackupFailure", "Backup failure for '{0}'.", project.Name));

                        if (IsOnBackupsPage)
                        {
                            BackupsViewModel.ShowNotification(msg, "Error", actionLabel, actionCommand);
                        }
                        else
                        {
                            GlobalNotificationCenter.Instance.Show(
                                msg,
                                NotificationSeverity.Error,
                                title,
                                actionLabel: actionLabel,
                                actionCommand: actionCommand);
                        }

                        if (ShouldRaiseSystemNotification)
                        {
                            GlobalNotificationCenter.Instance.ShowSystem(
                                msg,
                                NotificationSeverity.Error,
                                title);
                        }
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        BackupsViewModel.BackupCurrentFile = AppViewModel.L("Backups.Notification.FailureTitle", "Backup failed");
                        BackupsViewModel.BackupEtaText =
                            string.IsNullOrWhiteSpace(BackupsViewModel.BackupEtaText)
                                ? ex.Message
                                : BackupsViewModel.BackupEtaText + " - " + AppViewModel.L("Backups.Status.FailedSuffix", "Failed");
                    });
                }
            }
            finally
            {
                if (projectId != 0)
                    _backupCancelRequested.TryRemove(projectId, out _);

                if (inFlightAdded)
                {
                    _manualBackupInFlight.TryRemove(projectId, out _);
                    int remaining = Interlocked.Decrement(ref _manualBackupInFlightCount);
                    if (remaining <= 0)
                    {
                        BackupsViewModel.ClearActiveBackups();
                        BackupsViewModel.IsBusy = false;
                        BackupsViewModel.BusyMessage = string.Empty;
                        TrayMenuRefreshRequested?.Invoke();
                    }
                    else
                    {
                        BackupsViewModel.BusyMessage = AppViewModel.L("Backups.Busy.All", "Backing up all projects...");
                    }
                }
            }
        }

        private void OnCreateBackupForAllProjectsRequested()
        {
            AppViewModel.RunDetached(OnCreateBackupForAllProjectsRequestedAsync, nameof(OnCreateBackupForAllProjectsRequestedAsync));
        }

        private async Task OnCreateBackupForAllProjectsRequestedAsync()
        {
            bool trayRun = _trayInitiatedBackup;
            _trayInitiatedBackup = false;
            DateTime start = DateTime.UtcNow;

            // Do not start "backup all" if a backup is already running.
            if (BackupsViewModel.IsBusy)
            {
                Telemetry.Log(TelemetryBackupAllSkipped, b => b.WithCode(TelemetryReason, "busy"));
                return;
            }

            if (ShouldPauseBackupsForBattery(out string? pauseReason))
            {
                BackupsViewModel.BackupCurrentFile = pauseReason;
                BackupsViewModel.BusyMessage = pauseReason;
                Telemetry.Log(TelemetryBackupAllSkipped, b => b.WithCode(TelemetryReason, "battery"));
                return;
            }


            BackupAllPreparationResult preparation = await Task.Run(() => PrepareBackupAll());

            if (!preparation.IsReady)
            {
                Telemetry.Log(TelemetryBackupAllSkipped, b => b.WithCode(TelemetryReason, preparation.FailureCode ?? "preflight_failed"));
                return;
            }

            if (Interlocked.CompareExchange(ref _backupAllInProgress, 1, 0) == 1)
                return;

            AppConfig cfg = preparation.Config!;
            LogBackupPolicyTransitionIfChanged(cfg, "backup-all-start");
            string activePolicyText = GetBackupPolicyChipTextForConfig(cfg);
            int maxSnapshotsToKeep = cfg.Backups.MaxSnapshotsPerProject;
            bool useArchiveMode = _settingsViewModel.UseBackupCompression;
            Telemetry.Log("backup_all_start", b => b
                .WithCount("destinationsConfigured", AppViewModel.GetAllDestinations(cfg).Count)
                .WithFlag(TelemetryUseArchiveMode, useArchiveMode));

            BackupsViewModel.BackupProgress = 0;
            BackupsViewModel.BackupCurrentFile = AppViewModel.L(BackupsStatusPreparingKey, BackupsStatusPreparingFallback);
            BackupsViewModel.BackupEtaText = string.Empty;
            BackupsViewModel.IsBusy = true;
            BackupsViewModel.BusyMessage = AppViewModel.L("Backups.Busy.All", "Backing up all projects...");
            if (trayRun && ShouldShowBackupWidget)
            {
                _backupWidgetService?.ShowForTrayBackup();
            }

            try
            {
                await Task.Run(async () =>
                {
                    var projects = _repo.GetAllProjects().ToList();
                    var results = new ConcurrentBag<(string name, string root, bool success)>();

                    if (projects.Count == 0)
                    {
                        Telemetry.Log(TelemetryBackupAllSkipped, b => b.WithCode(TelemetryReason, "no_projects"));
                        return;
                    }

                    var progressPerProject = new ConcurrentDictionary<int, double>();
                    DateTime lastAggregateUiUpdateUtc = DateTime.MinValue;

                    // Reset per-project cards and add entry place-holders
                    BackupsViewModel.ClearActiveBackups();
                    foreach (Project? p in projects)
                    {
                        BackupsViewModel.UpdateActiveBackup(
                            p.Id.ToString(),
                            p.Name,
                            0,
                            AppViewModel.L(BackupsStatusPreparingKey, BackupsStatusPreparingFallback),
                            string.Empty,
                            policyText: activePolicyText);
                    }

                    void UpdateAggregateProgress(string currentFile, string etaText)
                        => UpdateAggregateBackupAllUi(progressPerProject, ref lastAggregateUiUpdateUtc, currentFile, etaText);

                    var tasks = projects.ConvertAll(project => Task.Run(async () =>
                    {
                        int projectId = project.Id;
                        ProjectDestinationSelection selection = ResolveDestinationsForProject(project, cfg);
                        if (!string.IsNullOrWhiteSpace(selection.WarningMessage))
                        {
                            BackupsViewModel.ShowNotification(selection.WarningMessage, "Warning");
                            Telemetry.Log("backup_all_destination_fallback", b => b
                                .WithCode(TelemetryReason, selection.WarningCode ?? "preferred_destination_fallback")
                                .WithHashedString(TelemetryProject, project.Name));
                        }

                        if (selection.Destinations.Count == 0)
                        {
                            string message = AppViewModel.L("Backups.Notification.NoDestination", "Backup could not start: no active destination configured.");
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_skipped", b => b
                                .WithHashedString(TelemetryProject, project.Name)
                                .WithHashedString(TelemetryProjectRoot, project.RootPath)
                                .WithCode(TelemetryReason, "no_destination"));
                            progressPerProject[project.Id] = 100;
                            Dispatcher.UIThread.Post(() =>
                            {
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    message,
                                    string.Empty,
                                    policyText: activePolicyText);
                            });
                            UpdateAggregateProgress(message, string.Empty);
                            return;
                        }

                        BackupDestination primaryDest = selection.Destinations[0];
                        DestinationResolution preparedPrimary = PrepareDestination(primaryDest, cfg);
                        if (!preparedPrimary.IsSuccess || string.IsNullOrWhiteSpace(preparedPrimary.EffectivePath))
                        {
                            string message = preparedPrimary.Message;
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_skipped", b => b
                                .WithHashedString(TelemetryProject, project.Name)
                                .WithHashedString(TelemetryProjectRoot, project.RootPath)
                                .WithCode(TelemetryReason, "destination_unreachable"));
                            progressPerProject[project.Id] = 100;
                            Dispatcher.UIThread.Post(() =>
                            {
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    message,
                                    string.Empty,
                                    policyText: activePolicyText);
                            });
                            UpdateAggregateProgress(message, string.Empty);
                            return;
                        }

                        string backupRoot = preparedPrimary.EffectivePath;
                        string primaryAlias = string.IsNullOrWhiteSpace(primaryDest.Alias)
                            ? primaryDest.Path
                            : primaryDest.Alias ?? primaryDest.Path;
                        string effectiveBackupRoot = backupRoot;
                        if (!TryResolveProjectRoot(project, cfg, out Project? resolvedProject, out string? rootError))
                        {
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_skipped", b => b
                                .WithHashedString(TelemetryProject, project.Name)
                                .WithHashedString(TelemetryProjectRoot, project.RootPath)
                                .WithCode(TelemetryReason, "project_root_missing"));
                            progressPerProject[project.Id] = 100;
                            Dispatcher.UIThread.Post(() =>
                            {
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    rootError,
                                    string.Empty,
                                    policyText: activePolicyText);
                            });
                            UpdateAggregateProgress(rootError, string.Empty);
                            return;
                        }

                        project = resolvedProject;

                        DriveHealthDecision driveDecision = await EvaluateDriveHealthAsync(project.RootPath, effectiveBackupRoot);
                        if (!string.IsNullOrWhiteSpace(driveDecision.Message))
                        {
                            ShowDriveHealthNotification(driveDecision.Message, driveDecision.Severity);
                        }
                        if (driveDecision.Block)
                        {
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_skipped", b => b
                                .WithHashedString(TelemetryProject, project.Name)
                                .WithHashedString(TelemetryProjectRoot, project.RootPath)
                                .WithCode(TelemetryReason, "drive_health"));
                            progressPerProject[project.Id] = 100;
                            Dispatcher.UIThread.Post(() =>
                            {
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    driveDecision.Message,
                                    string.Empty,
                                    policyText: activePolicyText);
                            });
                            UpdateAggregateProgress(driveDecision.Message, string.Empty);
                            return;
                        }

                        try
                        {
                            _ = TryShowPreflightEstimateAsync(project, effectiveBackupRoot, primaryAlias, useArchiveMode, cfg);

                            int? archiveUploadBufferBytes = await EnsureArchiveUploadBufferAsync(
                                primaryDest,
                                cfg,
                                effectiveBackupRoot,
                                useArchiveMode,
                                CancellationToken.None);
                            bool isRemoteDestination = IsRemoteDestinationPath(effectiveBackupRoot)
                                || IsRemoteDestinationPath(primaryDest.Path);
                            bool allowParallelUpload = cfg.Backups.EnableParallelArchiveUpload;
                            bool preferParallelUpload = allowParallelUpload && isRemoteDestination;
                            if (!allowParallelUpload)
                            {
                                RuntimeLog.WriteVerbose($"[BackupService] Parallel archive upload disabled by user settings for '{primaryAlias}'.");
                            }
                            var sw = Stopwatch.StartNew();
                            BackupService.BackupRunResult backupResult = await _backupService.RunBackupAsync(
                                project,
                                effectiveBackupRoot,
                                isAuto: false,
                                progressCallback: (percent, currentFile, etaText) =>
                                {
                                    if (_backupCancelRequested.ContainsKey(project.Id))
                                        return;

                                    if (!ShouldUpdateBackupUi(project.Id, percent, etaText))
                                        return;

                                    // Per-project label for its own card
                                    bool isFinalizing = !string.IsNullOrWhiteSpace(etaText) &&
                                                       etaText.Contains("Finalizing", StringComparison.OrdinalIgnoreCase);

                                    string label;
                                    if (isFinalizing)
                                    {
                                        label = AppViewModel.L("Backups.Status.Finalizing", "Finalizing...");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Uploading", StringComparison.OrdinalIgnoreCase))
                                    {
                                        label = AppViewModel.L("Backups.Stage.Uploading", "Uploading archive");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Compressing", StringComparison.OrdinalIgnoreCase))
                                    {
                                        label = AppViewModel.L("Backups.Stage.Compressing", "Compressing archive");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(currentFile))
                                    {
                                        label = currentFile;
                                    }
                                    else if (percent <= 0.1)
                                    {
                                        label = AppViewModel.L(BackupsStatusPreparingKey, BackupsStatusPreparingFallback);
                                    }
                                    else if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Copying", StringComparison.OrdinalIgnoreCase))
                                    {
                                        label = AppViewModel.L("Backups.Stage.Copying", "Copying files");
                                    }
                                    else if (percent < 100)
                                    {
                                        label = AppViewModel.L("Backups.Status.Running", "Running backup...");
                                    }
                                    else
                                    {
                                        label = AppViewModel.L("Backups.Status.Finalizing", "Finalizing...");
                                    }

                                    progressPerProject[project.Id] = percent;
                                    UpdateAggregateProgress(currentFile, etaText);

                                    // Update that project's card
                                    BackupsViewModel.UpdateActiveBackup(
                                        project.Id.ToString(),
                                        project.Name,
                                        percent,
                                        label,
                                        etaText,
                                        allowCancel: !isFinalizing,
                                        policyText: activePolicyText);
                                    LogBackupProgress(project.Id, project.Name, percent, label, etaText);
                                },
                                useArchiveMode: useArchiveMode,
                                maxSnapshotsToKeep: maxSnapshotsToKeep,
                                minimumFreeSpacePercent: _settingsViewModel.MinimumFreeSpacePercent,
                                preferredFinalBackupRoot: null,
                                destinationPath: effectiveBackupRoot,
                                destinationAlias: primaryAlias,
                                skipIfNoChanges: true,
                                useRsyncDelta: _settingsViewModel.UseRsyncDelta,
                                useIncrementalBackups: _settingsViewModel.UseIncrementalBackups,
                                archiveUploadBufferBytes: archiveUploadBufferBytes,
                                preferRunnerProgressOnly: isRemoteDestination,
                                preferParallelArchiveUpload: preferParallelUpload,
                                useScanCache: _settingsViewModel.EnableScanCache,
                                aggressiveScanCache: _settingsViewModel.AggressiveScanCache,
                                enableCheckpointedRetry: primaryDest.EnableCheckpointResume
                            );
                            sw.Stop();

                            if (backupResult.SkippedForNoChanges)
                            {
                                progressPerProject[project.Id] = 100;
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    AppViewModel.L("Backups.Status.NoChanges", "No changes detected"),
                                    string.Empty,
                                    policyText: activePolicyText);
                                UpdateAggregateProgress(string.Empty, string.Empty);
                                results.Add((project.Name, project.RootPath, true));
                                Telemetry.Log("backup_all_project_skipped", b => b
                                    .WithHashedString(TelemetryProject, project.Name)
                                    .WithHashedString(TelemetryProjectRoot, project.RootPath)
                                    .WithCode(TelemetryReason, "no_changes"));
                                return;
                            }

                            if (backupResult.Cancelled)
                            {
                                results.Add((project.Name, project.RootPath, false));
                                Telemetry.Log("backup_all_project_cancelled", b => b
                                    .WithHashedString(TelemetryProject, project.Name)
                                    .WithHashedString(TelemetryProjectRoot, project.RootPath));
                                progressPerProject[project.Id] = 0;
                                UpdateAggregateProgress(string.Empty, string.Empty);
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    AppViewModel.L("Backups.Status.Cancelled", "Cancelled"),
                                    string.Empty,
                                    allowCancel: false,
                                    policyText: activePolicyText);
                                return;
                            }

                            progressPerProject[project.Id] = 100;
                            UpdateAggregateProgress(string.Empty, string.Empty);
                            BackupsViewModel.UpdateActiveBackup(
                                project.Id.ToString(),
                                project.Name,
                                100,
                                AppViewModel.L("Backups.Status.Completed", "Completed"),
                                string.Empty,
                                policyText: activePolicyText);
                            results.Add((project.Name, project.RootPath, backupResult.BackupId > 0));
                            if (backupResult.BackupId > 0)
                            {
                                RecordBackupThroughput(backupResult.BackupId, sw.Elapsed, useArchiveMode);
                                TryExportMetadataForBackup(cfg, primaryDest, effectiveBackupRoot, backupResult.BackupId);
                            }
                            Telemetry.Log("backup_all_project_success", b => b
                                .WithHashedString(TelemetryProject, project.Name)
                                .WithHashedString(TelemetryProjectRoot, project.RootPath)
                                .WithFlag(TelemetryUseArchiveMode, useArchiveMode));
                        }
                        catch (OperationCanceledException)
                        {
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_cancelled", b => b
                                .WithHashedString(TelemetryProject, project.Name)
                                .WithHashedString(TelemetryProjectRoot, project.RootPath));
                            progressPerProject[project.Id] = 0;
                            UpdateAggregateProgress(string.Empty, string.Empty);
                            BackupsViewModel.UpdateActiveBackup(
                                project.Id.ToString(),
                                project.Name,
                                0,
                                AppViewModel.L("Backups.Status.Cancelled", "Cancelled"),
                                string.Empty,
                                allowCancel: false,
                                policyText: activePolicyText);
                            return;
                        }
                        catch (Exception ex)
                        {
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_failure", b => b
                                .WithHashedString(TelemetryProject, project.Name)
                                .WithHashedString(TelemetryProjectRoot, project.RootPath)
                                .WithFlag(TelemetryUseArchiveMode, useArchiveMode)
                                .WithException(ex));
                            throw;
                        }
                        finally
                        {
                            _backupCancelRequested.TryRemove(projectId, out _);
                        }
                    }));

                    await Task.WhenAll(tasks);

                    Telemetry.Log("backup_all_success", b => b
                        .WithCount("projects", projects.Count)
                        .WithCount("succeeded", results.Count(r => r.success))
                        .WithCount("failed", results.Count(r => !r.success))
                        .WithFlag(TelemetryUseArchiveMode, useArchiveMode)
                        .WithNumber(TelemetryDurationSeconds, (DateTime.UtcNow - start).TotalSeconds));
                });

                // First reload history so the new backups appear.
                ReloadBackupsVmData();
                await DashboardViewModel.RefreshAsync();

                // --- After all backups: optional verification / post-hash ---
                AppConfig cfgAfterAll = await Task.Run(_configStore.Load);
                List<BackupDestination> allDestinations = AppViewModel.GetAllDestinations(cfgAfterAll);
                List<Backup> allLatest = _repo.GetLatestBackupsPerProject();
                var projectsById = _repo.GetAllProjects()
                    .GroupBy(p => p.Id)
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (Backup latest in allLatest)
                {
                    if (!projectsById.TryGetValue(latest.ProjectId, out Project? proj))
                        continue;
                    if (proj == null)
                        continue;

                    if (AppViewModel.ShouldRunVerification(proj, isAutoRun: false, cfgAfterAll.Backups.VerifyAfterCreate))
                    {
                        string? destinationRoot = ResolveDestinationRootForBackup(
                            latest,
                            allDestinations,
                            cfgAfterAll.Backups.BackupRoot);
                        StartVerificationAsync(proj, latest, destinationRoot ?? string.Empty, "backup_all_verify_failed");
                    }
                    else
                    {
                        StartPostBackupHashingAsync(proj, latest.SnapshotId);
                    }
                }

                // Then clear the active backup cards on the UI thread,
                // so the overlay collapses only after history is updated.
                Dispatcher.UIThread.Post(() =>
                {
                    BackupsViewModel.ClearActiveBackups();

                    if (NotificationsEnabled && _settingsViewModel.NotifyOnBackupSuccess)
                    {
                        string msg = AppViewModel.L("Backups.Notification.AllSuccess", "All project backups completed successfully.");
                        string title = AppViewModel.L("Backups.Notification.AllSuccessTitle", "Backups completed");

                        _notificationService.ShowInfo(
                            title,
                            msg,
                            NotificationKind.Backup);

                        BackupsViewModel.ShowNotification(
                            msg,
                            "Info");

                        if (!IsOnBackupsPage)
                        {
                            GlobalNotificationCenter.Instance.Show(
                                msg,
                                NotificationSeverity.Info,
                                title);
                        }

                        if (ShouldRaiseSystemNotification)
                        {
                            GlobalNotificationCenter.Instance.ShowSystem(
                                msg,
                                NotificationSeverity.Info,
                                title);
                        }
                    }
                });
            }
            catch (Exception ex)
            {

                Telemetry.Log("backup_all_failure", b => b
                    .WithException(ex)
                    .WithFlag(TelemetryUseArchiveMode, useArchiveMode)
                    .WithNumber(TelemetryDurationSeconds, (DateTime.UtcNow - start).TotalSeconds));

                if (NotificationsEnabled)
                {
                    string msg = AppViewModel.L("Backups.Notification.AllFailureMessage", "Backup all projects failed. Check logs for details.");
                    string title = AppViewModel.L("Backups.Notification.AllFailureTitle", "Backup-all failed");
                    string actionLabel = AppViewModel.L("Logs.CopySnippet", "Copy log snippet");
                    System.Windows.Input.ICommand actionCommand = CreateCopyLogSnippetCommand(
                        AppViewModel.L("Logs.Snippet.BackupAllFailure", "Backup-all failure."));

                    if (IsOnBackupsPage)
                    {
                        BackupsViewModel.ShowNotification(msg, "Error", actionLabel, actionCommand);
                    }
                    else
                    {
                        GlobalNotificationCenter.Instance.Show(
                            msg,
                            NotificationSeverity.Error,
                            title,
                            actionLabel: actionLabel,
                            actionCommand: actionCommand);
                    }

                    if (ShouldRaiseSystemNotification)
                    {
                        GlobalNotificationCenter.Instance.ShowSystem(
                            msg,
                            NotificationSeverity.Error,
                            title);
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    BackupsViewModel.BackupCurrentFile = AppViewModel.L("Backups.Notification.AllFailureTitle", "Backup-all failed");
                    BackupsViewModel.BackupEtaText =
                        string.IsNullOrWhiteSpace(BackupsViewModel.BackupEtaText)
                            ? ex.Message
                                : BackupsViewModel.BackupEtaText + " - " + AppViewModel.L("Backups.Status.FailedSuffix", "Failed");
                });

                // Clear cards on failure (ensure this runs on the UI thread)
                Dispatcher.UIThread.Post(() => BackupsViewModel.ClearActiveBackups());
            }
            finally
            {
                BackupsViewModel.IsBusy = false;
                BackupsViewModel.BusyMessage = string.Empty;

                TrayMenuRefreshRequested?.Invoke();

                Interlocked.Exchange(ref _backupAllInProgress, 0);
            }
        }

        private async Task TryShowPreflightEstimateAsync(Project project, string backupRoot, string? labelPrefix, bool useArchiveMode, AppConfig cfg)
        {
            try
            {
                string activePolicyText = GetBackupPolicyChipTextForConfig(cfg);
                double throughput = useArchiveMode
                    ? cfg.Backups.LastBackupThroughputArchiveMbSec
                    : cfg.Backups.LastBackupThroughputCopyMbSec;
                if (throughput <= 0)
                {
                    throughput = cfg.Backups.LastBackupThroughputMbSec;
                }
                BackupService.BackupPreflightResult preflight = await Task.Run(
                        () => _backupService.PreflightBackupAsync(
                            project,
                            backupRoot,
                            throughputMbSec: throughput,
                            useArchiveMode: useArchiveMode,
                            cacheTtl: TimeSpan.FromSeconds(45),
                            ct: CancellationToken.None))
                    .ConfigureAwait(false);

                string sizeLabel = BackupSnapshotItem.FormatSize(preflight.TotalBytes);
                string estimateLabel = string.Empty;
                string etaText = FormatEta(preflight.EstimatedSeconds);
                if (!string.IsNullOrWhiteSpace(etaText))
                {
                    estimateLabel = Lf(
                        "Backups.Preflight.Message",
                        "Estimated {0} files, {1} total, ETA {2}.",
                        preflight.TotalFiles,
                        sizeLabel,
                        etaText);
                }
                else
                {
                    estimateLabel = Lf(
                        "Backups.Preflight.MessageNoEta",
                        "Estimated {0} files, {1} total.",
                        preflight.TotalFiles,
                        sizeLabel);
                }

                if (!string.IsNullOrWhiteSpace(labelPrefix))
                {
                    estimateLabel = $"[{labelPrefix}] {estimateLabel}";
                }

                string projectId = project.Id.ToString();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    BackupProgressItem? active = BackupsViewModel.ActiveBackups.FirstOrDefault(p => p.ProjectId == projectId);
                    if (active is null || active.Progress <= 0.1d)
                    {
                        BackupsViewModel.UpdateActiveBackup(
                            projectId,
                            project.Name,
                            0,
                            AppViewModel.L("Backups.Progress.Estimating", "Estimating..."),
                            estimateLabel,
                            allowCancel: true,
                            policyText: activePolicyText);
                    }

                    if (!preflight.HasEnoughSpace && preflight.VolumeFreeBytes.HasValue)
                    {
                        string freeLabel = BackupSnapshotItem.FormatSize(preflight.VolumeFreeBytes.Value);
                        string warning = Lf(
                            "Backups.Preflight.LowDisk",
                            "Backup may not fit on the destination. Free space: {0}.",
                            freeLabel);

                        BackupsViewModel.ShowNotification(warning, "Warning");
                        if (!IsOnBackupsPage)
                        {
                            GlobalNotificationCenter.Instance.Show(
                                warning,
                                NotificationSeverity.Warning,
                                AppViewModel.L("Backups.Preflight.Title", "Backup estimate"));
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Backup] Preflight estimate failed: {ex.Message}");
            }
        }

        private static string FormatEta(double? seconds)
        {
            if (!seconds.HasValue || seconds.Value <= 0)
                return string.Empty;

            var eta = TimeSpan.FromSeconds(seconds.Value);
            if (eta.TotalHours >= 1)
                return $"{(int)eta.TotalHours}h {eta.Minutes}m";

            return eta.ToString(@"mm\:ss");
        }

    }
}
