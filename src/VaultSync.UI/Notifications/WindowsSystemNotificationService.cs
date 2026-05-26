using System;
using System.Threading;
using VaultSync.UI.Infrastructure;

#if WINDOWS
using CommunityToolkit.WinUI.Notifications;
using Windows.UI.Notifications;
#endif

namespace VaultSync.UI.Notifications
{
    public sealed class WindowsSystemNotificationService : ISystemNotificationService
    {
#if WINDOWS
        private static int s_failureCount;
#endif

        public void ShowSystemNotification(NotificationRequest request)
        {
            string title = string.IsNullOrWhiteSpace(request.Title)
                ? "VaultSync"
                : request.Title;

            string message = string.IsNullOrWhiteSpace(request.Message)
                ? string.Empty
                : request.Message;

#if WINDOWS
            try
            {
                // Build the toast content using CommunityToolkit
                ToastContentBuilder builder = new ToastContentBuilder()
                    .AddText(title);

                if (!string.IsNullOrWhiteSpace(message))
                {
                    builder.AddText(message);
                }

                ToastContent toastContent = builder.GetToastContent();
                var toast = new ToastNotification(toastContent.GetXml())
                {
                    Group = "VaultSync"
                };

                if (!string.IsNullOrWhiteSpace(request.GroupKey))
                {
                    toast.Tag = SanitizeTag(request.GroupKey);
                }

                // Use the compat notifier so unpackaged Win32 builds can still raise toasts.
                ToastNotifierCompat notifier = ToastNotificationManagerCompat.CreateToastNotifier();
                notifier.Show(toast);
            }
            catch (Exception ex)
            {
                int failureCount = Interlocked.Increment(ref s_failureCount);
                if (failureCount == 1)
                {
                    DiagnosticsLogger.RecordException("Windows notification failed", ex, includeStack: false);
                    return;
                }

                if (failureCount == 2)
                {
                    DiagnosticsLogger.Record("Windows notification failures suppressed for this session after the first failure.");
                }
            }
#else
            // Non-Windows targets fall back to logging only
#endif
        }

#if WINDOWS
        private static string SanitizeTag(string groupKey)
        {
            Span<char> buffer = stackalloc char[Math.Min(groupKey.Length, 60)];
            int length = 0;

            foreach (char ch in groupKey)
            {
                if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
                {
                    buffer[length++] = ch;
                }
                else if (length == 0 || buffer[length - 1] != '-')
                {
                    buffer[length++] = '-';
                }

                if (length >= buffer.Length)
                    break;
            }

            return length == 0
                ? "VaultSync"
                : new string(buffer[..length]).Trim('-');
        }
#endif
    }
}
