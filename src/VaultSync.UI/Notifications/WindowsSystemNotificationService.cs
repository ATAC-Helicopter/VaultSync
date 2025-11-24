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

                // NOTE: "VaultSync" here is your AUMID / app ID for toast routing.
                // For unpackaged apps you may need to register a compatible AUMID in the app.manifest.
                var notifier = ToastNotificationManager.CreateToastNotifier("VaultSync");
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
