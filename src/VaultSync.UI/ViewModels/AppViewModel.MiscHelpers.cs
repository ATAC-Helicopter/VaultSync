using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Notifications;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI.ViewModels
{
    public partial class AppViewModel
    {
        private string BuildBackupProgressLabel(string? etaText, string? currentFile, double percent)
        {
            var isFinalizing = !string.IsNullOrWhiteSpace(etaText) &&
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
            var totalMb = totalBytes.HasValue && totalBytes.Value > 0
                ? totalBytes.Value / (1024d * 1024d)
                : (double?)null;
            var downloadedMb = downloadedBytes / (1024d * 1024d);
            var rateMb = bytesPerSecond.HasValue && bytesPerSecond.Value > 0
                ? bytesPerSecond.Value / (1024d * 1024d)
                : (double?)null;

            var sizeText = totalMb.HasValue
                ? $"{downloadedMb:0.0}/{totalMb.Value:0.0} MB"
                : $"{downloadedMb:0.0} MB";

            var rateText = rateMb.HasValue
                ? $"{rateMb.Value:0.0} MB/s"
                : L("Update.Download.Waiting", "Waiting for network...");

            var status = $"{prefix} ({sizeText}) - {rateText}";

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
            var buffer = new byte[1024 * 128];
            long totalRead = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            long lastBytes = 0;

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;

                if (progress is null)
                    continue;

                var elapsed = stopwatch.Elapsed;
                if (elapsed - lastReport < TimeSpan.FromMilliseconds(250))
                    continue;

                var deltaBytes = totalRead - lastBytes;
                var deltaTime = (elapsed - lastReport).TotalSeconds;
                var bytesPerSecond = deltaTime > 0 ? deltaBytes / deltaTime : (double?)null;

                progress(totalRead, totalBytes, bytesPerSecond);
                lastReport = elapsed;
                lastBytes = totalRead;
            }

            if (progress is not null)
            {
                var elapsed = stopwatch.Elapsed;
                var deltaBytes = totalRead - lastBytes;
                var deltaTime = (elapsed - lastReport).TotalSeconds;
                var bytesPerSecond = deltaTime > 0 ? deltaBytes / deltaTime : (double?)null;
                progress(totalRead, totalBytes, bytesPerSecond);
            }
        }

        private static string GetCurrentVersionString()
        {
            var assembly = typeof(AppViewModel).Assembly;
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion))
                return informationalVersion.Trim();

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        private static string StripBuildMetadata(string version)
        {
            var plus = version.IndexOf('+');
            return plus >= 0 ? version.Substring(0, plus) : version;
        }

        private bool ShouldPauseBackupsForBattery(out string reason)
        {
            reason = L("Backups.Notification.BatteryPaused", "Backups paused on battery power.");

            if (_settingsViewModel?.PauseBackupsOnBattery != true)
                return false;

            return _powerStatusProvider.GetPowerState() == PowerState.OnBattery;
        }

        private string L(string key, string fallback) => LStatic(key, fallback);

        private string Lf(string key, string fallback, params object[] args)
        {
            var text = L(key, fallback);
            return args is { Length: > 0 }
                ? string.Format(text, args)
                : text;
        }

        private static string LStatic(string key, string fallback)
        {
            var value = LocalizationProvider.Service?.GetString(key);
            if (string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal))
                return fallback;
            return value;
        }

        private static string ResolveSystemLanguageCode(LocalizationService localizationService)
        {
            var uiLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
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

            var title = severity switch
            {
                NotificationSeverity.Error => L("Backups.Notification.ErrorTitle", "Backup error"),
                NotificationSeverity.Warning => L("Backups.Notification.WarningTitle", "Backup paused"),
                _ => L("Backups.Notification.InfoTitle", "Backup info")
            };

            if (IsOnBackupsPage)
            {
                BackupsViewModel.ShowNotification(message, severity.ToString());
            }
            else
            {
                GlobalNotificationCenter.Instance.Show(
                    message,
                    severity,
                    title);
            }

            if (ShouldRaiseSystemNotification)
            {
                GlobalNotificationCenter.Instance.ShowSystem(
                    message,
                    severity,
                    title);
            }
        }

        private void MaybeNotifyRestoreRecommended(Project project)
        {
            if (project == null)
                return;

            if (!_restoreAdvisoryShown.TryAdd(project.Id, 0))
                return;

            var message = Lf(
                "Backups.Notification.RestoreRequiredForProject",
                "Imported history is newer for '{0}'. Consider restoring before creating new backups.",
                project.Name);
            ShowBackupSkipNotification(message, NotificationSeverity.Warning);
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

            if (!string.IsNullOrWhiteSpace(project.RootPath) && Directory.Exists(project.RootPath))
                return true;

            var projectsRoot = cfg.ProjectsRoot;
            if (!string.IsNullOrWhiteSpace(projectsRoot))
            {
                var fallback = Path.Combine(projectsRoot, project.Name);
                if (Directory.Exists(fallback))
                {
                    _repo.UpdateProjectPath(project.Name, fallback, out _);
                    resolvedProject = project with
                    {
                        RootPath = fallback
                    };
                    return true;
                }
            }

            var expected = string.IsNullOrWhiteSpace(project.RootPath)
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

            ShowBackupSkipNotification(message, NotificationSeverity.Error);
        }
    }
}
