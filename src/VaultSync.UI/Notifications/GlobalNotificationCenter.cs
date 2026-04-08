using System;
using System.Windows.Input;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI.Notifications
{
    /// <summary>
    /// Abstraction for OS-level/system notifications (macOS, Windows, etc.).
    /// Implementations are responsible for using platform APIs.
    /// </summary>
    public interface ISystemNotificationService
    {
        void ShowSystemNotification(NotificationRequest request);
    }

    /// <summary>
    /// Immutable description of a toast notification request.
    /// </summary>
    public sealed class NotificationRequest
    {
        public string Message { get; }
        public string? Title { get; }
        public NotificationSeverity Severity { get; }
        public TimeSpan Duration { get; }
        public string? ActionLabel { get; }
        public ICommand? ActionCommand { get; }
        public string? GroupKey { get; }

        public NotificationRequest(
            string message,
            NotificationSeverity severity,
            string? title,
            TimeSpan duration,
            string? actionLabel,
            ICommand? actionCommand,
            string? groupKey = null)
        {
            Message  = message ?? throw new ArgumentNullException(nameof(message));
            Severity = severity;
            Title    = title;
            Duration = duration;
            ActionLabel = actionLabel;
            ActionCommand = actionCommand;
            GroupKey = groupKey;
        }
    }

    /// <summary>
    /// Global notification center used to show floating toasts anywhere in the app.
    /// VMs call GlobalNotificationCenter.Instance.Show(...), the toast host listens.
    /// </summary>
    public sealed class GlobalNotificationCenter
    {
        public static GlobalNotificationCenter Instance { get; } = new();
        public bool SuppressNotifications { get; set; }

        /// <summary>
        /// Optional filter to decide whether a system notification should be shown.
        /// Returning false will drop the OS-level notification (in-app toasts still fire).
        /// </summary>
        public Func<NotificationRequest, bool>? ShouldShowSystemNotification { get; set; }

        /// <summary>
        /// Optional system notification service that can be wired at startup
        /// (for macOS/Windows native notifications). If null, only in-app toasts
        /// will be used.
        /// </summary>
        public ISystemNotificationService? SystemNotificationService { get; set; }

        private GlobalNotificationCenter() { }

        public event Action<NotificationRequest>? NotificationRequested;

        public void Show(
            string message,
            NotificationSeverity severity = NotificationSeverity.Info,
            string? title = null,
            TimeSpan? duration = null,
            string? actionLabel = null,
            ICommand? actionCommand = null,
            string? groupKey = null)
        {
            if (SuppressNotifications)
                return;

            var request = new NotificationRequest(
                message,
                severity,
                title,
                duration ?? ComputeSmartDuration(message, severity, actionLabel, actionCommand),
                actionLabel,
                actionCommand,
                groupKey);

            NotificationRequested?.Invoke(request);
        }

        /// <summary>
        /// Shows a system-level (OS) notification if a SystemNotificationService
        /// has been configured. This does not affect the in-app toast host.
        /// </summary>
        public void ShowSystem(
            string message,
            NotificationSeverity severity = NotificationSeverity.Info,
            string? title = null,
            TimeSpan? duration = null,
            string? groupKey = null)
        {
            if (SuppressNotifications)
                return;

            var request = new NotificationRequest(
                message,
                severity,
                title,
                duration ?? ComputeSmartDuration(message, severity, actionLabel: null, actionCommand: null),
                actionLabel: null,
                actionCommand: null,
                groupKey: groupKey);

            if (ShouldShowSystemNotification is not null && !ShouldShowSystemNotification(request))
                return;

            SystemNotificationService?.ShowSystemNotification(request);
        }

        private static TimeSpan ComputeSmartDuration(
            string message,
            NotificationSeverity severity,
            string? actionLabel,
            ICommand? actionCommand)
        {
            var seconds = severity switch
            {
                NotificationSeverity.Error => 9,
                NotificationSeverity.Warning => 7,
                _ => 5
            };

            if (!string.IsNullOrWhiteSpace(actionLabel) && actionCommand is not null)
                seconds += 2;

            var length = message?.Length ?? 0;
            if (length > 180)
                seconds += 2;
            else if (length > 90)
                seconds += 1;

            return TimeSpan.FromSeconds(Math.Clamp(seconds, 4, 12));
        }
    }
}
