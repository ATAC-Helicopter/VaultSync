using System;
using System.Diagnostics;

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
                var title = string.IsNullOrWhiteSpace(request.Title)
                    ? "VaultSync"
                    : request.Title;

                var message = request.Message ?? string.Empty;

                var escapedTitle   = EscapeAppleScriptString(title);
                var escapedMessage = EscapeAppleScriptString(message);

                var script =
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
