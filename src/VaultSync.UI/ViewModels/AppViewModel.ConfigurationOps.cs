using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private static readonly TimeSpan AutoBackupDestinationWakeDelay = TimeSpan.FromSeconds(10);

        private void InitializeDestinationStatusOverview(BackupsViewModel vm)
        {
            AppConfig cfg = _config;
            List<BackupDestination> destinations = AppViewModel.GetAllDestinations(cfg);
            bool allowToggle = cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 };
            vm.ResetDestinationStatuses(destinations, allowToggle);
            QueueDestinationOverviewRefresh(vm);
        }

        private void QueueConfigReload(Action<AppConfig> apply, string context)
        {
            if (Interlocked.Exchange(ref _configReloadInFlight, 1) == 1)
            {
                _ = Interlocked.Exchange(ref _configReloadQueued, 1);
                return;
            }

            DetachedTask.Run(() =>
            {
                try
                {
                    AppConfig cfg = _configStore.Load();
                    Dispatcher.UIThread.Post(() => apply(cfg));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Config] Failed to reload for {context}: {ex.Message}");
                }
                finally
                {
                    _ = Interlocked.Exchange(ref _configReloadInFlight, 0);
                    if (Interlocked.Exchange(ref _configReloadQueued, 0) == 1)
                    {
                        QueueConfigReload(apply, context);
                    }
                }
            }, $"{nameof(QueueConfigReload)}:{context}");
        }

        private void QueueDestinationOverviewRefresh(BackupsViewModel? vm)
        {
            if (vm is null)
                return;

            if (Interlocked.Exchange(ref _destinationOverviewRefreshInFlight, 1) == 1)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    AppConfig cfg = _configStore.Load();
                    List<BackupDestination> destinations = AppViewModel.GetAllDestinations(cfg);
                    bool allowToggle = cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 };
                    vm.ResetDestinationStatuses(destinations, allowToggle);

                    IReadOnlyList<DestinationProbeSummary> summaries = GetDestinationProbeSummaries(cfg);
                    foreach (DestinationProbeSummary summary in summaries)
                    {
                        vm.UpdateDestinationStatus(summary.Id, summary.Message, summary.Severity, summary.Alias);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Destinations] Failed to refresh overview: {ex.Message}");
                }
                finally
                {
                    _ = Interlocked.Exchange(ref _destinationOverviewRefreshInFlight, 0);
                }
            });
        }

        public void NotifySoftCrashBanner(string? logPath)
        {
            SoftCrashBannerMessage = AppViewModel.L(
                "Errors.SoftCrash.Message",
                "VaultSync hit an unexpected error but kept running. A log was saved.");
            _softCrashLogPath = logPath;
            _showSoftCrashBanner = true;
            OnPropertyChanged(nameof(ShowSoftCrashBanner));
            OnPropertyChanged(nameof(CanCopySoftCrashLog));
            _copySoftCrashLogCommand.RaiseCanExecuteChanged();
        }

        private void DismissSoftCrashBanner()
        {
            _showSoftCrashBanner = false;
            OnPropertyChanged(nameof(ShowSoftCrashBanner));
        }

        private async Task CopySoftCrashLogAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_softCrashLogPath))
                    return;

                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime &&
                    lifetime.MainWindow?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(_softCrashLogPath);
                }
            }
            catch
            {
                // Best effort: ignore clipboard failures.
            }
        }

        private ICommand CreateCopyLogSnippetCommand(string contextLabel)
        {
            return new RelayCommand(async _ =>
            {
                string? snippet = _logConsoleService.GetRecentSnippet(30, contextLabel);
                if (string.IsNullOrWhiteSpace(snippet))
                    return;

                _ = await ClipboardHelper.TryCopyAsync(snippet);
            });
        }

        private void EnforceRetentionOnStartup()
        {
            try
            {
                AppConfig cfg = _config;
                int maxToKeep = cfg.Backups.MaxSnapshotsPerProject;
                if (maxToKeep <= 0)
                    return;

                string backupRoot = cfg.Backups.BackupLocation ?? string.Empty;
                var projects = _repo.GetAllProjects().ToList();
                foreach (Project? project in projects)
                {
                    _backupService.EnforceRetentionForProject(project.Id, backupRoot, maxToKeep);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackupRetention] Startup retention failed: {ex.Message}");
            }
        }

        private void OnBackupRetentionDeleted(Backup backup)
        {
            if (backup is null)
                return;

            if (string.IsNullOrWhiteSpace(backup.ExternalId))
                return;

            if (string.IsNullOrWhiteSpace(backup.DestinationPath))
                return;

            string machineId = Environment.MachineName;
            _ = Task.Run(() =>
            {
                try
                {
                    AppConfig cfg = _configStore.Load();
                    if (!cfg.Backups.EnableMetadataSync)
                        return;

                    Console.WriteLine($"[MetadataSync] Export tombstone for backup {backup.Id} -> '{backup.DestinationPath}'.");
                    MetadataSyncService.ExportBackupTombstoneToStore(
                        backup.DestinationPath,
                        backup.ExternalId,
                        _currentVersionString,
                        machineId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MetadataSync] Export tombstone failed for backup {backup.Id}: {ex.Message}");
                }
            });
        }

        private void CleanupIncompleteBackupsOnStartup()
        {
            try
            {
                AppConfig cfg = _config;
                List<BackupDestination> destinations = AppViewModel.GetActiveDestinations(cfg);
                if (destinations.Count == 0)
                    return;

                foreach (BackupDestination dest in destinations)
                {
                    NetworkCredentialProfile? profile = string.IsNullOrWhiteSpace(dest.CredentialName)
                        ? null
                        : cfg.Network.Credentials.FirstOrDefault(c =>
                            c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));

                    DestinationResolution resolution = _networkMountService.PrepareDestination(dest, profile);
                    if (!resolution.IsSuccess || string.IsNullOrWhiteSpace(resolution.EffectivePath))
                        continue;

                    IEnumerable<Project> projects = _repo.GetAllProjects();
                    var projectFolders = projects
                        .Select(p => BackupService.GetProjectBackupFolderName(p.Name))
                        .ToList();
                    int removed = BackupService.CleanupIncompleteBackups(resolution.EffectivePath, projectFolders);
                    if (removed > 0)
                    {
                        Console.WriteLine($"[BackupCleanup] Removed {removed} incomplete backup(s) under '{resolution.EffectivePath}'.");
                    }

                    _networkMountService.Cleanup(resolution);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BackupCleanup] Startup cleanup failed: {ex.Message}");
            }
        }

        public void AttachBackupWidgetService(IBackupWidgetService? service)
        {
            _backupWidgetService = service;
        }

        // ---------- Backups wiring ----------

        private void ReloadBackupsVmData()
        {
            _ = ReloadBackupsVmDataAsync(force: false);
        }

        private Task ReloadBackupsVmDataAsync(bool force)
        {
            if (Interlocked.Exchange(ref _reloadBackupsInFlight, 1) == 1)
            {
                _ = Interlocked.Exchange(ref _reloadBackupsQueued, 1);
                return Task.CompletedTask;
            }

            // Fetch and materialize data off the UI thread to reduce perceived hangs,
            // then marshal the lightweight ViewModel update back to the UI thread.
            return Task.Run(() =>
            {
                try
                {
                    _repo.EnsureSchema();
                    bool onBackupsPage = IsOnBackupsPage;
                    DateTime now = DateTime.UtcNow;
                    bool cacheFresh = _backupsCacheProjects is not null
                        && _backupsCacheBackups is not null
                        && (now - _backupsCacheUpdatedUtc) < BackupsCacheTtl;

                    if (!force && !onBackupsPage && cacheFresh)
                    {
                        return;
                    }

                    var projects = _repo.GetAllProjects().ToList();
                    bool useLightweight = !force && !onBackupsPage;
                    List<Backup> backups = useLightweight
                        ? [.. _repo.GetRecentBackupsByProject(limitPerProject: 5)]
                        : [.. _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)];

                    HashSet<int> disabledAuto = _config.Backups.AutoBackupDisabledProjects?.ToHashSet() ?? [];

                    _backupsCacheProjects = projects;
                    _backupsCacheBackups = backups;
                    _backupsCacheDisabledAuto = disabledAuto;
                    _backupsCacheUpdatedUtc = DateTime.UtcNow;
                    _backupsCachePartial = useLightweight;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (onBackupsPage || force)
                        {
                            BackupsViewModel.LoadFromBackups(projects, backups, disabledAuto);
                            BackupsViewModel.RefreshBackupDriveHealth();
                        }
                    });

                    // Destination reconciliation runs when a backup has already prepared the destination.
                    // Backups-page refresh should not wake sleeping disks or network shares just to test status.
                }
                finally
                {
                    _ = Interlocked.Exchange(ref _reloadBackupsInFlight, 0);
                    if (Interlocked.Exchange(ref _reloadBackupsQueued, 0) == 1)
                    {
                        ReloadBackupsVmData();
                    }
                }
            });
        }

        private BackupProjectPreparation CreateManualBackupPreparation(int projectId)
        {
            AppConfig cfg = _configStore.GetSnapshot();
            Project? project = _repo.GetProjectById(projectId);
            ProjectDestinationSelection selection = project is null
                ? new ProjectDestinationSelection(AppViewModel.GetActiveDestinations(cfg), null, null)
                : ResolveDestinationsForProject(project, cfg);
            return new BackupProjectPreparation(cfg, selection.Destinations, project, selection.WarningMessage, selection.WarningCode);
        }

        private int PruneMissingBackupsFromPreparedDestination(BackupDestination dest, string effectivePath, AppConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(effectivePath))
                return 0;

            List<Backup> backups = [.. _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow)];
            if (backups.Count == 0)
                return 0;

            List<BackupDestination> destinations = AppViewModel.GetActiveDestinations(cfg);
            if (destinations.Count == 0)
                return 0;

            int removed = 0;
            foreach (Backup backup in backups)
            {
                if (string.IsNullOrWhiteSpace(backup.Path))
                    continue;

                if (!BackupBelongsToDestination(backup, dest, destinations.Count))
                    continue;

                if (!BackupSafetyService.TryCombinePathUnderRoot(effectivePath, backup.Path, out string fullPath))
                    continue;

                if (Directory.Exists(fullPath) || File.Exists(fullPath))
                    continue;

                _repo.DeleteBackupById(backup.Id);
                TryDeleteSnapshotIfOrphan(backup.ProjectId, backup.SnapshotId);
                removed++;
            }

            if (removed > 0)
            {
                RuntimeLog.WriteVerbose($"[Backups] Pruned {removed} missing backup database entr{(removed == 1 ? "y" : "ies")} from prepared destination '{dest.Alias ?? dest.Path}'.");
            }

            return removed;
        }

        private static bool BackupBelongsToDestination(Backup backup, BackupDestination dest, int activeDestinationCount)
        {
            if (!string.IsNullOrWhiteSpace(backup.DestinationPath) &&
                string.Equals(backup.DestinationPath, dest.Path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(backup.DestinationPath))
                return false;

            if (!string.IsNullOrWhiteSpace(backup.DestinationAlias))
            {
                return string.Equals(backup.DestinationAlias, dest.Alias, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(backup.DestinationAlias, dest.Path, StringComparison.OrdinalIgnoreCase);
            }

            return activeDestinationCount == 1;
        }

        private int ScanDestinationsForUntrackedBackups(List<Project> projects, List<Backup> backups)
        {
            if (Interlocked.Exchange(ref _destinationScanInFlight, 1) == 1)
                return 0;

            try
            {
                DateTime now = DateTime.UtcNow;
                if ((now - _lastDestinationScanUtc) < DestinationScanInterval)
                    return 0;

                _lastDestinationScanUtc = now;

                AppConfig cfg = _config;
                List<BackupDestination> destinations = AppViewModel.GetActiveDestinations(cfg);
                if (destinations.Count == 0)
                    return 0;

                var projectBySlug = projects.ToDictionary(
                    p => BackupService.GetProjectBackupFolderName(p.Name),
                    p => p,
                    StringComparer.OrdinalIgnoreCase);

                HashSet<string> existingKeys = BuildExistingBackupKeys(backups);
                int added = 0;

                foreach (BackupDestination dest in destinations)
                {
                    if (!ShouldScanDestination(dest))
                        continue;

                    NetworkCredentialProfile? profile = string.IsNullOrWhiteSpace(dest.CredentialName)
                        ? null
                        : cfg.Network.Credentials.FirstOrDefault(c =>
                            c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));

                    DestinationResolution resolution = _networkMountService.PrepareDestination(dest, profile);
                    if (!resolution.IsSuccess || string.IsNullOrWhiteSpace(resolution.EffectivePath))
                        continue;

                    string destRoot = resolution.EffectivePath;

                    foreach (KeyValuePair<string, Project> projectEntry in projectBySlug)
                    {
                        string projectFolder = Path.Combine(destRoot, projectEntry.Key);
                        if (!Directory.Exists(projectFolder))
                            continue;

                        foreach (string backupFolder in SafeEnumerateDirectories(projectFolder))
                        {
                            string folderName = Path.GetFileName(backupFolder);
                            if (!TryParseBackupTimestamp(folderName, out DateTime createdUtc))
                                continue;

                            if (!IsBackupFolderComplete(backupFolder))
                                continue;

                            string relativePath = Path.GetRelativePath(destRoot, backupFolder);
                            string key = BuildBackupKey(dest, relativePath);
                            if (existingKeys.Contains(key))
                                continue;

                            long sizeBytes = TryGetArchiveSize(backupFolder);
                            int snapshotId = _repo.CreateSnapshotFromMetadata(
                                string.Empty,
                                projectEntry.Value.Id,
                                createdUtc,
                                0,
                                sizeBytes);

                            bool isProtected = IsBackupProtectedOnDisk(backupFolder);
                            bool isEncrypted = false;
                            string cryptoDescriptorJson = BackupCryptoDescriptor.PlainMetadataJson;
                            if (BackupArchiveCryptoService.TryReadDescriptor(backupFolder, out BackupCryptoDescriptor? descriptor, out bool encrypted))
                            {
                                isEncrypted = encrypted;
                                cryptoDescriptorJson = descriptor.ToMetadataJson(encrypted);
                            }
                            _ = _repo.CreateBackupFromMetadata(
                                string.Empty,
                                projectEntry.Value.Id,
                                snapshotId,
                                createdUtc,
                                "manual",
                                sizeBytes,
                                relativePath,
                                dest.Path ?? destRoot,
                                dest.Alias ?? string.Empty,
                                isProtected,
                                isImported: true,
                                isEncrypted: isEncrypted,
                                cryptoDescriptorJson: cryptoDescriptorJson);

                            _ = existingKeys.Add(key);
                            added++;
                        }
                    }

                    _networkMountService.Cleanup(resolution);
                }

                if (added > 0)
                {
                    Console.WriteLine($"[Backups] Imported {added} untracked backup(s) from destinations.");
                }

                return added;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Backups] Destination scan failed: {ex.Message}");
                return 0;
            }
            finally
            {
                _ = Interlocked.Exchange(ref _destinationScanInFlight, 0);
            }
        }

        private bool ShouldScanDestination(BackupDestination dest)
        {
            if (dest is null)
                return false;

            if (!IsRemoteDestinationPath(dest.Path))
                return true;

            string id = DestinationStatusItem.GetId(dest);
            if (_destinationProbeSummaries.TryGetValue(id, out DestinationProbeSummary? summary))
            {
                if (summary.Reachable)
                    return true;

                if ((DateTime.UtcNow - summary.LastChecked) < DestinationProbeFailureBackoff)
                    return false;
            }

            return false;
        }

        private static HashSet<string> BuildExistingBackupKeys(IEnumerable<Backup> backups)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Backup backup in backups)
            {
                if (string.IsNullOrWhiteSpace(backup.Path))
                    continue;

                if (!string.IsNullOrWhiteSpace(backup.DestinationAlias))
                {
                    _ = keys.Add($"{backup.DestinationAlias}|{backup.Path}");
                }

                if (!string.IsNullOrWhiteSpace(backup.DestinationPath))
                {
                    _ = keys.Add($"{backup.DestinationPath}|{backup.Path}");
                }
            }

            return keys;
        }

        private static string BuildBackupKey(BackupDestination dest, string relativePath)
        {
            string key = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path ?? string.Empty : dest.Alias;
            return $"{key}|{relativePath}";
        }

        private static IEnumerable<string> SafeEnumerateDirectories(string root)
        {
            try
            {
                return Directory.EnumerateDirectories(root);
            }
            catch
            {
                return [];
            }
        }

        private static bool IsBackupFolderComplete(string backupFolder)
        {
            string inProgress = Path.Combine(backupFolder, ".vaultsync_inprogress");
            if (File.Exists(inProgress))
                return false;

            string completed = Path.Combine(backupFolder, ".vaultsync_complete");
            string archive = Path.Combine(backupFolder, BackupArchiveCryptoService.PlainArchiveFileName);
            string encryptedArchive = Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
            if (File.Exists(completed) || File.Exists(archive) || File.Exists(encryptedArchive))
                return true;

            try
            {
                return Directory.EnumerateFileSystemEntries(backupFolder)
                    .Any(entry =>
                    {
                        string name = Path.GetFileName(entry);
                        return !name.StartsWith(".vaultsync_", StringComparison.OrdinalIgnoreCase);
                    });
            }
            catch
            {
                return false;
            }
        }

        private static long TryGetArchiveSize(string backupFolder)
        {
            try
            {
                return BackupArchiveCryptoService.GetStoredArchiveSize(backupFolder);
            }
            catch
            {
                // ignore size probe failures
            }

            return 0;
        }

        private static bool IsBackupProtectedOnDisk(string backupFolder)
        {
            try
            {
                string marker = Path.Combine(backupFolder, BackupProtectionMarkerFileName);
                if (File.Exists(marker))
                    return true;
            }
            catch
            {
                return true;
            }

            // Avoid startup write probes on potentially protected/network locations.
            // Treat non-writable folders as protected using a non-throwing heuristic.
            return !IsLikelyWritableDirectory(backupFolder);
        }

        private static bool TryParseBackupTimestamp(string? folderName, out DateTime createdUtc)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                createdUtc = DateTime.UtcNow;
                return false;
            }

            return DateTime.TryParseExact(
                folderName,
                "yyyy-MM-dd_HH-mm-ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out createdUtc);
        }

        public AppConfig GetConfigSnapshot() => _config;

        private sealed record BackupProjectPreparation(
            AppConfig Config,
            List<BackupDestination> Destinations,
            Project? Project,
            string? DestinationWarning,
            string? DestinationWarningCode);

        private void ConfigureAutoBackupTimer()
        {
            _autoBackupTimer?.Dispose();
            _autoBackupTimer = null;

            int intervalMinutes = _config.Backups.IntervalMinutes;
            if (!_config.Backups.EnableAutoBackups || intervalMinutes <= 0)
            {
                DiagnosticsLogger.Record(
                    $"[AutoBackup] Timer disabled. Enabled={_config.Backups.EnableAutoBackups}; IntervalMinutes={intervalMinutes}.");
                return;
            }

            var interval = TimeSpan.FromMinutes(intervalMinutes);

            // Use a wrapper to avoid unobserved exceptions from the async timer callback crashing the process.
            _autoBackupTimer = new Timer(
                _ => _ = SafeRunAutoBackupsAsync(),
                null,
                interval,
                interval);
            DiagnosticsLogger.Record($"[AutoBackup] Timer configured. IntervalMinutes={intervalMinutes}; FirstDueUtc={DateTime.UtcNow.Add(interval):O}.");
        }

        private async Task SafeRunAutoBackupsAsync()
        {
            try
            {
                await RunAutoBackupsAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppViewModel] Auto-backup timer failed: {ex}");
                Telemetry.Log("auto_backup_timer_failure", b => b.WithException(ex));
            }
        }

        private async Task RunAutoBackupsAsync()
        {
            if (Interlocked.Exchange(ref _autoBackupInFlight, 1) == 1)
            {
                DiagnosticsLogger.Record("[AutoBackup] Tick skipped: previous run still in flight.");
                return;
            }

            try
            {
                DiagnosticsLogger.Record("[AutoBackup] Tick started.");
                LogBackupPolicyTransitionIfChanged(_config, "auto-backup-tick");

                if (BackupsViewModel.IsBusy)
                {
                    DiagnosticsLogger.Record("[AutoBackup] Tick skipped: backups view is busy.");
                    Telemetry.Log("auto_backup_skipped", b => b
                        .WithCode("reason", "busy"));
                    return;
                }

                if (ShouldPauseBackupsForBattery(out string? pauseReason))
                {
                    DiagnosticsLogger.Record($"[AutoBackup] Tick skipped: {pauseReason}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        BackupsViewModel.BackupCurrentFile = pauseReason;
                        BackupsViewModel.BusyMessage = pauseReason;
                    });
                    Telemetry.Log("auto_backup_skipped", b => b
                        .WithCode("reason", "battery"));
                    return;
                }

                if (ShouldPauseAutoBackupsForQuietHours(_config, out string? quietReason, out DateTimeOffset? quietResumeAtLocal))
                {
                    DiagnosticsLogger.Record($"[AutoBackup] Tick skipped: {quietReason}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        BackupsViewModel.BackupCurrentFile = quietReason;
                        BackupsViewModel.BusyMessage = quietReason;
                    });

                    Telemetry.Log("auto_backup_skipped", b =>
                    {
                        _ = b.WithCode("reason", "quiet_hours");
                        if (quietResumeAtLocal.HasValue)
                            _ = b.WithHashedString("resumeAtLocal", quietResumeAtLocal.Value.ToString("O", CultureInfo.InvariantCulture));
                    });
                    return;
                }

                AutoBackupPreparation preparation = await Task.Run(PrepareAutoBackupRun);
                if (!preparation.IsReady)
                {
                    DiagnosticsLogger.Record($"[AutoBackup] Tick skipped: {preparation.FailureCode ?? "preflight_failed"}.");
                    Telemetry.Log("auto_backup_skipped", b => b
                        .WithCode("reason", preparation.FailureCode ?? "preflight_failed"));
                    return;
                }

                AppConfig? cfg = preparation.Config;
                ISet<int>? disabled = preparation.DisabledProjects;
                List<Project>? projects = preparation.Projects;

                bool useArchiveMode = _settingsViewModel.UseBackupCompression;
                int backupAttempts = 0;
                int backupSucceeded = 0;
                int backupFailed = 0;
                int destinationUnreachable = 0;
                List<BackupDestination> activeDestinations = AppViewModel.GetActiveDestinations(cfg);
                DiagnosticsLogger.Record(
                    $"[AutoBackup] Prepared run. Projects={projects.Count}; Disabled={disabled.Count}; Destinations={AppViewModel.GetAllDestinations(cfg).Count}; ActiveDestinations={activeDestinations.Count}; ArchiveMode={useArchiveMode}.");
                await WarmAutoBackupDestinationsAsync(cfg, activeDestinations).ConfigureAwait(false);

                int maxParallel = Math.Max(1, Environment.ProcessorCount);
                using var throttler = new SemaphoreSlim(maxParallel);

                var tasks = projects
                        .Where(p => !disabled.Contains(p.Id))
                        .Select(async project =>
                        {
                            await throttler.WaitAsync();
                            try
                            {
                                ProjectDestinationSelection selection = ResolveDestinationsForProject(project, cfg);
                                if (!string.IsNullOrWhiteSpace(selection.WarningMessage))
                                {
                                    Telemetry.Log("auto_backup_destination_fallback", b => b
                                        .WithCode("reason", selection.WarningCode ?? "preferred_destination_fallback")
                                        .WithHashedString("project", project.Name));
                                }

                                if (selection.Destinations.Count == 0)
                                {
                                    Telemetry.Log("auto_backup_skipped", b => b
                                        .WithCode("reason", "no_destination")
                                        .WithHashedString("project", project.Name));
                                    return;
                                }

                                if (cfg.Backups.PromptRestoreAfterImport && project.NeedsRestore)
                                {
                                    MaybeNotifyRestoreRecommended(project);
                                    Telemetry.Log("auto_backup_advisory", b => b
                                        .WithCode("reason", "restore_recommended")
                                        .WithHashedString("project", project.Name));
                                }

                                if (!TryResolveProjectRoot(project, cfg, out Project? resolvedProject, out string? rootError))
                                {
                                    MaybeNotifyProjectRootMissing(project, rootError);
                                    Telemetry.Log("auto_backup_skipped", b => b
                                        .WithCode("reason", "project_root_missing")
                                        .WithHashedString("project", project.Name)
                                        .WithHashedString("projectRoot", project.RootPath));
                                    return;
                                }

                                project = resolvedProject;
                                int? sharedSnapshotId = null;
                                bool metadataWritten = false;

                                var destinationResolutions = new List<(BackupDestination Dest, DestinationResolution Resolution)>();
                                foreach (BackupDestination dest in selection.Destinations)
                                {
                                    DestinationResolution resolution = await PrepareDestinationForAutoBackupAsync(dest, cfg).ConfigureAwait(false);
                                    if (!resolution.IsSuccess)
                                    {
                                        _ = Interlocked.Increment(ref destinationUnreachable);
                                        continue;
                                    }

                                    destinationResolutions.Add((dest, resolution));
                                }

                                if (destinationResolutions.Count == 0)
                                {
                                    Telemetry.Log("auto_backup_skipped", b => b
                                        .WithCode("reason", "no_destination")
                                        .WithHashedString("project", project.Name));
                                    return;
                                }

                                try
                                {
                                    foreach ((BackupDestination dest, DestinationResolution resolution) in destinationResolutions)
                                    {
                                        DriveHealthDecision driveDecision = await EvaluateDriveHealthAsync(project.RootPath, resolution.EffectivePath);
                                        if (!string.IsNullOrWhiteSpace(driveDecision.Message))
                                        {
                                            ShowDriveHealthNotification(driveDecision.Message, driveDecision.Severity);
                                        }
                                        if (driveDecision.Block)
                                        {
                                            continue;
                                        }

                                        string destLabel = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path;
                                        int retryMaxAttempts = Math.Clamp(dest.RetryMaxAttempts, 1, 10);
                                        int retryBaseDelaySeconds = Math.Clamp(dest.RetryBackoffSeconds, 1, 300);

                                        try
                                        {
                                            bool destinationSucceeded = false;
                                            bool noChangesDetected = false;
                                            for (int attemptIndex = 1; attemptIndex <= retryMaxAttempts; attemptIndex++)
                                            {
                                                try
                                                {
                                                    int? archiveUploadBufferBytes = await EnsureArchiveUploadBufferAsync(
                                                        dest,
                                                        cfg,
                                                        resolution.EffectivePath,
                                                        useArchiveMode,
                                                        CancellationToken.None);
                                                    _ = Interlocked.Increment(ref backupAttempts);
                                                    bool isRemoteDestination = IsRemoteDestinationPath(resolution.EffectivePath)
                                                        || IsRemoteDestinationPath(dest.Path);
                                                    bool allowParallelUpload = cfg.Backups.EnableParallelArchiveUpload;
                                                    bool preferParallelUpload = allowParallelUpload && isRemoteDestination;
                                                    if (!allowParallelUpload)
                                                    {
                                                        Console.WriteLine($"[BackupService] Parallel archive upload disabled by user settings for '{destLabel}'.");
                                                    }
                                                    var sw = Stopwatch.StartNew();
                                                    BackupService.BackupRunResult backupResult = await _backupService.RunBackupAsync(
                                                        project,
                                                        resolution.EffectivePath,
                                                        isAuto: true,
                                                        progressCallback: null,
                                                        useArchiveMode: useArchiveMode,
                                                        fullSnapshotHash: _settingsViewModel.UseFullSnapshotHash,
                                                        maxSnapshotsToKeep: cfg.Backups.MaxSnapshotsPerProject,
                                                        minimumFreeSpacePercent: _settingsViewModel.MinimumFreeSpacePercent,
                                                        preferredFinalBackupRoot: null,
                                                        reuseSnapshotId: metadataWritten ? sharedSnapshotId : null,
                                                        writeMetadata: !metadataWritten,
                                                        destinationPath: resolution.EffectivePath,
                                                        destinationAlias: destLabel,
                                                        skipIfNoChanges: true,
                                                        useRsyncDelta: _settingsViewModel?.UseRsyncDelta ?? false,
                                                        useIncrementalBackups: _settingsViewModel?.UseIncrementalBackups ?? false,
                                                        archiveUploadBufferBytes: archiveUploadBufferBytes,
                                                        preferRunnerProgressOnly: isRemoteDestination,
                                                        preferParallelArchiveUpload: preferParallelUpload,
                                                        useScanCache: _settingsViewModel.EnableScanCache,
                                                        aggressiveScanCache: _settingsViewModel.AggressiveScanCache,
                                                        enableCheckpointedRetry: dest.EnableCheckpointResume,
                                                        ct: CancellationToken.None);
                                                    sw.Stop();

                                                    if (backupResult.SkippedForNoChanges)
                                                    {
                                                        Telemetry.Log("auto_backup_skipped", b => b
                                                            .WithCode("reason", "no_changes")
                                                            .WithHashedString("project", project.Name)
                                                            .WithHashedString("destinationPath", dest.Path ?? string.Empty));
                                                        noChangesDetected = true;
                                                        destinationSucceeded = true;
                                                        break;
                                                    }

                                                    if (!metadataWritten && backupResult.BackupId > 0)
                                                    {
                                                        metadataWritten = true;
                                                        if (!sharedSnapshotId.HasValue)
                                                        {
                                                            Backup? created = _repo.GetBackupById(backupResult.BackupId);
                                                            sharedSnapshotId = created?.SnapshotId ?? sharedSnapshotId;
                                                        }
                                                    }

                                                    if (backupResult.BackupId > 0)
                                                    {
                                                        _ = Interlocked.Increment(ref backupSucceeded);
                                                        RecordBackupThroughput(backupResult.BackupId, sw.Elapsed, useArchiveMode);
                                                        TryExportMetadataForBackup(cfg, dest, resolution.EffectivePath, backupResult.BackupId);
                                                        destinationSucceeded = true;
                                                        break;
                                                    }
                                                }
                                                catch (Exception ex) when (attemptIndex < retryMaxAttempts)
                                                {
                                                    _ = Interlocked.Increment(ref backupFailed);
                                                    int delaySeconds = Math.Min(300, retryBaseDelaySeconds * (1 << Math.Max(0, attemptIndex - 1)));
                                                    Telemetry.Log("auto_backup_destination_retry", b => b
                                                        .WithHashedString("project", project.Name)
                                                        .WithHashedString("projectRoot", project.RootPath)
                                                        .WithHashedString("destinationPath", dest.Path)
                                                        .WithHashedString("destinationAlias", dest.Alias ?? string.Empty)
                                                        .WithCount("attempt", attemptIndex + 1)
                                                        .WithCount("maxAttempts", retryMaxAttempts)
                                                        .WithFlag("useArchiveMode", useArchiveMode)
                                                        .WithException(ex));
                                                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), CancellationToken.None);
                                                }
                                            }

                                            if (!destinationSucceeded)
                                            {
                                                _ = Interlocked.Increment(ref backupFailed);
                                            }
                                            if (noChangesDetected)
                                            {
                                                break;
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            _ = Interlocked.Increment(ref backupFailed);
                                            Telemetry.Log("auto_backup_failure", b => b
                                                .WithHashedString("project", project.Name)
                                                .WithHashedString("projectRoot", project.RootPath)
                                                .WithHashedString("destinationPath", dest.Path)
                                                .WithHashedString("destinationAlias", dest.Alias ?? string.Empty)
                                                .WithFlag("useArchiveMode", useArchiveMode)
                                                .WithException(ex));
                                        }
                                    }
                                }
                                finally
                                {
                                    foreach ((_, DestinationResolution resolution) in destinationResolutions)
                                    {
                                        _networkMountService.Cleanup(resolution);
                                    }
                                }

                                if (metadataWritten && sharedSnapshotId.HasValue)
                                {
                                    Backup? latest = _repo.GetLatestBackupForProject(project.Id);
                                    if (latest is not null && AppViewModel.ShouldRunVerification(project, isAutoRun: true, cfg.Backups.VerifyAfterCreate))
                                    {
                                        string? destinationRoot = ResolveDestinationRootForBackup(
                                            latest,
                                            AppViewModel.GetAllDestinations(cfg),
                                            cfg.Backups.BackupRoot);
                                        StartVerificationAsync(project, latest, destinationRoot ?? string.Empty, "auto_backup_verify_failed");
                                    }
                                    else
                                    {
                                        StartPostBackupHashingAsync(project, sharedSnapshotId.Value);
                                    }
                                }
                            }
                            finally
                            {
                                _ = throttler.Release();
                            }
                        })
                        .ToList();

                await Task.WhenAll(tasks);

                // Marshal UI collection updates back to the UI thread to avoid cross-thread crashes.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ReloadBackupsVmData();
                    _ = DashboardViewModel.RefreshAsync();
                });

                Telemetry.Log("auto_backup_tick", b => b
                    .WithCount("projects", projects.Count)
                    .WithCount("destinations", AppViewModel.GetAllDestinations(cfg).Count)
                    .WithCount("attempts", backupAttempts)
                    .WithCount("succeeded", backupSucceeded)
                    .WithCount("failed", backupFailed)
                    .WithCount("destinationsUnreachable", destinationUnreachable)
                    .WithFlag("useArchiveMode", useArchiveMode)
                    .WithNumber("intervalMinutes", cfg.Backups.IntervalMinutes));
                DiagnosticsLogger.Record(
                    $"[AutoBackup] Tick complete. Projects={projects.Count}; Attempts={backupAttempts}; Succeeded={backupSucceeded}; Failed={backupFailed}; DestinationsUnreachable={destinationUnreachable}.");
            }
            finally
            {
                _ = Interlocked.Exchange(ref _autoBackupInFlight, 0);
            }
        }

        private bool ShouldPauseAutoBackupsForQuietHours(
            AppConfig cfg,
            out string reason,
            out DateTimeOffset? resumeAtLocal)
        {
            QuietHoursDecision decision = QuietHoursPolicy.Evaluate(
                cfg.Backups.EnableQuietHours,
                cfg.Backups.QuietHoursStart,
                cfg.Backups.QuietHoursEnd,
                DateTimeOffset.Now);

            resumeAtLocal = decision.ResumeAtLocal;
            if (!decision.IsInQuietHours)
            {
                reason = string.Empty;
                return false;
            }

            string startLabel = decision.StartTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            string endLabel = decision.EndTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
            string resumeLabel = resumeAtLocal?.ToString("g", CultureInfo.CurrentCulture)
                ?? AppViewModel.L("Backups.QuietHours.ResumeUnknown", "the end of quiet hours");

            reason = Lf(
                "Backups.Notification.QuietHoursPaused",
                "Backups paused during quiet hours ({0}-{1}). Next run after {2}.",
                startLabel,
                endLabel,
                resumeLabel);
            return true;
        }

        private void OnAutoBackupPreferenceChanged(int projectId, bool enabled)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    AppConfig cfg = _configStore.Load();
                    List<int> list = cfg.Backups.AutoBackupDisabledProjects ?? [];
                    if (!enabled)
                    {
                        if (!list.Contains(projectId))
                            list.Add(projectId);
                    }
                    else
                    {
                        _ = list.Remove(projectId);
                    }

                    cfg.Backups.AutoBackupDisabledProjects = list;
                    _configStore.Save(cfg);
                    DiagnosticsLogger.Record(
                        $"[AutoBackup] Preference changed. ProjectId={projectId}; Enabled={enabled}; DisabledCount={list.Count}.");

                    Dispatcher.UIThread.Post(() =>
                    {
                        _config.Backups.AutoBackupDisabledProjects = list;
                        _backupsViewModel?.RefreshAutoBackupFlagsFromConfig();
                        ConfigureAutoBackupTimer();
                    });

                    AppViewModel.RunDetached(
                        () => ExportMetadataForProjectSettingsChangeAsync(projectId),
                        nameof(ExportMetadataForProjectSettingsChangeAsync));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AutoBackup] Failed to update preference: {ex.Message}");
                }
            });
        }

        private void OnAutoBackupGroupPreferenceChanged(IReadOnlyList<int>? projectIds, bool enabled)
        {
            int[] ids = projectIds?
                .Where(id => id > 0)
                .Distinct()
                .ToArray() ?? [];

            if (ids.Length == 0)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    AppConfig cfg = _configStore.Load();
                    List<int> list = cfg.Backups.AutoBackupDisabledProjects ?? [];
                    foreach (int projectId in ids)
                    {
                        if (enabled)
                        {
                            _ = list.Remove(projectId);
                        }
                        else if (!list.Contains(projectId))
                        {
                            list.Add(projectId);
                        }
                    }

                    cfg.Backups.AutoBackupDisabledProjects = list;
                    _configStore.Save(cfg);
                    DiagnosticsLogger.Record(
                        $"[AutoBackup] Group preference changed. ProjectIds={string.Join(',', ids)}; Enabled={enabled}; DisabledCount={list.Count}.");

                    Dispatcher.UIThread.Post(() =>
                    {
                        _config.Backups.AutoBackupDisabledProjects = list;
                        _backupsViewModel?.RefreshAutoBackupFlagsFromConfig();
                        ConfigureAutoBackupTimer();
                    });

                    foreach (int projectId in ids)
                    {
                        AppViewModel.RunDetached(
                            () => ExportMetadataForProjectSettingsChangeAsync(projectId),
                            $"{nameof(ExportMetadataForProjectSettingsChangeAsync)}-{projectId}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AutoBackup] Failed to sync grouped preference update: {ex.Message}");
                }
            });
        }

        private void OnProjectSettingsMetadataChanged(int projectId)
        {
            AppViewModel.RunDetached(
                () => ExportMetadataForProjectSettingsChangeAsync(projectId),
                nameof(OnProjectSettingsMetadataChanged));
        }

        private void OnProjectRemovedFromDatabase(int projectId, string externalId)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(externalId))
                        return;

                    AppConfig cfg = _configStore.Load();
                    if (cfg.Backups.AutoBackupDisabledProjects?.Remove(projectId) is true)
                    {
                        _configStore.Save(cfg);
                    }

                    foreach (BackupDestination dest in AppViewModel.GetAllDestinations(cfg))
                    {
                        if (!AppViewModel.IsMetadataSyncEnabled(cfg, dest))
                            continue;

                        DestinationResolution resolution = await PrepareDestinationAsync(dest, cfg).ConfigureAwait(false);
                        if (!resolution.IsSuccess || string.IsNullOrWhiteSpace(resolution.EffectivePath))
                            continue;

                        MetadataSyncService.TryExportProjectTombstone(
                            resolution.EffectivePath,
                            externalId,
                            Environment.MachineName);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MetadataSync] Project tombstone export failed for projectId={projectId}: {ex.Message}");
                }
            });
        }

        private void OnPreferredDestinationChanged(int projectId, string preferredDestinationId)
        {
            AppViewModel.RunDetached(
                () => OnPreferredDestinationChangedAsync(projectId, preferredDestinationId),
                nameof(OnPreferredDestinationChangedAsync));
        }

        private async Task OnPreferredDestinationChangedAsync(int projectId, string preferredDestinationId)
        {
            try
            {
                _repo.UpdateProjectPreferredDestination(projectId, preferredDestinationId ?? string.Empty);
                await _projectsViewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Projects] Failed to update preferred destination for project {projectId}: {ex.Message}");
            }
        }

        private void OnProjectEncryptionPolicyChanged(int projectId, string encryptionPolicy)
        {
            AppViewModel.RunDetached(
                () => OnProjectEncryptionPolicyChangedAsync(projectId, encryptionPolicy),
                nameof(OnProjectEncryptionPolicyChangedAsync));
        }

        private async Task OnProjectEncryptionPolicyChangedAsync(int projectId, string encryptionPolicy)
        {
            try
            {
                Project? project = _repo.GetProjectById(projectId);
                if (project is null)
                    return;

                _repo.UpdateProjectEncryptionSettings(
                    projectId,
                    ProjectEncryptionPolicy.Normalize(encryptionPolicy),
                    string.IsNullOrWhiteSpace(project.EncryptionKeyRef) ? null : project.EncryptionKeyRef);
                await ExportMetadataForProjectSettingsChangeAsync(projectId);
                await _projectsViewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Projects] Failed to update encryption policy for project {projectId}: {ex.Message}");
            }
        }

        private void OnProjectRestoreModeChanged(int projectId, string restoreMode)
        {
            AppViewModel.RunDetached(
                () => OnProjectRestoreModeChangedAsync(projectId, restoreMode),
                nameof(OnProjectRestoreModeChangedAsync));
        }

        private async Task OnProjectRestoreModeChangedAsync(int projectId, string restoreMode)
        {
            try
            {
                _repo.UpdateProjectRestoreMode(projectId, restoreMode);
                await ExportMetadataForProjectSettingsChangeAsync(projectId);
                await _projectsViewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Projects] Failed to update restore mode for project {projectId}: {ex.Message}");
            }
        }

        private void OnProjectVerificationPolicyChanged(int projectId, string verificationPolicy)
        {
            AppViewModel.RunDetached(
                () => OnProjectVerificationPolicyChangedAsync(projectId, verificationPolicy),
                nameof(OnProjectVerificationPolicyChangedAsync));
        }

        private async Task OnProjectVerificationPolicyChangedAsync(int projectId, string verificationPolicy)
        {
            try
            {
                _repo.UpdateProjectVerificationPolicy(projectId, verificationPolicy);
                await ExportMetadataForProjectSettingsChangeAsync(projectId);
                await _projectsViewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Projects] Failed to update verification policy for project {projectId}: {ex.Message}");
            }
        }

        private void OnProjectEncryptionRequestedFromBackups(int projectId)
        {
            _ = _projectEncryptionEnrollmentService.EditProjectEncryptionSecretAsync(projectId);
        }

        private void OnProjectEncryptionRequestedFromProjects(ProjectItemViewModel? project)
        {
            if (project is null)
                return;

            int projectId = project.ProjectId;
            if (projectId <= 0)
            {
                Project? dbProject = _repo.GetProjectByName(project.Name);
                projectId = dbProject?.Id ?? 0;
            }

            if (projectId <= 0)
                return;

            _ = _projectEncryptionEnrollmentService.EditProjectEncryptionSecretAsync(projectId);
        }

        private void OnEnrollProjectEncryptionRequested()
        {
            _ = _projectEncryptionEnrollmentService.StartProjectSelectionEnrollmentAsync();
        }

        private void OnDestinationActiveChanged(DestinationStatusItem item, bool isActive)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    AppConfig cfg = _configStore.Load();
                    if (!cfg.Backups.UseAdvancedDestinations ||
                        cfg.Backups.Destinations is null ||
                        cfg.Backups.Destinations.Count == 0)
                    {
                        return;
                    }

                    var target = new BackupDestination
                    {
                        Path = item.Path,
                        Alias = item.Alias
                    };
                    BackupDestination? destEntry = cfg.Backups.Destinations
                        .FirstOrDefault(d => DestinationsMatch(d.Path, d.Alias, target));
                    if (destEntry is null || destEntry.Active == isActive)
                        return;

                    destEntry.Active = isActive;
                    _configStore.Save(cfg);

                    Dispatcher.UIThread.Post(() =>
                    {
                        _config = cfg;
                        if (_settingsViewModel is not null)
                        {
                            BackupDestinationViewModel? vmDest = _settingsViewModel.Destinations
                                .FirstOrDefault(d => DestinationsMatch(d.Path, d.Alias, destEntry));
                            if (vmDest != null)
                            {
                                vmDest.Active = isActive;
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Destinations] Failed to update active flag: {ex.Message}");
                }
            });
        }

        private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
        {
            string propertyName = e.PropertyName ?? string.Empty;
            if (propertyName == nameof(SettingsViewModel.SaveStatus))
            {
                return;
            }
            QueueConfigReload(cfg =>
            {
                _config = cfg;
                LogBackupPolicyTransitionIfChanged(_config, $"settings:{propertyName}");

                if (propertyName is nameof(SettingsViewModel.EnableAutoBackups)
                    or nameof(SettingsViewModel.AutoBackupIntervalMinutes))
                {
                    ConfigureAutoBackupTimer();
                }

                if (propertyName is nameof(SettingsViewModel.EnableMaintenanceWindow)
                    or nameof(SettingsViewModel.MaintenanceWindowStart)
                    or nameof(SettingsViewModel.MaintenanceWindowEnd)
                    or nameof(SettingsViewModel.MaintenanceRunConsistencyScan)
                    or nameof(SettingsViewModel.MaintenanceRunRepairDryRun)
                    or nameof(SettingsViewModel.MaintenanceRunMetadataRefresh))
                {
                    ConfigureMaintenanceTimer();
                }

                if (propertyName == nameof(SettingsViewModel.CheckForUpdatesOnStartup))
                {
                    StartUpdateCheck();
                    ConfigureUpdateCheckTimer();
                }

                if (propertyName == nameof(SettingsViewModel.BetaChannelEnabled))
                {
                    StartUpdateCheck();
                }

                if (propertyName == nameof(SettingsViewModel.UpdateCheckIntervalMinutes))
                {
                    ConfigureUpdateCheckTimer();
                }

                if (propertyName is nameof(SettingsViewModel.EnableVerboseLogging)
                    or nameof(SettingsViewModel.SaveVerboseLogs))
                {
                    UpdateLogConsoleSettings();
                }

                if (propertyName is nameof(SettingsViewModel.UseAdvancedDestinations))
                {
                    RefreshDestinationOptionSources(cfg);
                }

                RefreshDestinationStatusOverview();
            }, "settings-change");
        }

        private void OnDestinationSettingsSaved()
        {
            QueueConfigReload(cfg =>
            {
                _config = cfg;
                RefreshDestinationOptionSources(cfg);
                RefreshDestinationStatusOverview();
            }, "settings-saved");
        }

        private void OnDestinationsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
            {
                foreach (BackupDestinationViewModel dest in e.NewItems)
                {
                    TrackDestinationViewModel(dest);
                }
            }

            if (e.OldItems is not null)
            {
                foreach (BackupDestinationViewModel dest in e.OldItems)
                {
                    UntrackDestinationViewModel(dest);
                }
            }

            RefreshDestinationOptionSources();
            RefreshDestinationStatusOverview();
        }

        private void TrackDestinationViewModel(BackupDestinationViewModel dest)
        {
            if (_observedDestinations.Add(dest))
            {
                dest.PropertyChanged += OnDestinationViewModelPropertyChanged;
            }
        }

        private void UntrackDestinationViewModel(BackupDestinationViewModel dest)
        {
            if (_observedDestinations.Remove(dest))
            {
                dest.PropertyChanged -= OnDestinationViewModelPropertyChanged;
            }
        }

        private void OnDestinationViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not BackupDestinationViewModel dest)
                return;

            if (e.PropertyName is nameof(BackupDestinationViewModel.Alias)
                or nameof(BackupDestinationViewModel.Path)
                or nameof(BackupDestinationViewModel.Active))
            {
                string targetId = DestinationStatusItem.GetId(new BackupDestination { Path = dest.Path, Alias = dest.Alias });
                _ = _destinationProbeSummaries.TryRemove(targetId, out _);
                RefreshDestinationOptionSources();
                RefreshDestinationStatusOverview();
            }
        }

        private void RefreshDestinationOptionSources()
        {
            QueueConfigReload(config =>
            {
                _config = config;
                RefreshDestinationOptionSources(config);
            }, "destinations-options");
        }

        private void RefreshDestinationOptionSources(AppConfig config)
        {
            _projectsViewModel.RefreshDestinationOptions(config);
            BackupsViewModel.RefreshDestinationOptions(config);
        }

        private void UpdateLogConsoleSettings()
        {
            _logConsoleService.Enabled = _settingsViewModel.EnableVerboseLogging;
            _logConsoleService.SaveToFile = _settingsViewModel.EnableVerboseLogging &&
                                            _settingsViewModel.SaveVerboseLogs;
        }

        private void OnOpenLogConsoleRequested()
        {
            DiagnosticsLogger.Record("Log console requested.");
            ShowLogConsole();
        }

    }
}
