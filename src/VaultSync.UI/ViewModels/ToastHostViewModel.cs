using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using VaultSync.UI.Notifications;

namespace VaultSync.UI.ViewModels.Notifications
{
    /// <summary>
    /// ViewModel for the global toast host - owns a stack of NotificationState items.
    /// </summary>
    public class ToastHostViewModel : ViewModelBase
    {
        private const int MaxVisibleToasts = 4;
        public ObservableCollection<NotificationState> Toasts { get; } = new();

        public ToastHostViewModel()
        {
            GlobalNotificationCenter.Instance.NotificationRequested += OnNotificationRequested;
        }

        private void OnNotificationRequested(NotificationRequest request)
        {
            void Apply()
            {
                var existing = Toasts.FirstOrDefault(toast => toast.Matches(request));
                if (existing is not null)
                {
                    existing.Show(
                        request.Message,
                        request.Severity,
                        request.Title,
                        request.Duration,
                        request.ActionLabel,
                        request.ActionCommand,
                        request.GroupKey,
                        incrementRepeat: true);

                    MoveToFront(existing);
                    return;
                }

                var toast = new NotificationState();
                toast.Closed += OnToastClosed;
                toast.Show(
                    request.Message,
                    request.Severity,
                    request.Title,
                    request.Duration,
                    request.ActionLabel,
                    request.ActionCommand,
                    request.GroupKey);

                Toasts.Add(toast);
                TrimToastStack();
            }

            if (!Dispatcher.UIThread.CheckAccess())
                Dispatcher.UIThread.Post(Apply);
            else
                Apply();
        }

        private void MoveToFront(NotificationState toast)
        {
            var index = Toasts.IndexOf(toast);
            if (index < 0 || index == Toasts.Count - 1)
                return;

            Toasts.RemoveAt(index);
            Toasts.Add(toast);
        }

        private void TrimToastStack()
        {
            while (Toasts.Count > MaxVisibleToasts)
            {
                var candidate = Toasts
                    .OrderBy(toast => toast.Severity switch
                    {
                        NotificationSeverity.Error => 2,
                        NotificationSeverity.Warning => 1,
                        _ => 0
                    })
                    .ThenBy(toast => toast.UpdatedUtc)
                    .FirstOrDefault();

                if (candidate is null)
                    break;

                candidate.Closed -= OnToastClosed;
                Toasts.Remove(candidate);
            }
        }

        private void OnToastClosed(NotificationState toast)
        {
            void Apply()
            {
                toast.Closed -= OnToastClosed;
                Toasts.Remove(toast);
            }

            if (!Dispatcher.UIThread.CheckAccess())
                Dispatcher.UIThread.Post(Apply);
            else
                Apply();
        }
    }
}
