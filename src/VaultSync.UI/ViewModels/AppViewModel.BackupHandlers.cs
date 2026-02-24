using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        private void OnBackupProjectRequested(ProjectBackupItem? item)
        {
            RunDetached(() => OnBackupProjectRequestedAsync(item), nameof(OnBackupProjectRequestedAsync));
        }

        private async Task OnBackupProjectRequestedAsync(ProjectBackupItem? item)
        {
            var trayRun = _trayInitiatedBackup;
            _trayInitiatedBackup = false;
            var start = DateTime.UtcNow;
            var inFlightAdded = false;
            var projectId = 0;

            if (ShouldPauseBackupsForBattery(out var pauseReason))
            {
                BackupsViewModel.BackupCurrentFile = pauseReason;
                BackupsViewModel.BusyMessage = pauseReason;
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.BatteryPaused"],
                    NotificationSeverity.Warning);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "battery"));
                return;
            }

            if (item is null)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.NoProject"],
                    NotificationSeverity.Warning);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "no_project"));
                return;
            }

            if (!int.TryParse(item.Id, out projectId))
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.InvalidProjectId"],
                    NotificationSeverity.Error);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "invalid_project_id"));
                return;
            }

            if (Volatile.Read(ref _backupAllInProgress) == 1)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.AlreadyRunning"],
                    NotificationSeverity.Info);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "backup_all_running")
                    .WithHashedString("project", item.Name));
                return;
            }

            if (BackupsViewModel.IsBusy && Volatile.Read(ref _manualBackupInFlightCount) == 0)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.AlreadyRunning"],
                    NotificationSeverity.Info);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "busy"));
                return;
            }

            var preparation = await Task.Run(() => CreateManualBackupPreparation(projectId));

            if (preparation.Destinations.Count == 0)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.NoDestination"],
                    NotificationSeverity.Warning);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "no_destination"));
                return; // later: show error in UI
            }

            if (preparation.Project is null)
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.ProjectNotFound"],
                    NotificationSeverity.Error);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "project_not_found"));
                return;
            }

            var cfg = preparation.Config;
            var destinations = preparation.Destinations;
            var project = preparation.Project;
            LogBackupPolicyTransitionIfChanged(cfg, "manual-backup-start");
            var activePolicyText = GetBackupPolicyChipTextForConfig(cfg);
            if (!string.IsNullOrWhiteSpace(preparation.DestinationWarning))
            {
                BackupsViewModel.ShowNotification(preparation.DestinationWarning, "Warning");
                Telemetry.Log("backup_single_destination_fallback", b => b
                    .WithCode("reason", preparation.DestinationWarningCode ?? "preferred_destination_fallback")
                    .WithHashedString("project", project.Name));
            }
            if (!TryResolveProjectRoot(project, cfg, out var resolvedProject, out var rootError))
            {
                MaybeNotifyProjectRootMissing(project, rootError);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "project_root_missing")
                    .WithHashedString("project", project.Name)
                    .WithHashedString("projectRoot", project.RootPath));
                return;
            }
            project = resolvedProject;
            if (cfg.Backups.PromptRestoreAfterImport && project.NeedsRestore)
            {
                MaybeNotifyRestoreRecommended(project);
                Telemetry.Log("backup_single_advisory", b => b
                    .WithCode("reason", "restore_recommended")
                    .WithHashedString("project", project.Name));
            }

            if (!_manualBackupInFlight.TryAdd(projectId, 0))
            {
                ShowBackupSkipNotification(
                    _localizationService["Backups.Notification.AlreadyRunning"],
                    NotificationSeverity.Info);
                Telemetry.Log("backup_single_skipped", b => b
                    .WithCode("reason", "duplicate")
                    .WithHashedString("project", item.Name));
                return;
            }
            inFlightAdded = true;
            var manualCount = Interlocked.Increment(ref _manualBackupInFlightCount);
            var isFirstManual = manualCount == 1;
            var maxSnapshotsToKeep = cfg.Backups.MaxSnapshotsPerProject;
            var useArchiveMode = _settingsViewModel.UseBackupCompression;
            Telemetry.Log("backup_single_start", b => b
                .WithHashedString("project", project.Name)
                .WithHashedString("projectRoot", project.RootPath)
                .WithCount("destinations", destinations.Count)
                .WithFlag("useArchiveMode", useArchiveMode));

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
                L("Backups.Status.Preparing", "Preparing backup..."),
                string.Empty,
                policyText: activePolicyText);
            if (isFirstManual)
            {
                var allowToggle = cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 };
                var overviewDestinations = GetAllDestinations(cfg);
                BackupsViewModel.ResetDestinationStatuses(overviewDestinations, allowToggle);
                RefreshDestinationStatusOverview();
            }

            BackupsViewModel.IsBusy = true;
            BackupsViewModel.BusyMessage = isFirstManual
                ? Lf("Backups.Busy.Single", "Backing up {0}...", project.Name)
                : L("Backups.Busy.All", "Backing up all projects...");
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
                var attempts = 0;
                var succeeded = 0;
                var failed = 0;
                var unreachable = 0;
                var driveBlocked = 0;

                foreach (var dest in destinations)
                {
                    var destId = DestinationStatusItem.GetId(dest);
                    var resolution = await PrepareDestinationAsync(dest, cfg);
                    if (!resolution.IsSuccess)
                    {
                        BackupsViewModel.UpdateDestinationStatus(destId, resolution.Message, "Error");
                    }

                    if (!resolution.IsSuccess)
                    {
                        unreachable++;
                        Telemetry.Log("backup_single_destination_unreachable", b => b
                            .WithHashedString("project", project.Name)
                            .WithHashedString("destinationPath", dest.Path)
                            .WithHashedString("destinationAlias", dest.Alias ?? string.Empty));
                        continue;
                    }

                    var driveDecision = await EvaluateDriveHealthAsync(project.RootPath, resolution.EffectivePath);
                    if (!string.IsNullOrWhiteSpace(driveDecision.Message))
                    {
                        ShowDriveHealthNotification(driveDecision.Message, driveDecision.Severity);
                    }
                    if (driveDecision.Block)
                    {
                        driveBlocked++;
                        BackupsViewModel.UpdateDestinationStatus(destId, driveDecision.Message, "Warning");
                        _networkMountService.Cleanup(resolution);
                        continue;
                    }

                    var labelPrefix = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path;
                    var archiveUploadBufferBytes = await EnsureArchiveUploadBufferAsync(
                        dest,
                        cfg,
                        resolution.EffectivePath,
                        useArchiveMode,
                        CancellationToken.None);

                    try
                    {
                        _ = TryShowPreflightEstimateAsync(project, resolution.EffectivePath, labelPrefix, useArchiveMode, cfg);

                        var backupResult = await Task.Run(async () =>
                        {
                            attempts++;
                            var isRemoteDestination = IsRemoteDestinationPath(resolution.EffectivePath)
                                || IsRemoteDestinationPath(dest.Path);
                            var allowParallelUpload = cfg.Backups.EnableParallelArchiveUpload;
                            var preferParallelUpload = allowParallelUpload && isRemoteDestination;
                            if (!allowParallelUpload)
                            {
                                Console.WriteLine($"[BackupService] Parallel archive upload disabled by user settings for '{labelPrefix}'.");
                            }
                            var sw = Stopwatch.StartNew();
                            var result = await _backupService.RunBackupAsync(
                                project,
                                resolution.EffectivePath,
                                isAuto: false,
                                progressCallback: (percent, currentFile, etaText) =>
                                {
                                    if (_backupCancelRequested.ContainsKey(project.Id))
                                        return;

                                    if (!ShouldUpdateBackupUi(project.Id, percent, etaText))
                                        return;

                                    var isFinalizing = !string.IsNullOrWhiteSpace(etaText) &&
                                                       etaText.Contains("Finalizing", StringComparison.OrdinalIgnoreCase);
                                    var label = BuildBackupProgressLabel(etaText, currentFile, percent);
                                    if (!string.IsNullOrWhiteSpace(labelPrefix))
                                        label = $"[{labelPrefix}] {label}";

                                    // Update per-project card (used by BackupsView overlay)
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

                                    // Keep legacy aggregate fields in sync (if anything else binds to them)
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
                                reuseSnapshotId: metadataWritten ? sharedSnapshotId : null,
                                preferredFinalBackupRoot: null,
                                writeMetadata: !metadataWritten,
                                destinationPath: resolution.EffectivePath,
                                destinationAlias: labelPrefix,
                                useRsyncDelta: _settingsViewModel?.UseRsyncDelta ?? false,
                                useIncrementalBackups: _settingsViewModel?.UseIncrementalBackups ?? false,
                                archiveUploadBufferBytes: archiveUploadBufferBytes,
                                preferRunnerProgressOnly: isRemoteDestination,
                                preferParallelArchiveUpload: preferParallelUpload,
                                useScanCache: _settingsViewModel.EnableScanCache,
                                aggressiveScanCache: _settingsViewModel.AggressiveScanCache
                            );
                            sw.Stop();

                            if (!metadataWritten && result.BackupId > 0)
                            {
                                metadataWritten = true;
                                metadataRoot = resolution.EffectivePath;
                                metadataBackupId = result.BackupId;

                                if (!sharedSnapshotId.HasValue && result.BackupId > 0)
                                {
                                    var created = _repo.GetBackupById(result.BackupId);
                                    sharedSnapshotId = created?.SnapshotId ?? sharedSnapshotId;
                                }
                            }

                            return (Result: result, Elapsed: sw.Elapsed);
                        });

                        if (backupResult.Result.SkippedForNoChanges)
                        {
                            Telemetry.Log("backup_single_skipped", b => b
                                .WithCode("reason", "no_changes")
                                .WithHashedString("project", project.Name)
                                .WithHashedString("destinationPath", dest.Path ?? string.Empty));
                            break;
                        }

                        if (backupResult.Result.Cancelled)
                        {
                            cancelled = true;
                            BackupsViewModel.UpdateActiveBackup(
                                project.Id.ToString(),
                                project.Name,
                                100,
                                L("Backups.Status.Cancelled", "Cancelled"),
                                string.Empty,
                                allowCancel: false,
                                policyText: activePolicyText);
                            Telemetry.Log("backup_single_cancelled", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("destinationPath", dest.Path ?? string.Empty)
                                .WithFlag("useArchiveMode", useArchiveMode));
                            break;
                        }

                        if (backupResult.Result.BackupId > 0)
                        {
                            BackupsViewModel.UpdateActiveBackup(
                                project.Id.ToString(),
                                project.Name,
                                100,
                                L("Backups.Status.Completed", "Completed"),
                                string.Empty,
                                policyText: activePolicyText);
                            succeeded++;
                            RecordBackupThroughput(backupResult.Result.BackupId, backupResult.Elapsed, useArchiveMode);
                            TryExportMetadataForBackup(cfg, dest, resolution.EffectivePath, backupResult.Result.BackupId);
                        }
                        else
                        {
                            failed++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Telemetry.Log("backup_single_cancelled", b => b
                            .WithHashedString("project", project.Name)
                            .WithHashedString("destinationPath", dest.Path)
                            .WithFlag("useArchiveMode", useArchiveMode));
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Telemetry.Log("backup_single_failure", b => b
                            .WithHashedString("project", project.Name)
                            .WithHashedString("projectRoot", project.RootPath)
                            .WithHashedString("destinationPath", dest.Path)
                            .WithHashedString("destinationAlias", dest.Alias ?? string.Empty)
                            .WithFlag("useArchiveMode", useArchiveMode)
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
                var cfgAfter = await Task.Run(AppConfigStore.Load);
                if (metadataRoot is not null)
                {
                    var latest = metadataBackupId.HasValue
                        ? _repo.GetBackupById(metadataBackupId.Value)
                        : _repo.GetLatestBackupForProject(project.Id);

                    if (latest != null)
                    {
                        if (cfgAfter.Backups.VerifyAfterCreate)
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
                    .WithHashedString("project", project.Name)
                    .WithHashedString("projectRoot", project.RootPath)
                    .WithCount("destinations", destinations.Count)
                    .WithCount("attempts", attempts)
                    .WithCount("succeeded", succeeded)
                    .WithCount("failed", failed)
                    .WithCount("destinationsUnreachable", unreachable)
                    .WithCount("driveBlocked", driveBlocked)
                    .WithFlag("useArchiveMode", useArchiveMode)
                    .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));

                // Notify success if enabled in settings and globally
                if (NotificationsEnabled && _settingsViewModel.NotifyOnBackupSuccess)
                {
                    var msg = Lf("Backups.Notification.Success", "Backup for '{0}' completed successfully.", project.Name);
                    var title = L("Backups.Notification.SuccessTitle", "Backup completed");

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
                BackupsViewModel.BackupCurrentFile = L("Backups.Notification.Cancelled", "Backup cancelled.");
                BackupsViewModel.BackupEtaText = string.Empty;
                Telemetry.Log("backup_single_cancelled", b => b
                    .WithHashedString("project", project?.Name ?? string.Empty)
                    .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));
            }
            catch (Exception ex)
            {

                // Detect the low-disk-space condition thrown by BackupService
                var isLowDisk =
                    ex is InvalidOperationException ioe &&
                    ioe.Message.Contains("does not have enough free space", StringComparison.OrdinalIgnoreCase);

                if (isLowDisk)
                {
                    // Low disk space: treat as a skipped backup with a clear warning,
                    // honoring the notifications settings.
                    Telemetry.Log("backup_single_low_disk", b => b
                        .WithHashedString("project", project.Name)
                        .WithHashedString("projectRoot", project.RootPath)
                        .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));

                    if (NotificationsEnabled && _settingsViewModel.NotifyOnLowDiskSpace)
                    {
                        var msg = Lf("Backups.Notification.LowDiskMessage", "Backup for '{0}' was skipped due to low disk space on the backup target.", project.Name);
                        var title = L("Backups.Notification.LowDiskTitle", "Low disk space");

                        // Always go through the central notification service so we get
                        // consistent logging and behavior.
                        _notificationService.ShowWarning(
                            title,
                            msg,
                            NotificationKind.Backup);

                        if (IsOnBackupsPage)
                        {
                            // When the user is on the Backups page, also show an in-page banner
                            // so the warning is clearly visible where the action happened.
                            BackupsViewModel.ShowNotification(
                                msg,
                                "Warning");
                        }
                        else
                        {
                            // When the user is elsewhere, show a global toast.
                            GlobalNotificationCenter.Instance.Show(
                                msg,
                                NotificationSeverity.Warning,
                                title);
                        }

                        if (ShouldRaiseSystemNotification)
                        {
                            GlobalNotificationCenter.Instance.ShowSystem(
                                msg,
                                NotificationSeverity.Warning,
                                title);
                        }
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        BackupsViewModel.BackupCurrentFile = L("Backups.Status.LowDisk", "Backup skipped: low disk space.");
                        BackupsViewModel.BackupEtaText =
                            string.IsNullOrWhiteSpace(BackupsViewModel.BackupEtaText)
                                ? ex.Message
                                : BackupsViewModel.BackupEtaText + " - " + L("Backups.Status.LowDiskSuffix", "Low disk space");
                    });
                }
                else
                {
                    // Generic backup failure path
                    Telemetry.Log("backup_single_failure", b => b
                        .WithHashedString("project", project.Name)
                        .WithHashedString("projectRoot", project.RootPath)
                        .WithFlag("useArchiveMode", useArchiveMode)
                        .WithException(ex)
                        .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));

                    if (NotificationsEnabled)
                    {
                        var msg = Lf("Backups.Notification.FailureMessage", "Backup failed for '{0}'. Check logs for details.", project.Name);
                        var title = L("Backups.Notification.FailureTitle", "Backup failed");
                        var actionLabel = L("Logs.CopySnippet", "Copy log snippet");
                        var actionCommand = CreateCopyLogSnippetCommand(
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
                        BackupsViewModel.BackupCurrentFile = L("Backups.Notification.FailureTitle", "Backup failed");
                        BackupsViewModel.BackupEtaText =
                            string.IsNullOrWhiteSpace(BackupsViewModel.BackupEtaText)
                                ? ex.Message
                                : BackupsViewModel.BackupEtaText + " - " + L("Backups.Status.FailedSuffix", "Failed");
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
                    var remaining = Interlocked.Decrement(ref _manualBackupInFlightCount);
                    if (remaining <= 0)
                    {
                        BackupsViewModel.ClearActiveBackups();
                        BackupsViewModel.IsBusy = false;
                        BackupsViewModel.BusyMessage = string.Empty;
                        TrayMenuRefreshRequested?.Invoke();
                    }
                    else
                    {
                        BackupsViewModel.BusyMessage = L("Backups.Busy.All", "Backing up all projects...");
                    }
                }
            }
        }

        private void OnCreateBackupForAllProjectsRequested()
        {
            RunDetached(OnCreateBackupForAllProjectsRequestedAsync, nameof(OnCreateBackupForAllProjectsRequestedAsync));
        }

        private async Task OnCreateBackupForAllProjectsRequestedAsync()
        {
            var trayRun = _trayInitiatedBackup;
            _trayInitiatedBackup = false;
            var start = DateTime.UtcNow;

            // Do not start "backup all" if a backup is already running.
            if (BackupsViewModel.IsBusy)
            {
                Telemetry.Log("backup_all_skipped", b => b.WithCode("reason", "busy"));
                return;
            }

            if (ShouldPauseBackupsForBattery(out var pauseReason))
            {
                BackupsViewModel.BackupCurrentFile = pauseReason;
                BackupsViewModel.BusyMessage = pauseReason;
                Telemetry.Log("backup_all_skipped", b => b.WithCode("reason", "battery"));
                return;
            }


            var preparation = await Task.Run(() => PrepareBackupAll());

            if (!preparation.IsReady)
            {
                Telemetry.Log("backup_all_skipped", b => b.WithCode("reason", preparation.FailureCode ?? "preflight_failed"));
                return;
            }

            if (Interlocked.CompareExchange(ref _backupAllInProgress, 1, 0) == 1)
                return;

            var cfg = preparation.Config!;
            LogBackupPolicyTransitionIfChanged(cfg, "backup-all-start");
            var activePolicyText = GetBackupPolicyChipTextForConfig(cfg);
            var maxSnapshotsToKeep = cfg.Backups.MaxSnapshotsPerProject;
            var useArchiveMode = _settingsViewModel.UseBackupCompression;
            Telemetry.Log("backup_all_start", b => b
                .WithCount("destinationsConfigured", GetAllDestinations(cfg).Count)
                .WithFlag("useArchiveMode", useArchiveMode));

            BackupsViewModel.BackupProgress = 0;
            BackupsViewModel.BackupCurrentFile = L("Backups.Status.Preparing", "Preparing backup...");
            BackupsViewModel.BackupEtaText = string.Empty;
            BackupsViewModel.IsBusy = true;
            BackupsViewModel.BusyMessage = L("Backups.Busy.All", "Backing up all projects...");
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
                        Telemetry.Log("backup_all_skipped", b => b.WithCode("reason", "no_projects"));
                        return;
                    }

                    var progressPerProject = new ConcurrentDictionary<int, double>();
                    var lastAggregateUiUpdateUtc = DateTime.MinValue;

                    // Reset per-project cards and add entry place-holders
                    BackupsViewModel.ClearActiveBackups();
                    foreach (var p in projects)
                    {
                        BackupsViewModel.UpdateActiveBackup(
                            p.Id.ToString(),
                            p.Name,
                            0,
                            L("Backups.Status.Preparing", "Preparing backup..."),
                            string.Empty,
                            policyText: activePolicyText);
                    }

                    void UpdateAggregateProgress(string currentFile, string etaText)
                        => UpdateAggregateBackupAllUi(progressPerProject, ref lastAggregateUiUpdateUtc, currentFile, etaText);

                    var tasks = projects.Select(project => Task.Run(async () =>
                    {
                        var projectId = project.Id;
                        var selection = ResolveDestinationsForProject(project, cfg);
                        if (!string.IsNullOrWhiteSpace(selection.WarningMessage))
                        {
                            BackupsViewModel.ShowNotification(selection.WarningMessage, "Warning");
                            Telemetry.Log("backup_all_destination_fallback", b => b
                                .WithCode("reason", selection.WarningCode ?? "preferred_destination_fallback")
                                .WithHashedString("project", project.Name));
                        }

                        if (selection.Destinations.Count == 0)
                        {
                            var message = L("Backups.Notification.NoDestination", "Backup could not start: no active destination configured.");
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_skipped", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath)
                                .WithCode("reason", "no_destination"));
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

                        var primaryDest = selection.Destinations[0];
                        var preparedPrimary = PrepareDestination(primaryDest, cfg);
                        if (!preparedPrimary.IsSuccess || string.IsNullOrWhiteSpace(preparedPrimary.EffectivePath))
                        {
                            var message = preparedPrimary.Message;
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_skipped", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath)
                                .WithCode("reason", "destination_unreachable"));
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

                        var backupRoot = preparedPrimary.EffectivePath;
                        var primaryAlias = string.IsNullOrWhiteSpace(primaryDest.Alias)
                            ? primaryDest.Path
                            : primaryDest.Alias ?? primaryDest.Path;
                        var effectiveBackupRoot = backupRoot;
                        if (!TryResolveProjectRoot(project, cfg, out var resolvedProject, out var rootError))
                        {
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_skipped", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath)
                                .WithCode("reason", "project_root_missing"));
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

                        var driveDecision = await EvaluateDriveHealthAsync(project.RootPath, effectiveBackupRoot);
                        if (!string.IsNullOrWhiteSpace(driveDecision.Message))
                        {
                            ShowDriveHealthNotification(driveDecision.Message, driveDecision.Severity);
                        }
                        if (driveDecision.Block)
                        {
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_skipped", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath)
                                .WithCode("reason", "drive_health"));
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

                            var archiveUploadBufferBytes = await EnsureArchiveUploadBufferAsync(
                                primaryDest,
                                cfg,
                                effectiveBackupRoot,
                                useArchiveMode,
                                CancellationToken.None);
                            var isRemoteDestination = IsRemoteDestinationPath(effectiveBackupRoot)
                                || IsRemoteDestinationPath(primaryDest.Path);
                            var allowParallelUpload = cfg.Backups.EnableParallelArchiveUpload;
                            var preferParallelUpload = allowParallelUpload && isRemoteDestination;
                            if (!allowParallelUpload)
                            {
                                Console.WriteLine($"[BackupService] Parallel archive upload disabled by user settings for '{primaryAlias}'.");
                            }
                            var sw = Stopwatch.StartNew();
                            var backupResult = await _backupService.RunBackupAsync(
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
                                    var isFinalizing = !string.IsNullOrWhiteSpace(etaText) &&
                                                       etaText.Contains("Finalizing", StringComparison.OrdinalIgnoreCase);

                                    string label;
                                    if (isFinalizing)
                                    {
                                        label = L("Backups.Status.Finalizing", "Finalizing...");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Uploading", StringComparison.OrdinalIgnoreCase))
                                    {
                                        label = L("Backups.Stage.Uploading", "Uploading archive");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Compressing", StringComparison.OrdinalIgnoreCase))
                                    {
                                        label = L("Backups.Stage.Compressing", "Compressing archive");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(currentFile))
                                    {
                                        label = currentFile;
                                    }
                                    else if (percent <= 0.1)
                                    {
                                        label = L("Backups.Status.Preparing", "Preparing backup...");
                                    }
                                    else if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Copying", StringComparison.OrdinalIgnoreCase))
                                    {
                                        label = L("Backups.Stage.Copying", "Copying files");
                                    }
                                    else if (percent < 100)
                                    {
                                        label = L("Backups.Status.Running", "Running backup...");
                                    }
                                    else
                                    {
                                        label = L("Backups.Status.Finalizing", "Finalizing...");
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
                                useRsyncDelta: _settingsViewModel?.UseRsyncDelta ?? false,
                                useIncrementalBackups: _settingsViewModel?.UseIncrementalBackups ?? false,
                                archiveUploadBufferBytes: archiveUploadBufferBytes,
                                preferRunnerProgressOnly: isRemoteDestination,
                                preferParallelArchiveUpload: preferParallelUpload,
                                useScanCache: _settingsViewModel.EnableScanCache,
                                aggressiveScanCache: _settingsViewModel.AggressiveScanCache
                            );
                            sw.Stop();

                            if (backupResult.SkippedForNoChanges)
                            {
                                progressPerProject[project.Id] = 100;
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    L("Backups.Status.NoChanges", "No changes detected"),
                                    string.Empty,
                                    policyText: activePolicyText);
                                UpdateAggregateProgress(string.Empty, string.Empty);
                                results.Add((project.Name, project.RootPath, true));
                                Telemetry.Log("backup_all_project_skipped", b => b
                                    .WithHashedString("project", project.Name)
                                    .WithHashedString("projectRoot", project.RootPath)
                                    .WithCode("reason", "no_changes"));
                                return;
                            }

                            if (backupResult.Cancelled)
                            {
                                results.Add((project.Name, project.RootPath, false));
                                Telemetry.Log("backup_all_project_cancelled", b => b
                                    .WithHashedString("project", project.Name)
                                    .WithHashedString("projectRoot", project.RootPath));
                                progressPerProject[project.Id] = 0;
                                UpdateAggregateProgress(string.Empty, string.Empty);
                                BackupsViewModel.UpdateActiveBackup(
                                    project.Id.ToString(),
                                    project.Name,
                                    100,
                                    L("Backups.Status.Cancelled", "Cancelled"),
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
                                L("Backups.Status.Completed", "Completed"),
                                string.Empty,
                                policyText: activePolicyText);
                            results.Add((project.Name, project.RootPath, backupResult.BackupId > 0));
                            if (backupResult.BackupId > 0)
                            {
                                RecordBackupThroughput(backupResult.BackupId, sw.Elapsed, useArchiveMode);
                                TryExportMetadataForBackup(cfg, primaryDest, effectiveBackupRoot, backupResult.BackupId);
                            }
                            Telemetry.Log("backup_all_project_success", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath)
                                .WithFlag("useArchiveMode", useArchiveMode));
                        }
                        catch (OperationCanceledException)
                        {
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_cancelled", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath));
                            progressPerProject[project.Id] = 0;
                            UpdateAggregateProgress(string.Empty, string.Empty);
                            BackupsViewModel.UpdateActiveBackup(
                                project.Id.ToString(),
                                project.Name,
                                0,
                                L("Backups.Status.Cancelled", "Cancelled"),
                                string.Empty,
                                allowCancel: false,
                                policyText: activePolicyText);
                            return;
                        }
                        catch (Exception ex)
                        {
                            results.Add((project.Name, project.RootPath, false));
                            Telemetry.Log("backup_all_project_failure", b => b
                                .WithHashedString("project", project.Name)
                                .WithHashedString("projectRoot", project.RootPath)
                                .WithFlag("useArchiveMode", useArchiveMode)
                                .WithException(ex));
                            throw;
                        }
                        finally
                        {
                            _backupCancelRequested.TryRemove(projectId, out _);
                        }
                    })).ToList();

                    await Task.WhenAll(tasks);

                    Telemetry.Log("backup_all_success", b => b
                        .WithCount("projects", projects.Count)
                        .WithCount("succeeded", results.Count(r => r.success))
                        .WithCount("failed", results.Count(r => !r.success))
                        .WithFlag("useArchiveMode", useArchiveMode)
                        .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));
                });

                // First reload history so the new backups appear.
                ReloadBackupsVmData();
                await DashboardViewModel.RefreshAsync();

                // --- After all backups: optional verification / post-hash ---
                var cfgAfterAll = await Task.Run(AppConfigStore.Load);
                var allDestinations = GetAllDestinations(cfgAfterAll);
                var allLatest = _repo.GetLatestBackupsPerProject();
                var projectsById = _repo.GetAllProjects()
                    .GroupBy(p => p.Id)
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (var latest in allLatest)
                {
                    if (!projectsById.TryGetValue(latest.ProjectId, out var proj))
                        continue;
                    if (proj == null)
                        continue;

                    if (cfgAfterAll.Backups.VerifyAfterCreate)
                    {
                        var destinationRoot = ResolveDestinationRootForBackup(
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
                        var msg = L("Backups.Notification.AllSuccess", "All project backups completed successfully.");
                        var title = L("Backups.Notification.AllSuccessTitle", "Backups completed");

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
                    .WithFlag("useArchiveMode", useArchiveMode)
                    .WithNumber("durationSeconds", (DateTime.UtcNow - start).TotalSeconds));

                if (NotificationsEnabled)
                {
                    var msg = L("Backups.Notification.AllFailureMessage", "Backup all projects failed. Check logs for details.");
                    var title = L("Backups.Notification.AllFailureTitle", "Backup-all failed");
                    var actionLabel = L("Logs.CopySnippet", "Copy log snippet");
                    var actionCommand = CreateCopyLogSnippetCommand(
                        L("Logs.Snippet.BackupAllFailure", "Backup-all failure."));

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
                    BackupsViewModel.BackupCurrentFile = L("Backups.Notification.AllFailureTitle", "Backup-all failed");
                    BackupsViewModel.BackupEtaText =
                        string.IsNullOrWhiteSpace(BackupsViewModel.BackupEtaText)
                            ? ex.Message
                                : BackupsViewModel.BackupEtaText + " - " + L("Backups.Status.FailedSuffix", "Failed");
                });

                // Clear cards on failure (ensure this runs on the UI thread)
                Dispatcher.UIThread.Post(() =>
                {
                    BackupsViewModel.ClearActiveBackups();
                });
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
                var activePolicyText = GetBackupPolicyChipTextForConfig(cfg);
                var throughput = useArchiveMode
                    ? cfg.Backups.LastBackupThroughputArchiveMbSec
                    : cfg.Backups.LastBackupThroughputCopyMbSec;
                if (throughput <= 0)
                {
                    throughput = cfg.Backups.LastBackupThroughputMbSec;
                }
                var preflight = await Task.Run(
                        () => _backupService.PreflightBackupAsync(
                            project,
                            backupRoot,
                            CancellationToken.None,
                            throughputMbSec: throughput,
                            useArchiveMode: useArchiveMode,
                            cacheTtl: TimeSpan.FromSeconds(45)))
                    .ConfigureAwait(false);

                var sizeLabel = BackupSnapshotItem.FormatSize(preflight.TotalBytes);
                var estimateLabel = string.Empty;
                var etaText = FormatEta(preflight.EstimatedSeconds);
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

                var projectId = project.Id.ToString();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var active = BackupsViewModel.ActiveBackups.FirstOrDefault(p => p.ProjectId == projectId);
                    if (active is null || active.Progress <= 0.1d)
                    {
                        BackupsViewModel.UpdateActiveBackup(
                            projectId,
                            project.Name,
                            0,
                            L("Backups.Progress.Estimating", "Estimating..."),
                            estimateLabel,
                            allowCancel: true,
                            policyText: activePolicyText);
                    }

                    if (!preflight.HasEnoughSpace && preflight.VolumeFreeBytes.HasValue)
                    {
                        var freeLabel = BackupSnapshotItem.FormatSize(preflight.VolumeFreeBytes.Value);
                        var warning = Lf(
                            "Backups.Preflight.LowDisk",
                            "Backup may not fit on the destination. Free space: {0}.",
                            freeLabel);

                        BackupsViewModel.ShowNotification(warning, "Warning");
                        if (!IsOnBackupsPage)
                        {
                            GlobalNotificationCenter.Instance.Show(
                                warning,
                                NotificationSeverity.Warning,
                                L("Backups.Preflight.Title", "Backup estimate"));
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
