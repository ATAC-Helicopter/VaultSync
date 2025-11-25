using System;

#if WINDOWS
using CommunityToolkit.WinUI.Notifications;
using Windows.UI.Notifications;
#endif

namespace VaultSync.UI.Notifications
{
    public sealed class WindowsSystemNotificationService : ISystemNotificationService
    {
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

                // Use the compat notifier so unpackaged Win32 builds can still raise toasts.
                var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
                notifier.Show(toast);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WindowsSystemNotificationService] Windows toast failed: {ex}");
            }
#else
            // Non-Windows targets fall back to logging only
            Console.WriteLine($"[WindowsSystemNotificationService][Stub] {title}: {message}");
#endif
        }
    }
}
