using System;

#if WINDOWS
using CommunityToolkit.WinUI.Notifications;
using Windows.UI.Notifications;
#endif

namespace VaultSync.UI.Notifications
{
    public sealed class WindowsSystemNotificationService : ISystemNotificationService
    {
        private const string WindowsToastGroup = "VaultSync";

        public void ShowSystemNotification(NotificationRequest request)
        {
            var title = string.IsNullOrWhiteSpace(request.Title)
                ? "VaultSync"
                : request.Title;

            var message = string.IsNullOrWhiteSpace(request.Message)
                ? string.Empty
                : request.Message;

#if WINDOWS
            try
            {
                // Build the toast content using CommunityToolkit
                var builder = new ToastContentBuilder()
                    .AddText(title);

                if (!string.IsNullOrWhiteSpace(message))
                {
                    builder.AddText(message);
                }

                var toastContent = builder.GetToastContent();
                var toast        = new ToastNotification(toastContent.GetXml());
                toast.Group = WindowsToastGroup;

                if (!string.IsNullOrWhiteSpace(request.GroupKey))
                {
                    toast.Tag = SanitizeTag(request.GroupKey);
                }

                // Use the compat notifier so unpackaged Win32 builds can still raise toasts.
                var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
                notifier.Show(toast);
            }
            catch (Exception ex)
            {
            }
#else
            // Non-Windows targets fall back to logging only
#endif
        }

#if WINDOWS
        private static string SanitizeTag(string groupKey)
        {
            Span<char> buffer = stackalloc char[Math.Min(groupKey.Length, 60)];
            var length = 0;

            foreach (var ch in groupKey)
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
                ? WindowsToastGroup
                : new string(buffer[..length]).Trim('-');
        }
#endif
    }
}
