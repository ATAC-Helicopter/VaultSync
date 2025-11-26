using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.ViewModels
{
    public sealed class BackupWidgetViewModel : ViewModelBase
    {
        public ObservableCollection<BackupProgressItem> ActiveBackups { get; }

        public ICommand CloseCommand { get; }
        public ICommand OpenAppCommand { get; }

        public string StatusText =>
            ActiveBackups.Count switch
            {
                0 => "No active backups",
                1 => "1 active backup",
                _ => $"{ActiveBackups.Count} active backups"
            };

        public bool HasActiveBackups => ActiveBackups.Any();

        public BackupWidgetViewModel(
            BackupsViewModel backupsViewModel,
            Action openMainWindow,
            Action hideWidget)
        {
            ActiveBackups = backupsViewModel?.ActiveBackups
                            ?? throw new ArgumentNullException(nameof(backupsViewModel));

            if (openMainWindow is null)
                throw new ArgumentNullException(nameof(openMainWindow));
            if (hideWidget is null)
                throw new ArgumentNullException(nameof(hideWidget));

            CloseCommand   = new RelayCommand(_ => hideWidget());
            OpenAppCommand = new RelayCommand(_ => openMainWindow());

            ActiveBackups.CollectionChanged += OnActiveBackupsChanged;
        }

        private void OnActiveBackupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(HasActiveBackups));
        }
    }
}
