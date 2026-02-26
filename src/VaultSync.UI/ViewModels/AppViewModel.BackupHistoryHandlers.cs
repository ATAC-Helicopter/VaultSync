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
            RunDetached(() => OnDeleteBackupRequestedAsync(snapshot), nameof(OnDeleteBackupRequestedAsync));
        }

        private async Task OnDeleteBackupRequestedAsync(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            if (!int.TryParse(snapshot.Id, out var backupId))
                return;

            var preparation = await Task.Run(() => PrepareDeleteBackup(backupId));
            if (!preparation.IsReady)
                return;
            var backup      = preparation.Backup;
            var snapshotId  = preparation.SnapshotId;
            var projectId   = preparation.ProjectId;
            var backupRoot  = preparation.BackupRoot;
            var projectName = preparation.ProjectName;
            var cardId = $"delete-{backupId}";
            DestinationResolution? deleteResolution = null;

            var deleteContext = await Task.Run(() =>
            {
                var cfg = AppConfigStore.Load();
                var destinations = GetAllDestinations(cfg);
                var matchedDestination = FindDestinationForBackup(backup, destinations, backupRoot);
                var hasCredentialProfile = HasCredentialProfile(cfg, matchedDestination);
                return (cfg, matchedDestination, hasCredentialProfile);
            });
            var cfg = deleteContext.cfg;
            var matchedDestination = deleteContext.matchedDestination;
            var hasCredentialProfile = deleteContext.hasCredentialProfile;

            var confirm = await ConfirmDeleteBackupAsync(projectName, snapshot.Timestamp);
            if (!confirm)
            {
                return;
            }

            BackupsViewModel.PinExpandedProject(snapshot.ProjectId);

            BackupsViewModel.ShowTransientOperation(cardId, projectName, L("Backups.Status.Deleting", "Deleting backup files..."));

            BackupsViewModel.IsBusy      = true;
            BackupsViewModel.BusyMessage = L("Backups.Status.Deleting", L("Backups.Status.Deleting", "Deleting backup files..."));

            var deleteSucceeded = false;
            var deleteError = string.Empty;
            var permissionDenied = false;
            NetworkCredentialProfile? tempProfile = null;

            try
            {
                async Task TryDeleteAsync(bool forceCredentials, NetworkCredentialProfile? overrideProfile = null)
                {
                    if (matchedDestination is not null)
                    {
                        var destToUse = matchedDestination;
                        var rootSubPath = string.Empty;
                        if (forceCredentials)
                        {
                            var pathToUse = matchedDestination.Path;
                            if (OperatingSystem.IsWindows() && TryResolveUncPath(pathToUse, out var uncPath))
                            {
                                pathToUse = uncPath;
                            }
                            if (OperatingSystem.IsWindows() && TrySplitUncPath(pathToUse, out var uncRoot, out var uncSubPath))
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

                        var profile = overrideProfile;
                        if (profile is null)
                        {
                            profile = string.IsNullOrWhiteSpace(destToUse.CredentialName)
                                ? null
                                : cfg.Network.Credentials.FirstOrDefault(c =>
                                    c.Name.Equals(destToUse.CredentialName, StringComparison.OrdinalIgnoreCase));
                        }

                        var resolution = _networkMountService.PrepareDestination(destToUse, profile);
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

                    var relativePath = backup.Path ?? string.Empty;
                    var fullPath     = Path.GetFullPath(Path.Combine(backupRoot, relativePath));

                    await Task.Run(() =>
                    {
                        try
                        {
                            if (Directory.Exists(fullPath))
                            {
                                deleteSucceeded = DeleteDirectoryRobust(fullPath, out var deleteFailure);
                                if (!deleteSucceeded && string.IsNullOrWhiteSpace(deleteError))
                                    deleteError = deleteFailure ?? L("Backups.Delete.Error", "Delete failed");
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
                    var retry = await ConfirmDeleteWithCredentialsAsync();
                    if (retry)
                    {
                        permissionDenied = false;
                        deleteError = string.Empty;
                        await TryDeleteAsync(forceCredentials: true);
                    }
                }

                if (!deleteSucceeded && permissionDenied && matchedDestination is not null && !hasCredentialProfile)
                {
                    var retry = await ConfirmDeleteWithTemporaryCredentialsAsync();
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
                        var title = L("Backups.Delete.ForceCredentialsTitle", "Credentials required");
                        var msg = L("Backups.Delete.ForceCredentialsMissing",
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
                    var title = L("Backups.Delete.FailedTitle", "Backup delete failed");
                    var msg = Lf("Backups.Delete.FailedMessage", "Could not delete backup '{0}'.", projectName);
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
                var finalLabel = deleteSucceeded
                    ? L("Backups.Status.Deleted", "Deleted")
                    : L("Backups.Status.FailedSuffix", "Failed");
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

            if (!int.TryParse(snapshot.Id, out var backupId))
                return;

            OpenBackupFolder(backupId);
        }

        private void OnOpenSettingsRequested()
        {
            NavigateSettings?.Execute(null);
        }

        private async Task<bool> ConfirmDeleteBackupAsync(string projectName, DateTime timestamp)
        {
            var cfg = await Task.Run(AppConfigStore.Load);
            if (!cfg.Behavior.ConfirmDeleteBackup)
                return true;

            var timeLabel = timestamp.ToString("g", CultureInfo.CurrentCulture);
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = L("Backups.Delete.Title", "Delete backup?"),
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
                    Text = L("Backups.Delete.Warning", "This removes data on the destination."),
                    TextWrapping = TextWrapping.Wrap
                };
                if (GetBrush("TextSecondary") is { } warningBrush)
                {
                    warning.Foreground = warningBrush;
                }

                var dontShowAgain = new CheckBox
                {
                    Content = L("Backups.Delete.DontShowAgain", "Don't show again"),
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
                    Content = L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var deleteButton = new Button
                {
                    Content = L("Backups.Delete.Confirm", "Delete backup"),
                    MinWidth = 140
                };
                deleteButton.Classes.Add("action-primary");

                Window? window = null;
                var confirmed = false;
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
                    Title = L("Backups.Delete.Title", "Delete backup?"),
                    Content = card,
                    CanResize = false,
                    Width = 540,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                var owner = GetMainWindow();
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
            if (Application.Current?.Resources.TryGetValue(key, out var value) == true)
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
                    Text = L("Backups.Delete.ForceCredentialsTitle", "Credentials required"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var question = new TextBlock
                {
                    Text = L("Backups.Delete.ForceCredentialsPrompt",
                        "Use destination credentials to force delete this backup?"),
                    TextWrapping = TextWrapping.Wrap
                };

                var hint = new TextBlock
                {
                    Text = L("Backups.Delete.ForceCredentialsHint",
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
                    Content = L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var forceButton = new Button
                {
                    Content = L("Backups.Delete.ForceCredentialsConfirm", "Use credentials"),
                    MinWidth = 160
                };
                forceButton.Classes.Add("action-primary");

                Window? window = null;
                var confirmed = false;
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
                    Title = L("Backups.Delete.ForceCredentialsTitle", "Credentials required"),
                    Content = card,
                    CanResize = false,
                    Width = 540,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                var owner = GetMainWindow();
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
                    Text = L("Backups.Delete.ForceCredentialsTitle", "Credentials required"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var question = new TextBlock
                {
                    Text = L("Backups.Delete.CredentialsPrompt",
                        "Enter destination credentials to force delete this backup. These credentials are used once and not saved."),
                    TextWrapping = TextWrapping.Wrap
                };

                var usernameLabel = new TextBlock
                {
                    Text = L("Backups.Delete.CredentialsUsername", "Username"),
                    FontWeight = FontWeight.SemiBold
                };
                var usernameBox = new TextBox
                {
                    Width = 320
                };

                var passwordLabel = new TextBlock
                {
                    Text = L("Backups.Delete.CredentialsPassword", "Password"),
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
                    Text = L("Backups.Delete.ForceCredentialsMissing",
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
                    Content = L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var forceButton = new Button
                {
                    Content = L("Backups.Delete.ForceCredentialsConfirm", "Use credentials"),
                    MinWidth = 160
                };
                forceButton.Classes.Add("action-primary");

                Window? window = null;
                var confirmed = false;
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
                    Title = L("Backups.Delete.ForceCredentialsTitle", "Credentials required"),
                    Content = card,
                    CanResize = false,
                    Width = 540,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                var owner = GetMainWindow();
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

                var username = usernameBox.Text?.Trim() ?? string.Empty;
                var password = passwordBox.Text ?? string.Empty;
                if (confirmed && (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)))
                    return (false, string.Empty, string.Empty);

                return (confirmed, username, password);
            });
        }

        /// <summary>
        /// Deletes a directory tree, clearing read-only attributes to avoid UnauthorizedAccess on Windows.
        /// </summary>
        private static bool DeleteDirectoryRobust(string path, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return true;

            // Clear read-only attributes on files and dirs before deletion.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
                }
                catch
                {
                    // ignore individual failures; deletion will surface issues later
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).Reverse())
            {
                try
                {
                    var attrs = File.GetAttributes(dir);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(dir, attrs & ~FileAttributes.ReadOnly);
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

            var trimmed = path.TrimStart('\\', '/');
            var parts = trimmed.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return false;

            root = $@"\\{parts[0]}\{parts[1]}";
            if (parts.Length > 2)
            {
                subPath = Path.Combine(parts.Skip(2).ToArray());
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

            var bufferSize = 0;
            var result = WNetGetUniversalName(path, UniversalNameInfoLevel, IntPtr.Zero, ref bufferSize);
            if (result != ErrorMoreData || bufferSize <= 0)
                return false;

            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                result = WNetGetUniversalName(path, UniversalNameInfoLevel, buffer, ref bufferSize);
                if (result != 0)
                    return false;

                var info = Marshal.PtrToStructure<UniversalNameInfo>(buffer);
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
            var backup = _repo.GetBackupById(backupId);
            if (backup is null)
                return DeleteBackupPreparation.Failure;

            var cfg = AppConfigStore.Load();
            var destinations = GetAllDestinations(cfg);
            var backupRoot = ResolveDestinationRootForBackup(backup, destinations, cfg.Backups.BackupRoot);
            if (string.IsNullOrWhiteSpace(backupRoot))
                return DeleteBackupPreparation.Failure;

            var project = _repo.GetProjectById(backup.ProjectId);
            var projectName = project?.Name ?? "Backup";

            return new DeleteBackupPreparation(true, backup, backupRoot, projectName, project?.Id ?? 0, backup.SnapshotId);
        }

        private static BackupDestination? FindDestinationForBackup(
            Backup backup,
            IReadOnlyList<BackupDestination> destinations,
            string backupRoot)
        {
            if (!string.IsNullOrWhiteSpace(backup.DestinationAlias))
            {
                var aliasMatch = destinations.FirstOrDefault(d =>
                    string.Equals(d.Alias ?? string.Empty, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.Path ?? string.Empty, backup.DestinationAlias, StringComparison.OrdinalIgnoreCase));
                if (aliasMatch is not null)
                    return aliasMatch;
            }

            if (!string.IsNullOrWhiteSpace(backup.DestinationPath))
            {
                var pathMatch = destinations.FirstOrDefault(d =>
                    string.Equals(d.Path ?? string.Empty, backup.DestinationPath, StringComparison.OrdinalIgnoreCase));
                if (pathMatch is not null)
                    return pathMatch;
            }

            var rootMatch = destinations.FirstOrDefault(d =>
                string.Equals(d.Path ?? string.Empty, backupRoot, StringComparison.OrdinalIgnoreCase));
            if (rootMatch is not null)
                return rootMatch;

            var prefixMatch = destinations.FirstOrDefault(d =>
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
            var backup = _repo.GetBackupById(backupId);
            if (backup is null)
            {
                Console.WriteLine($"[Restore] Backup id {backupId} not found.");
                return RestoreBackupPreparation.Failure;
            }

            var cfg = AppConfigStore.Load();
            var destinations = GetAllDestinations(cfg);
            var backupRoot = ResolveDestinationRootForBackup(backup, destinations, cfg.Backups.BackupRoot);
            if (string.IsNullOrWhiteSpace(backupRoot))
            {
                Console.WriteLine($"[Restore] No backup root found for id={backupId}, path='{backup.Path}', dest='{backup.DestinationPath}', alias='{backup.DestinationAlias}'.");
                return RestoreBackupPreparation.Failure;
            }

            if (string.IsNullOrWhiteSpace(backup.Path))
            {
                Console.WriteLine($"[Restore] Backup path missing for id={backupId}. Root='{backupRoot}', rel='{backup.Path}'.");
                return RestoreBackupPreparation.Failure;
            }

            var backupFullPath = Path.Combine(backupRoot, backup.Path);
            if (!Directory.Exists(backupFullPath))
            {
                Console.WriteLine($"[Restore] Backup path missing for id={backupId}. Root='{backupRoot}', rel='{backup.Path}', full='{backupFullPath}'.");
                return RestoreBackupPreparation.Failure;
            }

            var project = _repo.GetProjectById(backup.ProjectId);
            if (project is null)
            {
                Console.WriteLine($"[Restore] Project id {backup.ProjectId} not found for backup id {backupId}.");
                return RestoreBackupPreparation.Failure;
            }

            var projectRoot = ResolveRestoreTarget(project);
            if (string.IsNullOrWhiteSpace(projectRoot))
                return RestoreBackupPreparation.Failure;

            var encryptedArchivePath = Path.Combine(backupFullPath, BackupArchiveCryptoService.EncryptedArchiveFileName);
            var isEncrypted = backup.IsEncrypted || File.Exists(encryptedArchivePath);

            return new RestoreBackupPreparation(
                true,
                backupFullPath,
                projectRoot,
                project.Id,
                project.Name,
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
                false,
                false,
                BackupModes.Full);
        }

        private List<string> ResolveEncryptedRestorePasswordCandidates(int projectId)
        {
            var project = _repo.GetProjectById(projectId);
            if (project is null)
                return new List<string>();

            var cfg = AppConfigStore.Load();
            var keyRefs = BackupEncryptionPolicyResolver.ResolveRestoreKeyRefs(project, cfg.Backups.Encryption);
            if (keyRefs.Count == 0)
                return new List<string>();

            var candidates = new List<string>(keyRefs.Count);
            foreach (var keyRef in keyRefs)
            {
                var secret = _credentialVault.GetSecret(
                    keyRef,
                    BackupEncryptionSecretUsername,
                    preferKeychain: true,
                    fallbackPlaintext: null);

                if (!string.IsNullOrWhiteSpace(secret))
                    candidates.Add(secret);
            }

            return candidates;
        }

        private string ResolveRestoreTarget(Project project)
        {
            if (!string.IsNullOrWhiteSpace(project.RootPath) && Directory.Exists(project.RootPath))
                return project.RootPath;

            var cfg = AppConfigStore.Load();
            if (!string.IsNullOrWhiteSpace(cfg.ProjectsRoot))
            {
                var projectsRoot = Path.Combine(cfg.ProjectsRoot, project.Name);
                Directory.CreateDirectory(projectsRoot);
                _repo.UpdateProjectPath(project.Name, projectsRoot, out _);
                Console.WriteLine($"[Restore] Project root missing. Using ProjectsRoot '{projectsRoot}'.");
                return projectsRoot;
            }

            var fallbackRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "VaultSync Restores",
                project.Name);

            Directory.CreateDirectory(fallbackRoot);
            _repo.UpdateProjectPath(project.Name, fallbackRoot, out _);

            Console.WriteLine($"[Restore] Project root missing. Using fallback restore path '{fallbackRoot}'.");
            return fallbackRoot;
        }

        private AutoBackupPreparation PrepareAutoBackupRun()
        {
            var cfg = AppConfigStore.Load();
            if (!cfg.Backups.EnableAutoBackups)
                return AutoBackupPreparation.Failure("disabled");

            var destinations = GetAllDestinations(cfg);
            if (destinations.Count == 0)
                return AutoBackupPreparation.Failure("no_destination");

            var projects = _repo.GetAllProjects().ToList();
            var disabled = cfg.Backups.AutoBackupDisabledProjects?.ToHashSet() ?? new HashSet<int>();

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
                    Text = L("Backups.Restore.EncryptedPasswordTitle", "Encrypted backup password"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var prompt = new TextBlock
                {
                    Text = string.Format(
                        CultureInfo.CurrentCulture,
                        L("Backups.Restore.EncryptedPasswordPrompt", "Enter the encryption password to restore '{0}'."),
                        projectName),
                    TextWrapping = TextWrapping.Wrap
                };

                var passwordLabel = new TextBlock
                {
                    Text = L("Backups.Restore.EncryptedPasswordLabel", "Password"),
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
                    Content = L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var restoreButton = new Button
                {
                    Content = L("Backups.Section.Restore", "Restore"),
                    MinWidth = 140
                };
                restoreButton.Classes.Add("action-primary");

                Window? window = null;
                var confirmed = false;
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
                    Title = L("Backups.Restore.EncryptedPasswordTitle", "Encrypted backup password"),
                    Content = card,
                    CanResize = false,
                    Width = 540,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                var owner = GetMainWindow();
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

                var password = passwordBox.Text ?? string.Empty;
                if (confirmed && string.IsNullOrWhiteSpace(password))
                    return (false, string.Empty);

                return (confirmed, password);
            });
        }

        private async Task<bool> ConfirmRestoreBackupAsync(RestoreBackupPreparation preparation)
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = L("Backups.Restore.ConfirmTitle", "Restore backup?"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var targetLabel = string.Format(
                    CultureInfo.CurrentCulture,
                    L("Backups.Restore.ConfirmPrompt", "Restore '{0}' into:\n{1}"),
                    preparation.ProjectName,
                    preparation.ProjectRoot);

                var question = new TextBlock
                {
                    Text = targetLabel,
                    TextWrapping = TextWrapping.Wrap
                };

                var guidanceHeader = new TextBlock
                {
                    Text = L("Backups.Restore.GuidanceHeader", "What happens next"),
                    FontWeight = FontWeight.SemiBold
                };

                var backupTypeLabel = preparation.IsImported
                    ? L("Backups.Snapshot.Type.Imported", "Imported")
                    : string.Equals(preparation.BackupMode, BackupModes.Incremental, StringComparison.OrdinalIgnoreCase)
                        ? L("Backups.Snapshot.Type.Incremental", "Incremental")
                        : L("Backups.Snapshot.Type.Full", "Full");

                var guidanceLines = new[]
                {
                    Lf("Backups.Restore.GuidanceType", "Type: {0}", backupTypeLabel),
                    L("Backups.Restore.GuidanceOverwrite", "Files with matching paths are overwritten by restored files."),
                    L("Backups.Restore.GuidanceKeepExtra", "Files that exist only in the current project folder are kept."),
                    preparation.IsEncrypted
                        ? L("Backups.Restore.GuidanceEncrypted", "If needed, VaultSync will ask for the encryption password before restore starts.")
                        : L("Backups.Restore.GuidancePlain", "No encryption password is required for this backup.")
                };

                var guidancePanel = new StackPanel { Spacing = 4 };
                foreach (var line in guidanceLines)
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

                var buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10
                };

                var cancelButton = new Button
                {
                    Content = L("Common.Cancel", "Cancel"),
                    MinWidth = 120,
                    IsCancel = true
                };
                cancelButton.Classes.Add("action-ghost");

                var restoreButton = new Button
                {
                    Content = L("Backups.Section.Restore", "Restore"),
                    MinWidth = 140,
                    IsDefault = true
                };
                restoreButton.Classes.Add("action-primary");

                Window? window = null;
                var confirmed = false;
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
                    Title = L("Backups.Restore.ConfirmTitle", "Restore backup?"),
                    Content = card,
                    CanResize = false,
                    Width = 620,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                var owner = GetMainWindow();
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
            RunDetached(() => OnRestoreBackupRequestedAsync(snapshot), nameof(OnRestoreBackupRequestedAsync));
        }

        private async Task OnRestoreBackupRequestedAsync(BackupSnapshotItem? snapshot)
        {
            if (snapshot is null)
                return;

            if (!int.TryParse(snapshot.Id, out var backupId))
                return;


            var preparation = await Task.Run(() => PrepareRestoreBackup(backupId));
            if (!preparation.IsReady)
            {
                BackupsViewModel.ShowNotification(
                    L("Backups.Status.RestoreFailed", "Restore failed."),
                    "Error");
                Console.WriteLine($"[Restore] Restore preparation failed for backupId={backupId}.");
                return;
            }

            var restoreConfirmed = await ConfirmRestoreBackupAsync(preparation);
            if (!restoreConfirmed)
                return;

            var projectRoot   = preparation.ProjectRoot;
            var backupFullPath = preparation.BackupFullPath;
            BackupsViewModel.IsBusy      = true;
            BackupsViewModel.BusyMessage = $"Restoring {preparation.ProjectName}...";
            var restoreCardId = $"restore-{backupId}";
            BackupsViewModel.UpdateActiveBackup(
                restoreCardId,
                preparation.ProjectName,
                0,
                L("Backups.Status.Restoring", "Restoring backup..."),
                string.Empty,
                allowCancel: false);

            var restoreSucceeded = false;
            try
            {
                void RunRestore(string? encryptionPassword) =>
                    RestoreDirectory(backupFullPath, projectRoot, encryptionPassword, (percent, currentFile) =>
                    {
                        var label = string.IsNullOrWhiteSpace(currentFile)
                            ? L("Backups.Status.Restoring", "Restoring backup...")
                            : currentFile;
                        BackupsViewModel.UpdateActiveBackup(
                            restoreCardId,
                            preparation.ProjectName,
                            percent,
                            label,
                            string.Empty,
                            allowCancel: false);
                    });

                if (!preparation.IsEncrypted)
                {
                    await Task.Run(() =>
                    {
                        Console.WriteLine($"[Restore] Starting restore for '{preparation.ProjectName}'.");
                        Console.WriteLine($"[Restore] Source='{backupFullPath}', Target='{projectRoot}'.");
                        RunRestore(null);
                        Console.WriteLine($"[Restore] Completed restore for '{preparation.ProjectName}'.");
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
                            var passwordPrompt = await ConfirmEncryptedRestorePasswordAsync(preparation.ProjectName);
                            if (!passwordPrompt.Confirmed)
                                return;

                            if (string.IsNullOrWhiteSpace(passwordPrompt.Password))
                            {
                                BackupsViewModel.ShowNotification(
                                    L("Backups.Restore.EncryptedPasswordRequired", "A password is required to restore encrypted backups."),
                                    "Error");
                                continue;
                            }

                            candidatePasswords.Enqueue(passwordPrompt.Password);
                        }

                        var restorePassword = candidatePasswords.Dequeue();
                        if (!attemptedPasswords.Add(restorePassword))
                            continue;

                        try
                        {
                            await Task.Run(() =>
                            {
                                Console.WriteLine($"[Restore] Starting restore for '{preparation.ProjectName}'.");
                                Console.WriteLine($"[Restore] Source='{backupFullPath}', Target='{projectRoot}'.");
                                RunRestore(restorePassword);
                                Console.WriteLine($"[Restore] Completed restore for '{preparation.ProjectName}'.");
                            });
                            restoreSucceeded = true;
                            break;
                        }
                        catch (Exception ex) when (IsEncryptedRestorePasswordError(ex))
                        {
                            Console.WriteLine($"[Restore] Restore decryption attempt failed for '{preparation.ProjectName}'. Trying next credential source.");
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

                var failureMessage = IsEncryptedRestorePasswordError(ex)
                    ? L("Backups.Status.RestoreWrongPassword", "Restore failed: invalid password or encrypted backup is corrupted.")
                    : ex.Message;

                Dispatcher.UIThread.Post(() =>
                {
                    BackupsViewModel.BackupCurrentFile = L("Backups.Status.RestoreFailed", "Restore failed.");
                    BackupsViewModel.BackupEtaText =
                        string.IsNullOrWhiteSpace(BackupsViewModel.BackupEtaText)
                            ? failureMessage
                                : BackupsViewModel.BackupEtaText + " - " + L("Backups.Status.FailedSuffix", "Failed");
                });
            }
            finally
            {
                if (restoreSucceeded)
                {
                    var restoredProject = _repo.GetProjectByName(preparation.ProjectName);
                    if (restoredProject != null && restoredProject.NeedsRestore)
                    {
                        _repo.UpdateProjectNeedsRestore(restoredProject.Id, false);
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

        private static void RestoreDirectory(string sourceDir, string targetDir, string? encryptionPassword, Action<double, string>? progress)
        {
            if (string.IsNullOrWhiteSpace(sourceDir))
                throw new ArgumentException("Source directory is required.", nameof(sourceDir));

            if (string.IsNullOrWhiteSpace(targetDir))
                throw new ArgumentException("Target directory is required.", nameof(targetDir));

            if (!Directory.Exists(sourceDir))
                throw new DirectoryNotFoundException($"Source directory '{sourceDir}' does not exist.");

            // Ensure target root exists
            Directory.CreateDirectory(targetDir);

            var archivePath = Path.Combine(sourceDir, BackupArchiveCryptoService.PlainArchiveFileName);
            if (File.Exists(archivePath))
            {
                ExtractArchiveWithProgress(archivePath, targetDir, progress);
                return;
            }

            var encryptedArchivePath = Path.Combine(sourceDir, BackupArchiveCryptoService.EncryptedArchiveFileName);
            if (File.Exists(encryptedArchivePath))
            {
                if (string.IsNullOrWhiteSpace(encryptionPassword))
                {
                    throw new InvalidOperationException(
                        "A password is required to restore encrypted backups.");
                }

                RestoreEncryptedArchiveWithProgress(sourceDir, targetDir, encryptionPassword, progress);
                return;
            }

            // Create all directories
            foreach (var dirPath in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDir, dirPath);
                var target   = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(target);
            }

            // Copy all files, overwriting existing ones but not deleting extras.
            CopyDirectoryWithProgress(sourceDir, targetDir, 0, 100, progress);
        }

        private static void ExtractArchiveWithProgress(string archivePath, string targetDir, Action<double, string>? progress)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var totalEntries = archive.Entries.Count;
            var processed = 0;

            foreach (var entry in archive.Entries)
            {
                var destinationPath = Path.Combine(targetDir, entry.FullName);
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                }
                else
                {
                    var parent = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(parent))
                        Directory.CreateDirectory(parent);

                    entry.ExtractToFile(destinationPath, overwrite: true);
                }

                processed++;
                progress?.Invoke(totalEntries == 0 ? 100 : processed * 100d / totalEntries, entry.FullName);
            }
        }

        private static void RestoreEncryptedArchiveWithProgress(
            string sourceDir,
            string targetDir,
            string password,
            Action<double, string>? progress)
        {
            var stagingRoot = Path.Combine(Path.GetTempPath(), $"vaultsync-restore-{Guid.NewGuid():N}");
            var stagingExtracted = Path.Combine(stagingRoot, "content");
            var stagingArchive = Path.Combine(stagingRoot, BackupArchiveCryptoService.PlainArchiveFileName);

            try
            {
                Directory.CreateDirectory(stagingExtracted);
                progress?.Invoke(5, "Decrypting backup...");

                var cryptoService = new BackupArchiveCryptoService();
                cryptoService.DecryptArchiveToPlainZip(sourceDir, password, stagingArchive);
                progress?.Invoke(30, "Decrypting backup...");

                ExtractArchiveWithProgress(stagingArchive, stagingExtracted, (percent, currentFile) =>
                {
                    var mapped = 30 + (percent * 0.5);
                    progress?.Invoke(Math.Clamp(mapped, 30, 80), currentFile);
                });

                progress?.Invoke(82, "Restoring backup...");
                CopyDirectoryWithProgress(stagingExtracted, targetDir, 82, 100, progress);
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
            double startPercent,
            double endPercent,
            Action<double, string>? progress)
        {
            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            var totalFiles = files.Length;
            var processed = 0;
            foreach (var filePath in files)
            {
                var relative = Path.GetRelativePath(sourceDir, filePath);
                var target = Path.Combine(targetDir, relative);

                var parentDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parentDir))
                    Directory.CreateDirectory(parentDir);

                File.Copy(filePath, target, overwrite: true);
                processed++;
                if (progress is not null)
                {
                    var ratio = totalFiles == 0 ? 1d : processed / (double)totalFiles;
                    var value = startPercent + ((endPercent - startPercent) * ratio);
                    progress(value, relative);
                }
            }

            if (totalFiles == 0)
                progress?.Invoke(endPercent, string.Empty);
        }

    }
}
