using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Input;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.ViewModels;

public sealed class SnapshotExplorerEntryViewModel
{
    public SnapshotExplorerEntryViewModel(SnapshotExplorerEntry entry)
    {
        Entry = entry;
    }

    public SnapshotExplorerEntry Entry { get; }
    public string Name => Entry.Name;
    public string Path => Entry.Path;
    public bool IsFolder => Entry.Kind == SnapshotExplorerEntryKind.Folder;
    public bool CanPreview => Entry.CanPreview;
    public string KindLabel => IsFolder ? "Folder" : "File";
    public string SizeLabel => IsFolder ? string.Empty : UiFormat.FormatBytes(Entry.SizeBytes);
    public string ModifiedLabel => Entry.ModifiedUtc.HasValue
        ? Entry.ModifiedUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : string.Empty;
}

public sealed class SnapshotExplorerViewModel : ViewModelBase
{
    private readonly SnapshotExplorerService _service;
    private readonly string _backupRoot;
    private readonly string _restoreTargetRoot;
    private string _currentPath = string.Empty;
    private string _searchText = string.Empty;
    private string _previewText = string.Empty;
    private string _statusText = string.Empty;
    private SnapshotExplorerEntryViewModel? _selectedEntry;

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
        OpenSelectedCommand = new RelayCommand(_ => OpenSelected(), _ => SelectedEntry?.IsFolder == true);
        PreviewSelectedCommand = new RelayCommand(_ => PreviewSelected(), _ => SelectedEntry?.CanPreview == true);
        RestoreSelectedCommand = new RelayCommand(_ => RestoreSelected(), _ => SelectedEntry is not null);
        GoUpCommand = new RelayCommand(_ => GoUp(), _ => !string.IsNullOrWhiteSpace(CurrentPath));
        RefreshCommand = new RelayCommand(_ => LoadEntries());
        ClearSearchCommand = new RelayCommand(_ =>
        {
            SearchText = string.Empty;
            LoadEntries();
        });
        LoadEntries();
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
                ((RelayCommand)GoUpCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string CurrentPathDisplay => string.IsNullOrWhiteSpace(CurrentPath) ? "/" : "/" + CurrentPath;

    public string SearchText
    {
        get => _searchText;
        set => SetField(ref _searchText, value);
    }

    public string PreviewText
    {
        get => _previewText;
        private set => SetField(ref _previewText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public SnapshotExplorerEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetField(ref _selectedEntry, value))
            {
                ((RelayCommand)OpenSelectedCommand).RaiseCanExecuteChanged();
                ((RelayCommand)PreviewSelectedCommand).RaiseCanExecuteChanged();
                ((RelayCommand)RestoreSelectedCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand OpenSelectedCommand { get; }
    public ICommand PreviewSelectedCommand { get; }
    public ICommand RestoreSelectedCommand { get; }
    public ICommand GoUpCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ClearSearchCommand { get; }

    private void LoadEntries()
    {
        try
        {
            SnapshotExplorerResult result = _service.List(_backupRoot, CurrentPath, SearchText);
            Entries.Clear();
            foreach (SnapshotExplorerEntry entry in result.Entries)
                Entries.Add(new SnapshotExplorerEntryViewModel(entry));

            StatusText = result.SourceKind == SnapshotExplorerSourceKind.EncryptedArchive
                ? "Encrypted backup browsing is not available in Snapshot Explorer yet."
                : $"{Entries.Count} item(s)";
            PreviewText = string.Empty;
            SelectedEntry = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Entries.Clear();
            StatusText = ex.Message;
        }
    }

    private void OpenSelected()
    {
        if (SelectedEntry?.IsFolder != true)
            return;

        CurrentPath = SelectedEntry.Path;
        SearchText = string.Empty;
        LoadEntries();
    }

    private void GoUp()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath))
            return;

        string? parent = Path.GetDirectoryName(CurrentPath.Replace('/', Path.DirectorySeparatorChar));
        CurrentPath = string.IsNullOrWhiteSpace(parent)
            ? string.Empty
            : parent.Replace(Path.DirectorySeparatorChar, '/');
        SearchText = string.Empty;
        LoadEntries();
    }

    private void PreviewSelected()
    {
        if (SelectedEntry is null)
            return;

        SnapshotPreviewResult preview = _service.PreviewText(_backupRoot, SelectedEntry.Path);
        PreviewText = preview.Success
            ? preview.Text + (preview.Truncated ? Environment.NewLine + Environment.NewLine + "[Preview truncated]" : string.Empty)
            : preview.Error;
    }

    private void RestoreSelected()
    {
        if (SelectedEntry is null)
            return;

        try
        {
            SnapshotRestoreSelectionResult result = _service.RestoreSelection(_backupRoot, _restoreTargetRoot, [SelectedEntry.Path]);
            StatusText = $"Restored {result.FileCount} file(s) to {_restoreTargetRoot}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            StatusText = $"Restore failed: {ex.Message}";
        }
    }
}
