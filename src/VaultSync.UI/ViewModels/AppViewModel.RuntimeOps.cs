using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Notifications;
using VaultSync.UI.ViewModels.Notifications;
using VaultSync.UI.Views;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private void EnsureNasMonitorStarted()
        {
            if (_nasMonitorTimer != null)
                return;

            // Check every 5 minutes; first check after 2 minutes.
            _nasMonitorTimer = new Timer(
                _ => _ = CheckNasAndMigrateAsync(),
                null,
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(5));
        }

        private void StopNasMonitor()
        {
            _nasMonitorTimer?.Dispose();
            _nasMonitorTimer = null;
        }

        private void EnsureDestinationProbeStarted()
        {
            if (_destinationProbeTimer is not null)
                return;

            _destinationProbeTimer = new Timer(
                _ => _ = ProbeDestinationsAsync(),
                null,
                TimeSpan.FromMinutes(10),
                TimeSpan.FromMinutes(10));

            var initialDelay = DateTime.UtcNow - _appStartUtc < TimeSpan.FromSeconds(10)
                ? TimeSpan.FromSeconds(10)
                : TimeSpan.Zero;
            _ = Task.Run(async () =>
            {
                if (initialDelay > TimeSpan.Zero)
                    await Task.Delay(initialDelay).ConfigureAwait(false);
                await ProbeDestinationsAsync().ConfigureAwait(false);
            });
        }

        public IReadOnlyList<DestinationProbeSummary> GetDestinationProbeSummaries()
        {
            return GetDestinationProbeSummaries(_config);
        }

        private IReadOnlyList<DestinationProbeSummary> GetDestinationProbeSummaries(AppConfig cfg)
        {
            var destinations = GetActiveDestinations(cfg);
            if (destinations.Count == 0)
            {
                _destinationProbeSummaries.Clear();
                return Array.Empty<DestinationProbeSummary>();
            }

            var activeIds = new HashSet<string>(
                destinations.Select(DestinationStatusItem.GetId),
                StringComparer.OrdinalIgnoreCase);

            foreach (var id in _destinationProbeSummaries.Keys.ToList())
            {
                if (!activeIds.Contains(id))
                {
                    _destinationProbeSummaries.TryRemove(id, out _);
                }
            }

            var summaries = _destinationProbeSummaries.Values
                .Where(s => activeIds.Contains(s.Id))
                .OrderBy(s => s.Alias, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var signature = BuildDestinationProbeSignature(summaries);
            lock (_destinationProbeCacheGate)
            {
                if (signature == _cachedDestinationProbeSignature &&
                    _cachedDestinationProbeSummaries.Count == summaries.Count)
                {
                    return _cachedDestinationProbeSummaries;
                }

                _cachedDestinationProbeSignature = signature;
                _cachedDestinationProbeSummaries = summaries;
            }

            return summaries;
        }

        private static string BuildDestinationProbeSignature(IReadOnlyList<DestinationProbeSummary> summaries)
        {
            if (summaries.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            foreach (var summary in summaries)
            {
                sb.Append(summary.Id).Append('|')
                  .Append(summary.Reachable).Append('|')
                  .Append(summary.Message).Append('|')
                  .Append(summary.LastChecked.ToString("O")).Append(';');
            }
            return sb.ToString();
        }

        private async Task ProbeDestinationsAsync()
        {
            if (Interlocked.Exchange(ref _destinationProbeInFlight, 1) == 1)
                return;

            try
            {
                var cfg = AppConfigStore.GetSnapshot();
                var destinations = GetActiveDestinations(cfg);

                var now = DateTime.UtcNow;
                foreach (var dest in destinations)
                {
                    if (!dest.Active)
                        continue;

                    var id = DestinationStatusItem.GetId(dest);
                    _destinationProbeSummaries.TryGetValue(id, out var previous);
                    if (previous is not null &&
                        previous.Reachable &&
                        (now - previous.LastChecked) < DestinationProbeMinInterval)
                    {
                        continue;
                    }

                    var result = await Task.Run(() => TryTestDestination(dest, cfg));
                    UpdateDestinationProbeSummary(dest, result);

                    if (result.Reachable && (previous is null || !previous.Reachable))
                    {
                        TryImportMetadataForDestination(cfg, dest, result.EffectivePath);
                    }

                    if (!result.Reachable && (previous is null || previous.Reachable))
                    {
                        var name = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias!;
                        var message = string.IsNullOrWhiteSpace(result.Message)
                            ? L("Destinations.Probe.DefaultHint", "Check mount/credentials.")
                            : result.Message;
                        GlobalNotificationCenter.Instance.Show(
                            Lf("Destinations.Probe.UnreachableMessage", "Destination '{0}' is unreachable. {1}", name, message),
                            NotificationSeverity.Warning,
                            L("Destinations.Probe.UnreachableTitle", "Destination unreachable"));
                    }
                }
            }
            catch
            {
                // swallow background probe errors
            }
            finally
            {
                Interlocked.Exchange(ref _destinationProbeInFlight, 0);
            }
        }

        private void UpdateDestinationProbeSummary(BackupDestination dest, DestinationTestResult result)
        {
            var id = DestinationStatusItem.GetId(dest);
            var alias = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path ?? string.Empty : dest.Alias ?? dest.Path ?? string.Empty;
            var message = string.IsNullOrWhiteSpace(result.Message)
                ? (result.Reachable
                    ? LStatic("Destinations.Test.Reachable", "Reachable")
                    : LStatic("Destinations.Test.Unavailable", "Unavailable"))
                : result.Message;

            var severity = result.Reachable
                ? (message.Contains(LStatic("Destinations.Test.ReadOnly", "Read-only"), StringComparison.OrdinalIgnoreCase)
                    ? "Warning"
                    : "Success")
                : "Error";

            _destinationProbeSummaries[id] = new DestinationProbeSummary(
                id,
                alias,
                dest.Path ?? string.Empty,
                result.Reachable,
                message,
                DateTime.UtcNow);

            BackupsViewModel.UpdateDestinationStatus(id, message, severity);
        }

        private void OnRefreshHistoryRequested()
        {
            RunDetached(OnRefreshHistoryRequestedAsync, nameof(OnRefreshHistoryRequestedAsync));
        }

        private async Task OnRefreshHistoryRequestedAsync()
        {
            try
            {
                await RefreshMetadataNowAsync();
            }
            catch
            {
                // ignore manual refresh failures for now
            }
        }

        private sealed record EncryptionRotationRequest(
            bool Confirmed,
            string? ProjectNameFilter,
            string OldPassword,
            string NewPassword);

        private async Task<EncryptionRotationRequest> ConfirmEncryptionRotationRequestAsync()
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var title = new TextBlock
                {
                    Text = L("Settings.Encryption.RotateDialogTitle", "Rotate encrypted backups"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold
                };

                var body = new TextBlock
                {
                    Text = L("Settings.Encryption.RotateDialogBody", "Optionally target one project, then provide old and new passwords to re-encrypt existing encrypted backups."),
                    TextWrapping = TextWrapping.Wrap
                };

                var projectLabel = new TextBlock
                {
                    Text = L("Settings.Encryption.RotateProjectFilter", "Project name (optional)"),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                var projectBox = new TextBox
                {
                    Width = 360,
                    Watermark = L("Settings.Encryption.RotateProjectFilterWatermark", "Leave empty to rotate all encrypted backups")
                };

                var oldPasswordLabel = new TextBlock
                {
                    Text = L("Settings.Encryption.RotateOldPassword", "Old password"),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                var oldPasswordBox = new TextBox
                {
                    Width = 360,
                    PasswordChar = '●'
                };

                var newPasswordLabel = new TextBlock
                {
                    Text = L("Settings.Encryption.RotateNewPassword", "New password"),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                var newPasswordBox = new TextBox
                {
                    Width = 360,
                    PasswordChar = '●'
                };

                var confirmPasswordLabel = new TextBlock
                {
                    Text = L("Settings.Encryption.RotateConfirmPassword", "Confirm new password"),
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 6, 0, 0)
                };

                var confirmPasswordBox = new TextBox
                {
                    Width = 360,
                    PasswordChar = '●'
                };

                var validationText = new TextBlock
                {
                    Foreground = Brushes.OrangeRed,
                    FontSize = 12,
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

                var rotateButton = new Button
                {
                    Content = L("Settings.Encryption.RotateExecute", "Rotate"),
                    MinWidth = 140
                };
                rotateButton.Classes.Add("action-primary");

                Window? window = null;
                var confirmed = false;
                cancelButton.Click += (_, _) => window?.Close();
                rotateButton.Click += (_, _) =>
                {
                    var oldPassword = oldPasswordBox.Text ?? string.Empty;
                    var newPassword = newPasswordBox.Text ?? string.Empty;
                    var newPasswordConfirm = confirmPasswordBox.Text ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(oldPassword) || string.IsNullOrWhiteSpace(newPassword))
                    {
                        validationText.Text = L("Settings.Encryption.RotateValidationPassword", "Both old and new passwords are required.");
                        return;
                    }

                    if (!string.Equals(newPassword, newPasswordConfirm, StringComparison.Ordinal))
                    {
                        validationText.Text = L("Settings.Encryption.RotateValidationMismatch", "New password and confirmation do not match.");
                        return;
                    }

                    confirmed = true;
                    window?.Close();
                };

                buttonRow.Children.Add(cancelButton);
                buttonRow.Children.Add(rotateButton);

                var content = new StackPanel { Spacing = 10 };
                content.Children.Add(title);
                content.Children.Add(body);
                content.Children.Add(projectLabel);
                content.Children.Add(projectBox);
                content.Children.Add(oldPasswordLabel);
                content.Children.Add(oldPasswordBox);
                content.Children.Add(newPasswordLabel);
                content.Children.Add(newPasswordBox);
                content.Children.Add(confirmPasswordLabel);
                content.Children.Add(confirmPasswordBox);
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
                    Title = L("Settings.Encryption.RotateDialogTitle", "Rotate encrypted backups"),
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

                return new EncryptionRotationRequest(
                    confirmed,
                    projectBox.Text?.Trim(),
                    oldPasswordBox.Text ?? string.Empty,
                    newPasswordBox.Text ?? string.Empty);
            });
        }

        private void OnRotateEncryptedBackupsRequested()
        {
            RunDetached(OnRotateEncryptedBackupsRequestedAsync, nameof(OnRotateEncryptedBackupsRequestedAsync));
        }

        private async Task OnRotateEncryptedBackupsRequestedAsync()
        {
            if (Volatile.Read(ref _backupAllInProgress) == 1 || Volatile.Read(ref _manualBackupInFlightCount) > 0)
            {
                BackupsViewModel.ShowNotification(
                    L("Settings.Encryption.RotateBusyBackups", "Wait for active backups to finish before rotating encrypted backups."),
                    "Warning");
                return;
            }

            var request = await ConfirmEncryptionRotationRequestAsync();
            if (!request.Confirmed)
                return;

            var cfg = await Task.Run(AppConfigStore.Load);
            var projects = await Task.Run(() => _repo.GetAllProjects().ToList());

            Project? scopedProject = null;
            if (!string.IsNullOrWhiteSpace(request.ProjectNameFilter))
            {
                scopedProject = projects.FirstOrDefault(p =>
                    string.Equals(p.Name, request.ProjectNameFilter, StringComparison.OrdinalIgnoreCase));
                if (scopedProject is null)
                {
                    BackupsViewModel.ShowNotification(
                        Lf("Settings.Encryption.RotateProjectNotFound", "Project '{0}' was not found.", request.ProjectNameFilter),
                        "Error");
                    return;
                }
            }

            var targetProjects = scopedProject is null
                ? projects
                : new List<Project> { scopedProject };

            var destinations = GetAllDestinations(cfg);
            var rotationService = new BackupKeyRotationService();
            BackupsViewModel.IsBusy = true;
            BackupsViewModel.BusyMessage = L("Settings.Encryption.RotateBusy", "Rotating encrypted backups...");
            try
            {
                var summary = await Task.Run(() =>
                {
                    var succeeded = 0;
                    var failed = 0;
                    var skipped = 0;
                    var failureMessages = new List<string>();

                    foreach (var project in targetProjects)
                    {
                        var backups = _repo.GetBackupsForProject(project.Id)
                            .Where(b => b.IsEncrypted)
                            .ToList();

                        foreach (var backup in backups)
                        {
                            var backupRoot = ResolveDestinationRootForBackup(backup, destinations, cfg.Backups.BackupRoot);
                            if (string.IsNullOrWhiteSpace(backupRoot) || string.IsNullOrWhiteSpace(backup.Path))
                            {
                                skipped++;
                                continue;
                            }

                            var backupFolder = Path.Combine(backupRoot, backup.Path);
                            if (!Directory.Exists(backupFolder))
                            {
                                skipped++;
                                continue;
                            }

                            try
                            {
                                var result = rotationService.RotateEncryptedBackup(
                                    backupFolder,
                                    request.OldPassword,
                                    request.NewPassword,
                                    cfg.Backups.Encryption);

                                _repo.UpdateBackupEncryptionMetadata(
                                    backup.Id,
                                    isEncrypted: true,
                                    result.CryptoDescriptorJson,
                                    result.TotalBytes);

                                succeeded++;
                            }
                            catch (Exception ex)
                            {
                                failed++;
                                failureMessages.Add($"[{project.Name}] {backup.Path}: {ex.Message}");
                            }
                        }
                    }

                    return (succeeded, failed, skipped, failureMessages);
                });

                if (summary.succeeded > 0)
                {
                    ReloadBackupsVmData();
                    await DashboardViewModel.RefreshAsync();
                    await _projectsViewModel.RefreshAsync();
                }

                foreach (var line in summary.failureMessages.Take(5))
                {
                    Console.WriteLine($"[Rotate] {line}");
                }

                if (summary.succeeded == 0 && summary.failed == 0)
                {
                    BackupsViewModel.ShowNotification(
                        L("Settings.Encryption.RotateNoBackups", "No encrypted backups matched the selected scope."),
                        "Info");
                    return;
                }

                var message = Lf(
                    "Settings.Encryption.RotateSummary",
                    "Rotation complete. Succeeded: {0}, Failed: {1}, Skipped: {2}.",
                    summary.succeeded,
                    summary.failed,
                    summary.skipped);
                BackupsViewModel.ShowNotification(
                    message,
                    summary.failed > 0 ? "Warning" : "Info");
            }
            finally
            {
                BackupsViewModel.IsBusy = false;
                BackupsViewModel.BusyMessage = string.Empty;
            }
        }

        private async Task RefreshMetadataNowAsync()
        {
            var cfg = await Task.Run(AppConfigStore.Load);
            if (!cfg.Backups.EnableMetadataSync)
            {
                Console.WriteLine("[MetadataSync] Refresh skipped: metadata sync disabled.");
                return;
            }

            Console.WriteLine("[MetadataSync] Manual refresh started.");
            var refreshNeeded = false;

            if (!string.IsNullOrWhiteSpace(cfg.ProjectsRoot))
            {
                var options = new MetadataSyncOptions(
                    AllowCreateProjects: true,
                    MarkNeedsRestoreOnImport: cfg.Backups.PromptRestoreAfterImport);
                var preview = await _metadataSyncService.PreviewImportFromStoreAsync(cfg.ProjectsRoot, options);
                var label = L("MetadataSync.Review.SourceProjectsRoot", "Projects root");
                    if (await ConfirmMetadataImportAsync(preview, label))
                    {
                        var result = await _metadataSyncService.ImportFromStoreAsync(cfg.ProjectsRoot, options);
                        Console.WriteLine($"[MetadataSync] Manual refresh (projects root) result: {result.Status} (projects={result.ImportedProjects}, snapshots={result.ImportedSnapshots}, backups={result.ImportedBackups}, tombstones={result.AppliedTombstones}).");
                        _ = Task.Run(() => ApplyRetentionAfterMetadataImport(cfg.ProjectsRoot, result));
                        refreshNeeded |= result.Status == MetadataSyncStatus.Success &&
                                         (result.ImportedProjects > 0 ||
                                          result.ImportedSnapshots > 0 ||
                                          result.ImportedBackups > 0 ||
                                      result.AppliedTombstones > 0);
                }
            }

            var destinations = GetActiveDestinations(cfg);
            foreach (var dest in destinations)
            {
                if (!dest.EnableMetadataSync)
                    continue;

                var profile = string.IsNullOrWhiteSpace(dest.CredentialName)
                    ? null
                    : cfg.Network.Credentials.FirstOrDefault(c =>
                        c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));

                var resolution = await Task.Run(() => _networkMountService.PrepareDestination(dest, profile));
                if (!resolution.IsSuccess)
                    continue;

                try
                {
                    var options = new MetadataSyncOptions(
                        AllowCreateProjects: true,
                        MarkNeedsRestoreOnImport: cfg.Backups.PromptRestoreAfterImport);
                    var preview = await _metadataSyncService.PreviewImportFromStoreAsync(resolution.EffectivePath, options);
                    var name = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias!;
                    var label = Lf("MetadataSync.Review.SourceDestination", "Destination: {0}", name);
                    if (await ConfirmMetadataImportAsync(preview, label))
                    {
                        var result = await _metadataSyncService.ImportFromStoreAsync(resolution.EffectivePath, options);
                        Console.WriteLine($"[MetadataSync] Manual refresh ({name}) result: {result.Status} (projects={result.ImportedProjects}, snapshots={result.ImportedSnapshots}, backups={result.ImportedBackups}, tombstones={result.AppliedTombstones}).");
                        _ = Task.Run(() => ApplyRetentionAfterMetadataImport(resolution.EffectivePath, result));
                        refreshNeeded |= result.Status == MetadataSyncStatus.Success &&
                                         (result.ImportedProjects > 0 ||
                                          result.ImportedSnapshots > 0 ||
                                          result.ImportedBackups > 0 ||
                                          result.AppliedTombstones > 0);
                    }
                }
                finally
                {
                    _networkMountService.Cleanup(resolution);
                }
            }

            if (refreshNeeded)
            {
                ReloadBackupsVmData();
                await DashboardViewModel.RefreshAsync();
                await _projectsViewModel.RefreshAsync();
            }
        }

        private async Task<bool> ConfirmMetadataImportAsync(MetadataSyncPreview preview, string sourceLabel)
        {
            if (preview.Status != MetadataSyncStatus.Success)
            {
                return false;
            }

            if (!preview.HasChanges)
            {
                Console.WriteLine($"[MetadataSync] Preview found no changes for '{sourceLabel}'.");
                return false;
            }

            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var window = new Views.MetadataSyncReviewWindow
                {
                    DataContext = new ViewModels.MetadataSyncReviewViewModel(_localizationService, preview, sourceLabel)
                };

                var owner = GetMainWindow();
                if (owner != null)
                {
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

                return window.DataContext is ViewModels.MetadataSyncReviewViewModel vm && vm.Confirmed;
            });
        }

        private static Window? GetMainWindow()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.MainWindow;
            return null;
        }

        private DestinationTestResult TryTestDestination(BackupDestination dest, AppConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(dest.Path))
                return new DestinationTestResult(false, false, string.Empty, LStatic("Destinations.Test.EmptyPath", "Destination path is empty."));

            DiagnosticsLogger.Record($"Destination test start: '{dest.Alias ?? dest.Path}'.");
            var profile = string.IsNullOrWhiteSpace(dest.CredentialName)
                ? null
                : cfg.Network.Credentials.FirstOrDefault(c =>
                    c.Name.Equals(dest.CredentialName, StringComparison.OrdinalIgnoreCase));

            var resolution = _networkMountService.PrepareDestination(dest, profile);
            if (!resolution.IsSuccess)
            {
                DiagnosticsLogger.Record($"Destination test failed: '{dest.Alias ?? dest.Path}' - {resolution.Message}");
                return new DestinationTestResult(false, false, resolution.EffectivePath ?? string.Empty, resolution.Message);
            }

            var testTarget = resolution.EffectivePath;

            try
            {
                Directory.CreateDirectory(testTarget);

                // Startup/background probes should avoid write attempts that can raise
                // first-chance UnauthorizedAccess exceptions in debugger sessions.
                // Use a non-throwing heuristic and keep real write validation for
                // explicit operations (backup/test actions).
                var writable = IsLikelyWritableDirectory(testTarget);
                var message = writable
                    ? LStatic("Destinations.Test.Reachable", "Reachable")
                    : LStatic("Destinations.Test.ReadOnly", "Read-only");

                return new DestinationTestResult(true, writable, testTarget, message);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Destination test exception: '{dest.Alias ?? dest.Path}' - {ex.GetType().Name} - {ex.Message}");
                return new DestinationTestResult(false, false, testTarget, ex.Message);
            }
            finally
            {
                DiagnosticsLogger.Record($"Destination test complete: '{dest.Alias ?? dest.Path}'.");
                if (resolution.MountedByUs)
                {
                    // Respect destination auto-unmount setting for reachability probes.
                    var cleanupDest = new BackupDestination
                    {
                        Path           = resolution.EffectivePath,
                        CredentialName = dest.CredentialName,
                        Active         = dest.Active,
                        AutoMount      = dest.AutoMount,
                        AutoUnmount    = dest.AutoUnmount,
                        PreMounted     = dest.PreMounted,
                        Alias          = dest.Alias
                    };

                    var cleanupResolution = resolution with { Destination = cleanupDest };
                    _networkMountService.Cleanup(cleanupResolution);
                }
            }
        }

        private sealed record DestinationTestResult(bool Reachable, bool Writable, string EffectivePath, string Message);

        private static bool IsLikelyWritableDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                var info = new DirectoryInfo(path);
                if (!info.Exists)
                    return false;

                if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                    return false;

                return true;
            }
            catch
            {
                // Keep startup probes resilient and non-throwing.
                return false;
            }
        }

        private async Task<int?> EnsureArchiveUploadBufferAsync(
            BackupDestination dest,
            AppConfig cfg,
            string effectivePath,
            bool useArchiveMode,
            CancellationToken ct)
        {
            if (!useArchiveMode)
                return null;

            if (!cfg.Backups.EnableArchiveUploadAutoTune)
            {
                var configured = GetConfiguredArchiveUploadBufferBytes(cfg, dest);
                if (configured.HasValue && configured.Value > 0)
                    return configured.Value;

                if (IsSmbPath(dest.Path) || IsSmbPath(effectivePath))
                    return 1024 * 1024;

                return 1024 * 1024;
            }

            var existing = GetConfiguredArchiveUploadBufferBytes(cfg, dest);
            if (existing.HasValue && existing.Value > 0)
                return existing.Value;

            try
            {
                var display = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias ?? dest.Path;
                Console.WriteLine($"[DestinationProbe] Auto-tuning archive upload buffer for '{display}'.");

                var timeoutSeconds = IsSmbPath(dest.Path) || IsSmbPath(effectivePath) ? 8 : 3;
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                var result = await Task.Run(
                    () => ProbeArchiveUploadBufferBytes(effectivePath, ct, timeoutCts.Token),
                    ct);

                if (result.TimedOut)
                {
                    Console.WriteLine($"[DestinationProbe] Auto-tune timed out for '{dest.Path}'. Falling back to default buffer.");
                    return null;
                }

                SaveArchiveUploadBufferBytes(cfg, dest, result.BufferBytes);

                var bufferMb = result.BufferBytes / (1024d * 1024d);
                Console.WriteLine($"[DestinationProbe] Archive upload buffer for '{display}' set to {bufferMb:0.#} MB ({result.Mbps:0.0} MB/s).");

                return result.BufferBytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DestinationProbe] Auto-tune failed for '{dest.Path}': {ex.Message}");
                return null;
            }
        }

        private int? GetConfiguredArchiveUploadBufferBytes(AppConfig cfg, BackupDestination dest)
        {
            if (cfg.Backups.UseAdvancedDestinations)
            {
                var match = FindMatchingDestination(cfg, dest);
                return match?.ArchiveUploadBufferBytes;
            }

            return cfg.Backups.LegacyArchiveUploadBufferBytes;
        }

        private void SaveArchiveUploadBufferBytes(AppConfig cfg, BackupDestination dest, int bufferBytes)
        {
            if (bufferBytes <= 0)
                return;

            if (cfg.Backups.UseAdvancedDestinations)
            {
                var match = FindMatchingDestination(cfg, dest);
                if (match is null)
                    return;

                match.ArchiveUploadBufferBytes = bufferBytes;
            }
            else
            {
                cfg.Backups.LegacyArchiveUploadBufferBytes = bufferBytes;
            }

            AppConfigStore.Save(cfg);
        }

        private readonly record struct ArchiveProbeResult(int BufferBytes, double Mbps, bool TimedOut);

        private static ArchiveProbeResult ProbeArchiveUploadBufferBytes(
            string effectivePath,
            CancellationToken operationCt,
            CancellationToken timeoutCt)
        {
            const int probeSizeBytes = 64 * 1024 * 1024;
            const int chunkSizeBytes = 4 * 1024 * 1024;
            const int fallbackBytes  = 4 * 1024 * 1024;

            var probeDir  = Path.Combine(effectivePath, ".vaultsync");
            var probeFile = Path.Combine(probeDir, $".upload_probe_{Guid.NewGuid():N}.bin");
            var createdDir = false;

            try
            {
                if (!Directory.Exists(probeDir))
                {
                    Directory.CreateDirectory(probeDir);
                    createdDir = true;
                }

                var buffer = new byte[chunkSizeBytes];
                var remaining = probeSizeBytes;
                var sw = Stopwatch.StartNew();

                using (var fs = new FileStream(
                           probeFile,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.None,
                           chunkSizeBytes,
                           FileOptions.SequentialScan))
                {
                    while (remaining > 0)
                    {
                        operationCt.ThrowIfCancellationRequested();
                        if (timeoutCt.IsCancellationRequested)
                            return new ArchiveProbeResult(fallbackBytes, 0, TimedOut: true);

                        var toWrite = Math.Min(chunkSizeBytes, remaining);
                        fs.Write(buffer, 0, toWrite);
                        remaining -= toWrite;
                    }

                    fs.Flush(true);
                }

                sw.Stop();
                var seconds = Math.Max(0.05, sw.Elapsed.TotalSeconds);
                var mbps = (probeSizeBytes / seconds) / (1024d * 1024d);
                var bufferBytes = SelectArchiveUploadBufferBytes(mbps);
                return new ArchiveProbeResult(bufferBytes, mbps, TimedOut: false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return new ArchiveProbeResult(fallbackBytes, 0, TimedOut: false);
            }
            finally
            {
                try
                {
                    if (File.Exists(probeFile))
                        File.Delete(probeFile);
                }
                catch
                {
                    // best-effort cleanup
                }

                if (createdDir)
                {
                    try
                    {
                        if (Directory.Exists(probeDir) && !Directory.EnumerateFileSystemEntries(probeDir).Any())
                            Directory.Delete(probeDir);
                    }
                    catch
                    {
                        // best-effort cleanup
                    }
                }
            }
        }

        private static int SelectArchiveUploadBufferBytes(double mbps)
        {
            if (mbps < 5)
                return 2 * 1024 * 1024;
            if (mbps < 15)
                return 4 * 1024 * 1024;
            if (mbps < 50)
                return 8 * 1024 * 1024;
            if (mbps < 150)
                return 16 * 1024 * 1024;
            if (mbps < 400)
                return 32 * 1024 * 1024;

            return 64 * 1024 * 1024;
        }

        private bool IsMetadataSyncEnabled(AppConfig cfg, BackupDestination dest)
        {
            if (!cfg.Backups.EnableMetadataSync)
                return false;

            if (!dest.EnableMetadataSync)
                return false;

            return true;
        }

        private bool IsMetadataImportEnabled(AppConfig cfg, BackupDestination dest)
        {
            if (!IsMetadataSyncEnabled(cfg, dest))
                return false;

            if (!cfg.Backups.AutoImportMetadata)
                return false;

            if (!dest.AutoImportMetadata)
                return false;

            return true;
        }

        private void TryImportMetadataForDestination(AppConfig cfg, BackupDestination dest, string effectivePath)
        {
            if (!IsMetadataImportEnabled(cfg, dest))
                return;

            if (string.IsNullOrWhiteSpace(effectivePath))
                return;

            var key = effectivePath.Trim();
            if (_metadataImportRetryAfter.TryGetValue(key, out var retryAfter) &&
                DateTime.UtcNow < retryAfter)
            {
                return;
            }
            if (_metadataImportAttempts.TryGetValue(key, out var last) &&
                DateTime.UtcNow - last < TimeSpan.FromMinutes(5))
            {
                return;
            }

            _metadataImportAttempts[key] = DateTime.UtcNow;
            var options = new MetadataSyncOptions(
                AllowCreateProjects: true,
                MarkNeedsRestoreOnImport: cfg.Backups.PromptRestoreAfterImport);
            var name = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias!;
            _ = Task.Run(() =>
            {
                try
                {
                    Console.WriteLine($"[MetadataSync] Auto import started for '{name}'.");
                    var result = _metadataSyncService.ImportFromStore(effectivePath, options);
                    Console.WriteLine($"[MetadataSync] Auto import ({name}) result: {result.Status} (projects={result.ImportedProjects}, snapshots={result.ImportedSnapshots}, backups={result.ImportedBackups}, tombstones={result.AppliedTombstones}).");
                    if (result.Status == MetadataSyncStatus.Success &&
                        (result.ImportedProjects > 0 || result.ImportedSnapshots > 0 || result.ImportedBackups > 0 || result.AppliedTombstones > 0))
                    {
                        GlobalNotificationCenter.Instance.Show(
                            Lf("MetadataSync.Notification.Imported", "Imported updates from '{0}'.", name),
                            NotificationSeverity.Info,
                            L("MetadataSync.Notification.Title", "Metadata import"));
                    }
                    if (result.Status != MetadataSyncStatus.Success)
                    {
                        _metadataImportRetryAfter[key] = DateTime.UtcNow.AddMinutes(15);
                    }
                    ApplyRetentionAfterMetadataImport(effectivePath, result);
                    MaybeRefreshAfterImport(result);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MetadataSync] Auto import failed for '{name}': {ex.Message}");
                    _metadataImportRetryAfter[key] = DateTime.UtcNow.AddMinutes(15);
                    var actionLabel = L("Logs.CopySnippet", "Copy log snippet");
                    var actionCommand = CreateCopyLogSnippetCommand(
                        Lf("Logs.Snippet.MetadataImportFailure", "Metadata import failed for '{0}'.", name));
                    GlobalNotificationCenter.Instance.Show(
                        Lf("MetadataSync.Notification.ImportFailed", "Metadata import failed for '{0}'. Check logs for details.", name),
                        NotificationSeverity.Error,
                        L("MetadataSync.Notification.Title", "Metadata import"),
                        actionLabel: actionLabel,
                        actionCommand: actionCommand);
                }
            });
        }

        private void TryImportMetadataFromRoot(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                return;

            var cfg = _config;
            if (!cfg.Backups.EnableMetadataSync || !cfg.Backups.AutoImportMetadata)
                return;

            if (DateTime.UtcNow < _metadataRootImportRetryAfterUtc)
                return;

            var options = new MetadataSyncOptions(
                AllowCreateProjects: true,
                MarkNeedsRestoreOnImport: cfg.Backups.PromptRestoreAfterImport);
            try
            {
                Console.WriteLine("[MetadataSync] Auto import started for projects root.");
                var result = _metadataSyncService.ImportFromStore(rootPath, options);
                Console.WriteLine($"[MetadataSync] Auto import (projects root) result: {result.Status} (projects={result.ImportedProjects}, snapshots={result.ImportedSnapshots}, backups={result.ImportedBackups}, tombstones={result.AppliedTombstones}).");
                if (result.Status != MetadataSyncStatus.Success)
                {
                    _metadataRootImportRetryAfterUtc = DateTime.UtcNow.AddMinutes(15);
                }
                ApplyRetentionAfterMetadataImport(rootPath, result);
                MaybeRefreshAfterImport(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MetadataSync] Auto import failed for projects root: {ex.Message}");
                _metadataRootImportRetryAfterUtc = DateTime.UtcNow.AddMinutes(15);
                var actionLabel = L("Logs.CopySnippet", "Copy log snippet");
                var actionCommand = CreateCopyLogSnippetCommand(
                    L("Logs.Snippet.MetadataImportRootFailure", "Metadata import failed for projects root."));
                GlobalNotificationCenter.Instance.Show(
                    L("MetadataSync.Notification.ImportRootFailed", "Metadata import failed for projects root. Check logs for details."),
                    NotificationSeverity.Error,
                    L("MetadataSync.Notification.Title", "Metadata import"),
                    actionLabel: actionLabel,
                    actionCommand: actionCommand);
            }
        }

        private void MaybeRefreshAfterImport(MetadataSyncResult result)
        {
            if (result.Status != MetadataSyncStatus.Success)
                return;

            if (result.ImportedProjects <= 0 &&
                result.ImportedSnapshots <= 0 &&
                result.ImportedBackups <= 0 &&
                result.AppliedTombstones <= 0)
            {
                return;
            }

            Dispatcher.UIThread.Post(RefreshUiAfterMetadataImport);
        }

        private void ApplyRetentionAfterMetadataImport(string rootPath, MetadataSyncResult result)
        {
            if (result.Status != MetadataSyncStatus.Success || result.ImportedBackups <= 0)
                return;

            if (string.IsNullOrWhiteSpace(rootPath))
                return;

            try
            {
                var cfg = AppConfigStore.Load();
                var maxSnapshotsToKeep = cfg.Backups.MaxSnapshotsPerProject;
                if (maxSnapshotsToKeep <= 0)
                    return;

                var projects = result.AffectedProjectIds.Count > 0
                    ? result.AffectedProjectIds
                    : _repo.GetAllProjects().Select(p => p.Id).ToArray();

                foreach (var projectId in projects)
                {
                    _backupService.EnforceRetentionForProject(projectId, rootPath, maxSnapshotsToKeep);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MetadataSync] Retention after import failed: {ex.Message}");
            }
        }

        private void RefreshUiAfterMetadataImport()
        {
            _ = RefreshUiAfterMetadataImportAsync();
        }

        private async Task RefreshUiAfterMetadataImportAsync()
        {
            if (Interlocked.Exchange(ref _metadataUiRefreshInFlight, 1) == 1)
            {
                Interlocked.Exchange(ref _metadataUiRefreshQueued, 1);
                return;
            }

            try
            {
                ReloadBackupsVmData();
                await DashboardViewModel.RefreshAsync();
                await _projectsViewModel.RefreshAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _metadataUiRefreshInFlight, 0);
                if (Interlocked.Exchange(ref _metadataUiRefreshQueued, 0) == 1)
                {
                    RefreshUiAfterMetadataImport();
                }
            }
        }

        private void TryExportMetadataForBackup(
            AppConfig cfg,
            BackupDestination dest,
            string effectivePath,
            int backupId,
            bool? forceBackfillOverride = null)
        {
            if (!IsMetadataSyncEnabled(cfg, dest))
                return;

            if (string.IsNullOrWhiteSpace(effectivePath) || backupId <= 0)
                return;

            var machineId = Environment.MachineName;
            var name = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias!;
            var forceBackfill = forceBackfillOverride ?? dest.ForceMetadataBackfill;
            _ = Task.Run(async () =>
            {
                try
                {
                    Console.WriteLine($"[MetadataSync] Export started for backup {backupId} -> '{name}'.");
                    var result = await _metadataSyncService.ExportBackupToStoreAsync(
                        effectivePath,
                        backupId,
                        _currentVersionString,
                        machineId,
                        forceBackfill);
                    Console.WriteLine($"[MetadataSync] Export ({name}) result: {result.Status}.");
                    if (forceBackfillOverride is null &&
                        dest.ForceMetadataBackfill &&
                        result.Status == MetadataSyncStatus.Success)
                    {
                        ClearDestinationForceBackfill(dest);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MetadataSync] Export failed for '{name}': {ex.Message}");
                }
            });
        }

        private async Task ExportMetadataForProjectSettingsChangeAsync(int projectId)
        {
            try
            {
                var project = _repo.GetProjectById(projectId);
                if (project is null)
                    return;

                var latestBackup = _repo.GetLatestBackupForProject(projectId);
                if (latestBackup is null || latestBackup.Id <= 0)
                    return;

                var cfg = await Task.Run(() => AppConfigStore.Load());
                var destinations = ResolveDestinationsForProject(project, cfg).Destinations;
                if (destinations.Count == 0)
                    return;

                foreach (var dest in destinations)
                {
                    if (!IsMetadataSyncEnabled(cfg, dest))
                        continue;

                    var resolution = await PrepareDestinationAsync(dest, cfg);
                    if (!resolution.IsSuccess || string.IsNullOrWhiteSpace(resolution.EffectivePath))
                        continue;

                    TryExportMetadataForBackup(
                        cfg,
                        dest,
                        resolution.EffectivePath,
                        latestBackup.Id,
                        forceBackfillOverride: true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MetadataSync] Project settings export failed for projectId={projectId}: {ex.Message}");
            }
        }

        private void ClearDestinationForceBackfill(BackupDestination dest)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var cfg = AppConfigStore.Load();
                    var destEntry = FindMatchingDestination(cfg, dest);
                    if (destEntry != null && destEntry.ForceMetadataBackfill)
                    {
                        destEntry.ForceMetadataBackfill = false;
                        AppConfigStore.Save(cfg);
                    }

                    if (_settingsViewModel is null)
                        return;

                    Dispatcher.UIThread.Post(() =>
                    {
                        var vmDest = _settingsViewModel.Destinations
                            .FirstOrDefault(d => DestinationsMatch(d.Path, d.Alias, dest));
                        if (vmDest != null)
                        {
                            vmDest.ForceMetadataBackfill = false;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MetadataSync] Failed to clear force-backfill flag: {ex.Message}");
                }
            });
        }

        private static BackupDestination? FindMatchingDestination(AppConfig cfg, BackupDestination target)
        {
            if (cfg.Backups.Destinations is null || cfg.Backups.Destinations.Count == 0)
                return null;

            return cfg.Backups.Destinations.FirstOrDefault(d => DestinationsMatch(d.Path, d.Alias, target));
        }

        private static bool DestinationsMatch(string? path, string? alias, BackupDestination target)
        {
            if (!string.IsNullOrWhiteSpace(path) &&
                !string.IsNullOrWhiteSpace(target.Path) &&
                string.Equals(path, target.Path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrWhiteSpace(alias) &&
                !string.IsNullOrWhiteSpace(target.Alias) &&
                string.Equals(alias, target.Alias, StringComparison.OrdinalIgnoreCase);
        }

        private Task CheckNasAndMigrateAsync()
        {
            if (Interlocked.Exchange(ref _nasMonitorInFlight, 1) == 1)
                return Task.CompletedTask;

            try
            {
                if (BackupsViewModel.IsBusy)
                    return Task.CompletedTask;

                var cfg = AppConfigStore.Load();

                if (_settingsViewModel?.PreferExternalDrives != true)
                    return Task.CompletedTask;

                var backupRoot = cfg.Backups.BackupRoot;
                if (string.IsNullOrWhiteSpace(backupRoot) || !IsNetworkPath(backupRoot))
                    return Task.CompletedTask;

                if (!Directory.Exists(backupRoot))
                    return Task.CompletedTask;

                var projects = _repo.GetAllProjects().ToList();
                var hadTemp = false;

                foreach (var project in projects)
                {
                    var tempRoot = Path.Combine(project.RootPath, ".vaultsync-temp-backups");
                    if (Directory.Exists(tempRoot))
                    {
                        hadTemp = true;
                        TryMigrateTempBackups(project, backupRoot);
                    }
                }

                // If no temp backups remain anywhere, stop the monitor to avoid unnecessary pings.
                if (!hadTemp)
                {
                    StopNasMonitor();
                }
            }
            catch (Exception ex)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _nasMonitorInFlight, 0);
            }

            return Task.CompletedTask;
        }

    }
}
