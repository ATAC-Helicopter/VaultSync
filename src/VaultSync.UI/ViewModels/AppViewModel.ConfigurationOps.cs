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
        private void InitializeDestinationStatusOverview(BackupsViewModel vm)
        {
            var cfg = _config;
            var destinations = GetAllDestinations(cfg);
            var allowToggle = cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 };
            vm.ResetDestinationStatuses(destinations, allowToggle);
            QueueDestinationOverviewRefresh(vm);
        }

        private void QueueConfigReload(Action<AppConfig> apply, string context)
        {
            if (Interlocked.Exchange(ref _configReloadInFlight, 1) == 1)
            {
                Interlocked.Exchange(ref _configReloadQueued, 1);
                return;
            }

            _ = Task.Run(() =>
            {
                try
                {
                    var cfg = AppConfigStore.Load();
                    Dispatcher.UIThread.Post(() => apply(cfg));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Config] Failed to reload for {context}: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _configReloadInFlight, 0);
                    if (Interlocked.Exchange(ref _configReloadQueued, 0) == 1)
                    {
                        QueueConfigReload(apply, context);
                    }
                }
            });
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
                    var cfg = AppConfigStore.Load();
                    var destinations = GetAllDestinations(cfg);
                    var allowToggle = cfg.Backups.UseAdvancedDestinations && cfg.Backups.Destinations is { Count: > 0 };
                    vm.ResetDestinationStatuses(destinations, allowToggle);

                    EnsureDestinationProbeStarted();
                    _ = Task.Run(ProbeDestinationsAsync);

                    var summaries = GetDestinationProbeSummaries(cfg);
                    foreach (var summary in summaries)
                    {
                        var severity = summary.Reachable ? "Success" : "Error";
                        vm.UpdateDestinationStatus(summary.Id, summary.Message, severity);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Destinations] Failed to refresh overview: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _destinationOverviewRefreshInFlight, 0);
                }
            });
        }

        public void NotifySoftCrashBanner(string? logPath)
        {
            SoftCrashBannerMessage = L(
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
                var snippet = _logConsoleService.GetRecentSnippet(30, contextLabel);
                if (string.IsNullOrWhiteSpace(snippet))
                    return;

                await ClipboardHelper.TryCopyAsync(snippet);
            });
        }

        private void EnforceRetentionOnStartup()
        {
            try
            {
                var cfg = _config;
                var maxToKeep = cfg.Backups.MaxSnapshotsPerProject;
                if (maxToKeep <= 0)
                    return;

                var backupRoot = cfg.Backups.BackupLocation ?? string.Empty;
                var projects = _repo.GetAllProjects().ToList();
                foreach (var project in projects)
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

            var machineId = Environment.MachineName;
            _ = Task.Run(() =>
            {
                try
                {
                    var cfg = AppConfigStore.Load();
                    if (!cfg.Backups.EnableMetadataSync)
                        return;

                    Console.WriteLine($"[MetadataSync] Export tombstone for backup {backup.Id} -> '{backup.DestinationPath}'.");
                    _metadataSyncService.ExportBackupTombstoneToStore(
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
                var cfg = _config;
                var destinations = GetActiveDestinations(cfg);
                if (destinations.Count == 0)
                    return;

                foreach (var dest in destinations)
                {
                    var profile = string.IsNullOrWhiteSpace(dest.CredentialName)
                        ? null
                        : cfg.Network.Credentials.FirstOrDefault(c =>
                            c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));

                    var resolution = _networkMountService.PrepareDestination(dest, profile);
                    if (!resolution.IsSuccess || string.IsNullOrWhiteSpace(resolution.EffectivePath))
                        continue;

                    var projects = _repo.GetAllProjects();
                    var projectFolders = projects
                        .Select(p => BackupService.GetProjectBackupFolderName(p.Name))
                        .ToList();
                    var removed = _backupService.CleanupIncompleteBackups(resolution.EffectivePath, projectFolders);
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
                Interlocked.Exchange(ref _reloadBackupsQueued, 1);
                return Task.CompletedTask;
            }

            // Fetch and materialize data off the UI thread to reduce perceived hangs,
            // then marshal the lightweight ViewModel update back to the UI thread.
            return Task.Run(() =>
            {
                try
                {
                    _repo.EnsureSchema();
                    var onBackupsPage = IsOnBackupsPage;
                    var now = DateTime.UtcNow;
                    var cacheFresh = _backupsCacheProjects is not null
                        && _backupsCacheBackups is not null
                        && (now - _backupsCacheUpdatedUtc) < BackupsCacheTtl;

                    if (!force && !onBackupsPage && cacheFresh)
                    {
                        return;
                    }

                    var projects = _repo.GetAllProjects().ToList();
                    var useLightweight = !force && !onBackupsPage;
                    var backups = useLightweight
                        ? _repo.GetRecentBackupsByProject(limitPerProject: 5).ToList()
                        : _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow).ToList();

                    var disabledAuto = _config.Backups.AutoBackupDisabledProjects?.ToHashSet() ?? new HashSet<int>();

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

                    if (onBackupsPage || force)
                    {
                        if (backups.Count > 0)
                        {
                            var scanAdded = ScanDestinationsForUntrackedBackups(projects, backups);
                            if (scanAdded > 0)
                            {
                                backups = _repo.GetBackupsInRange(DateTime.MinValue, DateTime.UtcNow).ToList();
                                useLightweight = false;
                                _backupsCacheBackups = backups;
                                _backupsCachePartial = useLightweight;
                                _backupsCacheUpdatedUtc = DateTime.UtcNow;

                                Dispatcher.UIThread.Post(() =>
                                {
                                    if (onBackupsPage || force)
                                    {
                                        BackupsViewModel.LoadFromBackups(projects, backups, disabledAuto);
                                        BackupsViewModel.RefreshBackupDriveHealth();
                                    }
                                });
                            }
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _reloadBackupsInFlight, 0);
                    if (Interlocked.Exchange(ref _reloadBackupsQueued, 0) == 1)
                    {
                        ReloadBackupsVmData();
                    }
                }
            });
        }

        private BackupProjectPreparation CreateManualBackupPreparation(int projectId)
        {
            var cfg = _config;
            var project = _repo.GetProjectById(projectId);
            var selection = project is null
                ? new ProjectDestinationSelection(GetActiveDestinations(cfg), null, null)
                : ResolveDestinationsForProject(project, cfg);
            return new BackupProjectPreparation(cfg, selection.Destinations, project, selection.WarningMessage, selection.WarningCode);
        }

        private int ScanDestinationsForUntrackedBackups(List<Project> projects, List<Backup> backups)
        {
            if (Interlocked.Exchange(ref _destinationScanInFlight, 1) == 1)
                return 0;

            try
            {
                var now = DateTime.UtcNow;
                if ((now - _lastDestinationScanUtc) < DestinationScanInterval)
                    return 0;

                _lastDestinationScanUtc = now;

                var cfg = _config;
                var destinations = GetActiveDestinations(cfg);
                if (destinations.Count == 0)
                    return 0;

                var projectBySlug = projects.ToDictionary(
                    p => BackupService.GetProjectBackupFolderName(p.Name),
                    p => p,
                    StringComparer.OrdinalIgnoreCase);

                var existingKeys = BuildExistingBackupKeys(backups);
                var added = 0;

                foreach (var dest in destinations)
                {
                    var profile = string.IsNullOrWhiteSpace(dest.CredentialName)
                        ? null
                        : cfg.Network.Credentials.FirstOrDefault(c =>
                            c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));

                    var resolution = _networkMountService.PrepareDestination(dest, profile);
                    if (!resolution.IsSuccess || string.IsNullOrWhiteSpace(resolution.EffectivePath))
                        continue;

                    var destRoot = resolution.EffectivePath;

                    foreach (var projectEntry in projectBySlug)
                    {
                        var projectFolder = Path.Combine(destRoot, projectEntry.Key);
                        if (!Directory.Exists(projectFolder))
                            continue;

                        foreach (var backupFolder in SafeEnumerateDirectories(projectFolder))
                        {
                            var folderName = Path.GetFileName(backupFolder);
                            if (!TryParseBackupTimestamp(folderName, out var createdUtc))
                                continue;

                            if (!IsBackupFolderComplete(backupFolder))
                                continue;

                            var relativePath = Path.GetRelativePath(destRoot, backupFolder);
                            var key = BuildBackupKey(dest, relativePath);
                            if (existingKeys.Contains(key))
                                continue;

                            var sizeBytes = TryGetArchiveSize(backupFolder);
                            var snapshotId = _repo.CreateSnapshotFromMetadata(
                                string.Empty,
                                projectEntry.Value.Id,
                                createdUtc,
                                0,
                                sizeBytes);

                            var isProtected = IsBackupProtectedOnDisk(backupFolder);
                            var isEncrypted = false;
                            var cryptoDescriptorJson = BackupCryptoDescriptor.PlainMetadataJson;
                            if (BackupArchiveCryptoService.TryReadDescriptor(backupFolder, out var descriptor, out var encrypted))
                            {
                                isEncrypted = encrypted;
                                cryptoDescriptorJson = descriptor.ToMetadataJson(encrypted);
                            }
                            _repo.CreateBackupFromMetadata(
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

                            existingKeys.Add(key);
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
                Interlocked.Exchange(ref _destinationScanInFlight, 0);
            }
        }

        private static HashSet<string> BuildExistingBackupKeys(IEnumerable<Backup> backups)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var backup in backups)
            {
                if (string.IsNullOrWhiteSpace(backup.Path))
                    continue;

                if (!string.IsNullOrWhiteSpace(backup.DestinationAlias))
                {
                    keys.Add($"{backup.DestinationAlias}|{backup.Path}");
                }

                if (!string.IsNullOrWhiteSpace(backup.DestinationPath))
                {
                    keys.Add($"{backup.DestinationPath}|{backup.Path}");
                }
            }

            return keys;
        }

        private static string BuildBackupKey(BackupDestination dest, string relativePath)
        {
            var key = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path ?? string.Empty : dest.Alias;
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
                return Array.Empty<string>();
            }
        }

        private static bool IsBackupFolderComplete(string backupFolder)
        {
            var inProgress = Path.Combine(backupFolder, ".vaultsync_inprogress");
            if (File.Exists(inProgress))
                return false;

            var completed = Path.Combine(backupFolder, ".vaultsync_complete");
            var archive = Path.Combine(backupFolder, BackupArchiveCryptoService.PlainArchiveFileName);
            var encryptedArchive = Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
            if (File.Exists(completed) || File.Exists(archive) || File.Exists(encryptedArchive))
                return true;

            try
            {
                return Directory.EnumerateFileSystemEntries(backupFolder)
                    .Any(entry =>
                    {
                        var name = Path.GetFileName(entry);
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
                var marker = Path.Combine(backupFolder, BackupProtectionMarkerFileName);
                if (File.Exists(marker))
                    return true;
            }
            catch
            {
                return true;
            }

            return !TryWriteProbeFile(backupFolder);
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

            var intervalMinutes = _config.Backups.IntervalMinutes;
            if (!_config.Backups.EnableAutoBackups || intervalMinutes <= 0)
                return;

            var interval = TimeSpan.FromMinutes(intervalMinutes);

            // Use a wrapper to avoid unobserved exceptions from the async timer callback crashing the process.
            _autoBackupTimer = new Timer(
                _ => _ = SafeRunAutoBackupsAsync(),
                null,
                interval,
                interval);
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
                return;

            try
            {
                if (BackupsViewModel.IsBusy)
                {
                    Telemetry.Log("auto_backup_skipped", b => b
                        .WithCode("reason", "busy"));
                    return;
                }

                if (ShouldPauseBackupsForBattery(out var pauseReason))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        BackupsViewModel.BackupCurrentFile = pauseReason;
                        BackupsViewModel.BusyMessage = pauseReason;
                    });
                    Telemetry.Log("auto_backup_skipped", b => b
                        .WithCode("reason", "battery"));
                    return;
                }

                var preparation = await Task.Run(PrepareAutoBackupRun);
                if (!preparation.IsReady)
                {
                    Telemetry.Log("auto_backup_skipped", b => b
                        .WithCode("reason", preparation.FailureCode ?? "preflight_failed"));
                    return;
                }

                var cfg = preparation.Config;
                var disabled = preparation.DisabledProjects;
                var projects = preparation.Projects;

                var useArchiveMode = _settingsViewModel.UseBackupCompression;
                var backupAttempts = 0;
                var backupSucceeded = 0;
                var backupFailed = 0;
                var destinationUnreachable = 0;

                var maxParallel = Math.Max(1, Environment.ProcessorCount);
                using var throttler = new SemaphoreSlim(maxParallel);

                var tasks = projects
                        .Where(p => !disabled.Contains(p.Id))
                        .Select(async project =>
                        {
                            await throttler.WaitAsync();
                            try
                            {
                                var selection = ResolveDestinationsForProject(project, cfg);
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

                                if (!TryResolveProjectRoot(project, cfg, out var resolvedProject, out var rootError))
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
                                foreach (var dest in selection.Destinations)
                                {
                                    var resolution = PrepareDestination(dest, cfg);
                                    if (!resolution.IsSuccess)
                                    {
                                        Interlocked.Increment(ref destinationUnreachable);
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
                                    foreach (var (dest, resolution) in destinationResolutions)
                                    {
                                        var driveDecision = await EvaluateDriveHealthAsync(project.RootPath, resolution.EffectivePath);
                                        if (!string.IsNullOrWhiteSpace(driveDecision.Message))
                                        {
                                            ShowDriveHealthNotification(driveDecision.Message, driveDecision.Severity);
                                        }
                                        if (driveDecision.Block)
                                        {
                                            continue;
                                        }

                                        var destLabel = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path;

                                        try
                                        {
                                            var archiveUploadBufferBytes = await EnsureArchiveUploadBufferAsync(
                                                dest,
                                                cfg,
                                                resolution.EffectivePath,
                                                useArchiveMode,
                                                CancellationToken.None);
                                            Interlocked.Increment(ref backupAttempts);
                                            var isRemoteDestination = IsRemoteDestinationPath(resolution.EffectivePath)
                                                || IsRemoteDestinationPath(dest.Path);
                                            var allowParallelUpload = cfg.Backups.EnableParallelArchiveUpload;
                                            var preferParallelUpload = allowParallelUpload && isRemoteDestination;
                                            if (!allowParallelUpload)
                                            {
                                                Console.WriteLine($"[BackupService] Parallel archive upload disabled by user settings for '{destLabel}'.");
                                            }
                                            var sw = Stopwatch.StartNew();
                                            var backupResult = await _backupService.RunBackupAsync(
                                                project,
                                                resolution.EffectivePath,
                                                isAuto: true,
                                                progressCallback: null,
                                                CancellationToken.None,
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
                                                aggressiveScanCache: _settingsViewModel.AggressiveScanCache);
                                            sw.Stop();

                                            if (backupResult.SkippedForNoChanges)
                                            {
                                                Telemetry.Log("auto_backup_skipped", b => b
                                                    .WithCode("reason", "no_changes")
                                                    .WithHashedString("project", project.Name)
                                                    .WithHashedString("destinationPath", dest.Path ?? string.Empty));
                                                // Skip the remaining destinations for this project to avoid redundant work.
                                                break;
                                            }

                                            if (!metadataWritten && backupResult.BackupId > 0)
                                            {
                                                metadataWritten = true;
                                                if (!sharedSnapshotId.HasValue)
                                                {
                                                    var created = _repo.GetBackupById(backupResult.BackupId);
                                                    sharedSnapshotId = created?.SnapshotId ?? sharedSnapshotId;
                                                }
                                            }

                                            if (backupResult.BackupId > 0)
                                            {
                                                Interlocked.Increment(ref backupSucceeded);
                                                RecordBackupThroughput(backupResult.BackupId, sw.Elapsed, useArchiveMode);
                                                TryExportMetadataForBackup(cfg, dest, resolution.EffectivePath, backupResult.BackupId);
                                            }
                                        }
                                        catch (Exception ex)
                                        {
                                            Interlocked.Increment(ref backupFailed);
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
                                    foreach (var (_, resolution) in destinationResolutions)
                                    {
                                        _networkMountService.Cleanup(resolution);
                                    }
                                }

                                if (metadataWritten && sharedSnapshotId.HasValue)
                                {
                                    StartPostBackupHashingAsync(project, sharedSnapshotId.Value);
                                }
                            }
                            finally
                            {
                                throttler.Release();
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
                    .WithCount("destinations", GetAllDestinations(cfg).Count)
                    .WithCount("attempts", backupAttempts)
                    .WithCount("succeeded", backupSucceeded)
                    .WithCount("failed", backupFailed)
                    .WithCount("destinationsUnreachable", destinationUnreachable)
                    .WithFlag("useArchiveMode", useArchiveMode)
                    .WithNumber("intervalMinutes", cfg.Backups.IntervalMinutes));
            }
            finally
            {
                Interlocked.Exchange(ref _autoBackupInFlight, 0);
            }
        }

        private void OnAutoBackupPreferenceChanged(int projectId, bool enabled)
        {
            Task.Run(() =>
            {
                try
                {
                    var cfg = AppConfigStore.Load();
                    var list = cfg.Backups.AutoBackupDisabledProjects ?? new List<int>();
                    if (!enabled)
                    {
                        if (!list.Contains(projectId))
                            list.Add(projectId);
                    }
                    else
                    {
                        list.Remove(projectId);
                    }

                    cfg.Backups.AutoBackupDisabledProjects = list;
                    AppConfigStore.Save(cfg);

                    Dispatcher.UIThread.Post(() =>
                    {
                        _config.Backups.AutoBackupDisabledProjects = list;
                        ConfigureAutoBackupTimer();
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AutoBackup] Failed to update preference: {ex.Message}");
                }
            });
        }

        private async void OnPreferredDestinationChanged(int projectId, string preferredDestinationId)
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

        private async void OnProjectEncryptionPolicyChanged(int projectId, string encryptionPolicy)
        {
            try
            {
                var project = _repo.GetProjectById(projectId);
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

        private void OnProjectEncryptionRequestedFromBackups(int projectId)
        {
            _ = _projectEncryptionEnrollmentService.EditProjectEncryptionSecretAsync(projectId);
        }

        private void OnProjectEncryptionRequestedFromProjects(ProjectItemViewModel? project)
        {
            if (project is null)
                return;

            var projectId = project.ProjectId;
            if (projectId <= 0)
            {
                var dbProject = _repo.GetProjectByName(project.Name);
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
            Task.Run(() =>
            {
                try
                {
                    var cfg = AppConfigStore.Load();
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
                    var destEntry = cfg.Backups.Destinations
                        .FirstOrDefault(d => DestinationsMatch(d.Path, d.Alias, target));
                    if (destEntry is null || destEntry.Active == isActive)
                        return;

                    destEntry.Active = isActive;
                    AppConfigStore.Save(cfg);

                    Dispatcher.UIThread.Post(() =>
                    {
                        _config = cfg;
                        if (_settingsViewModel is not null)
                        {
                            var vmDest = _settingsViewModel.Destinations
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
            var propertyName = e.PropertyName ?? string.Empty;
            QueueConfigReload(cfg =>
            {
                _config = cfg;

                if (propertyName is nameof(SettingsViewModel.EnableAutoBackups)
                    or nameof(SettingsViewModel.AutoBackupIntervalMinutes))
                {
                    ConfigureAutoBackupTimer();
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
                    RefreshDestinationOptionSources();
                }

                RefreshDestinationStatusOverview();
            }, "settings-change");
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
            if (sender is not BackupDestinationViewModel)
                return;

            if (e.PropertyName is nameof(BackupDestinationViewModel.Alias)
                or nameof(BackupDestinationViewModel.Path)
                or nameof(BackupDestinationViewModel.Active))
            {
                RefreshDestinationOptionSources();
                RefreshDestinationStatusOverview();
            }
        }

        private void RefreshDestinationOptionSources()
        {
            QueueConfigReload(config =>
            {
                _projectsViewModel.RefreshDestinationOptions(config);
                BackupsViewModel.RefreshDestinationOptions(config);
            }, "destinations-options");
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

