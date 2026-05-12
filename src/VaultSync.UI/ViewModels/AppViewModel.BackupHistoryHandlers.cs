using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Media;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.UI.Notifications;
using VaultSync.UI.ViewModels.Notifications;
using VaultSync.UI.Views;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private void OnDeleteBackupRequested(BackupSnapshotItem? snapshot)
        {
            AppViewModel.RunDetached(() => OnDeleteBackupRequestedAsync(snapshot), nameof(OnDeleteBackupRequestedAsync));
        }

        private async Task OnDeleteBackupRequestedAsync(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            if (!int.TryParse(snapshot.Id, out int backupId))
                return;

            DeleteBackupPreparation preparation = await Task.Run(() => PrepareDeleteBackup(backupId));
            if (!preparation.IsReady)
                return;
            Backup? backup      = preparation.Backup;
            int snapshotId  = preparation.SnapshotId;
            int projectId   = preparation.ProjectId;
            string backupRoot  = preparation.BackupRoot;
            string projectName = preparation.ProjectName;
            string cardId = $"delete-{backupId}";
            DestinationResolution? deleteResolution = null;

            var deleteContext = await Task.Run(() =>
            {
                AppConfig cfg = AppConfigStore.GetSnapshot();
                List<BackupDestination> destinations = AppViewModel.GetAllDestinations(cfg);
                BackupDestination? matchedDestination = FindDestinationForBackup(backup, destinations, backupRoot);
                bool hasCredentialProfile = HasCredentialProfile(cfg, matchedDestination);
                return (cfg, matchedDestination, hasCredentialProfile);
            });
            AppConfig cfg = deleteContext.cfg;
            BackupDestination? matchedDestination = deleteContext.matchedDestination;
            bool hasCredentialProfile = deleteContext.hasCredentialProfile;

            bool confirm = await ConfirmDeleteBackupAsync(projectName, snapshot.Timestamp);
            if (!confirm)
            {
                return;
            }

            BackupsViewModel.PinExpandedProject(snapshot.ProjectId);

            BackupsViewModel.ShowTransientOperation(cardId, projectName, AppViewModel.L("Backups.Status.Deleting", "Deleting backup files..."));

            BackupsViewModel.IsBusy      = true;
            BackupsViewModel.BusyMessage = AppViewModel.L("Backups.Status.Deleting", AppViewModel.L("Backups.Status.Deleting", "Deleting backup files..."));

            bool deleteSucceeded = false;
            string deleteError = string.Empty;
            bool permissionDenied = false;
            NetworkCredentialProfile? tempProfile = null;

            try
            {
                async Task TryDeleteAsync(bool forceCredentials, NetworkCredentialProfile? overrideProfile = null)
                {
                    if (matchedDestination is not null)
                    {
                        BackupDestination destToUse = matchedDestination;
                        string rootSubPath = string.Empty;
                        if (forceCredentials)
                        {
                            string? pathToUse = matchedDestination.Path;
                            if (OperatingSystem.IsWindows() && TryResolveUncPath(pathToUse, out string? uncPath))
                            {
                                pathToUse = uncPath;
                            }
                            if (OperatingSystem.IsWindows() && TrySplitUncPath(pathToUse, out string? uncRoot, out string? uncSubPath))
                            {
                                pathToUse = uncRoot;
                                rootSubPath = uncSubPath;
                            }

                            destToUse = new BackupDestination
                            {
                                Path = pathToUse,
                                CredentialName = matchedDestination.CredentialName,
                                Active = true,
                                AutoMount = true,
                                AutoUnmount = true,
                                PreMounted = false,
                                Alias = matchedDestination.Alias,
                                EnableMetadataSync = matchedDestination.EnableMetadataSync,
                                AutoImportMetadata = matchedDestination.AutoImportMetadata,
                                ForceMetadataBackfill = matchedDestination.ForceMetadataBackfill,
                                ArchiveUploadBufferBytes = matchedDestination.ArchiveUploadBufferBytes
                            };
                        }

                        NetworkCredentialProfile? profile = overrideProfile;
                        if (profile is null)
                        {
                            profile = string.IsNullOrWhiteSpace(destToUse.CredentialName)
                                ? null
                                : cfg.Network.Credentials.FirstOrDefault(c =>
                                    c.Name.Equals(destToUse.CredentialName, StringComparison.OrdinalIgnoreCase));
                        }

                        DestinationResolution resolution = _networkMountService.PrepareDestination(destToUse, profile);
                        if (!resolution.IsSuccess)
                        {
                            deleteError = resolution.Message;
                            deleteSucceeded = false;
                            permissionDenied = IsMountPermissionFailure(resolution.Message);
                            return;
                        }

                        if (resolution.IsSuccess && !string.IsNullOrWhiteSpace(resolution.EffectivePath))
                        {
                            deleteResolution = resolution;
                            backupRoot = string.IsNullOrWhiteSpace(rootSubPath)
                                ? resolution.EffectivePath
                                : Path.Combine(resolution.EffectivePath, rootSubPath);
                        }
                    }

                    string relativePath = backup.Path ?? string.Empty;
                    if (!TryCombinePathUnderRoot(backupRoot, relativePath, out string? fullPath, out string? combineError))
                    {
                        deleteError = combineError ?? AppViewModel.L("Backups.Delete.Error", "Delete failed");
                        deleteSucceeded = false;
                        return;
                    }

                    await Task.Run(() =>
                    {
                        try
                        {
                            if (Directory.Exists(fullPath))
                            {
                                deleteSucceeded = DeleteDirectoryRobust(fullPath, out string? deleteFailure, out bool deletePermissionDenied);
                                if (!deleteSucceeded && string.IsNullOrWhiteSpace(deleteError))
                                    deleteError = deleteFailure ?? AppViewModel.L("Backups.Delete.Error", "Delete failed");
                                if (deletePermissionDenied)
                                    permissionDenied = true;
                            }
                            else if (File.Exists(fullPath))
                            {
                                File.Delete(fullPath);
                                deleteSucceeded = !File.Exists(fullPath);
                            }
                            else
                            {
                                deleteSucceeded = true;
                            }
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            deleteError = ex.Message;
                            deleteSucceeded = false;
                            permissionDenied = true;
                        }
                        catch (IOException ex)
                        {
                            deleteError = ex.Message;
                            deleteSucceeded = false;
                            permissionDenied = IsAccessDenied(ex);
                        }
                        catch (Exception ex)
                        {
                            deleteError = ex.Message;
                            deleteSucceeded = false;
                        }
                        finally
                        {
                            if (deleteSucceeded)
                            {
                                _repo.DeleteBackupById(backupId);
                                TryDeleteSnapshotIfOrphan(projectId, snapshotId);
                            }
                        }
                    });
                }

                await TryDeleteAsync(forceCredentials: false);

                if (!deleteSucceeded && permissionDenied && matchedDestination is not null && hasCredentialProfile)
                {
                    bool retry = await ConfirmDeleteWithCredentialsAsync();
                    if (retry)
                    {
                        permissionDenied = false;
                        deleteError = string.Empty;
                        await TryDeleteAsync(forceCredentials: true);
                    }
                }

                if (!deleteSucceeded && permissionDenied && matchedDestination is not null && !hasCredentialProfile)
                {
                    (bool Confirmed, string Username, string Password) retry = await ConfirmDeleteWithTemporaryCredentialsAsync();
                    if (retry.Confirmed)
                    {
                        tempProfile = new NetworkCredentialProfile
                        {
                            Name = "DeleteOnce",
                            Username = retry.Username,
                            Password = retry.Password,
                            UseKeychain = false,
                            KeyRef = string.Empty
                        };
                        permissionDenied = false;
                        deleteError = string.Empty;
                        await TryDeleteAsync(forceCredentials: true, overrideProfile: tempProfile);
                    }
                    else
                    {
                        string title = AppViewModel.L("Backups.Delete.ForceCredentialsTitle", "Credentials required");
                        string msg = AppViewModel.L("Backups.Delete.ForceCredentialsMissing",
                            "Assign a credential profile to this destination in Settings. If your usual user cannot delete backups, the NAS root/admin user may be required.");
                        BackupsViewModel.ShowNotification(msg, "Error");
                        if (!IsOnBackupsPage)
                        {
                            GlobalNotificationCenter.Instance.Show(msg, NotificationSeverity.Error, title);
                        }
                    }
                }

                if (deleteSucceeded)
                {
                    ReloadBackupsVmData();
                    await DashboardViewModel.RefreshAsync();
                }
                else
                {
                    string title = AppViewModel.L("Backups.Delete.FailedTitle", "Backup delete failed");
                    string msg = Lf("Backups.Delete.FailedMessage", "Could not delete backup '{0}'.", projectName);
                    if (permissionDenied)
                    {
                        msg = $"{msg} " + AppViewModel.L(
                            "Backups.Delete.PermissionHint",
                            "VaultSync could not remove one or more protected files on the destination. Verify destination permissions/credentials and retry.");
                    }
                    if (!string.IsNullOrWhiteSpace(deleteError))
                    {
                        msg = $"{msg} {deleteError}";
                    }

                    BackupsViewModel.ShowNotification(msg, "Error");
                    if (!IsOnBackupsPage)
                    {
                        GlobalNotificationCenter.Instance.Show(msg, NotificationSeverity.Error, title);
                    }
                }
            }
            finally
            {
                string finalLabel = deleteSucceeded
                    ? AppViewModel.L("Backups.Status.Deleted", "Deleted")
                    : AppViewModel.L("Backups.Status.FailedSuffix", "Failed");
                BackupsViewModel.CompleteTransientOperation(cardId, finalLabel);
                BackupsViewModel.IsBusy      = false;
                BackupsViewModel.BusyMessage = string.Empty;

                if (deleteResolution is not null)
                {
                    _networkMountService.Cleanup(deleteResolution);
                }
            }
        }

        private void OnOpenBackupFolderRequested(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            if (!int.TryParse(snapshot.Id, out int backupId))
                return;

            OpenBackupFolder(backupId);
        }

        private void OnOpenSettingsRequested()
        {
            NavigateSettings?.Execute(null);
        }

        private async Task<bool> ConfirmDeleteBackupAsync(string projectName, DateTime timestamp)
        {
            AppConfig cfg = await Task.Run(AppConfigStore.Load);
            if (!cfg.Behavior.ConfirmDeleteBackup)
                return true;

            string timeLabel = timestamp.ToString("g", CultureInfo.CurrentCulture);
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Delete.Title", "Delete backup?"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var question = new TextBlock
                {
                    Text = Lf("Backups.Delete.Message", "Delete the backup for '{0}' from {1}?", projectName, timeLabel),
                    TextWrapping = TextWrapping.Wrap
                };

                var warning = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Delete.Warning", "This removes data on the destination."),
                    TextWrapping = TextWrapping.Wrap
                };
                if (GetBrush("TextSecondary") is { } warningBrush)
                {
                    warning.Foreground = warningBrush;
                }

                var dontShowAgain = new CheckBox
                {
                    Content = AppViewModel.L("Backups.Delete.DontShowAgain", "Don't show again"),
                    Margin = new Thickness(0, 2, 0, 0)
                };

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10
                };

                var cancelButton = new Button
                {
                    Content = AppViewModel.L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var deleteButton = new Button
                {
                    Content = AppViewModel.L("Backups.Delete.Confirm", "Delete backup"),
                    MinWidth = 140
                };
                deleteButton.Classes.Add("action-primary");

                Window? window = null;
                bool confirmed = false;
                cancelButton.Click += (_, _) => window?.Close();
                deleteButton.Click += (_, _) =>
                {
                    confirmed = true;
                    window?.Close();
                };

                buttonRow.Children.Add(cancelButton);
                buttonRow.Children.Add(deleteButton);

                var content = new StackPanel
                {
                    Spacing = 12
                };
                content.Children.Add(title);
                content.Children.Add(question);
                content.Children.Add(warning);
                content.Children.Add(dontShowAgain);
                content.Children.Add(buttonRow);

                var card = new Border
                {
                    Padding = new Thickness(18),
                    Margin = new Thickness(16)
                };
                card.Classes.Add("card");
                card.Child = content;

                window = new Window
                {
                    Title = AppViewModel.L("Backups.Delete.Title", "Delete backup?"),
                    Content = card,
                    CanResize = false,
                    Width = 540,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                Window? owner = GetMainWindow();
                if (owner != null)
                {
                    window.Icon = owner.Icon;
                    await window.ShowDialog(owner);
                }
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    void OnClosed(object? _, EventArgs __) => tcs.TrySetResult(true);
                    window.Closed += OnClosed;
                    window.Show();
                    await tcs.Task;
                    window.Closed -= OnClosed;
                }

                if (confirmed && dontShowAgain.IsChecked == true)
                {
                    cfg.Behavior.ConfirmDeleteBackup = false;
                    AppConfigStore.Save(cfg);
                    if (_settingsViewModel is not null)
                    {
                        _settingsViewModel.ConfirmDeleteBackups = false;
                    }
                }

                return confirmed;
            });
        }

        private static IBrush? GetBrush(string key)
        {
            if (Application.Current?.Resources.TryGetValue(key, out object? value) == true)
            {
                return value as IBrush;
            }

            return null;
        }

        private async Task<bool> ConfirmDeleteWithCredentialsAsync()
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Delete.ForceCredentialsTitle", "Credentials required"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var question = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Delete.ForceCredentialsPrompt",
                        "Use destination credentials to force delete this backup?"),
                    TextWrapping = TextWrapping.Wrap
                };

                var hint = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Delete.ForceCredentialsHint",
                        "Recommended for NAS shares when delete is denied."),
                    TextWrapping = TextWrapping.Wrap
                };
                if (GetBrush("TextSecondary") is { } hintBrush)
                {
                    hint.Foreground = hintBrush;
                }

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10
                };

                var cancelButton = new Button
                {
                    Content = AppViewModel.L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var forceButton = new Button
                {
                    Content = AppViewModel.L("Backups.Delete.ForceCredentialsConfirm", "Use credentials"),
                    MinWidth = 160
                };
                forceButton.Classes.Add("action-primary");

                Window? window = null;
                bool confirmed = false;
                cancelButton.Click += (_, _) => window?.Close();
                forceButton.Click += (_, _) =>
                {
                    confirmed = true;
                    window?.Close();
                };

                buttonRow.Children.Add(cancelButton);
                buttonRow.Children.Add(forceButton);

                var content = new StackPanel { Spacing = 12 };
                content.Children.Add(title);
                content.Children.Add(question);
                content.Children.Add(hint);
                content.Children.Add(buttonRow);

                var card = new Border
                {
                    Padding = new Thickness(18),
                    Margin = new Thickness(16)
                };
                card.Classes.Add("card");
                card.Child = content;

                window = new Window
                {
                    Title = AppViewModel.L("Backups.Delete.ForceCredentialsTitle", "Credentials required"),
                    Content = card,
                    CanResize = false,
                    Width = 540,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                Window? owner = GetMainWindow();
                if (owner != null)
                {
                    window.Icon = owner.Icon;
                    await window.ShowDialog(owner);
                }
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    void OnClosed(object? _, EventArgs __) => tcs.TrySetResult(true);
                    window.Closed += OnClosed;
                    window.Show();
                    await tcs.Task;
                    window.Closed -= OnClosed;
                }

                return confirmed;
            });
        }

        private async Task<(bool Confirmed, string Username, string Password)> ConfirmDeleteWithTemporaryCredentialsAsync()
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Delete.ForceCredentialsTitle", "Credentials required"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var question = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Delete.CredentialsPrompt",
                        "Enter destination credentials to force delete this backup. These credentials are used once and not saved."),
                    TextWrapping = TextWrapping.Wrap
                };

                var usernameLabel = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Delete.CredentialsUsername", "Username"),
                    FontWeight = FontWeight.SemiBold
                };
                var usernameBox = new TextBox
                {
                    Width = 320
                };

                var passwordLabel = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Delete.CredentialsPassword", "Password"),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                var passwordBox = new TextBox
                {
                    Width = 320,
                    PasswordChar = '●'
                };

                var hint = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Delete.ForceCredentialsMissing",
                        "Assign a credential profile to this destination in Settings. If your usual user cannot delete backups, the NAS root/admin user may be required."),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                if (GetBrush("TextSecondary") is { } hintBrush)
                {
                    hint.Foreground = hintBrush;
                }

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10
                };

                var cancelButton = new Button
                {
                    Content = AppViewModel.L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var forceButton = new Button
                {
                    Content = AppViewModel.L("Backups.Delete.ForceCredentialsConfirm", "Use credentials"),
                    MinWidth = 160
                };
                forceButton.Classes.Add("action-primary");

                Window? window = null;
                bool confirmed = false;
                cancelButton.Click += (_, _) => window?.Close();
                forceButton.Click += (_, _) =>
                {
                    confirmed = true;
                    window?.Close();
                };

                buttonRow.Children.Add(cancelButton);
                buttonRow.Children.Add(forceButton);

                var content = new StackPanel { Spacing = 10 };
                content.Children.Add(title);
                content.Children.Add(question);
                content.Children.Add(usernameLabel);
                content.Children.Add(usernameBox);
                content.Children.Add(passwordLabel);
                content.Children.Add(passwordBox);
                content.Children.Add(hint);
                content.Children.Add(buttonRow);

                var card = new Border
                {
                    Padding = new Thickness(18),
                    Margin = new Thickness(16)
                };
                card.Classes.Add("card");
                card.Child = content;

                window = new Window
                {
                    Title = AppViewModel.L("Backups.Delete.ForceCredentialsTitle", "Credentials required"),
                    Content = card,
                    CanResize = false,
                    Width = 540,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                Window? owner = GetMainWindow();
                if (owner != null)
                {
                    window.Icon = owner.Icon;
                    await window.ShowDialog(owner);
                }
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    void OnClosed(object? _, EventArgs __) => tcs.TrySetResult(true);
                    window.Closed += OnClosed;
                    window.Show();
                    await tcs.Task;
                    window.Closed -= OnClosed;
                }

                string username = usernameBox.Text?.Trim() ?? string.Empty;
                string password = passwordBox.Text ?? string.Empty;
                if (confirmed && (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)))
                    return (false, string.Empty, string.Empty);

                return (confirmed, username, password);
            });
        }

        /// <summary>
        /// Deletes a directory tree, clearing read-only attributes to avoid UnauthorizedAccess on Windows.
        /// </summary>
        private static bool DeleteDirectoryRobust(string path, out string? error)
            => DeleteDirectoryRobust(path, out error, out _);

        private static bool DeleteDirectoryRobust(string path, out string? error, out bool permissionDenied)
        {
            error = null;
            permissionDenied = false;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return true;

            // Clear read-only attributes on files and dirs before deletion.
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    FileAttributes attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
                catch (UnauthorizedAccessException)
                {
                    permissionDenied = true;
                }
                catch
                {
                    // ignore individual failures; deletion will surface issues later
                }
            }

            foreach (string? dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).Reverse())
            {
                try
                {
                    FileAttributes attrs = File.GetAttributes(dir);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(dir, attrs & ~FileAttributes.ReadOnly);
                }
                catch (UnauthorizedAccessException)
                {
                    permissionDenied = true;
                }
                catch
                {
                    // ignore
                }
            }

            try
            {
                Directory.Delete(path, recursive: true);
                return !Directory.Exists(path);
            }
            catch (UnauthorizedAccessException ex)
            {
                permissionDenied = true;
                if (TryDeleteDirectoryManually(path, ref permissionDenied, out string? manualError))
                    return true;

                error = string.IsNullOrWhiteSpace(manualError) ? ex.Message : manualError;
                return false;
            }
            catch (IOException ex)
            {
                if (TryDeleteDirectoryManually(path, ref permissionDenied, out string? manualError))
                    return true;

                error = string.IsNullOrWhiteSpace(manualError) ? ex.Message : manualError;
                return false;
            }
            catch (Exception ex)
            {
                if (IsAccessDenied(ex))
                    permissionDenied = true;
                error = ex.Message;
                return false;
            }
        }

        private static bool TryDeleteDirectoryManually(string path, ref bool permissionDenied, out string? error)
        {
            error = null;
            if (!Directory.Exists(path))
                return true;

            try
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        permissionDenied = true;
                    }
                    catch
                    {
                        // best effort
                    }

                    try
                    {
                        File.Delete(file);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        permissionDenied = true;
                        error = ex.Message;
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }
                }

                foreach (string? dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                                             .OrderByDescending(d => d.Length))
                {
                    try
                    {
                        Directory.Delete(dir, recursive: false);
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        permissionDenied = true;
                        error = ex.Message;
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }
                }

                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: false);

                return !Directory.Exists(path);
            }
            catch (UnauthorizedAccessException ex)
            {
                permissionDenied = true;
                error = ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryCombinePathUnderRoot(string root, string relativePath, out string fullPath, out string? error)
        {
            fullPath = string.Empty;
            error = null;

            try
            {
                string normalizedRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                string safeRelative = string.IsNullOrWhiteSpace(relativePath) ? string.Empty : relativePath.Trim();
                if (Path.IsPathFullyQualified(safeRelative))
                {
                    error = "Backup path is absolute and outside destination root.";
                    return false;
                }

                string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, safeRelative));
                if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Backup path resolves outside destination root.";
                    return false;
                }

                fullPath = candidate;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool IsAccessDenied(Exception ex)
        {
            const int accessDenied = unchecked((int)0x80070005);
            return ex.HResult == accessDenied ||
                   ex is UnauthorizedAccessException;
        }

        private static bool IsMountPermissionFailure(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("access", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("denied", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TrySplitUncPath(string? path, out string root, out string subPath)
        {
            root = string.Empty;
            subPath = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (!path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) &&
                !path.StartsWith(@"//", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string trimmed = path.TrimStart('\\', '/');
            string[] parts = trimmed.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return false;

            root = $@"\\{parts[0]}\{parts[1]}";
            if (parts.Length > 2)
            {
                subPath = Path.Combine([.. parts.Skip(2)]);
            }

            return !string.IsNullOrWhiteSpace(subPath);
        }

        private const int UniversalNameInfoLevel = 0x00000001;
        private const int ErrorMoreData = 234;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct UniversalNameInfo
        {
            public string? lpUniversalName;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetUniversalName(string localPath, int infoLevel, IntPtr buffer, ref int bufferSize);

        private static bool TryResolveUncPath(string? path, out string? uncPath)
        {
            uncPath = null;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(@"//", StringComparison.OrdinalIgnoreCase))
            {
                uncPath = path;
                return true;
            }

            if (!OperatingSystem.IsWindows())
                return false;

            if (path.Length < 2 || path[1] != ':')
                return false;

            int bufferSize = 0;
            int result = WNetGetUniversalName(path, UniversalNameInfoLevel, IntPtr.Zero, ref bufferSize);
            if (result != ErrorMoreData || bufferSize <= 0)
                return false;

            nint buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                result = WNetGetUniversalName(path, UniversalNameInfoLevel, buffer, ref bufferSize);
                if (result != 0)
                    return false;

                UniversalNameInfo info = Marshal.PtrToStructure<UniversalNameInfo>(buffer);
                if (string.IsNullOrWhiteSpace(info.lpUniversalName))
                    return false;

                uncPath = info.lpUniversalName;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private DeleteBackupPreparation PrepareDeleteBackup(int backupId)
        {
            Backup? backup = _repo.GetBackupById(backupId);
            if (backup is null)
                return DeleteBackupPreparation.Failure;

            AppConfig cfg = AppConfigStore.GetSnapshot();
            List<BackupDestination> destinations = AppViewModel.GetAllDestinations(cfg);
            string? backupRoot = ResolveDestinationRootForBackup(backup, destinations, cfg.Backups.BackupRoot);
            if (string.IsNullOrWhiteSpace(backupRoot))
                return DeleteBackupPreparation.Failure;

            Project? project = _repo.GetProjectById(backup.ProjectId);
            string projectName = project?.Name ?? "Backup";

            return new DeleteBackupPreparation(true, backup, backupRoot, projectName, project?.Id ?? 0, backup.SnapshotId);
        }

        private static BackupDestination? FindDestinationForBackup(
            Backup backup,
            IReadOnlyList<BackupDestination> destinations,
            string backupRoot)
        {
            if (!string.IsNullOrWhiteSpace(backup.DestinationAlias))
            {
                BackupDestination? aliasMatch = destinations.FirstOrDefault(d =>
                    string.Equals(d.Alias ?? string.Empty, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.Path ?? string.Empty, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase));
                if (aliasMatch is not null)
                    return aliasMatch;
            }

            if (!string.IsNullOrWhiteSpace(backup.DestinationPath))
            {
                BackupDestination? pathMatch = destinations.FirstOrDefault(d =>
                    string.Equals(d.Path ?? string.Empty, backup.DestinationPath, StringComparison.OrdinalIgnoreCase));
                if (pathMatch is not null)
                    return pathMatch;
            }

            BackupDestination? rootMatch = destinations.FirstOrDefault(d =>
                string.Equals(d.Path ?? string.Empty, backupRoot, StringComparison.OrdinalIgnoreCase));
            if (rootMatch is not null)
                return rootMatch;

            BackupDestination? prefixMatch = destinations.FirstOrDefault(d =>
                !string.IsNullOrWhiteSpace(d.Path) &&
                !string.IsNullOrWhiteSpace(backupRoot) &&
                backupRoot.StartsWith(d.Path!, StringComparison.OrdinalIgnoreCase));
            if (prefixMatch is not null)
                return prefixMatch;

            return rootMatch;
        }

        private static bool HasCredentialProfile(AppConfig cfg, BackupDestination? dest)
        {
            if (dest is null || string.IsNullOrWhiteSpace(dest.CredentialName))
                return false;

            return cfg.Network.Credentials.Any(c =>
                c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));
        }

        private sealed record DeleteBackupPreparation(
            bool IsReady,
            Backup? Backup,
            string BackupRoot,
            string ProjectName,
            int ProjectId,
            int SnapshotId)
        {
            public static DeleteBackupPreparation Failure => new(false, null, string.Empty, string.Empty, 0, 0);
        }

        private RestoreBackupPreparation PrepareRestoreBackup(int backupId)
        {
            Backup? backup = _repo.GetBackupById(backupId);
            if (backup is null)
            {
                RuntimeLog.WriteVerbose($"[Restore] Backup id {backupId} not found.");
                return RestoreBackupPreparation.Failure;
            }

            AppConfig cfg = AppConfigStore.GetSnapshot();
            List<BackupDestination> destinations = AppViewModel.GetAllDestinations(cfg);
            string? backupRoot = ResolveDestinationRootForBackup(backup, destinations, cfg.Backups.BackupRoot);
            if (string.IsNullOrWhiteSpace(backupRoot))
            {
                RuntimeLog.WriteVerbose($"[Restore] No backup root found for id={backupId}, path='{backup.Path}', dest='{backup.DestinationPath}', alias='{backup.DestinationAlias}'.");
                return RestoreBackupPreparation.Failure;
            }

            if (string.IsNullOrWhiteSpace(backup.Path))
            {
                RuntimeLog.WriteVerbose($"[Restore] Backup path missing for id={backupId}. Root='{backupRoot}', rel='{backup.Path}'.");
                return RestoreBackupPreparation.Failure;
            }

            if (!TryCombinePathUnderRoot(backupRoot, backup.Path, out string? backupFullPath, out string? backupPathError))
            {
                RuntimeLog.WriteVerbose($"[Restore] Backup path rejected for id={backupId}. Root='{backupRoot}', rel='{backup.Path}', error='{backupPathError}'.");
                return RestoreBackupPreparation.Failure;
            }
            if (!Directory.Exists(backupFullPath))
            {
                RuntimeLog.WriteVerbose($"[Restore] Backup path missing for id={backupId}. Root='{backupRoot}', rel='{backup.Path}', full='{backupFullPath}'.");
                return RestoreBackupPreparation.Failure;
            }

            Project? project = _repo.GetProjectById(backup.ProjectId);
            if (project is null)
            {
                RuntimeLog.WriteVerbose($"[Restore] Project id {backup.ProjectId} not found for backup id {backupId}.");
                return RestoreBackupPreparation.Failure;
            }

            string restoreMode = ProjectRestoreMode.Normalize(project.RestoreMode);
            string projectRoot = ResolveRestoreTarget(project, restoreMode);
            if (string.IsNullOrWhiteSpace(projectRoot))
                return RestoreBackupPreparation.Failure;

            string encryptedArchivePath = Path.Combine(backupFullPath, BackupArchiveCryptoService.EncryptedArchiveFileName);
            bool isEncrypted = backup.IsEncrypted || File.Exists(encryptedArchivePath);

            return new RestoreBackupPreparation(
                true,
                backupFullPath,
                projectRoot,
                project.Id,
                project.Name,
                restoreMode,
                isEncrypted,
                backup.IsImported,
                BackupModes.Normalize(backup.BackupMode));
        }

        private sealed record RestoreBackupPreparation(
            bool IsReady,
            string BackupFullPath,
            string ProjectRoot,
            int ProjectId,
            string ProjectName,
            string RestoreMode,
            bool IsEncrypted,
            bool IsImported,
            string BackupMode)
        {
            public static RestoreBackupPreparation Failure => new(
                false,
                string.Empty,
                string.Empty,
                0,
                string.Empty,
                ProjectRestoreMode.Direct,
                false,
                false,
                BackupModes.Full);
        }

        private List<string> ResolveEncryptedRestorePasswordCandidates(int projectId)
        {
            Project? project = _repo.GetProjectById(projectId);
            if (project is null)
                return [];

            AppConfig cfg = AppConfigStore.GetSnapshot();
            IReadOnlyList<string> keyRefs = BackupEncryptionPolicyResolver.ResolveRestoreKeyRefs(project, cfg.Backups.Encryption);
            if (keyRefs.Count == 0)
                return [];

            var candidates = new List<string>(keyRefs.Count);
            foreach (string keyRef in keyRefs)
            {
                string? secret = _credentialVault.GetSecret(
                    keyRef,
                    BackupEncryptionSecretUsername,
                    preferKeychain: true,
                    fallbackPlaintext: null);

                if (!string.IsNullOrWhiteSpace(secret))
                    candidates.Add(secret);
            }

            return candidates;
        }

        private string ResolveRestoreTarget(Project project, string restoreMode)
        {
            string mode = ProjectRestoreMode.Normalize(restoreMode);
            if (string.Equals(mode, ProjectRestoreMode.Sandbox, StringComparison.OrdinalIgnoreCase))
            {
                string safeProjectName = string.Concat(project.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                if (string.IsNullOrWhiteSpace(safeProjectName))
                    safeProjectName = "Project";

                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string sandboxRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VaultSync",
                    "restore-sandbox",
                    safeProjectName,
                    stamp);

                Directory.CreateDirectory(sandboxRoot);
                RuntimeLog.WriteVerbose($"[Restore] Sandbox mode active. Using sandbox path '{sandboxRoot}'.");
                return sandboxRoot;
            }

            if (!string.IsNullOrWhiteSpace(project.RootPath) && Directory.Exists(project.RootPath))
                return project.RootPath;

            AppConfig cfg = AppConfigStore.GetSnapshot();
            if (!string.IsNullOrWhiteSpace(cfg.ProjectsRoot))
            {
                string projectsRoot = Path.Combine(cfg.ProjectsRoot, project.Name);
                Directory.CreateDirectory(projectsRoot);
                TryUpdateProjectRootPath(project, projectsRoot);
                RuntimeLog.WriteVerbose($"[Restore] Project root missing. Using ProjectsRoot '{projectsRoot}'.");
                return projectsRoot;
            }

            string fallbackRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "VaultSync Restores",
                project.Name);

            Directory.CreateDirectory(fallbackRoot);
            TryUpdateProjectRootPath(project, fallbackRoot);

            RuntimeLog.WriteVerbose($"[Restore] Project root missing. Using fallback restore path '{fallbackRoot}'.");
            return fallbackRoot;
        }

        private AutoBackupPreparation PrepareAutoBackupRun()
        {
            AppConfig cfg = AppConfigStore.GetSnapshot();
            if (!cfg.Backups.EnableAutoBackups)
                return AutoBackupPreparation.Failure("disabled");

            List<BackupDestination> destinations = AppViewModel.GetAllDestinations(cfg);
            if (destinations.Count == 0)
                return AutoBackupPreparation.Failure("no_destination");

            var projects = _repo.GetAllProjects().ToList();
            HashSet<int> disabled = cfg.Backups.AutoBackupDisabledProjects?.ToHashSet() ?? [];

            return AutoBackupPreparation.Success(cfg, projects, disabled);
        }

        private sealed record AutoBackupPreparation(
            bool IsReady,
            string? FailureCode,
            AppConfig? Config,
            List<Project>? Projects,
            ISet<int>? DisabledProjects)
        {
            public static AutoBackupPreparation Failure(string reason) =>
                new(false, reason, null, null, null);

            public static AutoBackupPreparation Success(
                AppConfig cfg,
                List<Project> projects,
                ISet<int> disabled) =>
                new(true, null, cfg, projects, disabled);
        }

        private async Task<(bool Confirmed, string Password)> ConfirmEncryptedRestorePasswordAsync(string projectName)
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Restore.EncryptedPasswordTitle", "Encrypted backup password"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var prompt = new TextBlock
                {
                    Text = string.Format(
                        CultureInfo.CurrentCulture,
                        AppViewModel.L("Backups.Restore.EncryptedPasswordPrompt", "Enter the encryption password to restore '{0}'."),
                        projectName),
                    TextWrapping = TextWrapping.Wrap
                };

                var passwordLabel = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Restore.EncryptedPasswordLabel", "Password"),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                var passwordBox = new TextBox
                {
                    Width = 320,
                    PasswordChar = '●'
                };

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10
                };

                var cancelButton = new Button
                {
                    Content = AppViewModel.L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var restoreButton = new Button
                {
                    Content = AppViewModel.L("Backups.Section.Restore", "Restore"),
                    MinWidth = 140
                };
                restoreButton.Classes.Add("action-primary");

                Window? window = null;
                bool confirmed = false;
                cancelButton.Click += (_, _) => window?.Close();
                restoreButton.Click += (_, _) =>
                {
                    confirmed = true;
                    window?.Close();
                };

                buttonRow.Children.Add(cancelButton);
                buttonRow.Children.Add(restoreButton);

                var content = new StackPanel { Spacing = 10 };
                content.Children.Add(title);
                content.Children.Add(prompt);
                content.Children.Add(passwordLabel);
                content.Children.Add(passwordBox);
                content.Children.Add(buttonRow);

                var card = new Border
                {
                    Padding = new Thickness(18),
                    Margin = new Thickness(16)
                };
                card.Classes.Add("card");
                card.Child = content;

                window = new Window
                {
                    Title = AppViewModel.L("Backups.Restore.EncryptedPasswordTitle", "Encrypted backup password"),
                    Content = card,
                    CanResize = false,
                    Width = 540,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                Window? owner = GetMainWindow();
                if (owner != null)
                {
                    window.Icon = owner.Icon;
                    await window.ShowDialog(owner);
                }
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    void OnClosed(object? _, EventArgs __) => tcs.TrySetResult(true);
                    window.Closed += OnClosed;
                    window.Show();
                    await tcs.Task;
                    window.Closed -= OnClosed;
                }

                string password = passwordBox.Text ?? string.Empty;
                if (confirmed && string.IsNullOrWhiteSpace(password))
                    return (false, string.Empty);

                return (confirmed, password);
            });
        }

        private async Task<(bool Confirmed, string RestoreMode, IReadOnlyList<string> SelectedTopLevelTargets)> ConfirmRestoreBackupAsync(
            RestoreBackupPreparation preparation,
            RestoreExecutionPreview preview)
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Restore.ConfirmTitle", "Restore backup?"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                string targetLabel = string.Format(
                    CultureInfo.CurrentCulture,
                    AppViewModel.L("Backups.Restore.ConfirmPrompt", "Restore '{0}' into:\n{1}"),
                    preparation.ProjectName,
                    preparation.ProjectRoot);

                var question = new TextBlock
                {
                    Text = targetLabel,
                    TextWrapping = TextWrapping.Wrap
                };

                var guidanceHeader = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Restore.GuidanceHeader", "What happens next"),
                    FontWeight = FontWeight.SemiBold
                };

                string backupTypeLabel = preparation.IsImported
                    ? AppViewModel.L("Backups.Snapshot.Type.Imported", "Imported")
                    : string.Equals(preparation.BackupMode, BackupModes.Incremental, StringComparison.OrdinalIgnoreCase)
                        ? AppViewModel.L("Backups.Snapshot.Type.Incremental", "Incremental")
                        : AppViewModel.L("Backups.Snapshot.Type.Full", "Full");
                string restoreModeLabel = string.Equals(preparation.RestoreMode, ProjectRestoreMode.Sandbox, StringComparison.OrdinalIgnoreCase)
                    ? AppViewModel.L("Backups.Restore.Mode.Sandbox", "Sandbox (restore to preview folder)")
                    : AppViewModel.L("Backups.Restore.Mode.Direct", "Direct (overwrite project path)");

                string[] guidanceLines = new[]
                {
                    Lf("Backups.Restore.GuidanceType", "Type: {0}", backupTypeLabel),
                    Lf("Backups.Restore.GuidanceMode", "Mode: {0}", restoreModeLabel),
                    AppViewModel.L("Backups.Restore.GuidanceOverwrite", "Files with matching paths are overwritten by restored files."),
                    AppViewModel.L("Backups.Restore.GuidanceKeepExtra", "Files that exist only in the current project folder are kept."),
                    preparation.IsEncrypted
                        ? AppViewModel.L("Backups.Restore.GuidanceEncrypted", "If needed, VaultSync will ask for the encryption password before restore starts.")
                        : AppViewModel.L("Backups.Restore.GuidancePlain", "No encryption password is required for this backup.")
                };

                var guidancePanel = new StackPanel { Spacing = 4 };
                foreach (string? line in guidanceLines)
                {
                    var row = new TextBlock
                    {
                        Text = "• " + line,
                        TextWrapping = TextWrapping.Wrap
                    };
                    if (GetBrush("TextSecondary") is { } secondary)
                        row.Foreground = secondary;
                    guidancePanel.Children.Add(row);
                }

                var previewHeader = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Restore.Preview.Header", "Restore preview"),
                    FontWeight = FontWeight.SemiBold
                };
                var previewPanel = new StackPanel { Spacing = 4 };
                if (!preview.IsAvailable)
                {
                    var unavailable = new TextBlock
                    {
                        Text = "• " + (string.IsNullOrWhiteSpace(preview.UnavailableReason)
                            ? AppViewModel.L("Backups.Restore.Preview.Unavailable", "Preview is unavailable for this backup.")
                            : preview.UnavailableReason),
                        TextWrapping = TextWrapping.Wrap
                    };
                    if (GetBrush("TextSecondary") is { } unavailableSecondary)
                        unavailable.Foreground = unavailableSecondary;
                    previewPanel.Children.Add(unavailable);
                }
                else
                {
                    string[] previewLines = new[]
                    {
                        Lf("Backups.Restore.Preview.TotalFiles", "Files in backup: {0}", preview.TotalFiles.ToString(CultureInfo.CurrentCulture)),
                        Lf("Backups.Restore.Preview.NewFiles", "New files to add: {0}", preview.NewFiles.ToString(CultureInfo.CurrentCulture)),
                        Lf("Backups.Restore.Preview.OverwriteFiles", "Files that will overwrite existing project files: {0}", preview.OverwriteFiles.ToString(CultureInfo.CurrentCulture)),
                        Lf("Backups.Restore.Preview.ConflictFiles", "Potential conflicts (project appears newer/different): {0}", preview.ConflictFiles.ToString(CultureInfo.CurrentCulture)),
                        Lf("Backups.Restore.Preview.ExtraFilesKept", "Existing project-only files that will be kept: {0}", preview.ExtraFilesKept.ToString(CultureInfo.CurrentCulture)),
                        Lf("Backups.Restore.Preview.TotalBytes", "Total restore data: {0}", BackupSnapshotItem.FormatSize(preview.TotalBytes))
                    };
                    foreach (string? line in previewLines)
                    {
                        var row = new TextBlock
                        {
                            Text = "• " + line,
                            TextWrapping = TextWrapping.Wrap
                        };
                        if (GetBrush("TextSecondary") is { } previewSecondary)
                            row.Foreground = previewSecondary;
                        previewPanel.Children.Add(row);
                    }
                }

                var restoreModeOptions = new List<RestoreModeOption>
                {
                    new(ProjectRestoreMode.Direct, AppViewModel.L("Backups.Restore.Mode.Direct", "Direct (overwrite project path)")),
                    new(ProjectRestoreMode.Sandbox, AppViewModel.L("Backups.Restore.Mode.Sandbox", "Sandbox (restore to preview folder)"))
                };
                var restoreModeCombo = new ComboBox
                {
                    ItemsSource = restoreModeOptions,
                    SelectedItem = restoreModeOptions.FirstOrDefault(o =>
                        string.Equals(o.Id, preparation.RestoreMode, StringComparison.OrdinalIgnoreCase))
                        ?? restoreModeOptions[0],
                    ItemTemplate = new FuncDataTemplate<RestoreModeOption>(
                        (option, _) => new TextBlock
                        {
                            Text = option?.Label ?? string.Empty,
                            TextWrapping = TextWrapping.NoWrap
                        },
                        supportsRecycling: true),
                    MinWidth = 360
                };
                var restoreModeSelector = new StackPanel { Spacing = 5 };
                restoreModeSelector.Children.Add(new TextBlock
                {
                    Text = AppViewModel.L("Backups.Restore.Mode.Label", "Restore mode"),
                    FontWeight = FontWeight.SemiBold
                });
                restoreModeSelector.Children.Add(restoreModeCombo);

                var targetSelector = new StackPanel { Spacing = 5 };
                var targetSelections = new List<CheckBox>();
                IReadOnlyList<string> targetOptions = preview.TopLevelTargets;
                bool showTargetSelector = preview.IsAvailable && targetOptions.Count > 1;
                if (showTargetSelector)
                {
                    targetSelector.Children.Add(new TextBlock
                    {
                        Text = AppViewModel.L("Backups.Restore.Selection.Header", "Restore targets"),
                        FontWeight = FontWeight.SemiBold
                    });
                    targetSelector.Children.Add(new TextBlock
                    {
                        Text = AppViewModel.L("Backups.Restore.Selection.Description", "Choose which top-level folders/files to restore."),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = GetBrush("TextSecondary")
                    });

                    foreach (string option in targetOptions)
                    {
                        var cb = new CheckBox
                        {
                            Content = option,
                            IsChecked = true
                        };
                        targetSelections.Add(cb);
                        targetSelector.Children.Add(cb);
                    }
                }

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10
                };

                var cancelButton = new Button
                {
                    Content = AppViewModel.L("Common.Cancel", "Cancel"),
                    MinWidth = 120,
                    IsCancel = true
                };
                cancelButton.Classes.Add("action-ghost");

                var restoreButton = new Button
                {
                    Content = AppViewModel.L("Backups.Section.Restore", "Restore"),
                    MinWidth = 140,
                    IsDefault = true
                };
                restoreButton.Classes.Add("action-primary");

                Window? window = null;
                bool confirmed = false;
                cancelButton.Click += (_, _) => window?.Close();
                restoreButton.Click += (_, _) =>
                {
                    confirmed = true;
                    window?.Close();
                };

                buttonRow.Children.Add(cancelButton);
                buttonRow.Children.Add(restoreButton);

                var content = new StackPanel { Spacing = 12 };
                content.Children.Add(title);
                content.Children.Add(question);
                content.Children.Add(guidanceHeader);
                content.Children.Add(guidancePanel);
                content.Children.Add(previewHeader);
                content.Children.Add(previewPanel);
                if (showTargetSelector)
                    content.Children.Add(targetSelector);
                content.Children.Add(restoreModeSelector);
                content.Children.Add(buttonRow);

                var card = new Border
                {
                    Padding = new Thickness(18),
                    Margin = new Thickness(16)
                };
                card.Classes.Add("card");
                card.Child = content;

                window = new Window
                {
                    Title = AppViewModel.L("Backups.Restore.ConfirmTitle", "Restore backup?"),
                    Content = card,
                    CanResize = false,
                    Width = 620,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                Window? owner = GetMainWindow();
                if (owner != null)
                {
                    window.Icon = owner.Icon;
                    await window.ShowDialog(owner);
                }
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    void OnClosed(object? _, EventArgs __) => tcs.TrySetResult(true);
                    window.Closed += OnClosed;
                    window.Show();
                    await tcs.Task;
                    window.Closed -= OnClosed;
                }

                string selectedMode = (restoreModeCombo.SelectedItem as RestoreModeOption)?.Id ?? preparation.RestoreMode;
                IReadOnlyList<string> selectedTargets = showTargetSelector
                    ? targetSelections.Where(cb => cb.IsChecked == true)
                        .Select(cb => cb.Content?.ToString() ?? string.Empty)
                        .Where(v => !string.IsNullOrWhiteSpace(v))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : [.. targetOptions];

                if (confirmed && showTargetSelector && selectedTargets.Count == 0)
                    return (false, ProjectRestoreMode.Normalize(selectedMode), Array.Empty<string>());

                return (confirmed, ProjectRestoreMode.Normalize(selectedMode), selectedTargets);
            });
        }

        private enum SandboxPostRestoreAction
        {
            Keep,
            Open,
            Apply
        }

        private sealed record SandboxPostRestoreDecision(
            SandboxPostRestoreAction Action,
            bool DeleteAfterApply);

        private async Task<SandboxPostRestoreDecision> ConfirmSandboxPostRestoreActionAsync(string projectName, string sandboxPath)
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Restore.Sandbox.Post.Title", "Sandbox restore completed"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var prompt = new TextBlock
                {
                    Text = Lf(
                        "Backups.Restore.Sandbox.Post.Prompt",
                        "Review the restored files in sandbox for '{0}', then choose what to do next.",
                        projectName),
                    TextWrapping = TextWrapping.Wrap
                };

                var pathLine = new TextBlock
                {
                    Text = sandboxPath,
                    TextWrapping = TextWrapping.Wrap
                };
                if (GetBrush("TextSecondary") is { } secondary)
                    pathLine.Foreground = secondary;

                var deleteAfterApply = new CheckBox
                {
                    Content = AppViewModel.L("Backups.Restore.Sandbox.Post.DeleteAfterApply", "Delete sandbox folder after apply"),
                    IsChecked = true
                };

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10
                };

                var keepButton = new Button
                {
                    Content = AppViewModel.L("Backups.Restore.Sandbox.Post.Keep", "Keep for later"),
                    MinWidth = 130
                };
                keepButton.Classes.Add("action-ghost");

                var openButton = new Button
                {
                    Content = AppViewModel.L("Backups.Restore.Sandbox.Post.Open", "Open sandbox"),
                    MinWidth = 130
                };
                openButton.Classes.Add("action-ghost");

                var applyButton = new Button
                {
                    Content = AppViewModel.L("Backups.Restore.Sandbox.Post.Apply", "Apply to project"),
                    MinWidth = 150
                };
                applyButton.Classes.Add("action-primary");

                Window? window = null;
                SandboxPostRestoreAction action = SandboxPostRestoreAction.Keep;
                keepButton.Click += (_, _) =>
                {
                    action = SandboxPostRestoreAction.Keep;
                    window?.Close();
                };
                openButton.Click += (_, _) =>
                {
                    action = SandboxPostRestoreAction.Open;
                    window?.Close();
                };
                applyButton.Click += (_, _) =>
                {
                    action = SandboxPostRestoreAction.Apply;
                    window?.Close();
                };

                buttonRow.Children.Add(keepButton);
                buttonRow.Children.Add(openButton);
                buttonRow.Children.Add(applyButton);

                var content = new StackPanel { Spacing = 12 };
                content.Children.Add(title);
                content.Children.Add(prompt);
                content.Children.Add(pathLine);
                content.Children.Add(deleteAfterApply);
                content.Children.Add(buttonRow);

                var card = new Border
                {
                    Padding = new Thickness(18),
                    Margin = new Thickness(16)
                };
                card.Classes.Add("card");
                card.Child = content;

                window = new Window
                {
                    Title = AppViewModel.L("Backups.Restore.Sandbox.Post.Title", "Sandbox restore completed"),
                    Content = card,
                    CanResize = false,
                    Width = 650,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                Window? owner = GetMainWindow();
                if (owner != null)
                {
                    window.Icon = owner.Icon;
                    await window.ShowDialog(owner);
                }
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    void OnClosed(object? _, EventArgs __) => tcs.TrySetResult(true);
                    window.Closed += OnClosed;
                    window.Show();
                    await tcs.Task;
                    window.Closed -= OnClosed;
                }

                return new SandboxPostRestoreDecision(
                    action,
                    deleteAfterApply.IsChecked == true);
            });
        }

        private async Task ApplySandboxRestoreToProjectAsync(
            Project project,
            string projectName,
            string sandboxPath,
            bool deleteAfterApply)
        {
            if (!Directory.Exists(sandboxPath))
            {
                BackupsViewModel.ShowNotification(
                    AppViewModel.L("Backups.Restore.Sandbox.ApplyMissing", "Sandbox folder no longer exists."),
                    "Error");
                return;
            }

            string targetPath = ResolveRestoreTarget(project, ProjectRestoreMode.Direct);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                BackupsViewModel.ShowNotification(
                    AppViewModel.L("Backups.Status.RestoreFailed", "Restore failed."),
                    "Error");
                return;
            }

            SandboxApplyPreview preview = await Task.Run(() => BuildSandboxApplyPreview(sandboxPath, targetPath));
            bool applyConfirmed = await ConfirmSandboxApplyAsync(projectName, targetPath, preview);
            if (!applyConfirmed)
                return;

            string applyCardId = $"sandbox-apply-{project.Id}";
            BackupsViewModel.IsBusy = true;
            BackupsViewModel.BusyMessage = AppViewModel.L("Backups.Restore.Sandbox.ApplyingBusy", "Applying sandbox restore...");
            BackupsViewModel.UpdateActiveBackup(
                applyCardId,
                projectName,
                0,
                AppViewModel.L("Backups.Restore.Sandbox.ApplyingBusy", "Applying sandbox restore..."),
                string.Empty,
                allowCancel: false);

            bool applySucceeded = false;
            string? cleanupError = null;
            try
            {
                await Task.Run(() =>
                {
                    CopyDirectoryWithProgress(sandboxPath, targetPath, null, 0, 100, update =>
                    {
                        string label = string.IsNullOrWhiteSpace(update.CurrentFile)
                            ? AppViewModel.L("Backups.Restore.Sandbox.ApplyingBusy", "Applying sandbox restore...")
                            : update.CurrentFile;
                        BackupsViewModel.UpdateActiveBackup(
                            applyCardId,
                            projectName,
                            update.Percent,
                            label,
                            string.Empty,
                            allowCancel: false);
                    });
                });

                _repo.UpdateProjectNeedsRestore(project.Id, false);
                applySucceeded = true;

                if (deleteAfterApply)
                {
                    if (!DeleteDirectoryRobust(sandboxPath, out cleanupError))
                    {
                        cleanupError ??= AppViewModel.L("Backups.Restore.Sandbox.CleanupFailed", "Sandbox cleanup failed.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Restore] Failed to apply sandbox restore for '{projectName}': {ex.Message}");
                BackupsViewModel.ShowNotification(
                    Lf("Backups.Restore.Sandbox.ApplyFailed", "Failed to apply sandbox restore: {0}", ex.Message),
                    "Error");
            }
            finally
            {
                BackupsViewModel.RemoveActiveBackup(applyCardId);
                BackupsViewModel.IsBusy = false;
                BackupsViewModel.BusyMessage = string.Empty;
            }

            if (!applySucceeded)
                return;

            if (string.IsNullOrWhiteSpace(cleanupError))
            {
                BackupsViewModel.ShowNotification(
                    AppViewModel.L("Backups.Restore.Sandbox.ApplyCompleted", "Sandbox restore applied to project."),
                    "Success");
            }
            else
            {
                BackupsViewModel.ShowNotification(
                    Lf(
                        "Backups.Restore.Sandbox.ApplyCompletedWithCleanupWarning",
                        "Sandbox restore applied, but cleanup failed: {0}",
                        cleanupError),
                    "Warning");
            }
        }

        private sealed record SandboxApplyPreview(
            int TotalFiles,
            int NewFiles,
            int OverwriteFiles,
            long TotalBytes,
            long OverwriteBytes);

        private sealed record RestoreExecutionPreview(
            bool IsAvailable,
            int TotalFiles,
            int NewFiles,
            int OverwriteFiles,
            int ConflictFiles,
            int ExtraFilesKept,
            long TotalBytes,
            IReadOnlyList<string> TopLevelTargets,
            string UnavailableReason)
        {
            public static RestoreExecutionPreview Unavailable(string reason) => new(
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                [],
                reason);
        }

        private RestoreExecutionPreview BuildRestoreExecutionPreview(RestoreBackupPreparation preparation)
        {
            if (preparation.IsEncrypted)
            {
                return RestoreExecutionPreview.Unavailable(
                    AppViewModel.L(
                        "Backups.Restore.Preview.EncryptedUnavailable",
                        "Preview is unavailable before decrypt for encrypted backups."));
            }

            if (!Directory.Exists(preparation.BackupFullPath) || !Directory.Exists(preparation.ProjectRoot))
            {
                return RestoreExecutionPreview.Unavailable(
                    AppViewModel.L("Backups.Restore.Preview.Unavailable", "Preview is unavailable for this backup."));
            }

            var sourceRelative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var topLevelTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalFiles = 0;
            int newFiles = 0;
            int overwriteFiles = 0;
            int conflictFiles = 0;
            long totalBytes = 0;
            string archivePath = Path.Combine(preparation.BackupFullPath, BackupArchiveCryptoService.PlainArchiveFileName);
            if (File.Exists(archivePath))
            {
                using ZipArchive archive = ZipFile.OpenRead(archivePath);
                foreach (ZipArchiveEntry? entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
                {
                    string relative = GetSafeArchiveEntryRelativePath(entry.FullName);
                    sourceRelative.Add(relative);
                    string topLevel = GetTopLevelSegment(relative);
                    if (!string.IsNullOrWhiteSpace(topLevel))
                        topLevelTargets.Add(topLevel);
                    totalFiles++;
                    totalBytes += Math.Max(0, entry.Length);

                    string targetPath = Path.Combine(preparation.ProjectRoot, relative);
                    if (!File.Exists(targetPath))
                    {
                        newFiles++;
                        continue;
                    }

                    overwriteFiles++;
                    var targetInfo = new FileInfo(targetPath);
                    DateTime sourceWriteUtc = entry.LastWriteTime.UtcDateTime == DateTime.MinValue
                        ? DateTime.MinValue
                        : entry.LastWriteTime.UtcDateTime;
                    bool targetSeemsNewer = sourceWriteUtc != DateTime.MinValue
                        && targetInfo.LastWriteTimeUtc > sourceWriteUtc.AddSeconds(1);
                    bool contentLooksDifferent = targetInfo.Length != entry.Length;
                    if (targetSeemsNewer || contentLooksDifferent)
                        conflictFiles++;
                }
            }
            else
            {
                string[] sourceFiles = Directory.GetFiles(preparation.BackupFullPath, "*", SearchOption.AllDirectories);
                foreach (string sourcePath in sourceFiles)
                {
                    string relative = Path.GetRelativePath(preparation.BackupFullPath, sourcePath);
                    sourceRelative.Add(relative);
                    string topLevel = GetTopLevelSegment(relative);
                    if (!string.IsNullOrWhiteSpace(topLevel))
                        topLevelTargets.Add(topLevel);
                    totalFiles++;

                    var sourceInfo = new FileInfo(sourcePath);
                    totalBytes += sourceInfo.Length;

                    string targetPath = Path.Combine(preparation.ProjectRoot, relative);
                    if (!File.Exists(targetPath))
                    {
                        newFiles++;
                        continue;
                    }

                    overwriteFiles++;
                    var targetInfo = new FileInfo(targetPath);
                    bool targetSeemsNewer = targetInfo.LastWriteTimeUtc > sourceInfo.LastWriteTimeUtc.AddSeconds(1);
                    bool contentLooksDifferent = targetInfo.Length != sourceInfo.Length;
                    if (targetSeemsNewer || contentLooksDifferent)
                        conflictFiles++;
                }
            }

            int extraFilesKept = 0;
            try
            {
                string[] targetFiles = Directory.GetFiles(preparation.ProjectRoot, "*", SearchOption.AllDirectories);
                foreach (string targetPath in targetFiles)
                {
                    string relative = Path.GetRelativePath(preparation.ProjectRoot, targetPath);
                    if (!sourceRelative.Contains(relative))
                        extraFilesKept++;
                }
            }
            catch
            {
                // best-effort; keep computed counts from source scan
            }

            return new RestoreExecutionPreview(
                true,
                totalFiles,
                newFiles,
                overwriteFiles,
                conflictFiles,
                extraFilesKept,
                totalBytes,
                topLevelTargets.OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).ToArray(),
                string.Empty);
        }

        private static SandboxApplyPreview BuildSandboxApplyPreview(string sandboxPath, string targetPath)
        {
            int totalFiles = 0;
            int newFiles = 0;
            int overwriteFiles = 0;
            long totalBytes = 0;
            long overwriteBytes = 0;

            foreach (string sourceFile in Directory.EnumerateFiles(sandboxPath, "*", SearchOption.AllDirectories))
            {
                totalFiles++;
                var fileInfo = new FileInfo(sourceFile);
                totalBytes += fileInfo.Length;

                string relativePath = Path.GetRelativePath(sandboxPath, sourceFile);
                string destinationFile = Path.Combine(targetPath, relativePath);
                if (File.Exists(destinationFile))
                {
                    overwriteFiles++;
                    overwriteBytes += fileInfo.Length;
                }
                else
                {
                    newFiles++;
                }
            }

            return new SandboxApplyPreview(totalFiles, newFiles, overwriteFiles, totalBytes, overwriteBytes);
        }

        private async Task<bool> ConfirmSandboxApplyAsync(string projectName, string targetPath, SandboxApplyPreview preview)
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Restore.Sandbox.ApplyConfirmTitle", "Apply sandbox restore to project?"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var prompt = new TextBlock
                {
                    Text = Lf(
                        "Backups.Restore.Sandbox.ApplyConfirmPrompt",
                        "Apply sandbox restore for '{0}' into:\n{1}",
                        projectName,
                        targetPath),
                    TextWrapping = TextWrapping.Wrap
                };

                var summaryHeader = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Restore.Sandbox.ApplySummaryHeader", "Apply summary"),
                    FontWeight = FontWeight.SemiBold
                };

                string[] summaryLines = new[]
                {
                    Lf("Backups.Restore.Sandbox.ApplySummaryTotalFiles", "Total files to copy: {0}", preview.TotalFiles.ToString(CultureInfo.CurrentCulture)),
                    Lf("Backups.Restore.Sandbox.ApplySummaryNewFiles", "New files: {0}", preview.NewFiles.ToString(CultureInfo.CurrentCulture)),
                    Lf("Backups.Restore.Sandbox.ApplySummaryOverwriteFiles", "Files that overwrite existing project files: {0}", preview.OverwriteFiles.ToString(CultureInfo.CurrentCulture)),
                    Lf("Backups.Restore.Sandbox.ApplySummaryTotalBytes", "Total data to write: {0}", BackupSnapshotItem.FormatSize(preview.TotalBytes)),
                    Lf("Backups.Restore.Sandbox.ApplySummaryOverwriteBytes", "Data that overwrites existing files: {0}", BackupSnapshotItem.FormatSize(preview.OverwriteBytes))
                };

                var summaryPanel = new StackPanel { Spacing = 4 };
                foreach (string? line in summaryLines)
                {
                    var row = new TextBlock
                    {
                        Text = "• " + line,
                        TextWrapping = TextWrapping.Wrap
                    };
                    if (GetBrush("TextSecondary") is { } secondary)
                        row.Foreground = secondary;
                    summaryPanel.Children.Add(row);
                }

                var warning = new TextBlock
                {
                    Text = AppViewModel.L("Backups.Restore.Sandbox.ApplyConfirmWarning", "Existing files with matching paths will be overwritten."),
                    TextWrapping = TextWrapping.Wrap
                };
                if (GetBrush("TextSecondary") is { } warningBrush)
                    warning.Foreground = warningBrush;

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10
                };

                var cancelButton = new Button
                {
                    Content = AppViewModel.L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var applyButton = new Button
                {
                    Content = AppViewModel.L("Backups.Restore.Sandbox.Post.Apply", "Apply to project"),
                    MinWidth = 150
                };
                applyButton.Classes.Add("action-primary");

                Window? window = null;
                bool confirmed = false;
                cancelButton.Click += (_, _) => window?.Close();
                applyButton.Click += (_, _) =>
                {
                    confirmed = true;
                    window?.Close();
                };

                buttonRow.Children.Add(cancelButton);
                buttonRow.Children.Add(applyButton);

                var content = new StackPanel { Spacing = 12 };
                content.Children.Add(title);
                content.Children.Add(prompt);
                content.Children.Add(summaryHeader);
                content.Children.Add(summaryPanel);
                content.Children.Add(warning);
                content.Children.Add(buttonRow);

                var card = new Border
                {
                    Padding = new Thickness(18),
                    Margin = new Thickness(16)
                };
                card.Classes.Add("card");
                card.Child = content;

                window = new Window
                {
                    Title = AppViewModel.L("Backups.Restore.Sandbox.ApplyConfirmTitle", "Apply sandbox restore to project?"),
                    Content = card,
                    CanResize = false,
                    Width = 700,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                Window? owner = GetMainWindow();
                if (owner != null)
                {
                    window.Icon = owner.Icon;
                    await window.ShowDialog(owner);
                }
                else
                {
                    var tcs = new TaskCompletionSource<bool>();
                    void OnClosed(object? _, EventArgs __) => tcs.TrySetResult(true);
                    window.Closed += OnClosed;
                    window.Show();
                    await tcs.Task;
                    window.Closed -= OnClosed;
                }

                return confirmed;
            });
        }

        private void OnRestoreBackupRequested(BackupSnapshotItem? snapshot)
        {
            AppViewModel.RunDetached(() => OnRestoreBackupRequestedAsync(snapshot), nameof(OnRestoreBackupRequestedAsync));
        }

        private async Task OnRestoreBackupRequestedAsync(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            if (!int.TryParse(snapshot.Id, out int backupId))
                return;


            RestoreBackupPreparation preparation = await Task.Run(() => PrepareRestoreBackup(backupId));
            if (!preparation.IsReady)
            {
                BackupsViewModel.ShowNotification(
                    AppViewModel.L("Backups.Status.RestoreFailed", "Restore failed."),
                    "Error");
                RuntimeLog.WriteVerbose($"[Restore] Restore preparation failed for backupId={backupId}.");
                return;
            }

            RestoreExecutionPreview restorePreview = await Task.Run(() => BuildRestoreExecutionPreview(preparation));
            (bool Confirmed, string RestoreMode, IReadOnlyList<string> SelectedTopLevelTargets) restoreDecision = await ConfirmRestoreBackupAsync(preparation, restorePreview);
            if (!restoreDecision.Confirmed)
                return;

            string selectedRestoreMode = ProjectRestoreMode.Normalize(restoreDecision.RestoreMode);
            IReadOnlyList<string> selectedTopLevelTargets = restoreDecision.SelectedTopLevelTargets;
            Project? restoreProject = _repo.GetProjectById(preparation.ProjectId);
            if (restoreProject is null)
            {
                BackupsViewModel.ShowNotification(
                    AppViewModel.L("Backups.Status.RestoreFailed", "Restore failed."),
                    "Error");
                RuntimeLog.WriteVerbose($"[Restore] Project not found during restore execution for backupId={backupId}.");
                return;
            }

            string projectRoot = ResolveRestoreTarget(restoreProject, selectedRestoreMode);
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                BackupsViewModel.ShowNotification(
                    AppViewModel.L("Backups.Status.RestoreFailed", "Restore failed."),
                    "Error");
                RuntimeLog.WriteVerbose($"[Restore] Restore target resolution failed for backupId={backupId}.");
                return;
            }

            string backupFullPath = preparation.BackupFullPath;
            BackupsViewModel.IsBusy      = true;
            BackupsViewModel.BusyMessage = $"Restoring {preparation.ProjectName}...";
            string restoreCardId = $"restore-{backupId}";
            BackupsViewModel.UpdateActiveBackup(
                restoreCardId,
                preparation.ProjectName,
                0,
                AppViewModel.L("Backups.Status.Restoring", "Restoring backup..."),
                string.Empty,
                allowCancel: false);

            bool restoreSucceeded = false;
            try
            {
                long lastProcessedBytes = 0;
                DateTime lastProgressSampleUtc = DateTime.UtcNow;
                double smoothedBytesPerSecond = 0;

                string BuildRestoreEtaLabel(RestoreProgressUpdate update)
                {
                    if (update.TotalBytes <= 0)
                        return string.Empty;

                    DateTime nowUtc = DateTime.UtcNow;
                    double elapsedSeconds = (nowUtc - lastProgressSampleUtc).TotalSeconds;
                    if (elapsedSeconds >= 0.2 && update.ProcessedBytes >= lastProcessedBytes)
                    {
                        double instantRate = (update.ProcessedBytes - lastProcessedBytes) / elapsedSeconds;
                        if (instantRate >= 0)
                        {
                            smoothedBytesPerSecond = smoothedBytesPerSecond <= 0
                                ? instantRate
                                : (smoothedBytesPerSecond * 0.75) + (instantRate * 0.25);
                        }

                        lastProcessedBytes = update.ProcessedBytes;
                        lastProgressSampleUtc = nowUtc;
                    }

                    string speedLabel = smoothedBytesPerSecond > 0
                        ? $"{BackupSnapshotItem.FormatSize((long)smoothedBytesPerSecond)}/s"
                        : AppViewModel.L("Backups.Progress.Estimating", "Estimating...");

                    string processedLabel = BackupSnapshotItem.FormatSize(Math.Max(0, update.ProcessedBytes));
                    string totalLabel = BackupSnapshotItem.FormatSize(update.TotalBytes);
                    string detailLabel = string.Format(
                        CultureInfo.CurrentCulture,
                        "Restoring ({0}/{1})",
                        processedLabel,
                        totalLabel);

                    return $"{speedLabel} - {detailLabel}";
                }

                void RunRestore(string? encryptionPassword) =>
                    RestoreDirectory(backupFullPath, projectRoot, encryptionPassword, selectedTopLevelTargets, update =>
                    {
                        string label = string.IsNullOrWhiteSpace(update.CurrentFile)
                            ? AppViewModel.L("Backups.Status.Restoring", "Restoring backup...")
                            : update.CurrentFile;
                        string etaLabel = BuildRestoreEtaLabel(update);
                        BackupsViewModel.UpdateActiveBackup(
                            restoreCardId,
                            preparation.ProjectName,
                            update.Percent,
                            label,
                            etaLabel,
                            allowCancel: false);
                    });

                if (!preparation.IsEncrypted)
                {
                    await Task.Run(() =>
                    {
                        RuntimeLog.WriteVerbose($"[Restore] Starting restore for '{preparation.ProjectName}'.");
                        RuntimeLog.WriteVerbose($"[Restore] Source='{backupFullPath}', Target='{projectRoot}'.");
                        RunRestore(null);
                        RuntimeLog.WriteVerbose($"[Restore] Completed restore for '{preparation.ProjectName}'.");
                    });
                    restoreSucceeded = true;
                }
                else
                {
                    var attemptedPasswords = new HashSet<string>(StringComparer.Ordinal);
                    var candidatePasswords = new Queue<string>(ResolveEncryptedRestorePasswordCandidates(preparation.ProjectId));

                    while (true)
                    {
                        if (candidatePasswords.Count == 0)
                        {
                            (bool Confirmed, string Password) passwordPrompt = await ConfirmEncryptedRestorePasswordAsync(preparation.ProjectName);
                            if (!passwordPrompt.Confirmed)
                                return;

                            if (string.IsNullOrWhiteSpace(passwordPrompt.Password))
                            {
                                BackupsViewModel.ShowNotification(
                                    AppViewModel.L("Backups.Restore.EncryptedPasswordRequired", "A password is required to restore encrypted backups."),
                                    "Error");
                                continue;
                            }

                            candidatePasswords.Enqueue(passwordPrompt.Password);
                        }

                        string restorePassword = candidatePasswords.Dequeue();
                        if (!attemptedPasswords.Add(restorePassword))
                            continue;

                        try
                        {
                            await Task.Run(() =>
                            {
                                RuntimeLog.WriteVerbose($"[Restore] Starting restore for '{preparation.ProjectName}'.");
                                RuntimeLog.WriteVerbose($"[Restore] Source='{backupFullPath}', Target='{projectRoot}'.");
                                RunRestore(restorePassword);
                                RuntimeLog.WriteVerbose($"[Restore] Completed restore for '{preparation.ProjectName}'.");
                            });
                            restoreSucceeded = true;
                            break;
                        }
                        catch (Exception ex) when (IsEncryptedRestorePasswordError(ex))
                        {
                            RuntimeLog.WriteVerbose($"[Restore] Restore decryption attempt failed for '{preparation.ProjectName}'. Trying next credential source.");
                        }
                    }
                }

                if (!restoreSucceeded)
                    return;

                restoreSucceeded = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Restore] Restore failed for '{preparation.ProjectName}': {ex.Message}");

                string failureMessage = IsEncryptedRestorePasswordError(ex)
                    ? AppViewModel.L("Backups.Status.RestoreWrongPassword", "Restore failed: invalid password or encrypted backup is corrupted.")
                    : ex.Message;

                Dispatcher.UIThread.Post(() =>
                {
                    BackupsViewModel.BackupCurrentFile = AppViewModel.L("Backups.Status.RestoreFailed", "Restore failed.");
                    BackupsViewModel.BackupEtaText =
                        string.IsNullOrWhiteSpace(BackupsViewModel.BackupEtaText)
                            ? failureMessage
                                : BackupsViewModel.BackupEtaText + " - " + AppViewModel.L("Backups.Status.FailedSuffix", "Failed");
                });
            }
            finally
            {
                if (restoreSucceeded)
                {
                    Project? restoredProject = _repo.GetProjectByName(preparation.ProjectName);
                    bool isDirectRestore = !string.Equals(selectedRestoreMode, ProjectRestoreMode.Sandbox, StringComparison.OrdinalIgnoreCase);
                    if (isDirectRestore && restoredProject != null && restoredProject.NeedsRestore)
                    {
                        _repo.UpdateProjectNeedsRestore(restoredProject.Id, false);
                    }

                    if (!isDirectRestore)
                    {
                        string sandboxPath = projectRoot;
                        SandboxPostRestoreDecision decision = await ConfirmSandboxPostRestoreActionAsync(preparation.ProjectName, sandboxPath);
                        if (decision.Action == SandboxPostRestoreAction.Open)
                        {
                            OpenPathInSystemFileManager(sandboxPath);
                            BackupsViewModel.ShowNotification(
                                Lf(
                                    "Backups.Restore.Sandbox.Completed",
                                    "Restore completed in sandbox folder:\n{0}",
                                    sandboxPath),
                                "Info");
                        }
                        else if (decision.Action == SandboxPostRestoreAction.Apply && restoredProject is not null)
                        {
                            await ApplySandboxRestoreToProjectAsync(
                                restoredProject,
                                preparation.ProjectName,
                                sandboxPath,
                                decision.DeleteAfterApply);
                        }
                        else
                        {
                            BackupsViewModel.ShowNotification(
                                Lf(
                                    "Backups.Restore.Sandbox.Completed",
                                    "Restore completed in sandbox folder:\n{0}",
                                    sandboxPath),
                                "Info");
                        }
                    }
                }
                BackupsViewModel.RemoveActiveBackup(restoreCardId);
                BackupsViewModel.IsBusy      = false;
                BackupsViewModel.BusyMessage = string.Empty;
            }
        }

        private static bool IsEncryptedRestorePasswordError(Exception ex)
        {
            Exception? current = ex;
            while (current is not null)
            {
                if (string.Equals(
                    current.Message,
                    BackupArchiveCryptoService.InvalidPasswordOrCorruptedMessage,
                    StringComparison.Ordinal))
                {
                    return true;
                }
                current = current.InnerException;
            }

            return false;
        }

        private readonly struct RestoreProgressUpdate
        {
            public RestoreProgressUpdate(double percent, string currentFile, long processedBytes, long totalBytes)
            {
                Percent = percent;
                CurrentFile = currentFile ?? string.Empty;
                ProcessedBytes = processedBytes;
                TotalBytes = totalBytes;
            }

            public double Percent { get; }
            public string CurrentFile { get; }
            public long ProcessedBytes { get; }
            public long TotalBytes { get; }
        }

        private static void RestoreDirectory(
            string sourceDir,
            string targetDir,
            string? encryptionPassword,
            IReadOnlyList<string>? selectedTopLevelTargets,
            Action<RestoreProgressUpdate>? progress)
        {
            if (string.IsNullOrWhiteSpace(sourceDir))
                throw new ArgumentException("Source directory is required.", nameof(sourceDir));

            if (string.IsNullOrWhiteSpace(targetDir))
                throw new ArgumentException("Target directory is required.", nameof(targetDir));

            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source directory '{sourceDir}' does not exist.");

            // Ensure target root exists
            Directory.CreateDirectory(targetDir);

            string archivePath = Path.Combine(sourceDir, BackupArchiveCryptoService.PlainArchiveFileName);
            if (File.Exists(archivePath))
            {
                ExtractArchiveWithProgress(archivePath, targetDir, selectedTopLevelTargets, progress);
                return;
            }

            string encryptedArchivePath = Path.Combine(sourceDir, BackupArchiveCryptoService.EncryptedArchiveFileName);
            if (File.Exists(encryptedArchivePath))
            {
                if (string.IsNullOrWhiteSpace(encryptionPassword))
                {
                    throw new InvalidOperationException(
                        "A password is required to restore encrypted backups.");
                }

                RestoreEncryptedArchiveWithProgress(sourceDir, targetDir, encryptionPassword, selectedTopLevelTargets, progress);
                return;
            }

            HashSet<string>? selectedTopLevels = BuildSelectedTopLevelSet(selectedTopLevelTargets);

            // Create all directories
            foreach (string dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDir, dirPath);
                if (!ShouldIncludeRelativePath(relative, selectedTopLevels))
                    continue;
                string target   = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(target);
            }

            // Copy all files, overwriting existing ones but not deleting extras.
            CopyDirectoryWithProgress(sourceDir, targetDir, selectedTopLevelTargets, 0, 100, progress);
        }

        private static void ExtractArchiveWithProgress(
            string archivePath,
            string targetDir,
            IReadOnlyList<string>? selectedTopLevelTargets,
            Action<RestoreProgressUpdate>? progress)
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            HashSet<string>? selectedTopLevels = BuildSelectedTopLevelSet(selectedTopLevelTargets);
            ZipArchiveEntry[] entries = [.. archive.Entries.Where(entry => ShouldIncludeRelativePath(entry.FullName, selectedTopLevels))];
            int totalEntries = entries.Length;
            int processed = 0;
            long totalBytes = entries.Where(e => !string.IsNullOrEmpty(e.Name)).Sum(e => Math.Max(0, e.Length));
            long processedBytes = 0;

            foreach (ZipArchiveEntry? entry in entries)
            {
                string destinationPath = GetSafeArchiveEntryPath(targetDir, entry.FullName);
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                }
                else
                {
                    string? parent = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);

                    entry.ExtractToFile(destinationPath, overwrite: true);
                }

                processed++;
                if (!string.IsNullOrEmpty(entry.Name))
                    processedBytes += Math.Max(0, entry.Length);

                progress?.Invoke(new RestoreProgressUpdate(
                    totalEntries == 0 ? 100 : processed * 100d / totalEntries,
                    entry.FullName,
                    processedBytes,
                    totalBytes));
            }
        }


        private static string GetSafeArchiveEntryRelativePath(string entryFullName)
        {
            string normalized = (entryFullName ?? string.Empty)
                .Replace('/', Path.DirectorySeparatorChar)
                .Trim();

            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            if (Path.IsPathFullyQualified(normalized))
                throw new InvalidDataException($"Archive entry '{entryFullName}' is absolute.");

            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vaultsync-archive-root"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(root, normalized));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive entry '{entryFullName}' escapes the extraction root.");

            return Path.GetRelativePath(root, candidate);
        }

        private static string GetSafeArchiveEntryPath(string root, string entryFullName)
        {
            string relative = GetSafeArchiveEntryRelativePath(entryFullName);
            string normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
            if (!candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Archive entry '{entryFullName}' escapes the extraction destination.");

            return candidate;
        }

        private static void RestoreEncryptedArchiveWithProgress(
            string sourceDir,
            string targetDir,
            string password,
            IReadOnlyList<string>? selectedTopLevelTargets,
            Action<RestoreProgressUpdate>? progress)
        {
            string stagingRoot = Path.Combine(Path.GetTempPath(), $"vaultsync-restore-{Guid.NewGuid():N}");
            string stagingExtracted = Path.Combine(stagingRoot, "content");
            string stagingArchive = Path.Combine(stagingRoot, BackupArchiveCryptoService.PlainArchiveFileName);

            try
            {
                Directory.CreateDirectory(stagingExtracted);
                progress?.Invoke(new RestoreProgressUpdate(5, "Decrypting backup...", 0, 0));

                var cryptoService = new BackupArchiveCryptoService();
                BackupArchiveCryptoService.DecryptArchiveToPlainZip(sourceDir, password, stagingArchive);
                progress?.Invoke(new RestoreProgressUpdate(30, "Decrypting backup...", 0, 0));

                ExtractArchiveWithProgress(stagingArchive, stagingExtracted, selectedTopLevelTargets, update =>
                {
                    double mapped = 30 + (update.Percent * 0.5);
                    progress?.Invoke(new RestoreProgressUpdate(
                        Math.Clamp(mapped, 30, 80),
                        update.CurrentFile,
                        update.ProcessedBytes,
                        update.TotalBytes));
                });

                progress?.Invoke(new RestoreProgressUpdate(82, "Restoring backup...", 0, 0));
                CopyDirectoryWithProgress(stagingExtracted, targetDir, selectedTopLevelTargets, 82, 100, progress);
            }
            finally
            {
                if (Directory.Exists(stagingRoot))
                {
                    try
                    {
                        DeleteDirectoryRobust(stagingRoot, out _);
                    }
                    catch
                    {
                        // best-effort cleanup
                    }
                }
            }
        }

        private static void CopyDirectoryWithProgress(
            string sourceDir,
            string targetDir,
            IReadOnlyList<string>? selectedTopLevelTargets,
            double startPercent,
            double endPercent,
            Action<RestoreProgressUpdate>? progress)
        {
            HashSet<string>? selectedTopLevels = BuildSelectedTopLevelSet(selectedTopLevelTargets);
            string[] files = [.. Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories).Where(filePath => ShouldIncludeRelativePath(Path.GetRelativePath(sourceDir, filePath), selectedTopLevels))];
            int totalFiles = files.Length;
            long totalBytes = files
                .Select(filePath => new FileInfo(filePath))
                .Sum(fileInfo => Math.Max(0, fileInfo.Length));
            long processedBytes = 0;
            int processed = 0;
            foreach (string? filePath in files)
            {
                long fileLength = Math.Max(0, new FileInfo(filePath).Length);
                string relative = Path.GetRelativePath(sourceDir, filePath);
                string target = Path.Combine(targetDir, relative);

                string? parentDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parentDir))
                    Directory.CreateDirectory(parentDir);

                File.Copy(filePath, target, overwrite: true);
                processedBytes += fileLength;
                processed++;
                if (progress is not null)
                {
                    double ratio = totalFiles == 0 ? 1d : processed / (double)totalFiles;
                    double value = startPercent + ((endPercent - startPercent) * ratio);
                    progress(new RestoreProgressUpdate(value, relative, processedBytes, totalBytes));
                }
            }

            if (totalFiles == 0)
                progress?.Invoke(new RestoreProgressUpdate(endPercent, string.Empty, 0, 0));
        }

        private static HashSet<string>? BuildSelectedTopLevelSet(IReadOnlyList<string>? selectedTopLevelTargets)
        {
            if (selectedTopLevelTargets is null || selectedTopLevelTargets.Count == 0)
                return null;

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in selectedTopLevelTargets)
            {
                string? normalized = value?.Trim();
                if (!string.IsNullOrWhiteSpace(normalized))
                    result.Add(normalized);
            }

            return result.Count == 0 ? null : result;
        }

        private static bool ShouldIncludeRelativePath(string? relativePath, HashSet<string>? selectedTopLevels)
        {
            if (selectedTopLevels is null || selectedTopLevels.Count == 0)
                return true;

            string topLevel = GetTopLevelSegment(relativePath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(topLevel))
                return false;

            return selectedTopLevels.Contains(topLevel);
        }

        private static string GetTopLevelSegment(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return string.Empty;

            string normalized = relativePath.Replace('\\', '/').Trim('/');
            if (normalized.Length == 0)
                return string.Empty;

            int slashIndex = normalized.IndexOf('/');
            return slashIndex >= 0
                ? normalized.Substring(0, slashIndex)
                : normalized;
        }

    }
}
