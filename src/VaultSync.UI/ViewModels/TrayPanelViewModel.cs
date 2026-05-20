using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class TrayPanelViewModel : ViewModelBase
{
    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private static readonly IBrush ReadyBrush =
        new ImmutableSolidColorBrush(Color.Parse("#22CC88"));
    private static readonly IBrush UnreachableBrush =
        new ImmutableSolidColorBrush(Color.Parse("#FF6B6B"));

    public ObservableCollection<TrayDestinationItem> Destinations { get; } = [];
    public ObservableCollection<TrayRecentBackupItem> RecentBackups { get; } = [];

    public string HeaderTitle { get; }
    public string HeaderSubtitle { get; }

    private string _destinationsSummary = string.Empty;
    public string DestinationsSummary
    {
        get => _destinationsSummary;
        private set => SetField(ref _destinationsSummary, value);
    }

    public bool HasDestinations => Destinations.Count > 0;
    public bool HasRecentBackups => RecentBackups.Count > 0;

    public ICommand OpenAppCommand { get; }
    public ICommand BackupAllCommand { get; }
    public ICommand SnapshotAllCommand { get; }
    public ICommand OpenBackupsCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand QuitCommand { get; }
    public ICommand CloseCommand { get; }

    public TrayPanelViewModel(
        string headerTitle,
        string headerSubtitle,
        Action openApp,
        Action backupAll,
        Action snapshotAll,
        Action openBackups,
        Action openSettings,
        Action quit,
        Action close)
    {
        HeaderTitle = headerTitle;
        HeaderSubtitle = headerSubtitle;

        OpenAppCommand = new RelayCommand(_ => openApp?.Invoke());
        BackupAllCommand = new RelayCommand(_ => backupAll?.Invoke());
        SnapshotAllCommand = new RelayCommand(_ => snapshotAll?.Invoke());
        OpenBackupsCommand = new RelayCommand(_ => openBackups?.Invoke());
        OpenSettingsCommand = new RelayCommand(_ => openSettings?.Invoke());
        QuitCommand = new RelayCommand(_ => quit?.Invoke());
        CloseCommand = new RelayCommand(_ => close?.Invoke());

        Destinations.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDestinations));
        RecentBackups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentBackups));
    }

    public void LoadDestinations(IEnumerable<TrayDestinationItem> items, string summary)
    {
        var list = items.ToList();
        if (TryUpdateDestinations(list))
        {
            DestinationsSummary = summary;
            return;
        }

        Destinations.Clear();
        foreach (TrayDestinationItem? item in list)
            Destinations.Add(item);

        DestinationsSummary = summary;
        OnPropertyChanged(nameof(HasDestinations));
    }

    public void LoadRecentBackups(IEnumerable<TrayRecentBackupItem> items)
    {
        var list = items.ToList();
        if (TryUpdateRecentBackups(list))
            return;

        RecentBackups.Clear();
        foreach (TrayRecentBackupItem? item in list)
            RecentBackups.Add(item);

        OnPropertyChanged(nameof(HasRecentBackups));
    }

    private bool TryUpdateDestinations(IList<TrayDestinationItem> incoming)
    {
        if (Destinations.Count != incoming.Count)
            return false;

        for (int i = 0; i < Destinations.Count; i++)
        {
            TrayDestinationItem existing = Destinations[i];
            TrayDestinationItem next = incoming[i];
            if (!AreSameDestinationKey(existing, next))
                return false;
            existing.UpdateFrom(next);
        }

        return true;
    }

    private bool TryUpdateRecentBackups(IList<TrayRecentBackupItem> incoming)
    {
        if (RecentBackups.Count != incoming.Count)
            return false;

        for (int i = 0; i < RecentBackups.Count; i++)
        {
            TrayRecentBackupItem existing = RecentBackups[i];
            TrayRecentBackupItem next = incoming[i];
            if (!AreSameRecentBackupKey(existing, next))
                return false;
            existing.UpdateFrom(next);
        }

        return true;
    }

    private static bool AreSameDestinationKey(TrayDestinationItem left, TrayDestinationItem right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        string.Equals(left.Path, right.Path, StringComparison.Ordinal);

    private static bool AreSameRecentBackupKey(TrayRecentBackupItem left, TrayRecentBackupItem right) =>
        string.Equals(left.ProjectName, right.ProjectName, StringComparison.Ordinal) &&
        string.Equals(left.Label, right.Label, StringComparison.Ordinal);

    public sealed class TrayDestinationItem : ViewModelBase
    {
        public string Name { get; }
        public string Path { get; }
        public static string StoredBytesText => string.Empty;
        public static bool HasStoredBytesText => false;
        public static string CleanupSuggestionText => string.Empty;
        public static bool HasCleanupSuggestionText => false;

        private bool _reachable;
        public bool Reachable
        {
            get => _reachable;
            set
            {
                if (SetField(ref _reachable, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusBrush));
                }
            }
        }

        public TrayDestinationItem(string name, string path, bool reachable)
        {
            Name = name;
            Path = path;
            _reachable = reachable;
        }

        public string Alias => Name;

        public string Status => StatusText;

        public static bool IsChecking => false;

        public IBrush DotBrush => StatusBrush;

        public IBrush ReachabilityBrush => StatusBrush;

        public string StatusText => Reachable
            ? L("Tray.Destinations.Ready", "Ready")
            : L("Tray.Destinations.Unreachable", "Unreachable");

        public IBrush StatusBrush => Reachable
            ? ReadyBrush
            : UnreachableBrush;

        public void UpdateFrom(TrayDestinationItem other)
        {
            Reachable = other.Reachable;
        }
    }

    public sealed class TrayRecentBackupItem : ViewModelBase
    {
        private readonly Action _openFolder;
        private readonly Action _viewInApp;
        private readonly Action _toggleKeep;
        private readonly Action _delete;

        public string ProjectName { get; }
        public string Label { get; }

        private bool _isProtected;
        public bool IsProtected
        {
            get => _isProtected;
            set
            {
                if (SetField(ref _isProtected, value))
                {
                    OnPropertyChanged(nameof(KeepLabel));
                }
            }
        }

        public string KeepLabel => IsProtected
            ? L("Tray.Recent.Unkeep", "Unkeep")
            : L("Tray.Recent.Keep", "Keep");

        public ICommand OpenFolderCommand { get; }
        public ICommand ViewInAppCommand { get; }
        public ICommand ToggleKeepCommand { get; }
        public ICommand DeleteCommand { get; }

        public TrayRecentBackupItem(
            string projectName,
            string label,
            bool isProtected,
            Action openFolder,
            Action viewInApp,
            Action toggleKeep,
            Action delete)
        {
            ProjectName = projectName;
            Label = label;
            _isProtected = isProtected;
            _openFolder = openFolder;
            _viewInApp = viewInApp;
            _toggleKeep = toggleKeep;
            _delete = delete;

            OpenFolderCommand = new RelayCommand(_ => _openFolder?.Invoke());
            ViewInAppCommand = new RelayCommand(_ => _viewInApp?.Invoke());
            ToggleKeepCommand = new RelayCommand(_ =>
            {
                _toggleKeep?.Invoke();
                IsProtected = !IsProtected;
            });
            DeleteCommand = new RelayCommand(_ => _delete?.Invoke());
        }

        public void UpdateFrom(TrayRecentBackupItem other)
        {
            IsProtected = other.IsProtected;
        }
    }
}
