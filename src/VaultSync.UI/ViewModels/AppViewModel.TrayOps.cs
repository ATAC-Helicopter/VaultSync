using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
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
        private void OnCancelActiveBackupRequested(BackupProgressItem? item)
        {
            if (item is null)
                return;

            if (!int.TryParse(item.ProjectId, out int projectId))
            {
                return;
            }

            // Actually cancel the running backup for this project.
            _backupCancelRequested[projectId] = 1;
            _backupService.CancelBackup(projectId);
            BackupsViewModel.UpdateActiveBackup(
                item.ProjectId,
                item.ProjectName,
                item.Progress,
                AppViewModel.L("Backups.Status.Cancelling", "Cancelling..."),
                string.Empty,
                allowCancel: false,
                policyText: item.PolicyText,
                activityPhase: ProtectionActivityPhase.Cancelling);
            Console.WriteLine($"[Backup] Cancel requested for projectId={projectId} ({item.ProjectName}).");
            Telemetry.Log("backup_cancel_requested", b => b
                .WithHashedString("projectId", item.ProjectId));

            // Do NOT remove the active backup card immediately.
            // Let the backup operation observe the cancellation token and finish,
            // then the existing completion logic (finally blocks / ReloadBackupsVmData)
            // will clear the cards and refresh the UI.
        }

        // ---------- Tray entry points ----------

        /// <summary>
        /// Triggered from the tray menu: backup all projects.
        /// Reuses the same logic as the Backups page \"backup all\" action.
        /// </summary>
        
        /// <summary>
        /// Returns the list of backup-capable projects for use in the tray menu.
        /// </summary>
        public IReadOnlyList<ProjectBackupItem> GetProjectsForBackupTray()
        {
            return BackupsViewModel.ProjectBackups.ToList();
        }

        /// <summary>
        /// Triggered from the tray menu: backup a specific project by its ProjectBackupItem.Id.
        /// This reuses the same pipeline as the Backups page per-project backup.
        /// </summary>
        public void RequestBackupProjectFromTray(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return;

            // Don't start if something is already running
            if (BackupsViewModel.IsBusy)
            {
                return;
            }

            ProjectBackupItem? projectItem = BackupsViewModel.ProjectBackups.FirstOrDefault(p => p.Id == projectId);
            if (projectItem == null)
            {
                return;
            }

            // When triggered from tray, navigate to the Backups page so the user
            // immediately sees the running backup card (when the window is shown).
            Dispatcher.UIThread.Post(() =>
            {
                if (NavigateBackups?.CanExecute(null) == true)
                {
                    NavigateBackups.Execute(null);
                }
            });

            _trayInitiatedBackup = true;

            OnBackupProjectRequested(projectItem);
        }

        public void RequestBackupAllFromTray()
        {
            // Do not start if something is already running.
            if (BackupsViewModel.IsBusy)
            {
                return;
            }

            // When triggered from tray, navigate to the Backups page so the user
            // immediately sees the running backup cards (when the window is shown).
            Dispatcher.UIThread.Post(() =>
            {
                if (NavigateBackups?.CanExecute(null) == true)
                {
                    NavigateBackups.Execute(null);
                }
            });

            _trayInitiatedBackup = true;

            OnCreateBackupForAllProjectsRequested();
        }

        /// <summary>
        /// Triggered from the tray menu: backup the selected project.
        /// Uses the current Backups-page project selection when available.
        /// Falls back to navigation only when nothing is selected yet.
        /// </summary>
        public void RequestBackupSelectedProjectFromTray()
        {
            ProjectBackupItem? selected = BackupsViewModel.SelectedProject;

            Dispatcher.UIThread.Post(() =>
            {
                if (NavigateBackups?.CanExecute(null) == true)
                {
                    NavigateBackups.Execute(null);
                }
            });

            if (selected is null || BackupsViewModel.IsBusy)
                return;

            _trayInitiatedBackup = true;
            OnBackupProjectRequested(selected);
        }

        /// <summary>
        /// Returns the list of projects used for snapshots (Projects page),
        /// for use in the tray's Snapshot submenu.
        /// Only returns projects that are actually added/tracked in VaultSync.
        /// Untracked/discovered entries normally have ProjectId <= 0 and should not appear in the tray.
        /// </summary>
        public IReadOnlyList<ProjectItemViewModel> GetProjectsForSnapshotTray()
        {
            // Only expose projects that are actually registered in the backup DB.
            return _projectsViewModel.Projects
                .Where(p => p.IsRegistered)
                .ToList();
        }

        /// <summary>
        /// Triggered from the tray menu: create a snapshot for a specific project by name.
        /// This reuses the ProjectsViewModel.TakeSnapshotForProjectFromTrayAsync pipeline,
        /// which in turn calls the existing TakeSnapshot() logic.
        /// </summary>
        public async Task TakeSnapshotForProjectFromTrayAsync(string projectName)
        {
            // When triggered from tray, navigate to the Projects page so the user
            // immediately sees the snapshot activity (when the window is shown).
            Dispatcher.UIThread.Post(() =>
            {
                if (NavigateProjects?.CanExecute(null) == true)
                {
                    NavigateProjects.Execute(null);
                }
            });

            await _projectsViewModel.TakeSnapshotForProjectFromTrayAsync(projectName);
        }

        /// <summary>
        /// Triggered from the tray menu: create snapshots for all projects.
        /// This reuses the ProjectsViewModel.TakeSnapshotAllFromTrayAsync pipeline,
        /// which in turn calls the existing TakeSnapshot() logic for each project.
        /// </summary>
        public async Task TakeSnapshotAllFromTrayAsync()
        {
            // When triggered from tray, navigate to the Projects page so the user
            // immediately sees the snapshot activity (when the window is shown).
            Dispatcher.UIThread.Post(() =>
            {
                if (NavigateProjects?.CanExecute(null) == true)
                {
                    NavigateProjects.Execute(null);
                }
            });

            await _projectsViewModel.TakeSnapshotAllFromTrayAsync();
        }

        private static string? TryResolveBackupPathForRead(string relativePath, IReadOnlyList<BackupDestination> destinations, string? legacyRoot)
        {
            foreach (BackupDestination? dest in destinations.OrderByDescending(d => d.Active))
            {
                if (string.IsNullOrWhiteSpace(dest.Path))
                    continue;

                if (!BackupSafetyService.TryCombinePathUnderRoot(dest.Path, relativePath, out string combined))
                    continue;
                if (Directory.Exists(combined) || File.Exists(combined))
                    return dest.Path;
            }

            if (!string.IsNullOrWhiteSpace(legacyRoot))
            {
                if (!BackupSafetyService.TryCombinePathUnderRoot(legacyRoot, relativePath, out string combined))
                    return null;
                if (Directory.Exists(combined) || File.Exists(combined))
                    return legacyRoot;
            }

            // fall back to first destination path even if not present, so caller can attempt/create
            BackupDestination? first = destinations.FirstOrDefault();
            return first?.Path ?? legacyRoot;
        }

        private static string? ResolveDestinationRootForBackup(Backup backup, IReadOnlyList<BackupDestination> destinations, string? legacyRoot)
        {
            if (!string.IsNullOrWhiteSpace(backup.Path))
            {
                foreach (BackupDestination? dest in destinations.Where(d => !string.IsNullOrWhiteSpace(d.Path)))
                {
                    if (!BackupSafetyService.TryCombinePathUnderRoot(dest.Path!, backup.Path, out string combined))
                        continue;
                    if (Directory.Exists(combined) || File.Exists(combined))
                        return dest.Path;
                }
            }

            if (!string.IsNullOrWhiteSpace(backup.DestinationPath))
                return backup.DestinationPath;

            if (!string.IsNullOrWhiteSpace(backup.DestinationAlias))
            {
                BackupDestination? match = destinations.FirstOrDefault(d =>
                    string.Equals(d.Alias ?? string.Empty, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.Path ?? string.Empty, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase));

                if (match is not null && !string.IsNullOrWhiteSpace(match.Path))
                {
                    if (BackupSafetyService.TryCombinePathUnderRoot(match.Path, backup.Path ?? string.Empty, out string combined) &&
                        (Directory.Exists(combined) || File.Exists(combined)))
                    {
                        return match.Path;
                    }
                }
            }

            return TryResolveBackupPathForRead(backup.Path ?? string.Empty, destinations, legacyRoot);
        }

        // ---------- Tray helpers: recent backups / keep / delete ----------

        public sealed record TrayBackupItem(int Id, int ProjectId, string Label, bool IsProtected);
        public sealed record TrayProjectBackups(int ProjectId, string ProjectName, IReadOnlyList<TrayBackupItem> Backups);
        public void OpenBackupFolderFromTray(int backupId)
        {
            OpenBackupFolder(backupId);
        }

        private void OnLockEncryptedOpenWorkspacesRequested()
        {
            AppViewModel.RunDetached(OnLockEncryptedOpenWorkspacesRequestedAsync, nameof(OnLockEncryptedOpenWorkspacesRequestedAsync));
        }

        private async Task OnLockEncryptedOpenWorkspacesRequestedAsync()
        {
            try
            {
                await LockEncryptedOpenWorkspacesNowAsync();
                BackupsViewModel.ShowNotification(
                    AppViewModel.L("Backups.OpenEncrypted.LockedNow", "Encrypted open folders were locked and cleaned up."),
                    "Info");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OpenEncrypted] Lock-now cleanup failed: {ex.Message}");
                BackupsViewModel.ShowNotification(
                    AppViewModel.L("Backups.OpenEncrypted.LockedNowFailed", "Failed to lock encrypted open folders."),
                    "Warning");
            }
        }

        private void OpenBackupFolder(int backupId)
        {
            AppViewModel.RunDetached(() => OpenBackupFolderAsync(backupId), nameof(OpenBackupFolderAsync));
        }

        private async Task OpenBackupFolderAsync(int backupId)
        {
            string? openCardId = $"open-{backupId}";
            string? extractedDirForCleanup = null;
            try
            {
                BackupFolderOpenPreparation preparation = await Task.Run(() => PrepareBackupFolderOpen(backupId));
                if (!preparation.IsReady || string.IsNullOrWhiteSpace(preparation.BackupFolder))
                {
                    BackupsViewModel.ShowNotification(
                        Lf("Backups.Notification.OpenFolderFailed", "Failed to open backup folder for '{0}'.", backupId.ToString(CultureInfo.CurrentCulture)),
                        "Error");
                    return;
                }

                if (preparation.IsEncrypted)
                {
                    BackupsViewModel.UpdateActiveBackup(
                        openCardId,
                        preparation.ProjectName,
                        0,
                        AppViewModel.L("Backups.OpenEncrypted.Opening", "Opening encrypted backup..."),
                        string.Empty,
                        allowCancel: false);

                    string? extractedDir = await OpenEncryptedBackupFolderAsync(preparation, openCardId);
                    if (string.IsNullOrWhiteSpace(extractedDir))
                        return;

                    extractedDirForCleanup = extractedDir;
                    BackupsViewModel.UpdateActiveBackup(
                        openCardId,
                        preparation.ProjectName,
                        100,
                        AppViewModel.L("Backups.OpenEncrypted.Ready", "Open complete"),
                        AppViewModel.L("Backups.OpenEncrypted.ReadyEta", "Decrypted content is ready."),
                        allowCancel: false);

                    // Schedule cleanup before shell-open so temp decrypted data is never left unscheduled.
                    ScheduleEncryptedOpenCleanup(extractedDir);
                    SystemFileLauncher.OpenPath(extractedDir);
                    return;
                }

                SystemFileLauncher.OpenPath(preparation.BackupFolder);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OpenFolder] Failed to open backup folder for id={backupId}: {ex.Message}");
                BackupsViewModel.ShowNotification(
                    Lf("Backups.Notification.OpenFolderFailed", "Failed to open backup folder for '{0}'.", backupId.ToString(CultureInfo.CurrentCulture)),
                    "Error");
                if (!string.IsNullOrWhiteSpace(extractedDirForCleanup))
                {
                    ScheduleEncryptedOpenCleanup(extractedDirForCleanup);
                }
            }
            finally
            {
                if (openCardId is not null)
                {
                    Dispatcher.UIThread.Post(() => BackupsViewModel.RemoveActiveBackup(openCardId));
                }
            }
        }

        private BackupFolderOpenPreparation PrepareBackupFolderOpen(int backupId)
        {
            Backup? backup = _repo.GetBackupById(backupId);
            if (backup is null)
                return BackupFolderOpenPreparation.Failure;

            AppConfig cfg = _configStore.GetSnapshot();
            List<BackupDestination> destinations = AppViewModel.GetAllDestinations(cfg);
            string? destinationRoot = ResolveDestinationRootForBackup(backup, destinations, cfg.Backups.BackupRoot);
            if (string.IsNullOrWhiteSpace(destinationRoot))
                return BackupFolderOpenPreparation.Failure;

            if (string.IsNullOrWhiteSpace(backup.Path))
                return BackupFolderOpenPreparation.Failure;

            if (!BackupSafetyService.TryCombinePathUnderRoot(destinationRoot, backup.Path, out string fullPath))
                return BackupFolderOpenPreparation.Failure;
            if (!Directory.Exists(fullPath))
                return BackupFolderOpenPreparation.Failure;

            string encryptedArchivePath = Path.Combine(fullPath, BackupArchiveCryptoService.EncryptedArchiveFileName);
            string projectName = _repo.GetProjectById(backup.ProjectId)?.Name ?? "backup";

            return new BackupFolderOpenPreparation(
                IsReady: true,
                BackupId: backup.Id,
                ProjectId: backup.ProjectId,
                ProjectName: projectName,
                BackupFolder: fullPath,
                IsEncrypted: backup.IsEncrypted || File.Exists(encryptedArchivePath));
        }

        private sealed record BackupFolderOpenPreparation(
            bool IsReady,
            int BackupId,
            int ProjectId,
            string ProjectName,
            string BackupFolder,
            bool IsEncrypted)
        {
            public static BackupFolderOpenPreparation Failure => new(false, 0, 0, string.Empty, string.Empty, false);
        }

        private async Task<string?> OpenEncryptedBackupFolderAsync(BackupFolderOpenPreparation preparation, string cardId)
        {
            CleanupStaleOpenBackupFolders();

            var attemptedPasswords = new HashSet<string>(StringComparer.Ordinal);
            var candidatePasswords = new Queue<string>(ResolveEncryptedRestorePasswordCandidates(preparation.ProjectId));
            if (TryGetEncryptedOpenSessionPassword(preparation.ProjectId, out string? cachedSessionPassword) &&
                !string.IsNullOrWhiteSpace(cachedSessionPassword))
            {
                candidatePasswords.Enqueue(cachedSessionPassword);
            }

            while (true)
            {
                if (candidatePasswords.Count == 0)
                {
                    (bool Confirmed, string Password) passwordPrompt = await ConfirmEncryptedRestorePasswordAsync(preparation.ProjectName);
                    if (!passwordPrompt.Confirmed)
                        return null;

                    if (string.IsNullOrWhiteSpace(passwordPrompt.Password))
                    {
                        BackupsViewModel.ShowNotification(
                            AppViewModel.L("Backups.Restore.EncryptedPasswordRequired", "A password is required to restore encrypted backups."),
                            "Warning");
                        continue;
                    }

                    candidatePasswords.Enqueue(passwordPrompt.Password);
                }

                string password = candidatePasswords.Dequeue();
                if (!attemptedPasswords.Add(password))
                    continue;

                try
                {
                    string extractedPath = await Task.Run(() => ExtractEncryptedBackupForOpen(preparation.BackupFolder, password, (percent, status, eta) =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            BackupsViewModel.UpdateActiveBackup(
                                cardId,
                                preparation.ProjectName,
                                percent,
                                status,
                                eta,
                                allowCancel: false);
                        });
                    }));

                    SetEncryptedOpenSession(preparation.ProjectId, password);
                    return extractedPath;
                }
                catch (Exception ex) when (IsEncryptedRestorePasswordError(ex))
                {
                    InvalidateEncryptedOpenSession(preparation.ProjectId, password);
                    BackupsViewModel.ShowNotification(
                        AppViewModel.L("Backups.Status.RestoreWrongPassword", "Restore failed: invalid password or encrypted backup is corrupted."),
                        "Warning");
                }
                catch (Exception ex)
                {
                    BackupsViewModel.ShowNotification(
                        Lf("Backups.Notification.OpenFolderFailed", "Failed to open backup folder for '{0}'.", preparation.ProjectName),
                        "Error");
                    Console.WriteLine($"[OpenEncrypted] Failed to open backup {preparation.BackupId}: {ex.Message}");
                    return null;
                }
            }
        }

        private static string ExtractEncryptedBackupForOpen(string backupFolder, string password, Action<double, string, string>? progress)
        {
            string encryptedArchivePath = Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
            if (!File.Exists(encryptedArchivePath))
                throw new FileNotFoundException("Encrypted archive not found.", encryptedArchivePath);

            string stagingRoot = Path.Combine(Path.GetTempPath(), $"vaultsync-open-{Guid.NewGuid():N}");
            string stagingArchive = Path.Combine(stagingRoot, BackupArchiveCryptoService.PlainArchiveFileName);
            string extractDir = Path.Combine(stagingRoot, "content");

            try
            {
                EncryptedOpenWorkspaceManager.RegisterOwnedWorkspace(stagingRoot);
                Directory.CreateDirectory(extractDir);
                progress?.Invoke(10, "Decrypting archive...", string.Empty);

                BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, password, stagingArchive);
                progress?.Invoke(40, "Decrypting archive...", string.Empty);

                using ZipArchive archive = ZipFile.OpenRead(stagingArchive);
                int totalEntries = archive.Entries.Count;
                int processed = 0;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string destinationPath = SafeZipExtractor.GetSafeEntryPath(extractDir, entry.FullName);
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destinationPath);
                    }
                    else
                    {
                        string? parentDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(parentDir))
                            Directory.CreateDirectory(parentDir);

                        entry.ExtractToFile(destinationPath, overwrite: true);
                    }

                    processed++;
                    double extractPercent = totalEntries == 0 ? 100d : (processed * 100d / totalEntries);
                    double mappedPercent = 40d + (extractPercent * 0.6d);
                    string fileLabel = string.IsNullOrWhiteSpace(entry.FullName) ? "Extracting..." : $"Extracting {entry.FullName}";
                    string etaLabel = totalEntries == 0
                        ? string.Empty
                        : $"Extracting {processed}/{totalEntries}";
                    progress?.Invoke(mappedPercent, fileLabel, etaLabel);
                }

                return extractDir;
            }
            catch
            {
                try
                {
                    if (Directory.Exists(stagingRoot))
                        Directory.Delete(stagingRoot, recursive: true);
                    EncryptedOpenWorkspaceManager.ForgetOwnedWorkspace(stagingRoot);
                }
                catch
                {
                    // best effort cleanup
                }

                throw;
            }
        }

        private static void CleanupStaleOpenBackupFolders()
        {
            try
            {
                EncryptedOpenWorkspaceManager.CleanupStaleWorkspaces(
                    Path.GetTempPath(),
                    DateTime.UtcNow,
                    EncryptedOpenStaleRetention);
            }
            catch
            {
                // best effort cleanup
            }
        }

        private void ScheduleEncryptedOpenCleanup(string extractedDir)
        {
            string? stagingRoot = ResolveEncryptedOpenStagingRoot(extractedDir);
            if (string.IsNullOrWhiteSpace(stagingRoot))
                return;
            TimeSpan cleanupDelay = GetEncryptedOpenTimeout();

            var cts = new CancellationTokenSource();
            _encryptedOpenCleanup.AddOrUpdate(stagingRoot, cts, (_, old) =>
            {
                old.Cancel();
                return cts;
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(cleanupDelay, cts.Token).ConfigureAwait(false);
                    await TryDeleteEncryptedOpenStagingRootAsync(stagingRoot, cts.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    // replaced by a newer schedule
                }
                catch
                {
                    // best effort cleanup
                }
                finally
                {
                    ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_encryptedOpenCleanup)
                        .Remove(new KeyValuePair<string, CancellationTokenSource>(stagingRoot, cts));
                    cts.Dispose();
                }
            }, CancellationToken.None);
        }

        private static string? ResolveEncryptedOpenStagingRoot(string extractedDir)
            => EncryptedOpenWorkspaceManager.ResolveWorkspaceRoot(extractedDir);

        private static async Task TryDeleteEncryptedOpenStagingRootAsync(string stagingRoot, CancellationToken ct)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!Directory.Exists(stagingRoot))
                        return;
                    Directory.Delete(stagingRoot, recursive: true);
                    EncryptedOpenWorkspaceManager.ForgetOwnedWorkspace(stagingRoot);
                    return;
                }
                catch
                {
                    if (attempt == 7)
                        return;
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                }
            }
        }

        private TimeSpan GetEncryptedOpenTimeout()
        {
            int minutes;
            try
            {
                int configured = _config?.Backups?.Encryption?.OpenUnlockTimeoutMinutes ?? DefaultEncryptedOpenTimeoutMinutes;
                minutes = Math.Clamp(configured, 1, 240);
            }
            catch
            {
                minutes = DefaultEncryptedOpenTimeoutMinutes;
            }

            return TimeSpan.FromMinutes(minutes);
        }

        private bool TryGetEncryptedOpenSessionPassword(int projectId, out string password)
        {
            password = string.Empty;
            if (!_encryptedOpenSessions.TryGetValue(projectId, out EncryptedOpenUnlockSession? session))
                return false;

            if (session.ExpiresUtc <= DateTime.UtcNow)
            {
                _encryptedOpenSessions.TryRemove(projectId, out _);
                return false;
            }

            password = session.Password;
            return !string.IsNullOrWhiteSpace(password);
        }

        private void SetEncryptedOpenSession(int projectId, string password)
        {
            if (string.IsNullOrWhiteSpace(password))
                return;

            _encryptedOpenSessions[projectId] = new EncryptedOpenUnlockSession(
                password,
                DateTime.UtcNow.Add(GetEncryptedOpenTimeout()));
        }

        private void InvalidateEncryptedOpenSession(int projectId, string attemptedPassword)
        {
            if (!_encryptedOpenSessions.TryGetValue(projectId, out EncryptedOpenUnlockSession? session))
                return;

            if (string.Equals(session.Password, attemptedPassword, StringComparison.Ordinal))
            {
                _encryptedOpenSessions.TryRemove(projectId, out _);
            }
        }

        private async Task LockEncryptedOpenWorkspacesNowAsync()
        {
            _encryptedOpenSessions.Clear();

            foreach (KeyValuePair<string, CancellationTokenSource> entry in _encryptedOpenCleanup.ToArray())
            {
                bool removed = ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_encryptedOpenCleanup)
                    .Remove(entry);
                if (removed)
                    await entry.Value.CancelAsync().ConfigureAwait(false);
            }

            await CleanupAllOpenBackupFoldersAsync().ConfigureAwait(false);
        }

        private static async Task CleanupAllOpenBackupFoldersAsync()
        {
            try
            {
                foreach (string dir in EncryptedOpenWorkspaceManager.GetOwnedWorkspacePaths())
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await TryDeleteEncryptedOpenStagingRootAsync(dir, cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }

        private sealed record EncryptedOpenUnlockSession(string Password, DateTime ExpiresUtc);

        private void UpdateBackupProtectionMarker(int backupId, bool isProtected)
        {
            Backup? selectedBackup = _repo.GetBackupById(backupId);
            if (selectedBackup is null)
                return;

            IEnumerable<Backup> matchingBackups = _repo.GetBackupsForProject(selectedBackup.ProjectId)
                .Where(backup => backup.SnapshotId == selectedBackup.SnapshotId);
            foreach (Backup backup in matchingBackups)
            {
                try
                {
                    string fullPath = PrepareBackupFolderOpen(backup.Id).BackupFolder;
                    if (string.IsNullOrWhiteSpace(fullPath))
                        continue;

                    string markerPath = Path.Combine(fullPath, BackupProtectionMarkerFileName);
                    if (isProtected)
                    {
                        File.WriteAllText(markerPath, $"keep:{DateTime.UtcNow:O}");
                    }
                    else if (File.Exists(markerPath))
                    {
                        File.Delete(markerPath);
                    }
                }
                catch
                {
                    // Marker files are best-effort; repository state remains authoritative.
                }
            }
        }

        public void ShowBackupInAppFromTray(int projectId)
        {
            try
            {
                ReloadBackupsVmData();
                ProjectBackupItem? projectItem = BackupsViewModel.ProjectBackups
                    .FirstOrDefault(p => int.TryParse(p.Id, out int pid) && pid == projectId);
                if (projectItem is not null)
                {
                    BackupsViewModel.SelectedProject = projectItem;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    SetCurrentView("Backups");
                });
            }
            catch
            {
                // ignore tray errors
            }
        }

        public IReadOnlyList<TrayProjectBackups> GetRecentBackupsForTray(int maxPerProject = 5)
        {
            try
            {
                var projects = _repo.GetAllProjects().ToList();
                var result   = new List<TrayProjectBackups>();
                IReadOnlyList<Backup> recent = _repo.GetRecentBackupsByProject(maxPerProject);
                var grouped = recent
                    .GroupBy(b => b.ProjectId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (Project? project in projects)
                {
                    grouped.TryGetValue(project.Id, out List<Backup>? projectBackups);
                    var backups = (projectBackups ?? [])
                        .OrderByDescending(b => b.CreatedUtc)
                        .Select(b =>
                        {
                            string ts   = b.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                            string keep = b.IsProtected ? AppViewModel.L("Tray.Recent.KeptSuffix", " * Keep") : string.Empty;
                            string label = $"{ts}{keep}";
                            return new TrayBackupItem(b.Id, project.Id, label, b.IsProtected);
                        })
                        .ToList();

                    result.Add(new TrayProjectBackups(project.Id, project.Name, backups));
                }

                return result;
            }
            catch
            {
                return [];
            }
        }

        public void ToggleBackupProtectionFromTray(int backupId)
        {
            try
            {
                Backup? backup = _repo.GetBackupById(backupId);
                if (backup is null)
                    return;

                bool newValue = !backup.IsProtected;
                _repo.SetBackupProtection(backupId, newValue);
                UpdateBackupProtectionMarker(backupId, newValue);
                BackupsViewModel.MarkSnapshotProtection(backup.SnapshotId, newValue);
                TrayMenuRefreshRequested?.Invoke();
            }
            catch
            {
                // Swallow for tray; avoid surfacing errors in the OS menu context.
            }
        }

        public void DeleteBackupFromTray(int backupId)
        {
            try
            {
                if (ShouldShowBackupWidget)
                {
                    _backupWidgetService?.ShowForTrayBackup();
                }

                var snapshot = new BackupSnapshotItem
                {
                    Id = backupId.ToString()
                };
                OnDeleteBackupRequested(snapshot);
            }
            catch
            {
                // Ignore tray errors to avoid blocking menu actions.
            }
            finally
            {
                TrayMenuRefreshRequested?.Invoke();
            }
        }

    }
}
