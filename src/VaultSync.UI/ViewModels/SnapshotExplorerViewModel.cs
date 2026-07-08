using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class SnapshotExplorerEntryViewModel : ViewModelBase
{
    private bool _isExpanded;

    public SnapshotExplorerEntryViewModel(SnapshotExplorerEntry entry, int depth = 0)
    {
        Entry = entry;
        Depth = depth;
    }

    public SnapshotExplorerEntry Entry { get; }
    public int Depth { get; }
    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public bool IsFolder => Entry.Kind == SnapshotExplorerEntryKind.Folder;
    public bool CanPreview => Entry.CanPreview;
    public double Indent => Depth * 18d;
    public string ToggleGlyph => IsFolder ? (IsExpanded ? "▾" : "▸") : " ";
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetField(ref _isExpanded, value))
                OnPropertyChanged(nameof(ToggleGlyph));
        }
    }

    public string KindLabel => IsFolder
        ? L("SnapshotExplorer.Kind.Folder", "Folder")
        : L("SnapshotExplorer.Kind.File", "File");
    public string SizeLabel => IsFolder ? string.Empty : UiFormat.FormatBytes(Entry.SizeBytes);
    public string ModifiedLabel => Entry.ModifiedUtc.HasValue
        ? Entry.ModifiedUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : string.Empty;

    private static string L(string key, string fallback)
    {
        string? value = LocalizationProvider.Service?.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }
}

public sealed class SnapshotExplorerViewModel : ViewModelBase
{
    private const string PreviewSelectFileKey = "SnapshotExplorer.Preview.SelectFile";
    private const string PreviewSelectFileFallback = "Select a file to preview its contents.";
    private readonly SnapshotExplorerService _service;
    private readonly string _backupRoot;
    private readonly string _restoreTargetRoot;
    private string _currentPath = string.Empty;
    private string _searchText = string.Empty;
    private string _previewText = string.Empty;
    private string _previewFileName = string.Empty;
    private string _statusText = string.Empty;
    private bool _isBusy;
    private bool _isEncryptedBackup;
    private int _previewRequestVersion;
    private SnapshotExplorerEntryViewModel? _selectedEntry;
    private readonly RelayCommand _openSelectedCommand;
    private readonly RelayCommand _previewSelectedCommand;
    private readonly RelayCommand _restoreSelectedCommand;
    private readonly RelayCommand _goUpCommand;
    private readonly RelayCommand _refreshCommand;
    private readonly RelayCommand _clearSearchCommand;

    public SnapshotExplorerViewModel(
        SnapshotExplorerService service,
        string backupRoot,
        string restoreTargetRoot,
        string title)
    {
        _service = service;
        _backupRoot = backupRoot;
        _restoreTargetRoot = restoreTargetRoot;
        Title = title;
        _openSelectedCommand = new RelayCommand(async _ => await ToggleSelectedFolderAsync(), _ => !IsBusy && SelectedEntry?.IsFolder == true);
        _previewSelectedCommand = new RelayCommand(async _ => await PreviewSelectedAsync(), _ => !IsBusy && SelectedEntry?.CanPreview == true);
        _restoreSelectedCommand = new RelayCommand(async _ => await RestoreSelectedAsync(), _ => !IsBusy && SelectedEntry is not null);
        _goUpCommand = new RelayCommand(async _ => await GoUpAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(CurrentPath));
        _refreshCommand = new RelayCommand(async _ => await LoadEntriesAsync(), _ => !IsBusy);
        _clearSearchCommand = new RelayCommand(async _ =>
        {
            SearchText = string.Empty;
            await LoadEntriesAsync();
        }, _ => !IsBusy && !string.IsNullOrWhiteSpace(SearchText));
        OpenSelectedCommand = _openSelectedCommand;
        PreviewSelectedCommand = _previewSelectedCommand;
        RestoreSelectedCommand = _restoreSelectedCommand;
        GoUpCommand = _goUpCommand;
        RefreshCommand = _refreshCommand;
        ClearSearchCommand = _clearSearchCommand;
        _ = LoadEntriesAsync();
    }

    public string Title { get; }
    public ObservableCollection<SnapshotExplorerEntryViewModel> Entries { get; } = [];

    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetField(ref _currentPath, value))
            {
                OnPropertyChanged(nameof(CurrentPathDisplay));
                _goUpCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CurrentPathDisplay => string.IsNullOrWhiteSpace(CurrentPath) ? "/" : "/" + CurrentPath;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
                _clearSearchCommand.RaiseCanExecuteChanged();
        }
    }

    public string PreviewText
    {
        get => _previewText;
        private set => SetField(ref _previewText, value);
    }

    public string PreviewFileName
    {
        get => _previewFileName;
        private set => SetField(ref _previewFileName, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                RaiseCommandStatesChanged();
        }
    }

    public bool IsEncryptedBackup
    {
        get => _isEncryptedBackup;
        private set => SetField(ref _isEncryptedBackup, value);
    }

    public string EncryptedBackupMessage => L(
        "SnapshotExplorer.Encrypted.Message",
        "This backup is encrypted. Snapshot Explorer can identify it, but encrypted archive browsing is not part of 1.8.2. Use the normal restore flow to recover files from encrypted backups.");

    public SnapshotExplorerEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetField(ref _selectedEntry, value))
            {
                RaiseCommandStatesChanged();
                HandleSelectedEntryChanged(value);
            }
        }
    }

    public ICommand OpenSelectedCommand { get; }
    public ICommand PreviewSelectedCommand { get; }
    public ICommand RestoreSelectedCommand { get; }
    public ICommand GoUpCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ClearSearchCommand { get; }

    private async Task LoadEntriesAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = L("SnapshotExplorer.Status.Loading", "Loading snapshot contents...");
        try
        {
            string currentPath = CurrentPath;
            string searchText = SearchText;
            SnapshotExplorerResult result = await Task.Run(() => _service.List(_backupRoot, currentPath, searchText));
            Entries.Clear();
            foreach (SnapshotExplorerEntry entry in result.Entries)
                Entries.Add(new SnapshotExplorerEntryViewModel(entry));

            IsEncryptedBackup = result.SourceKind == SnapshotExplorerSourceKind.EncryptedArchive;
            StatusText = IsEncryptedBackup
                ? L("SnapshotExplorer.Status.Encrypted", "Encrypted backup detected. Use the normal restore flow for encrypted archives.")
                : LF("SnapshotExplorer.Status.ItemCount", "{0} item(s)", Entries.Count);
            PreviewText = SelectFilePreviewText();
            PreviewFileName = string.Empty;
            SelectedEntry = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Entries.Clear();
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ToggleSelectedFolderAsync()
    {
        if (SelectedEntry?.IsFolder != true)
            return;

        await ToggleFolderAsync(SelectedEntry);
    }

    private async Task GoUpAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath))
            return;

        string? parent = Path.GetDirectoryName(CurrentPath.Replace('/', Path.DirectorySeparatorChar));
        CurrentPath = string.IsNullOrWhiteSpace(parent)
            ? string.Empty
            : parent.Replace(Path.DirectorySeparatorChar, '/');
        SearchText = string.Empty;
        await LoadEntriesAsync();
    }

    private void HandleSelectedEntryChanged(SnapshotExplorerEntryViewModel? entry)
    {
        int version = ++_previewRequestVersion;
        if (entry is null)
        {
            IsBusy = false;
            PreviewText = SelectFilePreviewText();
            PreviewFileName = string.Empty;
            return;
        }

        if (entry.IsFolder)
        {
            _ = ToggleFolderAsync(entry);
            return;
        }

        _ = PreviewSelectedAsync(entry.Path, version);
    }

    private async Task ToggleFolderAsync(SnapshotExplorerEntryViewModel folder)
    {
        if (!folder.IsFolder)
            return;

        if (folder.IsExpanded)
        {
            CollapseFolder(folder);
            PreviewFileName = string.Empty;
            PreviewText = SelectFilePreviewText();
            StatusText = LF("SnapshotExplorer.Status.ItemCount", "{0} item(s)", Entries.Count);
            return;
        }

        await ExpandFolderAsync(folder);
    }

    private async Task ExpandFolderAsync(SnapshotExplorerEntryViewModel folder)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        PreviewFileName = string.Empty;
        PreviewText = L("SnapshotExplorer.Preview.OpeningFolder", "Opening folder...");
        StatusText = LF("SnapshotExplorer.Status.OpeningFolder", "Opening {0}...", folder.Path);

        try
        {
            SnapshotExplorerResult result = await Task.Run(() => _service.List(_backupRoot, folder.Path));
            int index = Entries.IndexOf(folder);
            if (index < 0)
                return;

            folder.IsExpanded = true;
            foreach (SnapshotExplorerEntry child in result.Entries.Reverse())
                Entries.Insert(index + 1, new SnapshotExplorerEntryViewModel(child, folder.Depth + 1));

            StatusText = LF("SnapshotExplorer.Status.ItemCount", "{0} item(s)", Entries.Count);
            PreviewText = SelectFilePreviewText();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            StatusText = ex.Message;
            PreviewText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CollapseFolder(SnapshotExplorerEntryViewModel folder)
    {
        int index = Entries.IndexOf(folder);
        if (index < 0)
            return;

        folder.IsExpanded = false;
        int removeAt = index + 1;
        while (removeAt < Entries.Count && Entries[removeAt].Depth > folder.Depth)
            Entries.RemoveAt(removeAt);
    }

    private Task PreviewSelectedAsync() =>
        PreviewSelectedAsync(SelectedEntry?.Path, ++_previewRequestVersion);

    private async Task PreviewSelectedAsync(string? selectedPath, int requestVersion)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            return;

        IsBusy = true;
        PreviewFileName = Path.GetFileName(selectedPath);
        StatusText = L("SnapshotExplorer.Status.PreviewLoading", "Loading preview...");
        try
        {
            SnapshotPreviewResult preview = await Task.Run(() => _service.PreviewText(_backupRoot, selectedPath));
            if (requestVersion != _previewRequestVersion || SelectedEntry?.Path != selectedPath)
                return;

            PreviewText = preview.Success
                ? preview.Text + (preview.Truncated ? Environment.NewLine + Environment.NewLine + L("SnapshotExplorer.Preview.Truncated", "[Preview truncated]") : string.Empty)
                : preview.Error;
            StatusText = preview.Success
                ? L("SnapshotExplorer.Status.PreviewReady", "Preview loaded.")
                : preview.Error;
        }
        finally
        {
            if (requestVersion == _previewRequestVersion)
                IsBusy = false;
        }
    }

    private async Task RestoreSelectedAsync()
    {
        string? path = SelectedEntry?.Path;
        if (string.IsNullOrWhiteSpace(path))
            return;

        IsBusy = true;
        StatusText = L("SnapshotExplorer.Status.Restoring", "Restoring selected item...");
        try
        {
            SnapshotRestoreSelectionResult result = await Task.Run(() => _service.RestoreSelection(_backupRoot, _restoreTargetRoot, [path]));
            StatusText = LF("SnapshotExplorer.Status.Restored", "Restored {0} file(s) to {1}", result.FileCount, _restoreTargetRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            StatusText = LF("SnapshotExplorer.Status.RestoreFailed", "Restore failed: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaiseCommandStatesChanged()
    {
        _openSelectedCommand.RaiseCanExecuteChanged();
        _previewSelectedCommand.RaiseCanExecuteChanged();
        _restoreSelectedCommand.RaiseCanExecuteChanged();
        _goUpCommand.RaiseCanExecuteChanged();
        _refreshCommand.RaiseCanExecuteChanged();
        _clearSearchCommand.RaiseCanExecuteChanged();
    }

    private static string L(string key, string fallback)
    {
        string? value = LocalizationProvider.Service?.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private static string LF(string key, string fallback, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, L(key, fallback), args);

    private string SelectFilePreviewText() =>
        IsEncryptedBackup
            ? EncryptedBackupMessage
            : L(PreviewSelectFileKey, PreviewSelectFileFallback);
}
