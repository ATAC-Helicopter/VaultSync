using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Notifications;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels.Notifications;
using VaultSync.UI.Views;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private void OnUpdateCheckRequested()
        {
            if (!CanUseSelfUpdate)
            {
                DiagnosticsLogger.Record("Manual update check ignored for Store distribution.");
                _settingsViewModel.SetStoreManagedUpdatesStatus();
                ClearUpdateState();
                return;
            }

            DiagnosticsLogger.Record("Manual update check requested.");
            Console.WriteLine("[Update] Manual update check requested.");
            StartUpdateCheck(ignoreSettings: true);
        }

        private void ShowLogConsole()
        {
            if (_logConsoleWindow is not null)
            {
                DiagnosticsLogger.Record("Log console already open; activating.");
                _logConsoleWindow.Activate();
                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                try
                {
                    DiagnosticsLogger.Record("Installing log capture for macOS.");
                    _logConsoleService.InstallCapture();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LogConsole] Capture install failed: {ex.Message}");
                    DiagnosticsLogger.Record($"Log capture install failed: {ex.GetType().Name} - {ex.Message}");
                }
            }

            DiagnosticsLogger.Record("Creating log console window.");
            var vm = new LogConsoleViewModel(_logConsoleService);
            var window = new LogConsoleWindow(vm);

            window.Show();

            window.Closed += (_, _) =>
            {
                DiagnosticsLogger.Record("Log console closed.");
                _logConsoleWindow = null;
            };

            _logConsoleWindow = window;
        }

        private void OnLanguageChanged()
        {
            try
            {
                var culture = new CultureInfo(_localizationService.CurrentLanguage);
                CultureInfo.CurrentCulture = culture;
                CultureInfo.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            catch
            {
                // Ignore culture switch failures to avoid breaking UI refresh.
            }

            Dispatcher.UIThread.Post(() =>
            {
                _projectsViewModel.RefreshLocalization();
                RefreshHeadersForCurrentView();
                RefreshCurrentViewLocalization();
                TrayMenuRefreshRequested?.Invoke();
            });
        }

        private void RefreshHeadersForCurrentView()
        {
            if (CurrentView == _projectsViewModel)
            {
                HeaderTitle  = L("Nav.Projects", "Projects");
                HeaderKicker = L("Main.HeaderProjects", "All repositories");
            }
            else if (CurrentView == _backupsViewModel)
            {
                HeaderTitle  = L("Nav.Backups", "Backups");
                HeaderKicker = L("Main.HeaderBackups", "Snapshots & restore");
            }
            else if (CurrentView == _historyViewModel)
            {
                HeaderTitle  = L("Nav.History", "History");
                HeaderKicker = L("Main.HeaderHistory", "Project timeline");
            }
            else if (CurrentView == _recoveryViewModel)
            {
                HeaderTitle  = L("Nav.Recovery", "Recovery");
                HeaderKicker = L("Main.HeaderRecovery", "Readiness & coverage");
            }
            else if (CurrentView == _settingsViewModel)
            {
                HeaderTitle  = L("Nav.Settings", "Settings");
                HeaderKicker = L("Main.HeaderSettings", "Preferences");
            }
            else
            {
                HeaderTitle  = L("Nav.Dashboard", "Dashboard");
                HeaderKicker = L("Main.HeaderOverview", "Overview");
            }
        }

        private void RefreshCurrentViewLocalization()
        {
            _projectsViewModel?.RefreshLocalization();
            if (_dashboardViewModel != null)
            {
                _dashboardViewModel.ReapplyLocalization();
                 _ =_dashboardViewModel.RefreshAsync(force: true);
            }
            if (_backupsViewModel != null)
            {
                _backupsViewModel.ReapplyLocalization();
                ReloadBackupsVmData();
            }
        }

        private void StartUpdateCheck(bool ignoreSettings = false)
        {
            using var timing = RuntimeTiming.Measure(ignoreSettings ? "Update check start forced" : "Update check start");
            if (!CanUseSelfUpdate)
            {
                DiagnosticsLogger.Record("GitHub update checks disabled for Store distribution.");
                _settingsViewModel.SetStoreManagedUpdatesStatus();
                ClearUpdateState();
                return;
            }

            DiagnosticsLogger.Record($"Update check start (ignoreSettings={ignoreSettings}, channel={CurrentUpdateChannel}).");
            CancelUpdateCheck();
            CancelUpdateRetry();

            if (!ignoreSettings && !_settingsViewModel.CheckForUpdatesOnStartup)
            {
                ClearUpdateState();
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!ignoreSettings && (now - _lastUpdateCheckUtc) < UpdateCheckMinInterval)
            {
                return;
            }
            _lastUpdateCheckUtc = now;

            _updateCheckCts = new CancellationTokenSource();
            Console.WriteLine($"[Update] Starting update check (channel={CurrentUpdateChannel}).");
            if (OperatingSystem.IsMacOS())
            {
                _logConsoleService.SetUiCaptureEnabled(false);
                _updateCheckLogCaptureSuppressed = 1;
                if (!_updateCheckLogServiceSuppressed)
                {
                    _updateCheckPrevLogEnabled = _logConsoleService.Enabled;
                    _updateCheckPrevSaveToFile = _logConsoleService.SaveToFile;
                    _logConsoleService.Enabled = false;
                    _logConsoleService.SaveToFile = false;
                    _updateCheckLogServiceSuppressed = true;
                }
            }
            _ = Task.Run(() => RunUpdateCheckAsync(_updateCheckCts.Token));
        }

        private void ConfigureUpdateCheckTimer()
        {
            _updateCheckTimer?.Dispose();
            _updateCheckTimer = null;
            CancelUpdateRetry();

            if (!CanUseSelfUpdate)
                return;

            if (!_settingsViewModel.CheckForUpdatesOnStartup)
                return;

            int intervalMinutes = Math.Max(15, _settingsViewModel.UpdateCheckIntervalMinutes);
            var interval = TimeSpan.FromMinutes(intervalMinutes);

            _updateCheckTimer = new Timer(_ =>
            {
                if (!_settingsViewModel.CheckForUpdatesOnStartup)
                    return;

                if (Interlocked.Exchange(ref _updateCheckInFlight, 1) == 1)
                    return;

                try
                {
                    StartUpdateCheck(ignoreSettings: true);
                }
                finally
                {
                    Interlocked.Exchange(ref _updateCheckInFlight, 0);
                }
            }, null, interval, interval);
        }

        private void StartDeferredStartupTasks()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    TimeSpan delay = OperatingSystem.IsMacOS()
                        ? TimeSpan.FromSeconds(30)
                        : TimeSpan.FromSeconds(2);
                    await Task.Delay(delay);
                    RecordStartupPhase("deferred-startup-begin");

                    AppConfig cfg = _configStore.GetSnapshot();
                    await RunStartupDestinationProbeAsync().ConfigureAwait(false);
                    RecordStartupPhase("destination-probe-complete");

                    if (cfg.Backups.EnableMetadataSync)
                    {
                        using (RuntimeTiming.Measure("Deferred startup metadata root import"))
                        {
                            TryImportMetadataFromRoot(cfg.ProjectsRoot ?? string.Empty);
                        }
                        RecordStartupPhase("metadata-import-queued");
                    }
                    else
                    {
                        RecordStartupPhase("metadata-import-skipped");
                    }

                    await RunStartupBackupIndexConsistencyCheckAsync().ConfigureAwait(false);
                    RecordStartupPhase("backup-index-scan-complete");
                    using (RuntimeTiming.Measure("Deferred startup update check dispatch"))
                    {
                        StartUpdateCheck();
                    }
                    RecordStartupPhase("update-check-started");
                    ConfigureUpdateCheckTimer();
                    RecordStartupPhase("update-timer-configured");
                    QueueDeferredProjectsRefresh();
                    RecordStartupPhase("startup-complete");
                }
                catch (Exception ex)
                {
                    RecordStartupPhase("startup-failed");
                    DiagnosticsLogger.Record($"Deferred startup failed: {ex.GetType().Name} - {ex.Message}");
                }
                finally
                {
                    PersistStartupDiagnosticsSummary();
                }
            });
        }

        private void QueueDeferredProjectsRefresh()
        {
            RunDetached(async () =>
            {
                TimeSpan delay = TimeSpan.FromSeconds(4) - (DateTime.UtcNow - _appStartUtc);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay).ConfigureAwait(false);

                await _projectsViewModel.RefreshAsync(forceDiscovery: false).ConfigureAwait(false);

                if (string.Equals(CurrentViewKey, "Dashboard", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(CurrentViewKey, "Projects", StringComparison.OrdinalIgnoreCase))
                {
                    await DashboardViewModel.RefreshAsync(force: true).ConfigureAwait(false);
                }
            }, nameof(QueueDeferredProjectsRefresh));
        }

        private void ScheduleLogCaptureInstall()
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        _logConsoleService.InstallCapture();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LogConsole] Capture install failed: {ex.Message}");
                    }
                });
            });
        }


        private async Task StartPatchInstallAsync()
        {
            if (!CanUseSelfUpdate)
            {
                PatchStatusMessage = L("Update.Store.ManagedDescription", "Updates are managed by Microsoft Store for this build.");
                _patchBlocked = true;
                _patchFailed = true;
                NotifyPatchAvailabilityChanged();
                OnPropertyChanged(nameof(ShowInstallerFallback));
                return;
            }

            if (!IsPatchAvailable || _pendingUpdateResult is null || IsPatchInstalling)
                return;

            if (PatchInstallRequiresInstallerFallback(AppContext.BaseDirectory))
            {
                PatchStatusMessage = L("Patch.Status.ManifestIncompatible", "Patch not available for this version. Use the installer instead.");
                _patchBlocked = true;
                _patchFailed = true;
                DiagnosticsLogger.Record($"Patch install blocked: install directory is not writable ({AppContext.BaseDirectory}).");
                NotifyPatchAvailabilityChanged();
                OnPropertyChanged(nameof(ShowInstallerFallback));
                return;
            }

            IsPatchInstalling = true;
            PatchStatusMessage = L("Patch.Status.Downloading", "Downloading patch...");
            _patchFailed = false;
            OnPropertyChanged(nameof(ShowInstallerFallback));

            try
            {
                PatchPreflightResult preflight = await PatchUpdateService.PreflightPatchAsync(
                    _pendingUpdateResult,
                    _currentVersionString,
                    CancellationToken.None);
                _pendingUpdateResult.Diagnostics.PatchPreflight = ToPatchPreflightDiagnostics(preflight, _currentVersionString);
                PersistUpdateDiagnostics(_pendingUpdateResult.Diagnostics);

                if (!preflight.Eligible || preflight.Plan is null)
                {
                    PatchStatusMessage = L("Patch.Status.ManifestIncompatible", "Patch manifest cannot be applied to this version.");
                    _patchBlocked = true;
                    _patchFailed = true;
                    NotifyPatchAvailabilityChanged();
                    OnPropertyChanged(nameof(ShowInstallerFallback));
                    return;
                }

                PatchPlan plan = preflight.Plan;

                string? archivePath = await PatchUpdateService.DownloadPatchArchiveAsync(
                    plan,
                    (downloaded, total, rate) =>
                    {
                        UpdateDownloadStatus(
                            L("Patch.Status.Downloading", "Downloading patch"),
                            downloaded,
                            total,
                            rate);
                    },
                    CancellationToken.None);
                if (archivePath is null)
                {
                    PatchStatusMessage = L("Patch.Status.DownloadFailed", "Failed to download or verify the patch.");
                    _patchFailed = true;
                    OnPropertyChanged(nameof(ShowInstallerFallback));
                    return;
                }

                PatchStatusMessage = L("Patch.Status.Installing", "Installing patch and restarting...");

                if (!PatchInstallService.TryLaunchPatchInstaller(plan, archivePath, out string? error))
                {
                    PatchStatusMessage = L("Patch.Status.InstallFailed", "Failed to start the patch installer.");
                    Debug.WriteLine($"[Patch] Failed to launch helper: {error}");
                    _patchFailed = true;
                    OnPropertyChanged(nameof(ShowInstallerFallback));
                    return;
                }

                ShutdownForPatchInstall();
                return;
            }
            catch (TaskCanceledException)
            {
                PatchStatusMessage = L("Patch.Status.Timeout", "Patch download timed out. Check your connection or use the installer.");
                _patchFailed = true;
                OnPropertyChanged(nameof(ShowInstallerFallback));
            }
            catch (Exception ex)
            {
                PatchStatusMessage = L("Patch.Status.DownloadFailed", "Failed to download or verify the patch.");
                Debug.WriteLine($"[Patch] Install failed: {ex}");
                _patchFailed = true;
                OnPropertyChanged(nameof(ShowInstallerFallback));
            }
            finally
            {
                IsPatchInstalling = false;
            }
        }

        private static void ShutdownForPatchInstall()
        {
            Dispatcher.UIThread.Post(() =>
            {
                DiagnosticsLogger.RecordWithStack("Shutdown for patch install requested.");
                App.MarkShuttingDown();
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
                else
                {
                    Environment.Exit(0);
                }
            });
        }

        private static bool CanWriteInstallDir(string installDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(installDir))
                    return false;

                Directory.CreateDirectory(installDir);
                string testPath = Path.Combine(installDir, $".vaultsync-write-test-{Guid.NewGuid():N}");
                using (new FileStream(testPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }
                File.Delete(testPath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool PatchInstallRequiresInstallerFallback(string installDir)
        {
            if (IsEnvFlagEnabled("VAULTSYNC_FORCE_INSTALLER_FALLBACK"))
                return true;

            if (OperatingSystem.IsWindows() || CanWriteInstallDir(installDir))
                return false;

            return !PatchInstallService.CanLaunchProtectedPatchInstall(installDir);
        }

        private void NotifyPatchAvailabilityChanged()
        {
            OnPropertyChanged(nameof(IsPatchAvailable));
            OnPropertyChanged(nameof(ShowPatchButton));
            OnPropertyChanged(nameof(ShowInstallerFallback));
            _installPatchCommand.RaiseCanExecuteChanged();
        }

        private async Task RunUpdateCheckAsync(CancellationToken cancellationToken)
        {
            using var timing = RuntimeTiming.Measure("Update check run");
            try
            {
                DiagnosticsLogger.Record("Update check running.");
                UpdateCheckEvaluation evaluation = await GitHubUpdateService.CheckForUpdateAsync(_currentVersionString, CurrentUpdateChannel, cancellationToken)
                    .ConfigureAwait(false);
                PersistUpdateDiagnostics(evaluation.Diagnostics);
                UpdateCheckResult? result = evaluation.Update;
                if (result is null)
                {
                    Console.WriteLine("[Update] No update available.");
                    RecordUpdateCheckSuccess();
                    Dispatcher.UIThread.Post(ClearUpdateState);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_config.Advanced.SkippedUpdateTag)
                    && string.Equals(result.TagName, _config.Advanced.SkippedUpdateTag, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[Update] Update skipped: tag={result.TagName}.");
                    RecordUpdateCheckSuccess();
                    Dispatcher.UIThread.Post(ClearUpdateState);
                    return;
                }

                if (result.HasPatch)
                {
                    PatchPreflightResult preflight = await PatchUpdateService.PreflightPatchAsync(result, _currentVersionString, cancellationToken)
                        .ConfigureAwait(false);
                    result.Diagnostics.PatchPreflight = ToPatchPreflightDiagnostics(preflight, _currentVersionString);
                    PersistUpdateDiagnostics(result.Diagnostics);
                    if (!preflight.Eligible)
                    {
                        _patchBlocked = true;
                        Console.WriteLine($"[Update] Patch preflight blocked: code={preflight.StatusCode}, installerFallback={preflight.RequiresInstaller}.");
                        DiagnosticsLogger.Record($"Patch preflight blocked: code={preflight.StatusCode}, installerFallback={preflight.RequiresInstaller}.");
                    }
                    else if (PatchInstallRequiresInstallerFallback(AppContext.BaseDirectory))
                    {
                        PatchPreflightDiagnostics diagnostics = result.Diagnostics.PatchPreflight;
                        diagnostics.StatusCode = "protected-install-requires-installer";
                        diagnostics.Message = "Install directory is not writable by the current user; installer fallback is required.";
                        diagnostics.Eligible = false;
                        diagnostics.RequiresInstaller = true;
                        PersistUpdateDiagnostics(result.Diagnostics);

                        _patchBlocked = true;
                        Console.WriteLine($"[Update] Patch preflight blocked: protected install requires installer fallback; installDir={AppContext.BaseDirectory}.");
                        DiagnosticsLogger.Record($"Patch preflight blocked: protected install requires installer fallback; installDir={AppContext.BaseDirectory}.");
                    }
                    else
                    {
                        _patchBlocked = false;
                    }
                }
                else
                {
                    _patchBlocked = false;
                    result.Diagnostics.PatchPreflight = new PatchPreflightDiagnostics
                    {
                        StatusCode = "no-patch-assets",
                        Message = "Release does not provide patch assets.",
                        CurrentVersion = _currentVersionString,
                        ManifestAllowedBaseVersions = [],
                        Eligible = false,
                        RequiresInstaller = result.HasInstaller,
                        HasManifest = false,
                        HasArchive = false,
                        HasInstaller = result.HasInstaller
                    };
                    PersistUpdateDiagnostics(result.Diagnostics);
                }

                Console.WriteLine($"[Update] Update available: tag={result.TagName}, name={result.ReleaseName}, patch={result.HasPatch}, installer={result.HasInstaller}.");
                RecordUpdateCheckSuccess();
                DiagnosticsLogger.Record($"Update available: tag={result.TagName}, patch={result.HasPatch}, installer={result.HasInstaller}.");
                Dispatcher.UIThread.Post(() => ApplyUpdateResult(result));
            }
            catch (OperationCanceledException)
            {
                DiagnosticsLogger.Record("Update check cancelled.");
            }
            catch (Exception ex)
            {
                // Silently ignore update failures; we don't want to disturb the user.
                DiagnosticsLogger.Record($"Update check failed: {ex.GetType().Name} - {ex.Message}");
                PersistUpdateDiagnostics(new UpdateCheckDiagnostics
                {
                    CheckedUtc = DateTimeOffset.UtcNow.ToString("O"),
                    Channel = CurrentUpdateChannel.ToString(),
                    CurrentVersion = _currentVersionString,
                    Decision = "error",
                    Error = ex.Message
                });
                RecordUpdateCheckFailure(ex);
            }
            finally
            {
                _updateCheckCts?.Dispose();
                _updateCheckCts = null;
                if (_updateCheckLogCaptureSuppressed == 1)
                {
                    _updateCheckLogCaptureSuppressed = 0;
                    Dispatcher.UIThread.Post(() =>
                        _logConsoleService.SetUiCaptureEnabled(true, loadSnapshot: false));
                }
                if (_updateCheckLogServiceSuppressed)
                {
                    _updateCheckLogServiceSuppressed = false;
                    _logConsoleService.Enabled = _updateCheckPrevLogEnabled;
                    _logConsoleService.SaveToFile = _updateCheckPrevSaveToFile;
                }
            }
        }

        private void ApplyUpdateResult(UpdateCheckResult result)
        {
            if (!CanUseSelfUpdate)
            {
                ClearUpdateState();
                return;
            }

            if (App.IsCrashing)
                return;

            IsInstallerDownloading = false;
            _patchFailed = false;
            _isUpdateBannerDismissed = false;
            IsUpdateAvailable = true;
            UpdateBannerMessage = Lf("Update.Banner", "New update available: {0} ({1})", result.ReleaseName, result.TagName);
            SetUpdateReleaseNotes(TrimUpdateReleaseNotes(result.ReleaseNotes));
            _updateReleaseUrl = (result.InstallerUrl ?? result.ReleaseUrl).ToString();
            _pendingUpdateResult = result;
            NotifyPatchAvailabilityChanged();
            PatchStatusMessage = string.Empty;
            OnPropertyChanged(nameof(ShowUpdateBanner));
            OnPropertyChanged(nameof(ShowInstallerFallback));
            OnPropertyChanged(nameof(ReleaseActionText));
            OnPropertyChanged(nameof(IsReleaseActionEnabled));
            OnPropertyChanged(nameof(CanSkipUpdate));

            string title = L("Update.Available.Title", "Update available");
            string channelLabel = CurrentUpdateChannel == GitHubReleaseChannel.Beta
                ? L("Update.Channel.Beta", "Beta")
                : L("Update.Channel.Stable", "Stable");
            string message = Lf("Update.Available.MessageChannel", "VaultSync {0} is ready on the {1} channel.", result.TagName, channelLabel);

            GlobalNotificationCenter.Instance.Show(
                message,
                NotificationSeverity.Info,
                title);

            if (ShouldRaiseSystemNotification && !OperatingSystem.IsMacOS())
            {
                GlobalNotificationCenter.Instance.ShowSystem(
                    message,
                    NotificationSeverity.Info,
                    title);
            }
        }

        private void SetUpdateReleaseNotes(string? notes)
        {
            _updateReleaseNotes = notes ?? string.Empty;
            OnPropertyChanged(nameof(UpdateTooltip));
        }

        private static string TrimUpdateReleaseNotes(string? notes)
        {
            if (string.IsNullOrWhiteSpace(notes))
                return string.Empty;

            string normalized = notes.Replace("\r", string.Empty).Trim();
            const int maxChars = 1200;
            if (normalized.Length <= maxChars)
                return normalized;

            return normalized[..maxChars] + "…";
        }

        private void ClearUpdateState()
        {
            IsUpdateAvailable = false;
            UpdateBannerMessage = string.Empty;
            _updateReleaseUrl = string.Empty;
            SetUpdateReleaseNotes(string.Empty);
            _pendingUpdateResult = null;
            _patchBlocked = false;
            _patchFailed = false;
            _isUpdateBannerDismissed = false;
            NotifyPatchAvailabilityChanged();
            PatchStatusMessage = string.Empty;
            OnPropertyChanged(nameof(ShowUpdateBanner));
            OnPropertyChanged(nameof(ShowInstallerFallback));
            OnPropertyChanged(nameof(ReleaseActionText));
            OnPropertyChanged(nameof(IsReleaseActionEnabled));
            OnPropertyChanged(nameof(CanSkipUpdate));
        }

        private void SkipUpdateVersion()
        {
            if (_pendingUpdateResult is null)
                return;

            string tag = _pendingUpdateResult.TagName ?? string.Empty;
            _ = Task.Run(() =>
            {
                try
                {
                    AppConfig cfg = _configStore.Load();
                    cfg.Advanced.SkippedUpdateTag = tag;
                    _configStore.Save(cfg);
                    Dispatcher.UIThread.Post(() => _config.Advanced.SkippedUpdateTag = tag);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Update] Failed to persist skipped tag: {ex.Message}");
                }
            });

            ClearUpdateState();
            _isUpdateBannerDismissed = true;
            OnPropertyChanged(nameof(ShowUpdateBanner));
        }

        private void DismissUpdateBanner()
        {
            if (!IsUpdateAvailable)
                return;

            _isUpdateBannerDismissed = true;
            OnPropertyChanged(nameof(ShowUpdateBanner));
        }

        private void CancelUpdateCheck()
        {
            if (_updateCheckCts is null)
                return;

            _updateCheckCts.Cancel();
            _updateCheckCts.Dispose();
            _updateCheckCts = null;
        }

        private void CancelUpdateRetry()
        {
            _updateCheckRetryTimer?.Dispose();
            _updateCheckRetryTimer = null;
        }

        private void RecordUpdateCheckSuccess()
        {
            _lastUpdateCheckAt = DateTimeOffset.Now;
            _lastUpdateCheckError = null;
            CancelUpdateRetry();
            Dispatcher.UIThread.Post(() =>
                _settingsViewModel.UpdateUpdateCheckStatus(_lastUpdateCheckAt, _lastUpdateCheckError));
        }

        private void RecordUpdateCheckFailure(Exception ex)
        {
            _lastUpdateCheckAt = DateTimeOffset.Now;
            _lastUpdateCheckError = ex.Message;
            Console.WriteLine($"[Update] Update check failed: {ex.GetType().Name}: {ex.Message}");
            Dispatcher.UIThread.Post(() =>
                _settingsViewModel.UpdateUpdateCheckStatus(_lastUpdateCheckAt, _lastUpdateCheckError));
            ScheduleUpdateRetry();
        }

        private void PersistUpdateDiagnostics(UpdateCheckDiagnostics diagnostics)
        {
            try
            {
                AppConfig cfg = _configStore.Load();
                cfg.Advanced.UpdateDiagnostics = diagnostics ?? new UpdateCheckDiagnostics();
                _configStore.Save(cfg);
                Dispatcher.UIThread.Post(() => _settingsViewModel.ReloadUpdateDiagnostics());
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Update diagnostics persist failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private static PatchPreflightDiagnostics ToPatchPreflightDiagnostics(PatchPreflightResult preflight, string currentVersion)
        {
            return new PatchPreflightDiagnostics
            {
                StatusCode = preflight.StatusCode,
                Message = preflight.Message,
                CurrentVersion = currentVersion,
                ManifestPreviousVersion = preflight.Manifest?.PreviousVersion ?? string.Empty,
                ManifestAllowedBaseVersions = PatchUpdateService.TryGetAllowedBaseVersions(
                    preflight.Manifest ?? new PatchManifest(),
                    out System.Collections.Generic.IReadOnlyList<string>? allowedBaseVersions,
                    out _,
                    out _)
                    ? [.. allowedBaseVersions]
                    : [],
                MatchedBaseVersion = PatchUpdateService.TryValidateAllowedBaseVersions(
                    preflight.Manifest ?? new PatchManifest(),
                    currentVersion,
                    out _,
                    out string? matchedBaseVersion,
                    out _,
                    out _)
                    ? matchedBaseVersion
                    : string.Empty,
                ManifestTargetVersion = preflight.Manifest?.TargetVersion ?? string.Empty,
                Eligible = preflight.Eligible,
                RequiresInstaller = preflight.RequiresInstaller,
                HasManifest = preflight.HasManifest,
                HasArchive = preflight.HasArchive,
                HasInstaller = preflight.HasInstaller
            };
        }

        private void ScheduleUpdateRetry()
        {
            if (_updateCheckRetryTimer is not null)
                return;

            var delay = TimeSpan.FromMinutes(5);
            _updateCheckRetryTimer = new Timer(_ =>
            {
                _updateCheckRetryTimer?.Dispose();
                _updateCheckRetryTimer = null;

                if (!_settingsViewModel.CheckForUpdatesOnStartup)
                    return;

                StartUpdateCheck(ignoreSettings: true);
            }, null, delay, Timeout.InfiniteTimeSpan);
        }

        private async Task OpenUpdateReleaseAsync()
        {
            if (IsInstallerDownloading)
                return;

            if (!CanUseSelfUpdate)
            {
                OpenMicrosoftStoreListing();
                return;
            }

            if (_pendingUpdateResult?.HasInstaller == true && _pendingUpdateResult.InstallerUrl is not null)
            {
                await DownloadAndLaunchInstallerAsync(_pendingUpdateResult.InstallerUrl, _pendingUpdateResult.InstallerName);
                return;
            }

            if (string.IsNullOrWhiteSpace(_updateReleaseUrl))
                return;

            TryOpenUrl(_updateReleaseUrl);
        }

        private void OpenMicrosoftStoreListing()
        {
            const string storeProtocolUrl = "ms-windows-store://pdp/?productid=9N9HRX4JCLCP";
            const string webStoreUrl = "https://apps.microsoft.com/detail/9N9HRX4JCLCP";

            if (OperatingSystem.IsWindows() && TryOpenUrl(storeProtocolUrl, showError: false))
                return;

            TryOpenUrl(webStoreUrl);
        }

        private async Task DownloadAndLaunchInstallerAsync(Uri installerUrl, string? installerName)
        {
            IsInstallerDownloading = true;
            PatchStatusMessage = L("Update.Installer.Downloading", "Downloading installer...");

            try
            {
                string downloadDir = Path.Combine(Path.GetTempPath(), "VaultSync", "updates");
                Directory.CreateDirectory(downloadDir);

                string fileName = string.IsNullOrWhiteSpace(installerName)
                    ? Path.GetFileName(installerUrl.LocalPath)
                    : installerName;
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = "VaultSync-Installer";
                }

                string tempPath = Path.Combine(downloadDir, $"{fileName}.download");
                string finalPath = Path.Combine(downloadDir, fileName);

                using HttpResponseMessage response = await s_installerClient.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                await using (Stream contentStream = await response.Content.ReadAsStreamAsync())
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await CopyToWithProgressAsync(
                        contentStream,
                        fileStream,
                        totalBytes,
                        (downloaded, total, rate) =>
                        {
                            UpdateDownloadStatus(
                                L("Update.Installer.Downloading", "Downloading installer"),
                                downloaded,
                                total,
                                rate);
                        },
                        CancellationToken.None);
                }

                File.Copy(tempPath, finalPath, overwrite: true);
                File.Delete(tempPath);
                EnsureInstallerLaunchPermissions(finalPath);

                PatchStatusMessage = L("Update.Installer.Launching", "Launching installer...");

                if (!TryLaunchInstaller(finalPath))
                {
                    PatchStatusMessage = L("Update.Installer.LaunchFailed", "Installer downloaded but could not be started.");
                    ShowUpdateError(PatchStatusMessage);
                    return;
                }

                PatchStatusMessage = L("Update.Installer.Launched", "Installer launched. VaultSync will close so setup can continue.");
                ShutdownForInstallerLaunch();
            }
            catch (TaskCanceledException)
            {
                PatchStatusMessage = L("Update.Installer.Timeout", "Installer download timed out. Check your connection or open the release page.");
                ShowUpdateError(PatchStatusMessage);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Update] Installer download failed: {ex}");
                PatchStatusMessage = L("Update.Installer.DownloadFailed", "Failed to download the installer. Open the release page instead.");
                ShowUpdateError(PatchStatusMessage);
            }
            finally
            {
                IsInstallerDownloading = false;
            }
        }

        private static bool TryLaunchInstaller(string installerPath)
        {
            if (OperatingSystem.IsLinux() &&
                installerPath.EndsWith(".deb", StringComparison.OrdinalIgnoreCase) &&
                TryLaunchDebianPackageInstall(installerPath))
            {
                return true;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = installerPath,
                    UseShellExecute = true
                };
                Process.Start(psi);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryLaunchDebianPackageInstall(string packagePath)
        {
            try
            {
                if (IsEnvFlagEnabled("VAULTSYNC_DEB_INSTALL_DRY_RUN"))
                {
                    DiagnosticsLogger.Record($"Debian package auto-install dry run: {packagePath}");
                    return true;
                }

                string? pkexec = FindExecutable("pkexec");
                string? aptGet = FindExecutable("apt-get");
                if (string.IsNullOrWhiteSpace(pkexec) || string.IsNullOrWhiteSpace(aptGet))
                {
                    DiagnosticsLogger.Record("Debian package auto-install unavailable: pkexec or apt-get not found.");
                    return false;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = pkexec,
                    UseShellExecute = false
                };

                psi.ArgumentList.Add(aptGet);
                psi.ArgumentList.Add("install");
                psi.ArgumentList.Add("--reinstall");
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add(packagePath);

                Process? process = Process.Start(psi);
                if (process is null)
                {
                    DiagnosticsLogger.Record("Debian package auto-install failed: pkexec did not start.");
                    return false;
                }

                DiagnosticsLogger.Record($"Debian package auto-install launched via pkexec: {packagePath}");
                return true;
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Debian package auto-install launch failed: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        private static string? FindExecutable(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string[] candidates =
            [
                Path.Combine("/usr/bin", name),
                Path.Combine("/bin", name),
                Path.Combine("/usr/local/bin", name),
                Path.Combine("/sbin", name),
                Path.Combine("/usr/sbin", name)
            ];

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path))
                return null;

            foreach (string entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    string candidate = Path.Combine(entry, name);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }

            return null;
        }

        private static bool IsEnvFlagEnabled(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureInstallerLaunchPermissions(string installerPath)
        {
            if (!OperatingSystem.IsLinux() || !installerPath.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                File.SetUnixFileMode(
                    installerPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"AppImage permission update failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private static void ShutdownForInstallerLaunch()
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(750).ConfigureAwait(false);

                Dispatcher.UIThread.Post(() =>
                {
                    DiagnosticsLogger.RecordWithStack("Shutdown for installer launch requested.");
                    App.MarkShuttingDown();
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    {
                        desktop.Shutdown();
                    }
                    else
                    {
                        Environment.Exit(0);
                    }
                });
            });
        }

        private bool TryOpenUrl(string url, bool showError = true)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                if (showError)
                {
                    string message = L("Update.Failed.Message", "Unable to open the release page; visit the GitHub releases manually.");
                    ShowUpdateError(message);
                }

                return false;
            }
        }

        private void ShowUpdateError(string message, string? titleOverride = null)
        {
            string title = titleOverride ?? L("Update.Failed.Title", "Update failed");
            GlobalNotificationCenter.Instance.Show(message, NotificationSeverity.Error, title);
            if (ShouldRaiseSystemNotification)
            {
                GlobalNotificationCenter.Instance.ShowSystem(message, NotificationSeverity.Error, title);
            }
        }

        private static HttpClient CreateInstallerHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(20)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VaultSync-Installer/1.0");
            return client;
        }

    }
}
