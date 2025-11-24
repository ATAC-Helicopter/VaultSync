using System;

namespace VaultSync.UI.Notifications
{
    /// <summary>
    /// Temporary stub implementation of ISystemNotificationService.
    /// For now it just logs to the console when an OS-level notification
    /// would be shown. Later we can replace this with real macOS/Windows
    /// notification integrations.
    /// </summary>
    public sealed class StubSystemNotificationService : ISystemNotificationService
    {
        public void ShowSystemNotification(NotificationRequest request)
        {
            Console.WriteLine(
                $"[SystemNotification][{request.Severity}] {request.Title ?? "(no title)"}: {request.Message}");
        }
    }
}