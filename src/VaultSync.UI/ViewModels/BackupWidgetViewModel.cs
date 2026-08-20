using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels
{
    public sealed class BackupWidgetViewModel : ViewModelBase
    {
        public ObservableCollection<BackupProgressItem> ActiveBackups { get; }

        public ICommand CloseCommand { get; }
        public ICommand OpenAppCommand { get; }
        public ICommand OpenBackupsCommand { get; }

        public string StatusText =>
            ActiveBackups.Count switch
            {
                0 => L("BackupWidget.Status.None", "No active backups"),
                1 => L("BackupWidget.Status.One", "1 active backup"),
                _ => string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    L("BackupWidget.Status.Many", "{0} active backups"),
                    ActiveBackups.Count)
            };

        public bool HasActiveBackups => ActiveBackups.Any();

        public BackupWidgetViewModel(
            BackupsViewModel backupsViewModel,
            Action openMainWindow,
            Action openBackupsView,
            Action hideWidget)
        {
            ActiveBackups = backupsViewModel?.ActiveBackups
                            ?? throw new ArgumentNullException(nameof(backupsViewModel));

            if (openMainWindow is null)
                throw new ArgumentNullException(nameof(openMainWindow));
            if (openBackupsView is null)
                throw new ArgumentNullException(nameof(openBackupsView));
            if (hideWidget is null)
                throw new ArgumentNullException(nameof(hideWidget));

            CloseCommand   = new RelayCommand(_ => hideWidget());
            OpenAppCommand = new RelayCommand(_ =>
            {
                openMainWindow();
                hideWidget();
            });
            OpenBackupsCommand = new RelayCommand(_ =>
            {
                openBackupsView();
                hideWidget();
            });

            ActiveBackups.CollectionChanged += OnActiveBackupsChanged;
        }

        private void OnActiveBackupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertiesChanged(nameof(StatusText), nameof(HasActiveBackups));
        }

        private static string L(string key, string fallback) =>
            LocalizationProvider.Service?.GetString(key) is { } value &&
            !string.Equals(value, key, StringComparison.OrdinalIgnoreCase)
                ? value
                : fallback;
    }
}
