using System;
using System.Diagnostics;
using System.IO;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.Notifications
{
    public sealed class MacSystemNotificationService : ISystemNotificationService
    {
        /// <summary>
        /// Shows a macOS Notification Center banner using `osascript`.
        /// </summary>
        /// <param name="request">Notification payload (title, message, severity, etc.).</param>
        public void ShowSystemNotification(NotificationRequest request)
        {
            try
            {
                string title = string.IsNullOrWhiteSpace(request.Title)
                    ? "VaultSync"
                    : request.Title;

                string message = request.Message ?? string.Empty;

                string escapedTitle   = EscapeAppleScriptString(title);
                string escapedMessage = EscapeAppleScriptString(message);
                string? iconPath = ResolveNotificationIconPath();

                if (TryShowWithTerminalNotifier(request, title, message, iconPath))
                    return;

                string script =
                    $"display notification \"{escapedMessage}\" with title \"{escapedTitle}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = "osascript",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };

                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);

                using var _ = Process.Start(psi);
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.RecordException("macOS notification failed", ex, includeStack: false);
            }
        }

        private static bool TryShowWithTerminalNotifier(NotificationRequest request, string title, string message, string? iconPath)
        {
            try
            {
                string? notifierPath = FindExecutablePath("terminal-notifier");
                if (string.IsNullOrWhiteSpace(notifierPath))
                    return false;

                var psi = new ProcessStartInfo
                {
                    FileName = notifierPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                };

                psi.ArgumentList.Add("-title");
                psi.ArgumentList.Add(title);
                psi.ArgumentList.Add("-message");
                psi.ArgumentList.Add(message);
                psi.ArgumentList.Add("-group");
                psi.ArgumentList.Add(string.IsNullOrWhiteSpace(request.GroupKey)
                    ? "VaultSync"
                    : $"VaultSync.{request.GroupKey}");

                if (!string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath))
                {
                    psi.ArgumentList.Add("-appIcon");
                    psi.ArgumentList.Add(iconPath);
                }

                using var process = Process.Start(psi);
                return process is not null;
            }
            catch
            {
                return false;
            }
        }

        private static string? FindExecutablePath(string executableName)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/which",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
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

        private static string? ResolveNotificationIconPath()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string candidate = Path.Combine(baseDir, "Assets", "vaultsync-tray.png");
                if (File.Exists(candidate))
                    return candidate;

                string flatCandidate = Path.Combine(baseDir, "vaultsync-tray.png");
                return File.Exists(flatCandidate) ? flatCandidate : null;
            }
            catch
            {
                return null;
            }
        }

        private static string EscapeAppleScriptString(string value)
        {
            // Basic escaping so quotes/backslashes don't break the AppleScript.
            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"");
        }
    }
}
