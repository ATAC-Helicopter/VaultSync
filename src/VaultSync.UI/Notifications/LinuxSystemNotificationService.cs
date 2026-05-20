using System;
using System.Diagnostics;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI.Notifications
{
    public sealed class LinuxSystemNotificationService : ISystemNotificationService
    {
        public void ShowSystemNotification(NotificationRequest request)
        {
            try
            {
                string? notifySend = FindExecutablePath("notify-send");
                if (string.IsNullOrWhiteSpace(notifySend))
                {
                    DiagnosticsLogger.Record("Linux notification skipped: notify-send not found.");
                    return;
                }

                string title = string.IsNullOrWhiteSpace(request.Title)
                    ? "VaultSync"
                    : request.Title;

                string message = string.IsNullOrWhiteSpace(request.Message)
                    ? string.Empty
                    : request.Message;

                var psi = new ProcessStartInfo
                {
                    FileName = notifySend,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };

                psi.ArgumentList.Add("--app-name=VaultSync");
                psi.ArgumentList.Add("--urgency");
                psi.ArgumentList.Add(GetUrgency(request.Severity));
                psi.ArgumentList.Add("--expire-time");
                psi.ArgumentList.Add(Math.Max(1000, (int)request.Duration.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
                psi.ArgumentList.Add(title);
                psi.ArgumentList.Add(message);

                using var process = Process.Start(psi);
                if (process is null)
                {
                    DiagnosticsLogger.Record("Linux notification failed: notify-send did not start.");
                }
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.RecordException("Linux notification failed", ex, includeStack: false);
            }
        }

        private static string GetUrgency(NotificationSeverity severity) =>
            severity switch
            {
                NotificationSeverity.Error => "critical",
                NotificationSeverity.Warning => "normal",
                _ => "low"
            };

        private static string? FindExecutablePath(string executableName)
        {
            try
            {
                string[] candidates =
                [
                    $"/usr/bin/{executableName}",
                    $"/usr/local/bin/{executableName}",
                    $"/bin/{executableName}"
                ];

                foreach (string candidate in candidates)
                {
                    if (System.IO.File.Exists(candidate))
                        return candidate;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/env",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("which");
                psi.ArgumentList.Add(executableName);

                using var process = Process.Start(psi);
                if (process is null)
                    return null;

                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(1000);
                return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)
                    ? output
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
