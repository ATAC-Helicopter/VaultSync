using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using VaultSync.UI.Notifications;
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
        private string _groupKey = string.Empty;
        private int _repeatCount = 1;
        private DateTimeOffset _createdUtc = DateTimeOffset.UtcNow;
        private DateTimeOffset _updatedUtc = DateTimeOffset.UtcNow;

        public event Action<NotificationState>? Closed;

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

        public string GroupKey
        {
            get => _groupKey;
            private set => SetField(ref _groupKey, value);
        }

        public int RepeatCount
        {
            get => _repeatCount;
            private set
            {
                if (SetField(ref _repeatCount, value))
                {
                    OnPropertyChanged(nameof(HasRepeatCount));
                    OnPropertyChanged(nameof(RepeatCountLabel));
                }
            }
        }

        public bool HasRepeatCount => RepeatCount > 1;
        public string RepeatCountLabel => $"x{RepeatCount}";

        public DateTimeOffset CreatedUtc
        {
            get => _createdUtc;
            private set => SetField(ref _createdUtc, value);
        }

        public DateTimeOffset UpdatedUtc
        {
            get => _updatedUtc;
            private set => SetField(ref _updatedUtc, value);
        }

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
            ICommand? actionCommand = null,
            string? groupKey = null,
            bool incrementRepeat = false)
        {
            RunOnUiThread(() =>
            {
                Message = message;
                Title = title ?? string.Empty;
                Severity = severity;
                ActionLabel = actionLabel ?? string.Empty;
                ActionCommand = actionCommand;
                GroupKey = groupKey ?? string.Empty;
                if (!incrementRepeat || !IsVisible)
                {
                    RepeatCount = 1;
                    CreatedUtc = DateTimeOffset.UtcNow;
                }
                else
                {
                    RepeatCount += 1;
                }

                UpdatedUtc = DateTimeOffset.UtcNow;
                IsVisible = true;
                StartAutoDismiss(duration ?? TimeSpan.FromSeconds(9));
            });
        }

        /// <summary>
        /// Hide/clear this notification.
        /// </summary>
        public void Clear()
        {
            RunOnUiThread(() =>
            {
                if (!IsVisible && string.IsNullOrWhiteSpace(Message))
                    return;

                var previous = Interlocked.Exchange(ref _cts, null);
                CancelAndDispose(previous);

                Message = string.Empty;
                Title = string.Empty;
                ActionLabel = string.Empty;
                ActionCommand = null;
                GroupKey = string.Empty;
                RepeatCount = 1;
                IsVisible = false;
                Closed?.Invoke(this);
            });
        }

        private void StartAutoDismiss(TimeSpan duration)
        {
            _ = StartAutoDismissAsync(duration);
        }

        private async Task StartAutoDismissAsync(TimeSpan duration)
        {
            var previous = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
            CancelAndDispose(previous);
            var local = _cts!;

            try
            {
                await Task.Delay(duration, local.Token).ConfigureAwait(false);
                if (local.IsCancellationRequested)
                    return;

                RunOnUiThread(Clear);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == local.Token || local.IsCancellationRequested)
            {
                // ignored: superseded by newer notification
            }
            finally
            {
                // Dispose only if still current; avoid disposing a newer token source.
                if (Interlocked.CompareExchange(ref _cts, null, local) == local)
                {
                    local.Dispose();
                }
            }
        }

        private static void CancelAndDispose(CancellationTokenSource? cts)
        {
            if (cts is null)
                return;

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Another UI/background path already retired this auto-dismiss token.
            }
            finally
            {
                cts.Dispose();
            }
        }

        public bool Matches(NotificationRequest request)
        {
            string requestKey = request.GroupKey ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(requestKey) && string.Equals(GroupKey, requestKey, StringComparison.Ordinal))
                return true;

            return string.Equals(Title, request.Title ?? string.Empty, StringComparison.Ordinal)
                   && string.Equals(Message, request.Message, StringComparison.Ordinal)
                   && Severity == request.Severity
                   && string.Equals(ActionLabel, request.ActionLabel ?? string.Empty, StringComparison.Ordinal);
        }

        private static void RunOnUiThread(Action action)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                Dispatcher.UIThread.Post(action);
            }
        }
    }
}
