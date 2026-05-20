using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Notifications;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private static void RunDetached(Func<Task> operation, string operationName)
        {
            _ = RunDetachedCoreAsync(operation, operationName);
        }

        private static async Task RunDetachedCoreAsync(Func<Task> operation, string operationName)
        {
            try
            {
                await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record(
                    $"Detached operation failed ({operationName}): {ex.GetType().Name} - {ex.Message}");
            }
        }

        private string BuildBackupProgressLabel(string? etaText, string? currentFile, double percent)
        {
            bool isFinalizing = !string.IsNullOrWhiteSpace(etaText) &&
                               etaText.Contains("Finalizing", StringComparison.OrdinalIgnoreCase);
            if (isFinalizing)
            {
                return L("Backups.Status.Finalizing", "Finalizing...");
            }

            if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Uploading", StringComparison.OrdinalIgnoreCase))
            {
                return L("Backups.Stage.Uploading", "Uploading archive");
            }

            if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Compressing", StringComparison.OrdinalIgnoreCase))
            {
                return L("Backups.Stage.Compressing", "Compressing archive");
            }

            if (!string.IsNullOrWhiteSpace(currentFile))
            {
                return currentFile;
            }

            if (percent <= 0.1)
            {
                return L("Backups.Status.Preparing", "Preparing backup...");
            }

            if (!string.IsNullOrWhiteSpace(etaText) && etaText.Contains("Copying", StringComparison.OrdinalIgnoreCase))
            {
                return L("Backups.Stage.Copying", "Copying files");
            }

            if (percent < 100)
            {
                return L("Backups.Status.Running", "Running backup...");
            }

            return L("Backups.Status.Finalizing", "Finalizing...");
        }

        private void UpdateDownloadStatus(string prefix, long downloadedBytes, long? totalBytes, double? bytesPerSecond)
        {
            double? totalMb = totalBytes.HasValue && totalBytes.Value > 0
                ? totalBytes.Value / (1024d * 1024d)
                : (double?)null;
            double downloadedMb = downloadedBytes / (1024d * 1024d);
            double? rateMb = bytesPerSecond.HasValue && bytesPerSecond.Value > 0
                ? bytesPerSecond.Value / (1024d * 1024d)
                : (double?)null;

            string sizeText = totalMb.HasValue
                ? $"{downloadedMb:0.0}/{totalMb.Value:0.0} MB"
                : $"{downloadedMb:0.0} MB";

            string rateText = rateMb.HasValue
                ? $"{rateMb.Value:0.0} MB/s"
                : L("Update.Download.Waiting", "Waiting for network...");

            string status = $"{prefix} ({sizeText}) - {rateText}";

            if (Dispatcher.UIThread.CheckAccess())
            {
                PatchStatusMessage = status;
            }
            else
            {
                Dispatcher.UIThread.Post(() => PatchStatusMessage = status);
            }
        }

        private static async Task CopyToWithProgressAsync(
            Stream source,
            Stream destination,
            long? totalBytes,
            Action<long, long?, double?>? progress,
            CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[1024 * 128];
            long totalRead = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            TimeSpan lastReport = TimeSpan.Zero;
            long lastBytes = 0;

            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;

                if (progress is null)
                    continue;

                TimeSpan elapsed = stopwatch.Elapsed;
                if (elapsed - lastReport < TimeSpan.FromMilliseconds(250))
                    continue;

                long deltaBytes = totalRead - lastBytes;
                double deltaTime = (elapsed - lastReport).TotalSeconds;
                double? bytesPerSecond = deltaTime > 0 ? deltaBytes / deltaTime : (double?)null;

                progress(totalRead, totalBytes, bytesPerSecond);
                lastReport = elapsed;
                lastBytes = totalRead;
            }

            if (progress is not null)
            {
                TimeSpan elapsed = stopwatch.Elapsed;
                long deltaBytes = totalRead - lastBytes;
                double deltaTime = (elapsed - lastReport).TotalSeconds;
                double? bytesPerSecond = deltaTime > 0 ? deltaBytes / deltaTime : (double?)null;
                progress(totalRead, totalBytes, bytesPerSecond);
            }
        }

        private static string GetCurrentVersionString()
        {
            Assembly assembly = typeof(AppViewModel).Assembly;
            string? informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion))
                return informationalVersion.Trim();

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        private static string StripBuildMetadata(string version)
        {
            int plus = version.IndexOf('+');
            return plus >= 0 ? version.Substring(0, plus) : version;
        }

        private bool ShouldPauseBackupsForBattery(out string reason)
        {
            reason = L("Backups.Notification.BatteryPaused", "Backups paused on battery power.");

            if (_settingsViewModel?.PauseBackupsOnBattery != true)
                return false;

            return _powerStatusProvider.GetPowerState() == PowerState.OnBattery;
        }

        private readonly record struct BackupPolicyState(
            bool IsThrottled,
            int? BandwidthLimitMbps,
            bool IsInQuietHours,
            string QuietHoursRangeLabel)
        {
            public string Signature =>
                $"{(IsThrottled ? 1 : 0)}|{BandwidthLimitMbps ?? 0}|{(IsInQuietHours ? 1 : 0)}|{QuietHoursRangeLabel}";
        }

        private static BackupPolicyState GetBackupPolicyState(AppConfig cfg, DateTimeOffset nowLocal)
        {
            int? throttledMbps = TransferPolicy.NormalizeBandwidthLimitMbps(
                cfg.Backups.EnableBandwidthLimit,
                cfg.Backups.MaxBandwidthMbps);

            QuietHoursDecision quietDecision = QuietHoursPolicy.Evaluate(
                cfg.Backups.EnableQuietHours,
                cfg.Backups.QuietHoursStart,
                cfg.Backups.QuietHoursEnd,
                nowLocal);

            string quietRange = $"{quietDecision.StartTime:hh\\:mm}-{quietDecision.EndTime:hh\\:mm}";
            return new BackupPolicyState(
                IsThrottled: throttledMbps is > 0,
                BandwidthLimitMbps: throttledMbps,
                IsInQuietHours: quietDecision.IsInQuietHours,
                QuietHoursRangeLabel: quietRange);
        }

        private string BuildPolicyChipText(BackupPolicyState state)
        {
            var chips = new List<string>(2);
            if (state.IsThrottled)
            {
                chips.Add(
                    state.BandwidthLimitMbps is > 0
                        ? Lf("Backups.Policy.ThrottledWithLimit", "Throttled ({0} Mbps)", state.BandwidthLimitMbps.Value)
                        : L("Backups.Policy.Throttled", "Throttled"));
            }

            if (state.IsInQuietHours)
            {
                chips.Add(L("Backups.Policy.QuietHours", "Quiet hours"));
            }

            return string.Join(" · ", chips);
        }

        public string GetBackupPolicyChipText()
        {
            return BuildPolicyChipText(GetBackupPolicyState(_config, DateTimeOffset.Now));
        }

        public string GetBackupPolicyTraySummary()
        {
            BackupPolicyState state = GetBackupPolicyState(_config, DateTimeOffset.Now);
            string chipText = BuildPolicyChipText(state);
            if (string.IsNullOrWhiteSpace(chipText))
                return string.Empty;

            return Lf("Tray.Policy.Active", "Policy: {0}", chipText);
        }

        public string GetBackupPolicySignatureForTray()
        {
            return GetBackupPolicyState(_config, DateTimeOffset.Now).Signature;
        }

        private string GetBackupPolicyChipTextForConfig(AppConfig cfg)
        {
            return BuildPolicyChipText(GetBackupPolicyState(cfg, DateTimeOffset.Now));
        }

        private void LogBackupPolicyTransitionIfChanged(AppConfig cfg, string source)
        {
            BackupPolicyState state = GetBackupPolicyState(cfg, DateTimeOffset.Now);
            bool changed = false;
            lock (_backupPolicyStateGate)
            {
                if (!string.Equals(_lastBackupPolicySignature, state.Signature, StringComparison.Ordinal))
                {
                    _lastBackupPolicySignature = state.Signature;
                    changed = true;
                }
            }

            if (!changed)
                return;

            string detail = BuildPolicyChipText(state);
            if (string.IsNullOrWhiteSpace(detail))
                detail = L("Backups.Policy.None", "No active transfer policy");

            string message = $"[Policy] source={source}; state={detail}";
            Console.WriteLine(message);
            DiagnosticsLogger.Record(message);
            TrayMenuRefreshRequested?.Invoke();
        }

        private static string L(string key, string fallback) => LStatic(key, fallback);

        private string Lf(string key, string fallback, params object[] args)
        {
            string text = L(key, fallback);
            return args is { Length: > 0 }
                ? string.Format(text, args)
                : text;
        }

        private static string LStatic(string key, string fallback)
        {
            string? value = LocalizationProvider.Service?.GetString(key);
            if (string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal))
                return fallback;
            return value;
        }

        private static string ResolveSystemLanguageCode(LocalizationService localizationService)
        {
            string uiLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (localizationService.SupportedLanguages.Any(l =>
                    string.Equals(l.Code, uiLang, StringComparison.OrdinalIgnoreCase)))
            {
                return uiLang;
            }

            return "en";
        }

        private void ShowBackupSkipNotification(string message, NotificationSeverity severity)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            string title = severity switch
            {
                NotificationSeverity.Error => L("Backups.Notification.ErrorTitle", "Backup error"),
                NotificationSeverity.Warning => L("Backups.Notification.WarningTitle", "Backup paused"),
                _ => L("Backups.Notification.InfoTitle", "Backup info")
            };

            ShowBackupNotification(message, severity, title);
        }

        private void ShowBackupNotification(
            string message,
            NotificationSeverity severity,
            string title,
            string? groupKey = null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsOnBackupsPage)
                {
                    BackupsViewModel.ShowNotification(message, severity.ToString());
                }
                else
                {
                    GlobalNotificationCenter.Instance.Show(
                        message,
                        severity,
                        title,
                        groupKey: groupKey);
                }

                if (ShouldRaiseSystemNotification)
                {
                    GlobalNotificationCenter.Instance.ShowSystem(
                        message,
                        severity,
                        title,
                        groupKey: groupKey);
                }
            });
        }

        private void QueueGroupedBackupProjectNotification(
            string key,
            string projectName,
            NotificationSeverity severity,
            string title,
            Func<IReadOnlyList<string>, string> messageFactory)
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return;

            bool shouldSchedule = false;

            lock (_groupedBackupNotificationGate)
            {
                if (!_groupedBackupNotifications.TryGetValue(key, out GroupedBackupNotificationBatch? batch))
                {
                    batch = new GroupedBackupNotificationBatch
                    {
                        Key = key,
                        Severity = severity,
                        Title = title,
                        MessageFactory = messageFactory
                    };
                    _groupedBackupNotifications[key] = batch;
                    shouldSchedule = true;
                }

                if (batch.ProjectNameSet.Add(projectName))
                {
                    batch.ProjectNames.Add(projectName);
                }
            }

            if (shouldSchedule)
            {
                _ = FlushGroupedBackupProjectNotificationAsync(key);
            }
        }

        private async Task FlushGroupedBackupProjectNotificationAsync(string key)
        {
            await Task.Delay(GroupedBackupNotificationDelay).ConfigureAwait(false);

            GroupedBackupNotificationBatch? batch = null;
            lock (_groupedBackupNotificationGate)
            {
                if (_groupedBackupNotifications.TryGetValue(key, out batch))
                {
                    _groupedBackupNotifications.Remove(key);
                }
            }

            if (batch is null || batch.ProjectNames.Count == 0)
                return;

            string message = batch.MessageFactory(batch.ProjectNames);
            if (string.IsNullOrWhiteSpace(message))
                return;

            ShowBackupNotification(
                message,
                batch.Severity,
                batch.Title,
                groupKey: key);
        }

        private static string FormatGroupedProjectNames(IReadOnlyList<string> names, int visibleLimit = 3)
        {
            if (names.Count == 0)
                return string.Empty;

            if (names.Count <= visibleLimit)
                return string.Join(", ", names);

            string visible = string.Join(", ", names.Take(visibleLimit));
            return $"{visible} +{names.Count - visibleLimit} more";
        }

        private void MaybeNotifyRestoreRecommended(Project project)
        {
            if (project == null)
                return;

            if (!_restoreAdvisoryShown.TryAdd(project.Id, 0))
                return;

            string title = L("Backups.Notification.RestoreRecommendedTitle", "Restore recommended");
            string singleMessage = Lf(
                "Backups.Notification.RestoreRequiredForProject",
                "Imported history is newer for '{0}'. Consider restoring before creating new backups.",
                project.Name);

            QueueGroupedBackupProjectNotification(
                "backup-restore-recommended",
                project.Name,
                NotificationSeverity.Warning,
                title,
                names => names.Count == 1
                    ? singleMessage
                    : Lf(
                        "Backups.Notification.RestoreRequiredMultiple",
                        "Imported history is newer for {0} projects: {1}. Review restore recommendations before creating new backups.",
                        names.Count,
                        FormatGroupedProjectNames(names)));
        }

        private bool TryResolveProjectRoot(Project project, AppConfig cfg, out Project resolvedProject, out string errorMessage)
        {
            resolvedProject = project;
            errorMessage = string.Empty;

            if (project is null)
            {
                errorMessage = L("Backups.Notification.ProjectRootMissing", "Project is not available on this machine. Update the project path or restore it.");
                return false;
            }

            string? projectsRoot = cfg.ProjectsRoot;
            if (ProjectRootResolver.TryResolveExistingProjectRoot(
                    projectsRoot,
                    project.Name,
                    project.RootPath,
                    out string resolvedRoot))
            {
                if (!string.Equals(
                        NormalizePathForComparison(project.RootPath),
                        NormalizePathForComparison(resolvedRoot),
                        StringComparison.OrdinalIgnoreCase))
                {
                    TryUpdateProjectRootPath(project, resolvedRoot);
                }

                resolvedProject = project with
                {
                    RootPath = resolvedRoot
                };
                return true;
            }

            string expected = string.IsNullOrWhiteSpace(project.RootPath)
                ? (projectsRoot ?? string.Empty)
                : project.RootPath;
            errorMessage = Lf(
                "Backups.Notification.ProjectRootMissing",
                "Project '{0}' isn't available on this machine. Expected at '{1}'. Update the project path or restore it.",
                project.Name,
                expected);
            return false;
        }

        private void MaybeNotifyProjectRootMissing(Project project, string message)
        {
            if (project == null || string.IsNullOrWhiteSpace(message))
                return;

            if (!_projectRootMissingNotified.TryAdd(project.Id, 0))
                return;

            string title = L("Backups.Notification.ProjectUnavailableTitle", "Projects unavailable");

            QueueGroupedBackupProjectNotification(
                "backup-project-root-missing",
                project.Name,
                NotificationSeverity.Error,
                title,
                names => names.Count == 1
                    ? message
                    : Lf(
                        "Backups.Notification.ProjectRootMissingMultiple",
                        "{0} projects aren't available on this machine: {1}. Update their paths or restore them.",
                        names.Count,
                        FormatGroupedProjectNames(names)));
        }
    }
}
