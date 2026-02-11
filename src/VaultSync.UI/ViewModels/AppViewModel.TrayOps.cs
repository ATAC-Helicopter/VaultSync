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

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private void OnCancelActiveBackupRequested(BackupProgressItem? item)
        {
            if (item is null)
                return;

            if (!int.TryParse(item.ProjectId, out var projectId))
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
                L("Backups.Status.Cancelling", "Cancelling..."),
                string.Empty,
                allowCancel: false);
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

            var projectItem = BackupsViewModel.ProjectBackups.FirstOrDefault(p => p.Id == projectId);
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
        /// For now we simply navigate to the Backups page so the user can pick a project
        /// and start the backup from there. Later we can wire this to the actual selection.
        /// </summary>
        public void RequestBackupSelectedProjectFromTray()
        {

            // For now, just bring the Backups page into view.
            if (NavigateBackups?.CanExecute(null) == true)
            {
                NavigateBackups.Execute(null);
            }

            // TODO (later): once BackupsViewModel exposes the currently selected project,
            // call OnBackupProjectRequested with that item to start the backup directly.
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
            foreach (var dest in destinations.OrderByDescending(d => d.Active))
            {
                if (string.IsNullOrWhiteSpace(dest.Path))
                    continue;

                var combined = Path.GetFullPath(Path.Combine(dest.Path, relativePath));
                if (Directory.Exists(combined) || File.Exists(combined))
                    return dest.Path;
            }

            if (!string.IsNullOrWhiteSpace(legacyRoot))
            {
                var combined = Path.GetFullPath(Path.Combine(legacyRoot, relativePath));
                if (Directory.Exists(combined) || File.Exists(combined))
                    return legacyRoot;
            }

            // fall back to first destination path even if not present, so caller can attempt/create
            var first = destinations.FirstOrDefault();
            return first?.Path ?? legacyRoot;
        }

        private static string? ResolveDestinationRootForBackup(Backup backup, IReadOnlyList<BackupDestination> destinations, string? legacyRoot)
        {
            if (!string.IsNullOrWhiteSpace(backup.Path))
            {
                foreach (var dest in destinations.Where(d => !string.IsNullOrWhiteSpace(d.Path)))
                {
                    var combined = Path.GetFullPath(Path.Combine(dest.Path!, backup.Path));
                    if (Directory.Exists(combined) || File.Exists(combined))
                        return dest.Path;
                }
            }

            if (!string.IsNullOrWhiteSpace(backup.DestinationPath))
                return backup.DestinationPath;

            if (!string.IsNullOrWhiteSpace(backup.DestinationAlias))
            {
                var match = destinations.FirstOrDefault(d =>
                    string.Equals(d.Alias ?? string.Empty, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.Path ?? string.Empty, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase));

                if (match is not null && !string.IsNullOrWhiteSpace(match.Path))
                {
                    var combined = Path.GetFullPath(Path.Combine(match.Path, backup.Path ?? string.Empty));
                    if (Directory.Exists(combined) || File.Exists(combined))
                        return match.Path;
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

        private async void OpenBackupFolder(int backupId)
        {
            var openCardId = $"open-{backupId}";
            string? extractedDirForCleanup = null;
            try
            {
                var preparation = await Task.Run(() => PrepareBackupFolderOpen(backupId));
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
                        L("Backups.OpenEncrypted.Opening", "Opening encrypted backup..."),
                        string.Empty,
                        allowCancel: false);

                    var extractedDir = await OpenEncryptedBackupFolderAsync(preparation, openCardId);
                    if (string.IsNullOrWhiteSpace(extractedDir))
                        return;

                    extractedDirForCleanup = extractedDir;
                    BackupsViewModel.UpdateActiveBackup(
                        openCardId,
                        preparation.ProjectName,
                        100,
                        L("Backups.OpenEncrypted.Ready", "Open complete"),
                        L("Backups.OpenEncrypted.ReadyEta", "Decrypted content is ready."),
                        allowCancel: false);

                    // Schedule cleanup before shell-open so temp decrypted data is never left unscheduled.
                    ScheduleEncryptedOpenCleanup(extractedDir);
                    OpenPathInSystemFileManager(extractedDir);
                    return;
                }

                OpenPathInSystemFileManager(preparation.BackupFolder);
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

        private void CleanupUnusedCredentialSecretsOnStartup()
        {
            try
            {
                var activeKeyRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var cfg = _config;
                if (!string.IsNullOrWhiteSpace(cfg.Backups.Encryption.KeyRef))
                    activeKeyRefs.Add(cfg.Backups.Encryption.KeyRef.Trim());

                if (cfg.Network.Credentials is { Count: > 0 })
                {
                    foreach (var cred in cfg.Network.Credentials)
                    {
                        if (!string.IsNullOrWhiteSpace(cred.KeyRef))
                            activeKeyRefs.Add(cred.KeyRef.Trim());
                    }
                }

                foreach (var project in _repo.GetAllProjects())
                {
                    if (!string.IsNullOrWhiteSpace(project.EncryptionKeyRef))
                        activeKeyRefs.Add(project.EncryptionKeyRef.Trim());
                }

                var removed = _credentialVault.CleanupUnusedSecrets(activeKeyRefs, TimeSpan.FromDays(30));
                if (removed > 0)
                {
                    Console.WriteLine($"[Security] Removed {removed} stale credential vault entries at startup.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Security] Credential vault cleanup failed: {ex.Message}");
            }
        }

        private BackupFolderOpenPreparation PrepareBackupFolderOpen(int backupId)
        {
            var backup = _repo.GetBackupById(backupId);
            if (backup is null)
                return BackupFolderOpenPreparation.Failure;

            var cfg = AppConfigStore.Load();
            var destinations = GetAllDestinations(cfg);
            var destinationRoot = ResolveDestinationRootForBackup(backup, destinations, cfg.Backups.BackupRoot);
            if (string.IsNullOrWhiteSpace(destinationRoot))
                return BackupFolderOpenPreparation.Failure;

            if (string.IsNullOrWhiteSpace(backup.Path))
                return BackupFolderOpenPreparation.Failure;

            var fullPath = Path.GetFullPath(Path.Combine(destinationRoot, backup.Path));
            if (!Directory.Exists(fullPath))
                return BackupFolderOpenPreparation.Failure;

            var encryptedArchivePath = Path.Combine(fullPath, BackupArchiveCryptoService.EncryptedArchiveFileName);
            var projectName = _repo.GetProjectById(backup.ProjectId)?.Name ?? "backup";

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

            while (true)
            {
                if (candidatePasswords.Count == 0)
                {
                    var passwordPrompt = await ConfirmEncryptedRestorePasswordAsync(preparation.ProjectName);
                    if (!passwordPrompt.Confirmed)
                        return null;

                    if (string.IsNullOrWhiteSpace(passwordPrompt.Password))
                    {
                        BackupsViewModel.ShowNotification(
                            L("Backups.Restore.EncryptedPasswordRequired", "A password is required to restore encrypted backups."),
                            "Warning");
                        continue;
                    }

                    candidatePasswords.Enqueue(passwordPrompt.Password);
                }

                var password = candidatePasswords.Dequeue();
                if (!attemptedPasswords.Add(password))
                    continue;

                try
                {
                    return await Task.Run(() => ExtractEncryptedBackupForOpen(preparation.BackupFolder, password, (percent, status, eta) =>
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
                }
                catch (Exception ex) when (IsEncryptedRestorePasswordError(ex))
                {
                    BackupsViewModel.ShowNotification(
                        L("Backups.Status.RestoreWrongPassword", "Restore failed: invalid password or encrypted backup is corrupted."),
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
            var encryptedArchivePath = Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
            if (!File.Exists(encryptedArchivePath))
                throw new FileNotFoundException("Encrypted archive not found.", encryptedArchivePath);

            var stagingRoot = Path.Combine(Path.GetTempPath(), $"vaultsync-open-{Guid.NewGuid():N}");
            var stagingArchive = Path.Combine(stagingRoot, BackupArchiveCryptoService.PlainArchiveFileName);
            var extractDir = Path.Combine(stagingRoot, "content");

            try
            {
                Directory.CreateDirectory(extractDir);
                progress?.Invoke(10, "Decrypting archive...", string.Empty);

                var cryptoService = new BackupArchiveCryptoService();
                cryptoService.DecryptArchiveToPlainZip(backupFolder, password, stagingArchive);
                progress?.Invoke(40, "Decrypting archive...", string.Empty);

                using var archive = ZipFile.OpenRead(stagingArchive);
                var totalEntries = archive.Entries.Count;
                var processed = 0;
                foreach (var entry in archive.Entries)
                {
                    var destinationPath = Path.Combine(extractDir, entry.FullName);
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destinationPath);
                    }
                    else
                    {
                        var parentDir = Path.GetDirectoryName(destinationPath);
                        if (!string.IsNullOrEmpty(parentDir))
                            Directory.CreateDirectory(parentDir);

                        entry.ExtractToFile(destinationPath, overwrite: true);
                    }

                    processed++;
                    var extractPercent = totalEntries == 0 ? 100d : (processed * 100d / totalEntries);
                    var mappedPercent = 40d + (extractPercent * 0.6d);
                    var fileLabel = string.IsNullOrWhiteSpace(entry.FullName) ? "Extracting..." : $"Extracting {entry.FullName}";
                    var etaLabel = totalEntries == 0
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
                var nowUtc = DateTime.UtcNow;
                foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "vaultsync-open-*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var createdUtc = Directory.GetCreationTimeUtc(dir);
                        var modifiedUtc = Directory.GetLastWriteTimeUtc(dir);
                        var referenceUtc = createdUtc > modifiedUtc ? createdUtc : modifiedUtc;
                        if ((nowUtc - referenceUtc) < EncryptedOpenStaleRetention)
                            continue;

                        Directory.Delete(dir, recursive: true);
                    }
                    catch
                    {
                        // best effort cleanup
                    }
                }
            }
            catch
            {
                // best effort cleanup
            }
        }

        private static void ScheduleEncryptedOpenCleanup(string extractedDir)
        {
            var stagingRoot = ResolveEncryptedOpenStagingRoot(extractedDir);
            if (string.IsNullOrWhiteSpace(stagingRoot))
                return;

            var cts = new CancellationTokenSource();
            var previous = _encryptedOpenCleanup.AddOrUpdate(stagingRoot, cts, (_, old) =>
            {
                try { old.Cancel(); } catch { }
                old.Dispose();
                return cts;
            });
            if (!ReferenceEquals(previous, cts))
            {
                // no-op, handled above
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(EncryptedOpenAutoCleanupDelay, cts.Token).ConfigureAwait(false);
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
                    if (_encryptedOpenCleanup.TryRemove(stagingRoot, out var token))
                    {
                        token.Dispose();
                    }
                }
            });
        }

        private static string? ResolveEncryptedOpenStagingRoot(string extractedDir)
        {
            if (string.IsNullOrWhiteSpace(extractedDir))
                return null;

            try
            {
                var full = Path.GetFullPath(extractedDir);
                var tempRoot = Path.GetFullPath(Path.GetTempPath());
                if (!full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                    return null;

                var current = new DirectoryInfo(full);
                while (current is not null)
                {
                    if (current.Name.StartsWith("vaultsync-open-", StringComparison.OrdinalIgnoreCase))
                        return current.FullName;
                    current = current.Parent;
                }
            }
            catch
            {
                // best effort path validation
            }

            return null;
        }

        private static async Task TryDeleteEncryptedOpenStagingRootAsync(string stagingRoot, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (!Directory.Exists(stagingRoot))
                        return;
                    Directory.Delete(stagingRoot, recursive: true);
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

        private static void OpenPathInSystemFileManager(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", path);
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", path);
                return;
            }

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }

        private void UpdateBackupProtectionMarker(int backupId, bool isProtected)
        {
            try
            {
                var fullPath = PrepareBackupFolderOpen(backupId).BackupFolder;
                if (string.IsNullOrWhiteSpace(fullPath))
                    return;

                var markerPath = Path.Combine(fullPath, BackupProtectionMarkerFileName);
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
                // best-effort marker update
            }
        }

        public void ShowBackupInAppFromTray(int projectId)
        {
            try
            {
                ReloadBackupsVmData();
                var projectItem = BackupsViewModel.ProjectBackups
                    .FirstOrDefault(p => int.TryParse(p.Id, out var pid) && pid == projectId);
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
                var projectsById = projects
                    .GroupBy(p => p.Id)
                    .ToDictionary(g => g.Key, g => g.First());
                var recent = _repo.GetRecentBackupsByProject(maxPerProject);
                var grouped = recent
                    .GroupBy(b => b.ProjectId)
                    .ToDictionary(g => g.Key, g => g.ToList());

                foreach (var project in projects)
                {
                    grouped.TryGetValue(project.Id, out var projectBackups);
                    var backups = (projectBackups ?? new List<Backup>())
                        .OrderByDescending(b => b.CreatedUtc)
                        .Select(b =>
                        {
                            var ts   = b.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                            var keep = b.IsProtected ? L("Tray.Recent.KeptSuffix", " * Keep") : string.Empty;
                            var label = $"{ts}{keep}";
                            return new TrayBackupItem(b.Id, project.Id, label, b.IsProtected);
                        })
                        .ToList();

                    result.Add(new TrayProjectBackups(project.Id, project.Name, backups));
                }

                return result;
            }
            catch
            {
                return Array.Empty<TrayProjectBackups>();
            }
        }

        public void ToggleBackupProtectionFromTray(int backupId)
        {
            try
            {
                var backup = _repo.GetBackupById(backupId);
                if (backup is null)
                    return;

                var newValue = !backup.IsProtected;
                _repo.SetBackupProtection(backupId, newValue);
                UpdateBackupProtectionMarker(backupId, newValue);
                BackupsViewModel.MarkBackupProtection(backupId, newValue);
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
