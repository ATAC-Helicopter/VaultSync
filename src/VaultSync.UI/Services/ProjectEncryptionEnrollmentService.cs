using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI.Services;

public sealed class ProjectEncryptionEnrollmentService
{
    private readonly SqliteRepository _repo;
    private readonly CredentialVault _credentialVault;
    private readonly Func<Window?> _getOwner;
    private readonly Func<int, Task> _exportMetadataForProjectSettingsChangeAsync;
    private readonly Func<Task> _refreshProjectsAsync;
    private readonly Func<Task> _refreshBackupsAsync;
    private readonly Action<string, NotificationSeverity> _showNotification;
    private readonly Action<string> _log;

    public ProjectEncryptionEnrollmentService(
        SqliteRepository repo,
        CredentialVault credentialVault,
        Func<Window?> getOwner,
        Func<int, Task> exportMetadataForProjectSettingsChangeAsync,
        Func<Task> refreshProjectsAsync,
        Func<Task> refreshBackupsAsync,
        Action<string, NotificationSeverity> showNotification,
        Action<string> log)
    {
        _repo = repo;
        _credentialVault = credentialVault;
        _getOwner = getOwner;
        _exportMetadataForProjectSettingsChangeAsync = exportMetadataForProjectSettingsChangeAsync;
        _refreshProjectsAsync = refreshProjectsAsync;
        _refreshBackupsAsync = refreshBackupsAsync;
        _showNotification = showNotification;
        _log = log;
    }

    public async Task StartProjectSelectionEnrollmentAsync()
    {
        try
        {
            var projects = _repo.GetAllProjects()
                .Where(p => p.Id > 0)
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (projects.Count == 0)
            {
                _showNotification(
                    L("Projects.Encryption.EnrollNoProjects", "Add a project before enrolling a project encryption password."),
                    NotificationSeverity.Warning);
                return;
            }

            int? selectedProjectId = await ConfirmProjectEncryptionEnrollmentTargetAsync(projects);
            if (!selectedProjectId.HasValue)
                return;

            await EditProjectEncryptionSecretAsync(selectedProjectId.Value);
        }
        catch (Exception ex)
        {
            _log($"[Projects] Failed to start project password enrollment: {ex.Message}");
            _showNotification(
                L("Projects.Encryption.PasswordUpdateFailed", "Failed to update project encryption password."),
                NotificationSeverity.Error);
        }
    }

    public async Task EditProjectEncryptionSecretAsync(int projectId)
    {
        Project? project = _repo.GetProjectById(projectId);
        if (project is null)
        {
            _showNotification(
                Lf("Projects.Encryption.ProjectNotFound", "Project with id {0} was not found.", projectId),
                NotificationSeverity.Error);
            return;
        }

        string? existingKeyRef = string.IsNullOrWhiteSpace(project.EncryptionKeyRef)
            ? null
            : project.EncryptionKeyRef.Trim();
        bool hasExistingSecret = !string.IsNullOrWhiteSpace(_credentialVault.GetSecret(
            existingKeyRef,
            BackupEncryptionCredentialIdentity.AccountName,
            preferKeychain: true,
            fallbackPlaintext: null));

        ProjectEncryptionSecretDialogResult dialogResult = await ConfirmProjectEncryptionSecretAsync(project.Name, hasExistingSecret);
        if (!dialogResult.Confirmed)
            return;

        try
        {
            if (dialogResult.ClearRequested)
            {
                _credentialVault.DeleteSecret(existingKeyRef, BackupEncryptionCredentialIdentity.AccountName);
                _repo.UpdateProjectEncryptionSettings(project.Id, project.EncryptionPolicy, null);
            }
            else
            {
                string normalizedPassword = dialogResult.Password?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(normalizedPassword))
                    return;

                string keyRef = CredentialVault.EnsureKeyRef(existingKeyRef, $"project-{project.Name}");
                _credentialVault.SaveSecret(keyRef, BackupEncryptionCredentialIdentity.AccountName, normalizedPassword, preferKeychain: true);
                _repo.UpdateProjectEncryptionSettings(project.Id, project.EncryptionPolicy, keyRef);
            }

            await _exportMetadataForProjectSettingsChangeAsync(project.Id);
            await _refreshProjectsAsync();
            await _refreshBackupsAsync();
        }
        catch (Exception ex)
        {
            _log($"[Projects] Failed to update encryption password for project {project.Id}: {ex.Message}");
            if (ex.Message == "LINUX_SECRET_TOOL_MISSING")
            {
                _showNotification(
                    L("Projects.Encryption.LinuxSecretToolMissing", "Linux secret storage is unavailable. Ensure 'libsecret' is installed and your keyring service is running."),
                    NotificationSeverity.Error);
            }
            else
            {
                _showNotification(
                    L("Projects.Encryption.PasswordUpdateFailed", "Failed to update project encryption password."),
                    NotificationSeverity.Error);
            }
        }
    }

    private sealed record ProjectEnrollmentOption(int ProjectId, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record ProjectEncryptionSecretDialogResult(bool Confirmed, bool ClearRequested, string Password);

    private async Task<int?> ConfirmProjectEncryptionEnrollmentTargetAsync(IReadOnlyList<Project> projects)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var options = projects
                .Select(p => new ProjectEnrollmentOption(p.Id, p.Name))
                .ToList();

            var title = new TextBlock
            {
                Text = L("Projects.Encryption.EnrollDialogTitle", "Enroll project encryption password"),
                FontSize = 18,
                FontWeight = FontWeight.SemiBold
            };

            var prompt = new TextBlock
            {
                Text = L("Projects.Encryption.EnrollDialogPrompt", "Choose a project to enroll a password for this machine."),
                TextWrapping = TextWrapping.Wrap
            };

            var combo = new ComboBox
            {
                Width = 360,
                ItemsSource = options,
                SelectedItem = options.FirstOrDefault()
            };

            var validationText = new TextBlock
            {
                Foreground = Brushes.OrangeRed,
                IsVisible = false,
                TextWrapping = TextWrapping.Wrap
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

            var continueButton = new Button
            {
                Content = L("Settings.Encryption.SetPassword", "Set password"),
                MinWidth = 160
            };
            continueButton.Classes.Add("action-primary");

            int selectedProjectId = 0;
            bool confirmed = false;
            Window? window = null;

            cancelButton.Click += (_, _) => window?.Close();
            continueButton.Click += (_, _) =>
            {
                if (combo.SelectedItem is not ProjectEnrollmentOption selected)
                {
                    validationText.Text = L("Projects.Encryption.EnrollValidationProject", "Select a project to continue.");
                    validationText.IsVisible = true;
                    return;
                }

                selectedProjectId = selected.ProjectId;
                confirmed = true;
                window?.Close();
            };

            buttonRow.Children.Add(cancelButton);
            buttonRow.Children.Add(continueButton);

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(title);
            content.Children.Add(prompt);
            content.Children.Add(combo);
            content.Children.Add(validationText);
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
                Title = L("Projects.Encryption.EnrollDialogTitle", "Enroll project encryption password"),
                Content = card,
                CanResize = false,
                Width = 560,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            Window? owner = _getOwner();
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

            return confirmed && selectedProjectId > 0 ? selectedProjectId : (int?)null;
        });
    }

    private async Task<ProjectEncryptionSecretDialogResult> ConfirmProjectEncryptionSecretAsync(string projectName, bool hasExistingSecret)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var title = new TextBlock
            {
                Text = L("Projects.Encryption.PasswordDialogTitle", "Project encryption password"),
                FontSize = 18,
                FontWeight = FontWeight.SemiBold
            };

            var prompt = new TextBlock
            {
                Text = string.Format(
                    CultureInfo.CurrentCulture,
                    L("Projects.Encryption.PasswordDialogPrompt", "Set or update the encryption password for '{0}'."),
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

            var confirmLabel = new TextBlock
            {
                Text = L("Settings.Encryption.RotateConfirmPassword", "Confirm new password"),
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var confirmBox = new TextBox
            {
                Width = 320,
                PasswordChar = '●'
            };

            var validationText = new TextBlock
            {
                Foreground = Brushes.OrangeRed,
                IsVisible = false,
                TextWrapping = TextWrapping.Wrap
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

            var clearButton = new Button
            {
                Content = L("Settings.Encryption.ClearPassword", "Clear password"),
                MinWidth = 150,
                IsVisible = hasExistingSecret
            };
            clearButton.Classes.Add("action-ghost");

            var saveButton = new Button
            {
                Content = L("Settings.Encryption.SetPassword", "Set password"),
                MinWidth = 160
            };
            saveButton.Classes.Add("action-primary");

            Window? window = null;
            bool confirmed = false;
            bool clearRequested = false;

            cancelButton.Click += (_, _) => window?.Close();
            clearButton.Click += (_, _) =>
            {
                clearRequested = true;
                confirmed = true;
                window?.Close();
            };
            saveButton.Click += (_, _) =>
            {
                string password = passwordBox.Text ?? string.Empty;
                string confirm = confirmBox.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(password))
                {
                    validationText.Text = L("Projects.Encryption.PasswordValidationRequired", "Password and confirmation are required.");
                    validationText.IsVisible = true;
                    return;
                }

                if (!string.Equals(password, confirm, StringComparison.Ordinal))
                {
                    validationText.Text = L("Projects.Encryption.PasswordValidationMismatch", "Password and confirmation do not match.");
                    validationText.IsVisible = true;
                    return;
                }

                confirmed = true;
                window?.Close();
            };

            buttonRow.Children.Add(cancelButton);
            buttonRow.Children.Add(clearButton);
            buttonRow.Children.Add(saveButton);

            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(title);
            content.Children.Add(prompt);
            content.Children.Add(passwordLabel);
            content.Children.Add(passwordBox);
            content.Children.Add(confirmLabel);
            content.Children.Add(confirmBox);
            content.Children.Add(validationText);
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
                Title = L("Projects.Encryption.PasswordDialogTitle", "Project encryption password"),
                Content = card,
                CanResize = false,
                Width = 560,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            Window? owner = _getOwner();
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

            return new ProjectEncryptionSecretDialogResult(
                confirmed,
                clearRequested,
                passwordBox.Text ?? string.Empty);
        });
    }

    private static string L(string key, string fallback)
    {
        string? value = LocalizationProvider.Service?.GetString(key);
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal))
            return fallback;
        return value;
    }

    private static string Lf(string key, string fallback, params object[] args)
    {
        string text = L(key, fallback);
        return args is { Length: > 0 }
            ? string.Format(CultureInfo.CurrentCulture, text, args)
            : text;
    }
}
