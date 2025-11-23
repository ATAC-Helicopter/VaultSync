using System;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI.Notifications
{
    /// <summary>
    /// Immutable description of a toast notification request.
    /// </summary>
    public sealed class NotificationRequest
    {
        public string Message { get; }
        public string? Title { get; }
        public NotificationSeverity Severity { get; }
        public TimeSpan Duration { get; }

        public NotificationRequest(
            string message,
            NotificationSeverity severity,
            string? title,
            TimeSpan duration)
        {
            Message  = message ?? throw new ArgumentNullException(nameof(message));
            Severity = severity;
            Title    = title;
            Duration = duration;
        }
    }

    /// <summary>
    /// Global notification center used to show floating toasts anywhere in the app.
    /// VMs call GlobalNotificationCenter.Instance.Show(...), the toast host listens.
    /// </summary>
    public sealed class GlobalNotificationCenter
    {
        public static GlobalNotificationCenter Instance { get; } = new();

        private GlobalNotificationCenter() { }

        public event Action<NotificationRequest>? NotificationRequested;

        public void Show(
            string message,
            NotificationSeverity severity = NotificationSeverity.Info,
            string? title = null,
            TimeSpan? duration = null)
        {
            var request = new NotificationRequest(
                message,
                severity,
                title,
                duration ?? TimeSpan.FromSeconds(4));

            NotificationRequested?.Invoke(request);
        }
    }
}