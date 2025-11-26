using System;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VaultSync.UI.ViewModels;
using VaultSync.UI.Views;

namespace VaultSync.UI.Services
{
    public interface IBackupWidgetService
    {
        void ShowForTrayBackup();
        void Hide();
    }

    /// <summary>
    /// Manages the tiny always-on-top backup widget window shown when backups are started from the tray.
    /// </summary>
    public sealed class BackupWidgetService : IBackupWidgetService, IDisposable
    {
        private readonly IClassicDesktopStyleApplicationLifetime _desktop;
        private readonly BackupsViewModel _backupsViewModel;
        private readonly Action _bringMainWindowToFront;

        private BackupWidgetWindow? _window;
        private bool _disposed;
        private bool _suppressHideUntilActivity;

        public BackupWidgetService(
            IClassicDesktopStyleApplicationLifetime desktop,
            BackupsViewModel backupsViewModel,
            Action bringMainWindowToFront)
        {
            _desktop               = desktop ?? throw new ArgumentNullException(nameof(desktop));
            _backupsViewModel      = backupsViewModel ?? throw new ArgumentNullException(nameof(backupsViewModel));
            _bringMainWindowToFront = bringMainWindowToFront ?? throw new ArgumentNullException(nameof(bringMainWindowToFront));

            _backupsViewModel.ActiveBackups.CollectionChanged += OnActiveBackupsChanged;
            _backupsViewModel.PropertyChanged += OnBackupsPropertyChanged;
        }

        public void ShowForTrayBackup()
        {
            if (_disposed)
                return;

            Dispatcher.UIThread.Post(() =>
            {
                EnsureWindow();
                if (_window is null)
                    return;

                _suppressHideUntilActivity = true;

                _window.Show();
                _window.Topmost = true;
                _window.Activate();
            });
        }

        public void Hide()
        {
            if (_disposed)
                return;

            Dispatcher.UIThread.Post(() => _window?.Hide());
        }

        private void EnsureWindow()
        {
            if (_window != null)
                return;

            _window = new BackupWidgetWindow
            {
                DataContext   = new BackupWidgetViewModel(_backupsViewModel, _bringMainWindowToFront, Hide),
                ShowInTaskbar = false,
                Topmost       = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };

            // If the user closes the widget, simply drop the instance so it can be recreated later.
            _window.Closed += (_, _) => _window = null;
        }

        private void OnActiveBackupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_disposed)
                return;

            if (_backupsViewModel.ActiveBackups.Count > 0)
            {
                _suppressHideUntilActivity = false;
                return;
            }

            // Avoid auto-hiding during the brief "cleared then repopulated" window at backup start.
            if (_suppressHideUntilActivity)
                return;

            if (!_backupsViewModel.IsBusy)
            {
                Hide();
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _backupsViewModel.ActiveBackups.CollectionChanged -= OnActiveBackupsChanged;
            _backupsViewModel.PropertyChanged -= OnBackupsPropertyChanged;
            _window?.Close();
        }

        private void OnBackupsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_disposed)
                return;

            if (e.PropertyName == nameof(BackupsViewModel.IsBusy))
            {
                if (!_backupsViewModel.IsBusy && _backupsViewModel.ActiveBackups.Count == 0)
                {
                    Hide();
                }
            }
        }
    }
}
