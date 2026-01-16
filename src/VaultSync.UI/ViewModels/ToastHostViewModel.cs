using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using VaultSync.UI.Notifications;

namespace VaultSync.UI.ViewModels.Notifications
{
    /// <summary>
    /// ViewModel for the global toast host - owns a stack of NotificationState items.
    /// </summary>
    public class ToastHostViewModel : ViewModelBase
    {
        public ObservableCollection<NotificationState> Toasts { get; } = new();

        public ToastHostViewModel()
        {
            GlobalNotificationCenter.Instance.NotificationRequested += OnNotificationRequested;
        }

        private void OnNotificationRequested(NotificationRequest request)
        {
            void Apply()
            {
                var toast = new NotificationState();
                toast.Show(
                    request.Message,
                    request.Severity,
                    request.Title,
                    request.Duration,
                    request.ActionLabel,
                    request.ActionCommand);

                // We keep it simple for now: when the toast auto-clears, it just becomes invisible.
                // If we later want to prune the collection, we can extend NotificationState to raise
                // an event on Clear() and remove it from Toasts here.
                Toasts.Add(toast);
            }

            if (!Dispatcher.UIThread.CheckAccess())
                Dispatcher.UIThread.Post(Apply);
            else
                Apply();
        }
    }
}
