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

            var restoreMode = ProjectRestoreMode.Normalize(project.RestoreMode);
            var projectRoot = ResolveRestoreTarget(project, restoreMode);
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

        private string ResolveRestoreTarget(Project project, string restoreMode)
        {
            var mode = ProjectRestoreMode.Normalize(restoreMode);
            if (string.Equals(mode, ProjectRestoreMode.Sandbox, StringComparison.OrdinalIgnoreCase))
            {
                var safeProjectName = string.Concat(project.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
                if (string.IsNullOrWhiteSpace(safeProjectName))
                    safeProjectName = "Project";

                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var sandboxRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VaultSync",
                    "restore-sandbox",
                    safeProjectName,
                    stamp);

                Directory.CreateDirectory(sandboxRoot);
                Console.WriteLine($"[Restore] Sandbox mode active. Using sandbox path '{sandboxRoot}'.");
                return sandboxRoot;
            }

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

        private async Task<(bool Confirmed, string RestoreMode)> ConfirmRestoreBackupAsync(RestoreBackupPreparation preparation)
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
                var restoreModeLabel = string.Equals(preparation.RestoreMode, ProjectRestoreMode.Sandbox, StringComparison.OrdinalIgnoreCase)
                    ? L("Backups.Restore.Mode.Sandbox", "Sandbox (restore to preview folder)")
                    : L("Backups.Restore.Mode.Direct", "Direct (overwrite project path)");

                var guidanceLines = new[]
                {
                    Lf("Backups.Restore.GuidanceType", "Type: {0}", backupTypeLabel),
                    Lf("Backups.Restore.GuidanceMode", "Mode: {0}", restoreModeLabel),
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

                var restoreModeOptions = new List<RestoreModeOption>
                {
                    new(ProjectRestoreMode.Direct, L("Backups.Restore.Mode.Direct", "Direct (overwrite project path)")),
                    new(ProjectRestoreMode.Sandbox, L("Backups.Restore.Mode.Sandbox", "Sandbox (restore to preview folder)"))
                };
                var restoreModeCombo = new ComboBox
                {
                    ItemsSource = restoreModeOptions,
                    SelectedItem = restoreModeOptions.FirstOrDefault(o =>
                        string.Equals(o.Id, preparation.RestoreMode, StringComparison.OrdinalIgnoreCase))
                        ?? restoreModeOptions[0],
                    MinWidth = 360
                };
                var restoreModeSelector = new StackPanel { Spacing = 5 };
                restoreModeSelector.Children.Add(new TextBlock
                {
                    Text = L("Backups.Restore.Mode.Label", "Restore mode"),
                    FontWeight = FontWeight.SemiBold
                });
                restoreModeSelector.Children.Add(restoreModeCombo);

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

                var selectedMode = (restoreModeCombo.SelectedItem as RestoreModeOption)?.Id ?? preparation.RestoreMode;
                return (confirmed, ProjectRestoreMode.Normalize(selectedMode));
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
                    Text = L("Backups.Restore.Sandbox.Post.Title", "Sandbox restore completed"),
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
                    Content = L("Backups.Restore.Sandbox.Post.DeleteAfterApply", "Delete sandbox folder after apply"),
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
                    Content = L("Backups.Restore.Sandbox.Post.Keep", "Keep for later"),
                    MinWidth = 130
                };
                keepButton.Classes.Add("action-ghost");

                var openButton = new Button
                {
                    Content = L("Backups.Restore.Sandbox.Post.Open", "Open sandbox"),
                    MinWidth = 130
                };
                openButton.Classes.Add("action-ghost");

                var applyButton = new Button
                {
                    Content = L("Backups.Restore.Sandbox.Post.Apply", "Apply to project"),
                    MinWidth = 150
                };
                applyButton.Classes.Add("action-primary");

                Window? window = null;
                var action = SandboxPostRestoreAction.Keep;
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
                    Title = L("Backups.Restore.Sandbox.Post.Title", "Sandbox restore completed"),
                    Content = card,
                    CanResize = false,
                    Width = 650,
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
                    L("Backups.Restore.Sandbox.ApplyMissing", "Sandbox folder no longer exists."),
                    "Error");
                return;
            }

            var targetPath = ResolveRestoreTarget(project, ProjectRestoreMode.Direct);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                BackupsViewModel.ShowNotification(
                    L("Backups.Status.RestoreFailed", "Restore failed."),
                    "Error");
                return;
            }

            var preview = await Task.Run(() => BuildSandboxApplyPreview(sandboxPath, targetPath));
            var applyConfirmed = await ConfirmSandboxApplyAsync(projectName, targetPath, preview);
            if (!applyConfirmed)
                return;

            var applyCardId = $"sandbox-apply-{project.Id}";
            BackupsViewModel.IsBusy = true;
            BackupsViewModel.BusyMessage = L("Backups.Restore.Sandbox.ApplyingBusy", "Applying sandbox restore...");
            BackupsViewModel.UpdateActiveBackup(
                applyCardId,
                projectName,
                0,
                L("Backups.Restore.Sandbox.ApplyingBusy", "Applying sandbox restore..."),
                string.Empty,
                allowCancel: false);

            var applySucceeded = false;
            string? cleanupError = null;
            try
            {
                await Task.Run(() =>
                {
                    CopyDirectoryWithProgress(sandboxPath, targetPath, 0, 100, update =>
                    {
                        var label = string.IsNullOrWhiteSpace(update.CurrentFile)
                            ? L("Backups.Restore.Sandbox.ApplyingBusy", "Applying sandbox restore...")
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
                        cleanupError ??= L("Backups.Restore.Sandbox.CleanupFailed", "Sandbox cleanup failed.");
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
                    L("Backups.Restore.Sandbox.ApplyCompleted", "Sandbox restore applied to project."),
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

        private static SandboxApplyPreview BuildSandboxApplyPreview(string sandboxPath, string targetPath)
        {
            var totalFiles = 0;
            var newFiles = 0;
            var overwriteFiles = 0;
            long totalBytes = 0;
            long overwriteBytes = 0;

            foreach (var sourceFile in Directory.EnumerateFiles(sandboxPath, "*", SearchOption.AllDirectories))
            {
                totalFiles++;
                var fileInfo = new FileInfo(sourceFile);
                totalBytes += fileInfo.Length;

                var relativePath = Path.GetRelativePath(sandboxPath, sourceFile);
                var destinationFile = Path.Combine(targetPath, relativePath);
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
                    Text = L("Backups.Restore.Sandbox.ApplyConfirmTitle", "Apply sandbox restore to project?"),
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
                    Text = L("Backups.Restore.Sandbox.ApplySummaryHeader", "Apply summary"),
                    FontWeight = FontWeight.SemiBold
                };

                var summaryLines = new[]
                {
                    Lf("Backups.Restore.Sandbox.ApplySummaryTotalFiles", "Total files to copy: {0}", preview.TotalFiles.ToString(CultureInfo.CurrentCulture)),
                    Lf("Backups.Restore.Sandbox.ApplySummaryNewFiles", "New files: {0}", preview.NewFiles.ToString(CultureInfo.CurrentCulture)),
                    Lf("Backups.Restore.Sandbox.ApplySummaryOverwriteFiles", "Files that overwrite existing project files: {0}", preview.OverwriteFiles.ToString(CultureInfo.CurrentCulture)),
                    Lf("Backups.Restore.Sandbox.ApplySummaryTotalBytes", "Total data to write: {0}", BackupSnapshotItem.FormatSize(preview.TotalBytes)),
                    Lf("Backups.Restore.Sandbox.ApplySummaryOverwriteBytes", "Data that overwrites existing files: {0}", BackupSnapshotItem.FormatSize(preview.OverwriteBytes))
                };

                var summaryPanel = new StackPanel { Spacing = 4 };
                foreach (var line in summaryLines)
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
                    Text = L("Backups.Restore.Sandbox.ApplyConfirmWarning", "Existing files with matching paths will be overwritten."),
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
                    Content = L("Common.Cancel", "Cancel"),
                    MinWidth = 120
                };
                cancelButton.Classes.Add("action-ghost");

                var applyButton = new Button
                {
                    Content = L("Backups.Restore.Sandbox.Post.Apply", "Apply to project"),
                    MinWidth = 150
                };
                applyButton.Classes.Add("action-primary");

                Window? window = null;
                var confirmed = false;
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
                    Title = L("Backups.Restore.Sandbox.ApplyConfirmTitle", "Apply sandbox restore to project?"),
                    Content = card,
                    CanResize = false,
                    Width = 700,
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

            var restoreDecision = await ConfirmRestoreBackupAsync(preparation);
            if (!restoreDecision.Confirmed)
                return;

            var selectedRestoreMode = ProjectRestoreMode.Normalize(restoreDecision.RestoreMode);
            var restoreProject = _repo.GetProjectById(preparation.ProjectId);
            if (restoreProject is null)
            {
                BackupsViewModel.ShowNotification(
                    L("Backups.Status.RestoreFailed", "Restore failed."),
                    "Error");
                Console.WriteLine($"[Restore] Project not found during restore execution for backupId={backupId}.");
                return;
            }

            var projectRoot = ResolveRestoreTarget(restoreProject, selectedRestoreMode);
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                BackupsViewModel.ShowNotification(
                    L("Backups.Status.RestoreFailed", "Restore failed."),
                    "Error");
                Console.WriteLine($"[Restore] Restore target resolution failed for backupId={backupId}.");
                return;
            }

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
                long lastProcessedBytes = 0;
                var lastProgressSampleUtc = DateTime.UtcNow;
                double smoothedBytesPerSecond = 0;

                string BuildRestoreEtaLabel(RestoreProgressUpdate update)
                {
                    if (update.TotalBytes <= 0)
                        return string.Empty;

                    var nowUtc = DateTime.UtcNow;
                    var elapsedSeconds = (nowUtc - lastProgressSampleUtc).TotalSeconds;
                    if (elapsedSeconds >= 0.2 && update.ProcessedBytes >= lastProcessedBytes)
                    {
                        var instantRate = (update.ProcessedBytes - lastProcessedBytes) / elapsedSeconds;
                        if (instantRate >= 0)
                        {
                            smoothedBytesPerSecond = smoothedBytesPerSecond <= 0
                                ? instantRate
                                : (smoothedBytesPerSecond * 0.75) + (instantRate * 0.25);
                        }

                        lastProcessedBytes = update.ProcessedBytes;
                        lastProgressSampleUtc = nowUtc;
                    }

                    var speedLabel = smoothedBytesPerSecond > 0
                        ? $"{BackupSnapshotItem.FormatSize((long)smoothedBytesPerSecond)}/s"
                        : L("Backups.Progress.Estimating", "Estimating...");

                    var processedLabel = BackupSnapshotItem.FormatSize(Math.Max(0, update.ProcessedBytes));
                    var totalLabel = BackupSnapshotItem.FormatSize(update.TotalBytes);
                    var detailLabel = string.Format(
                        CultureInfo.CurrentCulture,
                        "Restoring ({0}/{1})",
                        processedLabel,
                        totalLabel);

                    return $"{speedLabel} - {detailLabel}";
                }

                void RunRestore(string? encryptionPassword) =>
                    RestoreDirectory(backupFullPath, projectRoot, encryptionPassword, update =>
                    {
                        var label = string.IsNullOrWhiteSpace(update.CurrentFile)
                            ? L("Backups.Status.Restoring", "Restoring backup...")
                            : update.CurrentFile;
                        var etaLabel = BuildRestoreEtaLabel(update);
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
                    var isDirectRestore = !string.Equals(selectedRestoreMode, ProjectRestoreMode.Sandbox, StringComparison.OrdinalIgnoreCase);
                    if (isDirectRestore && restoredProject != null && restoredProject.NeedsRestore)
                    {
                        _repo.UpdateProjectNeedsRestore(restoredProject.Id, false);
                    }

                    if (!isDirectRestore)
                    {
                        var sandboxPath = projectRoot;
                        var decision = await ConfirmSandboxPostRestoreActionAsync(preparation.ProjectName, sandboxPath);
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

        private static void RestoreDirectory(string sourceDir, string targetDir, string? encryptionPassword, Action<RestoreProgressUpdate>? progress)
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

        private static void ExtractArchiveWithProgress(string archivePath, string targetDir, Action<RestoreProgressUpdate>? progress)
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var totalEntries = archive.Entries.Count;
            var processed = 0;
            var totalBytes = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Sum(e => Math.Max(0, e.Length));
            long processedBytes = 0;

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
                if (!string.IsNullOrEmpty(entry.Name))
                    processedBytes += Math.Max(0, entry.Length);

                progress?.Invoke(new RestoreProgressUpdate(
                    totalEntries == 0 ? 100 : processed * 100d / totalEntries,
                    entry.FullName,
                    processedBytes,
                    totalBytes));
            }
        }

        private static void RestoreEncryptedArchiveWithProgress(
            string sourceDir,
            string targetDir,
            string password,
            Action<RestoreProgressUpdate>? progress)
        {
            var stagingRoot = Path.Combine(Path.GetTempPath(), $"vaultsync-restore-{Guid.NewGuid():N}");
            var stagingExtracted = Path.Combine(stagingRoot, "content");
            var stagingArchive = Path.Combine(stagingRoot, BackupArchiveCryptoService.PlainArchiveFileName);

            try
            {
                Directory.CreateDirectory(stagingExtracted);
                progress?.Invoke(new RestoreProgressUpdate(5, "Decrypting backup...", 0, 0));

                var cryptoService = new BackupArchiveCryptoService();
                cryptoService.DecryptArchiveToPlainZip(sourceDir, password, stagingArchive);
                progress?.Invoke(new RestoreProgressUpdate(30, "Decrypting backup...", 0, 0));

                ExtractArchiveWithProgress(stagingArchive, stagingExtracted, update =>
                {
                    var mapped = 30 + (update.Percent * 0.5);
                    progress?.Invoke(new RestoreProgressUpdate(
                        Math.Clamp(mapped, 30, 80),
                        update.CurrentFile,
                        update.ProcessedBytes,
                        update.TotalBytes));
                });

                progress?.Invoke(new RestoreProgressUpdate(82, "Restoring backup...", 0, 0));
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
            Action<RestoreProgressUpdate>? progress)
        {
            var files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
            var totalFiles = files.Length;
            var totalBytes = files
                .Select(filePath => new FileInfo(filePath))
                .Sum(fileInfo => Math.Max(0, fileInfo.Length));
            long processedBytes = 0;
            var processed = 0;
            foreach (var filePath in files)
            {
                var fileLength = Math.Max(0, new FileInfo(filePath).Length);
                var relative = Path.GetRelativePath(sourceDir, filePath);
                var target = Path.Combine(targetDir, relative);

                var parentDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parentDir))
                    Directory.CreateDirectory(parentDir);

                File.Copy(filePath, target, overwrite: true);
                processedBytes += fileLength;
                processed++;
                if (progress is not null)
                {
                    var ratio = totalFiles == 0 ? 1d : processed / (double)totalFiles;
                    var value = startPercent + ((endPercent - startPercent) * ratio);
                    progress(new RestoreProgressUpdate(value, relative, processedBytes, totalBytes));
                }
            }

            if (totalFiles == 0)
                progress?.Invoke(new RestoreProgressUpdate(endPercent, string.Empty, 0, 0));
        }

    }
}
