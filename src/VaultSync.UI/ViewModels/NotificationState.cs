using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using VaultSync.UI.ViewModels;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.ViewModels.Notifications
{
    public enum NotificationSeverity
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Reusable state + behavior for a single notification card/banner.
    /// </summary>
    public class NotificationState : ViewModelBase
    {
        private string _message = string.Empty;
        private string _title   = string.Empty;
        private NotificationSeverity _severity = NotificationSeverity.Info;
        private bool _isVisible;
        private string _actionLabel = string.Empty;
        private ICommand? _actionCommand;
        private CancellationTokenSource? _cts;

        public string Message
        {
            get => _message;
            set => SetField(ref _message, value);
        }

        /// <summary>
        /// Optional short title (you can leave it empty if not needed).
        /// </summary>
        public string Title
        {
            get => _title;
            set => SetField(ref _title, value);
        }

        public NotificationSeverity Severity
        {
            get => _severity;
            set => SetField(ref _severity, value);
        }

        public bool IsVisible
        {
            get => _isVisible;
            set => SetField(ref _isVisible, value);
        }

        public string ActionLabel
        {
            get => _actionLabel;
            set
            {
                if (SetField(ref _actionLabel, value))
                {
                    OnPropertyChanged(nameof(HasAction));
                }
            }
        }

        public ICommand? ActionCommand
        {
            get => _actionCommand;
            set
            {
                if (SetField(ref _actionCommand, value))
                {
                    OnPropertyChanged(nameof(HasAction));
                }
            }
        }

        public bool HasAction => !string.IsNullOrWhiteSpace(ActionLabel) && ActionCommand is not null;

        public ICommand DismissCommand { get; }

        public NotificationState()
        {
            DismissCommand = new RelayCommand(_ => Clear());
        }

        /// <summary>
        /// Show a notification. Duration is optional (defaults to ~4s auto-hide).
        /// </summary>
        public void Show(
            string message,
            NotificationSeverity severity = NotificationSeverity.Info,
            string? title = null,
            TimeSpan? duration = null,
            string? actionLabel = null,
            ICommand? actionCommand = null)
        {
            Message  = message;
            Title    = title ?? string.Empty;
            Severity = severity;
            ActionLabel = actionLabel ?? string.Empty;
            ActionCommand = actionCommand;
            IsVisible = true;

            StartAutoDismiss(duration ?? TimeSpan.FromSeconds(4));
        }

        /// <summary>
        /// Hide/clear this notification.
        /// </summary>
        public void Clear()
        {
            Message  = string.Empty;
            Title    = string.Empty;
            ActionLabel = string.Empty;
            ActionCommand = null;
            IsVisible = false;
        }

        private async void StartAutoDismiss(TimeSpan duration)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var local = _cts;

            try
            {
                await Task.Delay(duration, local.Token);
                if (local.IsCancellationRequested)
                    return;

                Clear();
            }
            catch (TaskCanceledException)
            {
                // ignored: superseded by newer notification
            }
        }
    }
}
