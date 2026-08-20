using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using Avalonia.Media;
using Avalonia.Threading;
using VaultSync.Core.Repositories;
using System.Threading.Tasks;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using VaultSync.Core.Models;
using System.Text.Json;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels.Notifications;
using System.Linq;
using VaultSync.UI.Notifications;
using System.Globalization;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

// dotnet format corrupts this multi-target file when applying IDE0008 fixes.
#pragma warning disable IDE0008

/// <summary>
/// Projects page view model - drives the list on the left and the
/// details / actions panel on the right.
/// </summary>
public partial class ProjectsViewModel : ViewModelBase
{
    private const string GenericPresetId = "generic";
    private const string NoPresetId = "no preset";
    private const string DefaultSnapshotActionKey = "Snapshots.Action.Default";
    private const string DefaultSnapshotActionFallback = "Snapshot now";
    internal const string NeutralBadgeBackground = "#2F3650";
    private const string VideoPresetId = "video";
    private static readonly string[] DefaultReusableTags = ["Work", "Games", "Media", "Critical", "Archive"];
    private sealed record ProjectRegistrationSnapshot(
        bool Missing,
        int ProjectId,
        string Preset,
        string TagsCsv,
        string GroupId,
        string PreferredDestinationId,
        string EncryptionPolicy,
        string EncryptionKeyRef);

    private readonly ProjectDiscoveryService _discovery = new();
    private readonly IAppConfigStore _configStore;
    private readonly IRepositoryFactory _repositoryFactory;
    private IReadOnlyList<DiscoveredProject> _cachedDiscovery = [];
    private string? _cachedDiscoveryRoot;
    private DateTime _cachedDiscoveryUtc;
    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromSeconds(10);
    private static string L(string key, string fallback)
    {
        string? value = LocalizationProvider.Service?.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private static string Lf(string key, string fallback, params object[] args)
    {
        var fmt = L(key, fallback);
        return string.Format(CultureInfo.CurrentCulture, fmt, args);
    }

    public sealed class ProjectTagColorSwatchViewModel
    {
        public ProjectTagColorSwatchViewModel(string hex)
        {
            Hex = hex;
            Swatch = Color.Parse(hex);
            SwatchBrush = new SolidColorBrush(Swatch);
            OutlineBrush = CreateOutlineBrush(Swatch);
        }

        public string Hex { get; }
        public Color Swatch { get; }
        public IBrush SwatchBrush { get; }
        public IBrush OutlineBrush { get; }

        private static SolidColorBrush CreateOutlineBrush(Color color)
        {
            var luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255d;
            return new SolidColorBrush(luminance > 0.62 ? Color.Parse("#24344A") : Color.Parse("#E2E8F0"));
        }
    }

    /// <summary>
    /// Preset options that can be applied to projects. These correspond to
    /// .vaultsyncignore-style profiles (Unity, .NET, etc.) plus an explicit
    /// "no preset" option.
    /// </summary>
    public ObservableCollection<string> AvailablePresets { get; } =
        [];
    public ObservableCollection<DestinationOption> DestinationOptions { get; } =
        [];
    public ObservableCollection<EncryptionPolicyOption> EncryptionPolicyOptions { get; } =
        [];
    public ObservableCollection<ProjectGroupOption> GroupOptions { get; } =
        [];
    public ObservableCollection<ProjectFolderViewModel> ProjectFolders { get; } =
        [];
    public ObservableCollection<ProjectItemViewModel> UngroupedProjects { get; } =
        [];
    public ObservableCollection<ProjectItemViewModel> Projects { get; } =
        [];
    private readonly Dictionary<string, PresetInfo> _presetCatalogById =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PresetRecommendation?> _presetRecommendationCache =
        new(StringComparer.OrdinalIgnoreCase);

    private ProjectItemViewModel? _selectedProject;
    private string _lastSelectedProjectName = string.Empty;
    private int _selectedProjectRefreshToken;
    private int _selectedProjectHistoryToken;
    private int _suppressProjectPersistence;
    private int _refreshInFlight;
    private int _refreshQueued;
    private readonly RelayCommand _openFolderCommand;
    private readonly RelayCommand _selectProjectCommand;
    private readonly RelayCommand _removeProjectCommand;
    private readonly RelayCommand _confirmRemoveProjectCommand;
    private readonly RelayCommand _applyPresetRecommendationCommand;
    private readonly RelayCommand _togglePresetEditorCommand;
    private readonly RelayCommand _reloadPresetEditorCommand;
    private readonly RelayCommand _savePresetEditorCommand;
    private readonly RelayCommand _previewPresetEditorCommand;
    private readonly RelayCommand _clonePresetEditorCommand;
    private readonly RelayCommand _exportPresetEditorCommand;
    private readonly RelayCommand _importPresetEditorCommand;
    private readonly RelayCommand _snapshotGroupCommand;
    private readonly RelayCommand _backupGroupCommand;
    private readonly RelayCommand _disableAutoBackupGroupCommand;
    private readonly RelayCommand _enableAutoBackupGroupCommand;
    private readonly RelayCommand _createProjectGroupCommand;
    private readonly RelayCommand _beginRenameProjectGroupCommand;
    private readonly RelayCommand _saveRenameProjectGroupCommand;
    private readonly RelayCommand _cancelRenameProjectGroupCommand;
    private readonly RelayCommand _requestDeleteProjectGroupCommand;
    private readonly RelayCommand _confirmDeleteProjectGroupCommand;
    private readonly RelayCommand _cancelDeleteProjectGroupCommand;
    private readonly RelayCommand _moveSelectedProjectToFolderCommand;
    private readonly RelayCommand _commitProjectTagInputCommand;
    private readonly RelayCommand _removeProjectTagCommand;
    private readonly RelayCommand _addExistingTagToSelectedProjectCommand;
    private readonly RelayCommand _toggleProjectTagColorEditorCommand;
    private readonly RelayCommand _applyProjectTagColorCommand;
    private readonly RelayCommand _resetProjectTagColorCommand;
    private readonly RelayCommand _applyProjectTagColorSwatchCommand;
    private bool _isProjectTagColorEditorOpen;
    private bool _projectTagColorSyncing;
    private string _projectTagColorHex = "#3A7AFE";
    private Color _selectedProjectTagColor = Color.Parse("#3A7AFE");
    private double _projectTagColorRed = 58;
    private double _projectTagColorGreen = 122;
    private double _projectTagColorBlue = 254;
    private double _projectTagColorHue = 219;
    private double _projectTagColorSaturation = 77;
    private double _projectTagColorValue = 100;
    public ProjectItemViewModel? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetField(ref _selectedProject, value))
            {
                if (value is not null && !string.IsNullOrWhiteSpace(value.Name))
                    _lastSelectedProjectName = value.Name;

                OnPropertyChanged(nameof(HasSelectedProject));
                OnPropertyChanged(nameof(ShowSelectedProjectEmptyState));
                IsRemoveProjectPreviewOpen = false;

                _openFolderCommand.RaiseCanExecuteChanged();
                _removeProjectCommand.RaiseCanExecuteChanged();
                _confirmRemoveProjectCommand.RaiseCanExecuteChanged();
                _moveSelectedProjectToFolderCommand.RaiseCanExecuteChanged();
                _applyPresetRecommendationCommand.RaiseCanExecuteChanged();
                _commitProjectTagInputCommand.RaiseCanExecuteChanged();
                _removeProjectTagCommand.RaiseCanExecuteChanged();
                _addExistingTagToSelectedProjectCommand.RaiseCanExecuteChanged();
                RefreshSelectedProjectRegistration();
                UpdateProjectPresetRecommendation(value);
                LoadSnapshotHistoryForSelectedProject();
                LoadPresetEditorForSelectedProject();
                RefreshSelectedProjectTags();
            }
        }
    }

    public bool HasProjects => Projects.Count > 0;
    public bool HasProjectFolders => ProjectFolders.Count > 0;
    public bool HasUngroupedProjects => UngroupedProjects.Count > 0;
    public bool ShowUngroupedSectionHeader => HasProjectFolders && HasUngroupedProjects;
    public bool ShowProjectsEmptyState => !HasProjects;
    public bool HasSelectedProject => SelectedProject is not null;
    public bool ShowSelectedProjectEmptyState => !HasSelectedProject;

    private bool _isRemoveProjectPreviewOpen;
    public bool IsRemoveProjectPreviewOpen
    {
        get => _isRemoveProjectPreviewOpen;
        private set => SetField(ref _isRemoveProjectPreviewOpen, value);
    }

    private string _removeProjectPreviewTitle = string.Empty;
    public string RemoveProjectPreviewTitle
    {
        get => _removeProjectPreviewTitle;
        private set => SetField(ref _removeProjectPreviewTitle, value);
    }

    private string _removeProjectPreviewDetail = string.Empty;
    public string RemoveProjectPreviewDetail
    {
        get => _removeProjectPreviewDetail;
        private set => SetField(ref _removeProjectPreviewDetail, value);
    }

    public bool ShowProjectAvatars { get; private set; } = true;

    private string _snapshotActionLabel = L(DefaultSnapshotActionKey, DefaultSnapshotActionFallback);
    public string SnapshotActionLabel
    {
        get => _snapshotActionLabel;
        set => SetField(ref _snapshotActionLabel, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetField(ref _isLoading, value);
    }

    // Reusable notification state for the Projects view.
    public NotificationState Notification { get; } = new NotificationState();

    public ICommand RefreshCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand SelectProjectCommand { get; }
    public ICommand RemoveProjectCommand { get; }
    public ICommand CancelRemoveProjectCommand { get; }
    public ICommand ConfirmRemoveProjectCommand { get; }
    public ICommand ReviewStoredBackupsCommand { get; }
    public ICommand SnapshotCommand { get; }
    public ICommand SnapshotGroupCommand { get; }
    public ICommand BackupGroupCommand { get; }
    public ICommand DisableAutoBackupGroupCommand { get; }
    public ICommand EnableAutoBackupGroupCommand { get; }
    public ICommand CreateProjectGroupCommand { get; }
    public ICommand BeginRenameProjectGroupCommand { get; }
    public ICommand SaveRenameProjectGroupCommand { get; }
    public ICommand CancelRenameProjectGroupCommand { get; }
    public ICommand RequestDeleteProjectGroupCommand { get; }
    public ICommand ConfirmDeleteProjectGroupCommand { get; }
    public ICommand CancelDeleteProjectGroupCommand { get; }
    public ICommand MoveSelectedProjectToFolderCommand { get; }
    public ICommand CommitProjectTagInputCommand { get; }
    public ICommand RemoveProjectTagCommand { get; }
    public ICommand AddExistingTagToSelectedProjectCommand { get; }
    public ICommand ToggleProjectTagColorEditorCommand { get; }
    public ICommand ApplyProjectTagColorCommand { get; }
    public ICommand ResetProjectTagColorCommand { get; }
    public ICommand ApplyProjectTagColorSwatchCommand { get; }
    public ICommand TakeSnapshotCommand => SnapshotCommand;
    public ICommand ManageProjectEncryptionCommand { get; }
    public ICommand ApplyPresetRecommendationCommand { get; }
    public ICommand TogglePresetEditorCommand { get; }
    public ICommand ReloadPresetEditorCommand { get; }
    public ICommand SavePresetEditorCommand { get; }
    public ICommand PreviewPresetEditorCommand { get; }
    public ICommand ClonePresetEditorCommand { get; }
    public ICommand ExportPresetEditorCommand { get; }
    public ICommand ImportPresetEditorCommand { get; }
    public ICommand ToggleSortCommand { get; }
    public ObservableCollection<ProjectTagColorSwatchViewModel> ProjectTagColorSwatches { get; } = [];
    public event Action<ProjectItemViewModel>? EditProjectEncryptionRequested;
    public event Action<int, string>? ProjectEncryptionPolicyChanged;
    public event Action<int>? ProjectSettingsMetadataChanged;
    public event Action<IReadOnlyList<int>>? BackupGroupRequested;
    public event Action<IReadOnlyList<int>, bool>? AutoBackupGroupPreferenceChanged;
    public event Action<int, string>? ProjectRemovedFromDatabase;
    public ObservableCollection<ProjectTagChip> SelectedProjectTags { get; } = [];
    public ObservableCollection<ProjectTagChip> ReusableProjectTags { get; } = [];
    private string _newProjectGroupName = string.Empty;
    public string NewProjectGroupName
    {
        get => _newProjectGroupName;
        set
        {
            if (SetField(ref _newProjectGroupName, value ?? string.Empty))
                _createProjectGroupCommand.RaiseCanExecuteChanged();
        }
    }
    private string _projectTagInput = string.Empty;
    public string ProjectTagInput
    {
        get => _projectTagInput;
        set
        {
            if (!SetField(ref _projectTagInput, value ?? string.Empty))
                return;

            ConsumeProjectTagInputDelimiters();
            _addExistingTagToSelectedProjectCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanEditProjectTagColor));
            OnPropertyChanged(nameof(ProjectTagColorTarget));
            OnPropertyChanged(nameof(ProjectTagColorToggleLabel));
            _toggleProjectTagColorEditorCommand.RaiseCanExecuteChanged();
            _applyProjectTagColorCommand.RaiseCanExecuteChanged();
            _resetProjectTagColorCommand.RaiseCanExecuteChanged();

            if (CanEditProjectTagColor)
                SyncProjectTagColorDraftFromInput();
            else
                IsProjectTagColorEditorOpen = false;
        }
    }

    public bool CanEditProjectTagColor => SelectedProject is not null && !string.IsNullOrWhiteSpace(ProjectTagInput);

    public bool IsProjectTagColorEditorOpen
    {
        get => _isProjectTagColorEditorOpen;
        set
        {
            if (!SetField(ref _isProjectTagColorEditorOpen, value))
                return;

            OnPropertyChanged(nameof(ProjectTagColorToggleLabel));
        }
    }

    public string ProjectTagColorToggleLabel =>
        IsProjectTagColorEditorOpen
            ? L("Projects.Tags.Color.Close", "Close color")
            : L("Projects.Tags.Color.Open", "Custom color");

    public string ProjectTagColorTarget => (ProjectTagInput ?? string.Empty).Trim();

    public Color SelectedProjectTagColor
    {
        get => _selectedProjectTagColor;
        set
        {
            if (_projectTagColorSyncing || _selectedProjectTagColor == value)
                return;

            _projectTagColorSyncing = true;
            try
            {
                _selectedProjectTagColor = value;
                OnPropertyChanged();
                _projectTagColorHex = ProjectTagAppearance.FormatHex(value.R, value.G, value.B);
                OnPropertyChanged(nameof(ProjectTagColorHex));
            }
            finally
            {
                _projectTagColorSyncing = false;
            }

            SyncRgbFromHex();
            RefreshProjectTagColorPreview();
        }
    }

    public string ProjectTagColorHex
    {
        get => _projectTagColorHex;
        set
        {
            var normalized = ProjectTagAppearance.NormalizeHex(value, _projectTagColorHex);
            if (!SetField(ref _projectTagColorHex, normalized))
                return;

            if (Color.TryParse(normalized, out var parsed))
            {
                _selectedProjectTagColor = parsed;
                OnPropertyChanged(nameof(SelectedProjectTagColor));
            }

            SyncRgbFromHex();
            RefreshProjectTagColorPreview();
        }
    }

    public double ProjectTagColorRed
    {
        get => _projectTagColorRed;
        set
        {
            var clamped = Math.Clamp(value, 0d, 255d);
            if (!SetField(ref _projectTagColorRed, clamped))
                return;
            SyncHexFromRgb();
        }
    }

    public double ProjectTagColorGreen
    {
        get => _projectTagColorGreen;
        set
        {
            var clamped = Math.Clamp(value, 0d, 255d);
            if (!SetField(ref _projectTagColorGreen, clamped))
                return;
            SyncHexFromRgb();
        }
    }

    public double ProjectTagColorBlue
    {
        get => _projectTagColorBlue;
        set
        {
            var clamped = Math.Clamp(value, 0d, 255d);
            if (!SetField(ref _projectTagColorBlue, clamped))
                return;
            SyncHexFromRgb();
        }
    }

    public double ProjectTagColorHue
    {
        get => _projectTagColorHue;
        set
        {
            var normalized = Math.Clamp(value, 0d, 360d);
            if (!SetField(ref _projectTagColorHue, normalized))
                return;
            SyncHexFromHsv();
        }
    }

    public double ProjectTagColorSaturation
    {
        get => _projectTagColorSaturation;
        set
        {
            var normalized = Math.Clamp(value, 0d, 100d);
            if (!SetField(ref _projectTagColorSaturation, normalized))
                return;
            SyncHexFromHsv();
        }
    }

    public double ProjectTagColorValue
    {
        get => _projectTagColorValue;
        set
        {
            var normalized = Math.Clamp(value, 0d, 100d);
            if (!SetField(ref _projectTagColorValue, normalized))
                return;
            SyncHexFromHsv();
        }
    }

    public string ProjectTagColorPreviewBackground => ProjectTagAppearance.BuildConfigFromAccent(ProjectTagColorHex).Background;
    public string ProjectTagColorPreviewForeground => ProjectTagAppearance.BuildConfigFromAccent(ProjectTagColorHex).Foreground;
    public string ProjectTagColorPreviewBorder => ProjectTagAppearance.BuildConfigFromAccent(ProjectTagColorHex).Border;
    public static string ProjectTagColorPickerLabel => L("Projects.Tags.Color.Picker", "Pick a color");
    public static string ProjectTagColorPaletteLabel => L("Projects.Tags.Color.Palette", "Quick palette");
    public static string ProjectTagColorGlobalHint => L("Projects.Tags.Color.GlobalHint", "Saved colors apply app-wide to this tag anywhere it appears.");
    private string _presetEditorContent = string.Empty;
    public string PresetEditorContent
    {
        get => _presetEditorContent;
        set => SetField(ref _presetEditorContent, value ?? string.Empty);
    }

    private string _presetEditorStatus = string.Empty;
    public string PresetEditorStatus
    {
        get => _presetEditorStatus;
        set => SetField(ref _presetEditorStatus, value ?? string.Empty);
    }

    private string _presetEditorPath = string.Empty;
    public string PresetEditorPath
    {
        get => _presetEditorPath;
        set => SetField(ref _presetEditorPath, value ?? string.Empty);
    }

    private string _presetEditorPathDisplay = string.Empty;
    public string PresetEditorPathDisplay
    {
        get => _presetEditorPathDisplay;
        set => SetField(ref _presetEditorPathDisplay, value ?? string.Empty);
    }

    private bool _hasPresetEditorTarget;
    public bool HasPresetEditorTarget
    {
        get => _hasPresetEditorTarget;
        set => SetField(ref _hasPresetEditorTarget, value);
    }

    private bool _isPresetEditorVisible;
    public bool IsPresetEditorVisible
    {
        get => _isPresetEditorVisible;
        set
        {
            if (!SetField(ref _isPresetEditorVisible, value))
                return;

            OnPropertyChanged(nameof(PresetEditorToggleLabel));
        }
    }

    public string PresetEditorToggleLabel =>
        IsPresetEditorVisible
            ? L("Projects.Preset.Editor.ToggleClose", "Close preset editor")
            : L("Projects.Preset.Editor.ToggleOpen", "Open preset editor");

    private string _presetEditorCloneId = string.Empty;
    public string PresetEditorCloneId
    {
        get => _presetEditorCloneId;
        set => SetField(ref _presetEditorCloneId, value ?? string.Empty);
    }

    private string _presetEditorImportPath = string.Empty;
    public string PresetEditorImportPath
    {
        get => _presetEditorImportPath;
        set => SetField(ref _presetEditorImportPath, value ?? string.Empty);
    }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                ApplyFilterAndSort();
            }
        }
    }

    public enum ProjectSortMode
    {
        Name,
        LastSnapshot
    }

    private ProjectSortMode _sortMode = ProjectSortMode.LastSnapshot;
    public ProjectSortMode SortMode
    {
        get => _sortMode;
        private set
        {
            if (SetField(ref _sortMode, value))
            {
                OnPropertyChanged(nameof(SortModeLabel));
                SortProjects();
            }
        }
    }

    public string SortModeLabel =>
        SortMode == ProjectSortMode.LastSnapshot
            ? L("Projects.Sort.Latest", "Sort: Latest snapshot")
            : L("Projects.Sort.Name", "Sort: Name");

    private readonly List<ProjectItemViewModel> _allProjects = [];
    private HashSet<int> _autoBackupDisabledProjectIds = [];
    private string _searchText = string.Empty;
    private int _initialLoadQueued;

    public ProjectsViewModel()
        : this(StaticAppConfigStore.Instance, new SqliteRepositoryFactory(StaticAppConfigStore.Instance))
    {
    }

    internal ProjectsViewModel(IAppConfigStore configStore, IRepositoryFactory? repositoryFactory = null)
    {
        _configStore = configStore;
        _repositoryFactory = repositoryFactory ?? new SqliteRepositoryFactory(_configStore);
        RefreshCommand = new RelayCommand(_ => Refresh());
        _selectProjectCommand = new RelayCommand(project => SelectedProject = project as ProjectItemViewModel);
        _openFolderCommand = new RelayCommand(_ => OpenFolder(), _ => SelectedProject is not null);
        _removeProjectCommand = new RelayCommand(_ => BeginRemoveProjectPreview(), _ => SelectedProject is not null);
        var cancelRemoveProjectCommand = new RelayCommand(_ => IsRemoveProjectPreviewOpen = false);
        _confirmRemoveProjectCommand = new RelayCommand(_ => RemoveProject(), _ => SelectedProject is not null);
        var reviewStoredBackupsCommand = new RelayCommand(_ =>
            App.AppViewModelInstance?.NavigateBackups?.Execute(null));
        _applyPresetRecommendationCommand = new RelayCommand(_ => ApplyPresetRecommendation(), _ =>
            SelectedProject is { RecommendedPreset.Length: > 0 });
        _togglePresetEditorCommand = new RelayCommand(_ => TogglePresetEditor(), _ => HasPresetEditorTarget);
        _reloadPresetEditorCommand = new RelayCommand(_ => _ = ReloadPresetEditorAsync(), _ => HasPresetEditorTarget);
        _savePresetEditorCommand = new RelayCommand(_ => SavePresetEditor(), _ => HasPresetEditorTarget);
        _previewPresetEditorCommand = new RelayCommand(_ => PreviewPresetEditor(), _ => HasPresetEditorTarget);
        _clonePresetEditorCommand = new RelayCommand(_ => ClonePresetEditor(), _ => HasPresetEditorTarget);
        _exportPresetEditorCommand = new RelayCommand(_ => ExportPresetEditor(), _ => HasPresetEditorTarget);
        _importPresetEditorCommand = new RelayCommand(_ => ImportPresetEditor());
        _snapshotGroupCommand = new RelayCommand(
            folder => _ = DetachedTask.RunAsync(() => SnapshotProjectGroupAsync(folder as ProjectFolderViewModel), "snapshot-project-folder"),
            folder => CanRunProjectGroupAction(folder as ProjectFolderViewModel));
        _backupGroupCommand = new RelayCommand(
            folder => _ = DetachedTask.RunAsync(() => BackupProjectGroupAsync(folder as ProjectFolderViewModel), "backup-project-folder"),
            folder => CanRunProjectGroupAction(folder as ProjectFolderViewModel));
        _disableAutoBackupGroupCommand = new RelayCommand(
            folder => _ = DetachedTask.RunAsync(() => SetAutoBackupForProjectGroupAsync(folder as ProjectFolderViewModel, false), "disable-auto-backup-project-folder"),
            folder => CanSetProjectGroupAutoBackup(folder as ProjectFolderViewModel, enabled: false));
        _enableAutoBackupGroupCommand = new RelayCommand(
            folder => _ = DetachedTask.RunAsync(() => SetAutoBackupForProjectGroupAsync(folder as ProjectFolderViewModel, true), "enable-auto-backup-project-folder"),
            folder => CanSetProjectGroupAutoBackup(folder as ProjectFolderViewModel, enabled: true));
        _createProjectGroupCommand = new RelayCommand(_ => CreateProjectGroup(), _ => CanCreateProjectGroup());
        _beginRenameProjectGroupCommand = new RelayCommand(
            folder => BeginRenameProjectGroup(folder as ProjectFolderViewModel),
            folder => folder is ProjectFolderViewModel { CanManage: true });
        _saveRenameProjectGroupCommand = new RelayCommand(
            folder => SaveRenameProjectGroup(folder as ProjectFolderViewModel),
            folder => CanSaveRenameProjectGroup(folder as ProjectFolderViewModel));
        _cancelRenameProjectGroupCommand = new RelayCommand(folder => CancelRenameProjectGroup(folder as ProjectFolderViewModel));
        _requestDeleteProjectGroupCommand = new RelayCommand(
            folder => RequestDeleteProjectGroup(folder as ProjectFolderViewModel),
            folder => folder is ProjectFolderViewModel { CanManage: true });
        _confirmDeleteProjectGroupCommand = new RelayCommand(
            folder => DeleteProjectGroup(folder as ProjectFolderViewModel),
            folder => folder is ProjectFolderViewModel { CanManage: true });
        _cancelDeleteProjectGroupCommand = new RelayCommand(folder => CancelDeleteProjectGroup(folder as ProjectFolderViewModel));
        _moveSelectedProjectToFolderCommand = new RelayCommand(
            _ => MoveSelectedProjectToFolder(),
            _ => SelectedProject is { HasPendingGroupChange: true, IsRegistered: true });
        _commitProjectTagInputCommand = new RelayCommand(_ => CommitProjectTagInput(), _ => SelectedProject is not null);
        _removeProjectTagCommand = new RelayCommand(tag => RemoveProjectTag(tag as string), _ => SelectedProject is not null);
        _addExistingTagToSelectedProjectCommand = new RelayCommand(
            tag => AddExistingTagToSelectedProject(tag as string),
            tag => SelectedProject is not null &&
                   (!string.IsNullOrWhiteSpace(tag as string) || !string.IsNullOrWhiteSpace(ProjectTagInput)));
        _toggleProjectTagColorEditorCommand = new RelayCommand(_ => ToggleProjectTagColorEditor(), _ => CanEditProjectTagColor);
        _applyProjectTagColorCommand = new RelayCommand(_ => ApplyProjectTagColor(), _ => CanEditProjectTagColor);
        _resetProjectTagColorCommand = new RelayCommand(_ => ResetProjectTagColor(), _ => CanEditProjectTagColor);
        _applyProjectTagColorSwatchCommand = new RelayCommand(hex => ApplyProjectTagColorSwatch(hex as string), hex => !string.IsNullOrWhiteSpace(hex as string));
        OpenFolderCommand = _openFolderCommand;
        SelectProjectCommand = _selectProjectCommand;
        RemoveProjectCommand = _removeProjectCommand;
        CancelRemoveProjectCommand = cancelRemoveProjectCommand;
        ConfirmRemoveProjectCommand = _confirmRemoveProjectCommand;
        ReviewStoredBackupsCommand = reviewStoredBackupsCommand;
        ApplyPresetRecommendationCommand = _applyPresetRecommendationCommand;
        TogglePresetEditorCommand = _togglePresetEditorCommand;
        ReloadPresetEditorCommand = _reloadPresetEditorCommand;
        SavePresetEditorCommand = _savePresetEditorCommand;
        PreviewPresetEditorCommand = _previewPresetEditorCommand;
        ClonePresetEditorCommand = _clonePresetEditorCommand;
        ExportPresetEditorCommand = _exportPresetEditorCommand;
        ImportPresetEditorCommand = _importPresetEditorCommand;
        SnapshotGroupCommand = _snapshotGroupCommand;
        BackupGroupCommand = _backupGroupCommand;
        DisableAutoBackupGroupCommand = _disableAutoBackupGroupCommand;
        EnableAutoBackupGroupCommand = _enableAutoBackupGroupCommand;
        CreateProjectGroupCommand = _createProjectGroupCommand;
        BeginRenameProjectGroupCommand = _beginRenameProjectGroupCommand;
        SaveRenameProjectGroupCommand = _saveRenameProjectGroupCommand;
        CancelRenameProjectGroupCommand = _cancelRenameProjectGroupCommand;
        RequestDeleteProjectGroupCommand = _requestDeleteProjectGroupCommand;
        ConfirmDeleteProjectGroupCommand = _confirmDeleteProjectGroupCommand;
        CancelDeleteProjectGroupCommand = _cancelDeleteProjectGroupCommand;
        MoveSelectedProjectToFolderCommand = _moveSelectedProjectToFolderCommand;
        CommitProjectTagInputCommand = _commitProjectTagInputCommand;
        RemoveProjectTagCommand = _removeProjectTagCommand;
        AddExistingTagToSelectedProjectCommand = _addExistingTagToSelectedProjectCommand;
        ToggleProjectTagColorEditorCommand = _toggleProjectTagColorEditorCommand;
        ApplyProjectTagColorCommand = _applyProjectTagColorCommand;
        ResetProjectTagColorCommand = _resetProjectTagColorCommand;
        ApplyProjectTagColorSwatchCommand = _applyProjectTagColorSwatchCommand;
        SnapshotCommand = new RelayCommand(_ => TakeSnapshot());
        ManageProjectEncryptionCommand = new RelayCommand(p => RequestProjectEncryptionPasswordEdit(p as ProjectItemViewModel ?? SelectedProject));
        ToggleSortCommand = new RelayCommand(_ => ToggleSortMode());

        foreach (var hex in new[]
                 {
                     "#111827", "#334155", "#64748B", "#E2E8F0",
                     "#DC2626", "#F97316", "#F59E0B", "#EAB308",
                     "#84CC16", "#22C55E", "#14B8A6", "#06B6D4",
                     "#0EA5E9", "#2563EB", "#4F8DFF", "#6366F1",
                     "#7C3AED", "#A855F7", "#EC4899", "#F43F5E"
                 })
        {
            ProjectTagColorSwatches.Add(new ProjectTagColorSwatchViewModel(hex));
        }

        LoadAvailablePresets();
        RefreshEncryptionPolicyOptions();
        LoadGroupOptions();
        RefreshReusableProjectTags();
        RefreshGroupAutoBackupStateFromConfig();

    }

    public void EnsureLoaded()
    {
        if (Projects.Count > 0 || IsLoading)
            return;

        if (_allProjects.Count > 0)
        {
            ApplyFilterAndSort();
            return;
        }

        if (Interlocked.Exchange(ref _initialLoadQueued, 1) == 1)
            return;

        _ = RunInitialLoadAsync();
    }

    private async Task RunInitialLoadAsync()
    {
        try
        {
            await RefreshAsync(forceDiscovery: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Record($"Projects initial load failed: {ex.GetType().Name} - {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _initialLoadQueued, 0);
        }
    }

    private void ShowNotification(string message, NotificationSeverity severity = NotificationSeverity.Info)
    {
        Notification.Show(message, severity);
    }

    private void NotifySnapshotOutcome(string message, bool success)
    {
        var cfg = _configStore.GetSnapshot();

        var wants = success
            ? cfg.Notifications.OnSnapshotSuccess
            : cfg.Notifications.OnSnapshotFailure;

        if (!wants)
            return;

        var severity = success ? NotificationSeverity.Info : NotificationSeverity.Error;
        var title = success
            ? L("Snapshots.Notification.SuccessTitle", "Snapshot completed")
            : L("Snapshots.Notification.FailureTitle", "Snapshot failed");

        GlobalNotificationCenter.Instance.Show(message, severity, title);

        if (cfg.Notifications.UseOsNotifications &&
            (!cfg.Notifications.OnlyWhenInactive || !MainWindow.IsForeground))
        {
            GlobalNotificationCenter.Instance.ShowSystem(message, severity, title);
        }
    }

    // Removed sample project seeding; production should show empty state when no projects exist.

    private void Refresh()
    {
        _ = RefreshAsync(forceDiscovery: true);
    }

    public async Task RefreshAsync(bool forceDiscovery = false)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await RefreshAsync(forceDiscovery).ConfigureAwait(true);
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });

            await completion.Task.ConfigureAwait(false);
            return;
        }

        if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
        {
            Interlocked.Exchange(ref _refreshQueued, 1);
            return;
        }

        try
        {
            IsLoading = true;

            var config = await Task.Run(_configStore.GetSnapshot);
            RefreshGroupAutoBackupStateFromConfig(config);
            ShowProjectAvatars = config.Appearance.ShowProjectAvatars;
            OnPropertyChanged(nameof(ShowProjectAvatars));
            RefreshDestinationOptionsInternal(config);
            var projectItems = await Task.Run(() =>
            {
                var discovered = GetDiscoveredProjects(config, forceDiscovery);
                return BuildProjectItems(config, discovered);
            });

            Projects.Clear();
            _allProjects.Clear();
            _allProjects.AddRange(projectItems);

            ApplyFilterAndSort();
            RefreshReusableProjectTags();

            if (SelectedProject != null && !Projects.Contains(SelectedProject))
            {
                SelectedProject = Projects.Count > 0 ? Projects[0] : null;
            }
            else if (SelectedProject == null && Projects.Count > 0)
            {
                SelectedProject = Projects[0];
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Record($"Projects refresh failed: {ex.GetType().Name} - {ex.Message}");
            ShowNotification(L("Projects.Notification.RefreshError", "Error refreshing projects. Check logs for details."), NotificationSeverity.Error);
        }
        finally
        {
            IsLoading = false;
            Interlocked.Exchange(ref _refreshInFlight, 0);
            if (Interlocked.Exchange(ref _refreshQueued, 0) == 1)
            {
                await RefreshAsync(forceDiscovery: forceDiscovery);
            }
        }
    }

    private IReadOnlyList<DiscoveredProject> GetDiscoveredProjects(AppConfig config, bool forceDiscovery)
    {
        var root = ResolveDiscoveryRoot(config);
        var cacheFresh = !forceDiscovery
            && string.Equals(_cachedDiscoveryRoot, root, StringComparison.OrdinalIgnoreCase)
            && (DateTime.UtcNow - _cachedDiscoveryUtc) < DiscoveryCacheTtl;

        if (cacheFresh)
            return _cachedDiscovery;

        IReadOnlyList<DiscoveredProject> discovered;
        try
        {
            discovered = _discovery.DiscoverAsync(config).GetAwaiter().GetResult();
        }
        catch
        {
            discovered = [];
        }

        _cachedDiscovery = discovered;
        _cachedDiscoveryRoot = root;
        _cachedDiscoveryUtc = DateTime.UtcNow;
        return discovered;
    }

    private static string ResolveDiscoveryRoot(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ProjectsRoot))
            return config.ProjectsRoot;

#if WINDOWS
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "Projects");
#else
        var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        return Path.Combine(home, "Projects");
#endif
    }

    private List<ProjectItemViewModel> BuildProjectItems(AppConfig config, IReadOnlyList<DiscoveredProject> discovered)
    {
        var hiddenPaths = GetHiddenProjectPathSet(config);
        var context = CreateProjectBuildContext(config);
        var projectSources = CreateProjectSources(discovered, context.RegisteredProjects);

        return projectSources.Count == 0
            ? []
            : [.. projectSources
                .Where(p => IsProjectVisible(p, hiddenPaths))
                .Select(p => CreateProjectItem(p, config, context))];
    }

    private ProjectBuildContext CreateProjectBuildContext(AppConfig config)
    {
        try
        {
            var repo = CreateRepository(config);
            var registeredProjects = repo.GetAllProjects().ToList();
            var projectsByName = registeredProjects
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var latestBackupsByProject = repo.GetLatestBackupsPerProject()
                .GroupBy(b => b.ProjectId)
                .ToDictionary(g => g.Key, g => g.First());

            return new ProjectBuildContext(
                repo,
                registeredProjects,
                projectsByName,
                repo.GetLatestSnapshotInfoByProject(),
                latestBackupsByProject);
        }
        catch (Exception)
        {
            return ProjectBuildContext.Empty;
        }
    }

    private static List<DiscoveredProject> CreateProjectSources(IReadOnlyList<DiscoveredProject> discovered, IReadOnlyList<Project> registeredProjects)
    {
        var projectSources = new List<DiscoveredProject>(discovered);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in discovered)
        {
            var normalized = NormalizeProjectPath(item.Path);
            if (!string.IsNullOrWhiteSpace(normalized))
                seenPaths.Add(normalized);
        }

        foreach (var project in registeredProjects)
        {
            AddRegisteredProjectSource(projectSources, seenPaths, project);
        }

        return projectSources;
    }

    private static void AddRegisteredProjectSource(List<DiscoveredProject> projectSources, HashSet<string> seenPaths, Project project)
    {
        var rootPath = project.RootPath?.Trim();
        if (string.IsNullOrWhiteSpace(rootPath))
            return;

        var normalizedRoot = NormalizeProjectPath(rootPath);
        if (!string.IsNullOrWhiteSpace(normalizedRoot) && seenPaths.Contains(normalizedRoot))
            return;

        projectSources.Add(new DiscoveredProject(
            string.IsNullOrWhiteSpace(project.Name)
                ? Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                : project.Name,
            rootPath,
            null,
            null));

        if (!string.IsNullOrWhiteSpace(normalizedRoot))
            seenPaths.Add(normalizedRoot);
    }

    private static bool IsProjectVisible(DiscoveredProject project, HashSet<string> hiddenPaths)
    {
        var normalizedPath = NormalizeProjectPath(project.Path);
        return string.IsNullOrWhiteSpace(normalizedPath) || !hiddenPaths.Contains(normalizedPath);
    }

    private ProjectItemViewModel CreateProjectItem(DiscoveredProject source, AppConfig config, ProjectBuildContext context)
    {
        var stats = ResolveProjectStats(source, context, out Project? existingProject);
        var vm = CreateProjectItemViewModel(source, existingProject, stats);

        vm.SetAvatarFromNameAndStore(source.Path, AvatarStore.GetAvatarForProject(source.Path), vm.ExternalId);
        UpdateProjectDestinationDisplay(vm, config);
        UpdateProjectEncryptionDisplay(vm, config);
        UpdateProjectPresetDisplay(vm);
        SetProjectGroupOption(vm);
        vm.PropertyChanged += OnProjectItemPropertyChanged;

        PopulateProjectSnapshots(vm, source);
        vm.IsRegistered = existingProject is not null;
        vm.IsAutoBackupEnabled = existingProject is null || !_autoBackupDisabledProjectIds.Contains(existingProject.Id);
        if (!vm.IsRegistered)
            vm.SnapshotHistoryLoaded = true;

        ApplyProjectHealth(vm, stats.LastSnapshotTime, vm.IsRegistered);
        ApplyRequiredPreset(vm, existingProject, context.Repository);
        UpdateProjectPresetRecommendation(vm);
        return vm;
    }

    private ProjectStats ResolveProjectStats(DiscoveredProject source, ProjectBuildContext context, out Project? existingProject)
    {
        existingProject = null;
        var stats = new ProjectStats(source.LastSnapshotTime, source.LastSnapshotSizeBytes);
        if (context.Repository is null)
            return stats;

        try
        {
            context.ProjectsByName.TryGetValue(source.Name, out existingProject);
            return existingProject is null
                ? stats
                : stats.WithRepositoryData(existingProject.Id, context.LatestSnapshotsByProject, context.LatestBackupsByProject);
        }
        catch (Exception)
        {
            return stats;
        }
    }

    private static ProjectItemViewModel CreateProjectItemViewModel(DiscoveredProject source, Project? existingProject, ProjectStats stats)
    {
        return new ProjectItemViewModel
        {
            Name = source.Name,
            Path = source.Path,
            ProjectId = existingProject?.Id ?? 0,
            ExternalId = existingProject?.ExternalId ?? string.Empty,
            LastSnapshot = stats.LastSnapshotTime ?? default,
            SizeBytes = stats.LastSnapshotBytes ?? 0,
            Preset = existingProject?.Preset ?? string.Empty,
            TagsCsv = existingProject?.Tags ?? string.Empty,
            GroupId = existingProject?.GroupId ?? ProjectGroupOption.UngroupedId,
            PreferredDestinationId = existingProject?.PreferredDestinationId ?? string.Empty,
            EncryptionPolicy = ProjectEncryptionPolicy.Normalize(existingProject?.EncryptionPolicy),
            EncryptionKeyRef = existingProject?.EncryptionKeyRef ?? string.Empty
        };
    }

    private static void PopulateProjectSnapshots(ProjectItemViewModel vm, DiscoveredProject source)
    {
        if (source.LastSnapshotTime.HasValue && source.LastSnapshotSizeBytes.HasValue)
        {
            vm.SetSnapshots([new ProjectSnapshotViewModel(source.LastSnapshotTime.Value, source.LastSnapshotSizeBytes.Value)]);
            vm.SnapshotHistoryLoaded = true;
            return;
        }

        vm.SetSnapshots([]);
    }

    private void ApplyRequiredPreset(ProjectItemViewModel vm, Project? existingProject, SqliteRepository? repo)
    {
        var resolvedPreset = ResolveRequiredPreset(vm);
        if (string.Equals(vm.Preset, resolvedPreset, StringComparison.Ordinal))
            return;

        vm.Preset = resolvedPreset;
        if (existingProject is null || repo is null)
            return;

        try
        {
            repo.UpdateProjectPreset(existingProject.Id, resolvedPreset);
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Record($"Project preset fallback persist failed for '{vm.Name}': {ex.GetType().Name} - {ex.Message}");
        }
    }

    private void RefreshDestinationOptionsInternal(AppConfig config)
    {
        DestinationOptions.Clear();

        DestinationOptions.Add(new DestinationOption(
            string.Empty,
            L("Projects.Destination.Auto", "Auto (active destinations)")));

        DestinationOptions.Add(new DestinationOption(
            Project.DestinationAllId,
            L("Projects.Destination.All", "All destinations")));

        if (config.Backups.UseAdvancedDestinations && config.Backups.Destinations is { Count: > 0 })
        {
            foreach (var dest in config.Backups.Destinations)
            {
                var label = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias;
                if (!dest.Active)
                {
                    var suffix = L("Projects.Destination.InactiveSuffix", " (inactive)");
                    label = $"{label}{suffix}";
                }

                var id = DestinationIdentityService.GetId(dest);
                DestinationOptions.Add(new DestinationOption(id, label));
            }
        }
    }

    public void RefreshDestinationOptions(AppConfig config)
    {
        RefreshDestinationOptionsInternal(config);
        foreach (var project in Projects)
        {
            UpdateProjectDestinationDisplay(project, config);
            UpdateProjectEncryptionDisplay(project, config);
        }
    }

    private void RefreshEncryptionPolicyOptions()
    {
        EncryptionPolicyOptions.Clear();
        EncryptionPolicyOptions.Add(new EncryptionPolicyOption(
            ProjectEncryptionPolicy.Inherit,
            L("Projects.EncryptionPolicy.Inherit", "Inherit global")));
        EncryptionPolicyOptions.Add(new EncryptionPolicyOption(
            ProjectEncryptionPolicy.Encrypted,
            L("Projects.EncryptionPolicy.Encrypted", "Encrypted")));
        EncryptionPolicyOptions.Add(new EncryptionPolicyOption(
            ProjectEncryptionPolicy.Plain,
            L("Projects.EncryptionPolicy.Plain", "Plain")));
    }

    private void UpdateProjectDestinationDisplay(ProjectItemViewModel vm, AppConfig config)
    {
        var id = DestinationIdentityService.NormalizePreferredDestinationId(vm.PreferredDestinationId, config.Backups.Destinations);
        if (string.IsNullOrWhiteSpace(id))
        {
            vm.PreferredDestinationDisplay = L("Projects.Destination.Auto", "Auto (active destinations)");
            vm.SetPreferredDestinationOption(DestinationOptions.FirstOrDefault(o => string.IsNullOrWhiteSpace(o.Id)));
            return;
        }

        if (string.Equals(id, Project.DestinationAllId, StringComparison.OrdinalIgnoreCase))
        {
            vm.PreferredDestinationDisplay = L("Projects.Destination.All", "All destinations");
            vm.SetPreferredDestinationOption(DestinationOptions.FirstOrDefault(o =>
                string.Equals(o.Id, Project.DestinationAllId, StringComparison.OrdinalIgnoreCase)));
            return;
        }

        var match = DestinationIdentityService.FindByPreferredDestinationId(config.Backups.Destinations, id);

        if (match != null)
        {
            vm.PreferredDestinationDisplay = string.IsNullOrWhiteSpace(match.Alias)
                ? match.Path
                : match.Alias;
        }
        else
        {
            vm.PreferredDestinationDisplay = id;
        }

        if (!string.Equals(vm.PreferredDestinationId, id, StringComparison.OrdinalIgnoreCase))
        {
            vm.PreferredDestinationId = id;
        }

        var optionMatch = DestinationOptions.FirstOrDefault(o =>
            string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
        if (optionMatch is null)
        {
            var fallback = DestinationOptions.FirstOrDefault(o => string.IsNullOrWhiteSpace(o.Id))
                           ?? DestinationOptions.FirstOrDefault();
            if (fallback != null)
            {
                vm.PreferredDestinationDisplay = fallback.Label;
                vm.SetPreferredDestinationOption(fallback);
                return;
            }
        }

        vm.SetPreferredDestinationOption(optionMatch);
    }

    private void UpdateProjectEncryptionDisplay(ProjectItemViewModel vm, AppConfig config)
    {
        vm.EncryptionPolicy = ProjectEncryptionPolicy.Normalize(vm.EncryptionPolicy);
        var optionMatch = EncryptionPolicyOptions.FirstOrDefault(o =>
            string.Equals(o.Id, vm.EncryptionPolicy, StringComparison.OrdinalIgnoreCase));
        vm.SetEncryptionPolicyOption(optionMatch ?? EncryptionPolicyOptions.FirstOrDefault());

        var effectiveEncrypted = ProjectEncryptionPolicy.IsEncrypted(
            vm.EncryptionPolicy,
            config.Backups.Encryption.Enabled);

        vm.EffectiveEncryptionDisplay = effectiveEncrypted
            ? L("Projects.EncryptionPolicy.EffectiveEncrypted", "Effective: Encrypted")
            : L("Projects.EncryptionPolicy.EffectivePlain", "Effective: Plain");

        bool hasSecret = CredentialVault.Instance.HasStoredSecret(
            string.IsNullOrWhiteSpace(vm.EncryptionKeyRef) ? null : vm.EncryptionKeyRef);
        vm.HasEncryptionSecret = hasSecret;
        vm.EncryptionSecretStatus = hasSecret
            ? L("Settings.Encryption.SecretStatusAvailable", "Password is enrolled in secure storage.")
            : L("Settings.Encryption.SecretStatusMissing", "No encryption password enrolled yet.");

        if (effectiveEncrypted && hasSecret)
        {
            vm.EncryptionBadgeText = L("Projects.EncryptionBadge.Protected", "Password protected");
            vm.EncryptionBadgeBackground = "#1F5A44";
            vm.EncryptionBadgeForeground = "#D6FFEB";
        }
        else if (effectiveEncrypted)
        {
            vm.EncryptionBadgeText = L("Projects.EncryptionBadge.MissingPassword", "Protection missing password");
            vm.EncryptionBadgeBackground = "#6A4A20";
            vm.EncryptionBadgeForeground = "#FFE7BE";
        }
        else
        {
            vm.EncryptionBadgeText = L("Projects.EncryptionBadge.NotProtected", "Not protected");
            vm.EncryptionBadgeBackground = NeutralBadgeBackground;
            vm.EncryptionBadgeForeground = "#C7D2FE";
        }
    }

    private void SortProjects()
    {
        if (Projects.Count <= 1)
            return;

        ApplyFilterAndSort();
    }

    private void ToggleSortMode()
    {
        SortMode = SortMode == ProjectSortMode.LastSnapshot
            ? ProjectSortMode.Name
            : ProjectSortMode.LastSnapshot;
    }

    private static bool MatchesSearchTerms(ProjectItemViewModel project, IEnumerable<string> terms)
    {
        return terms.All(term =>
            (!string.IsNullOrEmpty(project.Name) && project.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(project.Path) && project.Path.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(project.TagsDisplay) && project.TagsDisplay.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
            project.TagChips.Any(tag =>
                !string.IsNullOrWhiteSpace(tag.Value) &&
                tag.Value.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private void ApplyFilterAndSort(bool autoSelectIfNone = true)
    {
        var newList = SortProjectItems(GetFilteredProjects()).ToList();
        SyncProjectsCollection(newList);
        RestoreProjectSelection(autoSelectIfNone);

        OnPropertiesChanged(
            nameof(HasProjects),
            nameof(ShowProjectsEmptyState),
            nameof(HasSelectedProject),
            nameof(ShowSelectedProjectEmptyState));
        RebuildProjectFolders(newList);
    }

    private IEnumerable<ProjectItemViewModel> GetFilteredProjects()
    {
        IEnumerable<ProjectItemViewModel> filtered = _allProjects;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var terms = SearchText
                .Split((char[])null!, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            filtered = filtered.Where(p => MatchesSearchTerms(p, terms));
        }

        return filtered;
    }

    private IOrderedEnumerable<ProjectItemViewModel> SortProjectItems(IEnumerable<ProjectItemViewModel> projects)
    {
        return SortMode switch
        {
            ProjectSortMode.LastSnapshot => projects
                .OrderByDescending(p => p.LastSnapshot == default ? DateTime.MinValue : p.LastSnapshot)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            _ => projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        };
    }

    private void SyncProjectsCollection(IReadOnlyList<ProjectItemViewModel> newList)
    {
        Projects.SyncWith(newList);
    }

    private void RestoreProjectSelection(bool autoSelectIfNone)
    {
        if (SelectedProject != null && !Projects.Contains(SelectedProject))
        {
            SelectedProject = autoSelectIfNone && Projects.Count > 0 ? Projects[0] : null;
            return;
        }

        if (SelectedProject != null)
            return;

        var restore = !string.IsNullOrWhiteSpace(_lastSelectedProjectName)
            ? Projects.FirstOrDefault(p => string.Equals(p.Name, _lastSelectedProjectName, StringComparison.OrdinalIgnoreCase))
            : null;

        if (restore is not null)
        {
            SelectedProject = restore;
            return;
        }

        if (autoSelectIfNone && Projects.Count > 0)
            SelectedProject = Projects[0];
    }


    private void RefreshSelectedProjectTags()
    {
        SelectedProjectTags.Clear();
        ProjectTagInput = string.Empty;
        IsProjectTagColorEditorOpen = false;
        if (SelectedProject is null)
            return;

        var config = ProjectTagAppearance.TryLoadConfig();
        foreach (var tag in ParseTags(SelectedProject.TagsCsv))
            SelectedProjectTags.Add(ProjectTagChip.Create(tag, config));
    }

    private void RefreshReusableProjectTags()
    {
        var allTags = DefaultReusableTags
            .Concat(_allProjects
            .SelectMany(p => ParseTags(p.TagsCsv))
            )
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ReusableProjectTags.Clear();
        var config = ProjectTagAppearance.TryLoadConfig();
        foreach (var tag in allTags)
            ReusableProjectTags.Add(ProjectTagChip.Create(tag, config));
    }

    private void ConsumeProjectTagInputDelimiters()
    {
        if (SelectedProject is null)
            return;

        var input = ProjectTagInput;
        if (string.IsNullOrWhiteSpace(input))
            return;

        var separators = new[] { ',', '\n', '\r', ';' };
        if (input.IndexOfAny(separators) < 0)
            return;

        var trailingDelimiter = separators.Contains(input[^1]);
        var parts = input.Split(separators, StringSplitOptions.None);
        var completeCount = trailingDelimiter ? parts.Length : Math.Max(parts.Length - 1, 0);
        var changed = false;

        for (var i = 0; i < completeCount; i++)
        {
            var token = parts[i].Trim();
            if (TryAddTagChip(token))
                changed = true;
        }

        var remainder = trailingDelimiter ? string.Empty : parts.LastOrDefault()?.Trim() ?? string.Empty;
        if (!string.Equals(ProjectTagInput, remainder, StringComparison.Ordinal))
            ProjectTagInput = remainder;

        if (changed)
            SyncSelectedProjectTagsToProject();
    }

    private void CommitProjectTagInput()
    {
        if (SelectedProject is null)
            return;

        ConsumeProjectTagInputDelimiters();
        var token = (ProjectTagInput ?? string.Empty).Trim();
        var added = TryAddTagChip(token);
        if (!string.IsNullOrWhiteSpace(ProjectTagInput))
            ProjectTagInput = string.Empty;

        if (!added)
            return;

        SyncSelectedProjectTagsToProject();
    }

    public void BeginEditProjectTag(string? tag)
    {
        if (SelectedProject is null || string.IsNullOrWhiteSpace(tag))
            return;

        RemoveProjectTag(tag, sync: false);
        ProjectTagInput = tag.Trim();
        IsProjectTagColorEditorOpen = true;
        SyncProjectTagColorDraft(tag.Trim());
    }

    private void RemoveProjectTag(string? tag)
    {
        RemoveProjectTag(tag, sync: true);
    }

    private void RemoveProjectTag(string? tag, bool sync)
    {
        if (SelectedProject is null || string.IsNullOrWhiteSpace(tag))
            return;

        var existing = SelectedProjectTags.FirstOrDefault(t =>
            string.Equals(t.Value, tag, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return;

        SelectedProjectTags.Remove(existing);
        if (sync)
            SyncSelectedProjectTagsToProject();
    }

    private bool TryAddTagChip(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        token = NormalizeTag(token);
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (SelectedProjectTags.Any(t => string.Equals(t.Value, token, StringComparison.OrdinalIgnoreCase)))
            return false;

        SelectedProjectTags.Add(ProjectTagChip.Create(token, ProjectTagAppearance.TryLoadConfig()));
        return true;
    }

    private void SyncSelectedProjectTagsToProject()
    {
        if (SelectedProject is null)
            return;

        var csv = string.Join(", ", SelectedProjectTags
            .Select(t => NormalizeTag(t.Value))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase));

        if (!string.Equals(SelectedProject.TagsCsv, csv, StringComparison.Ordinal))
            SelectedProject.TagsCsv = csv;
    }

    private void AddExistingTagToSelectedProject(string? tag)
    {
        var token = string.IsNullOrWhiteSpace(tag) ? ProjectTagInput : tag;
        if (SelectedProject is null || string.IsNullOrWhiteSpace(token))
            return;

        var normalized = NormalizeTag(token);
        if (TryAddTagChip(normalized))
            SyncSelectedProjectTagsToProject();

        if (string.IsNullOrWhiteSpace(tag) ||
            string.Equals((ProjectTagInput ?? string.Empty).Trim(), normalized, StringComparison.OrdinalIgnoreCase))
        {
            ProjectTagInput = string.Empty;
        }
    }

    private void ToggleProjectTagColorEditor()
    {
        if (!CanEditProjectTagColor)
            return;

        IsProjectTagColorEditorOpen = !IsProjectTagColorEditorOpen;
        if (IsProjectTagColorEditorOpen)
            SyncProjectTagColorDraftFromInput();
    }

    private void ApplyProjectTagColor()
    {
        var tag = ProjectTagColorTarget;
        if (!CanEditProjectTagColor || string.IsNullOrWhiteSpace(tag))
            return;

        var cfg = _configStore.Load();
        cfg.Appearance.TagColors ??= new Dictionary<string, TagColorConfig>(StringComparer.OrdinalIgnoreCase);
        cfg.Appearance.TagColors[tag] = ProjectTagAppearance.BuildConfigFromAccent(ProjectTagColorHex);
        _configStore.Save(cfg);
        RefreshProjectTagAppearance(cfg);
        ShowNotification(Lf("Projects.Tags.ColorApplied", "Saved custom color for tag '{0}'.", tag));
    }

    private void ResetProjectTagColor()
    {
        var tag = ProjectTagColorTarget;
        if (!CanEditProjectTagColor || string.IsNullOrWhiteSpace(tag))
            return;

        var (background, _, _) = ProjectTagChip.GetDefaultPalette(tag);
        ProjectTagColorHex = background;

        var cfg = _configStore.Load();
        cfg.Appearance.TagColors ??= new Dictionary<string, TagColorConfig>(StringComparer.OrdinalIgnoreCase);
        cfg.Appearance.TagColors.Remove(tag);
        _configStore.Save(cfg);
        RefreshProjectTagAppearance(cfg);
        ShowNotification(Lf("Projects.Tags.ColorReset", "Reset tag '{0}' to the default palette.", tag));
    }

    private void RefreshProjectTagAppearance(AppConfig? config = null)
    {
        config ??= _configStore.GetSnapshot();
        RefreshSelectedProjectTags();
        RefreshReusableProjectTags();
        foreach (var project in _allProjects)
            project.RefreshTagChips(config);
    }

    private void SyncProjectTagColorDraftFromInput()
    {
        var tag = ProjectTagColorTarget;
        if (string.IsNullOrWhiteSpace(tag))
            return;

        SyncProjectTagColorDraft(tag);
    }

    private void SyncProjectTagColorDraft(string tag)
    {
        var cfg = _configStore.GetSnapshot();
        var accent = ProjectTagAppearance.Resolve(tag, cfg.Appearance.TagColors).Background;

        _projectTagColorSyncing = true;
        try
        {
            _projectTagColorHex = accent;
            OnPropertyChanged(nameof(ProjectTagColorHex));
            if (Color.TryParse(accent, out var parsed))
            {
                _selectedProjectTagColor = parsed;
                OnPropertyChanged(nameof(SelectedProjectTagColor));
            }
            if (ProjectTagAppearance.TryParseRgb(accent, out var red, out var green, out var blue))
            {
                _projectTagColorRed = red;
                _projectTagColorGreen = green;
                _projectTagColorBlue = blue;
                OnPropertiesChanged(
                    nameof(ProjectTagColorRed),
                    nameof(ProjectTagColorGreen),
                    nameof(ProjectTagColorBlue));

                ProjectTagAppearance.RgbToHsv(red, green, blue, out var hue, out var saturation, out var value);
                _projectTagColorHue = hue;
                _projectTagColorSaturation = saturation;
                _projectTagColorValue = value;
                OnPropertiesChanged(
                    nameof(ProjectTagColorHue),
                    nameof(ProjectTagColorSaturation),
                    nameof(ProjectTagColorValue));
            }
        }
        finally
        {
            _projectTagColorSyncing = false;
        }

        RefreshProjectTagColorPreview();
    }

    private void SyncRgbFromHex()
    {
        if (_projectTagColorSyncing)
            return;

        if (!ProjectTagAppearance.TryParseRgb(_projectTagColorHex, out var red, out var green, out var blue))
            return;

        _projectTagColorSyncing = true;
        try
        {
            _projectTagColorRed = red;
            _projectTagColorGreen = green;
            _projectTagColorBlue = blue;
            OnPropertiesChanged(
                nameof(ProjectTagColorRed),
                nameof(ProjectTagColorGreen),
                nameof(ProjectTagColorBlue));

            ProjectTagAppearance.RgbToHsv(red, green, blue, out var hue, out var saturation, out var value);
            _projectTagColorHue = hue;
            _projectTagColorSaturation = saturation;
            _projectTagColorValue = value;
            OnPropertiesChanged(
                nameof(ProjectTagColorHue),
                nameof(ProjectTagColorSaturation),
                nameof(ProjectTagColorValue));
        }
        finally
        {
            _projectTagColorSyncing = false;
        }
    }

    private void SyncHexFromRgb()
    {
        if (_projectTagColorSyncing)
            return;

        _projectTagColorSyncing = true;
        try
        {
            _projectTagColorHex = ProjectTagAppearance.FormatHex(
                (byte)Math.Round(_projectTagColorRed),
                (byte)Math.Round(_projectTagColorGreen),
                (byte)Math.Round(_projectTagColorBlue));
            OnPropertyChanged(nameof(ProjectTagColorHex));
        }
        finally
        {
            _projectTagColorSyncing = false;
        }

        RefreshProjectTagColorPreview();
    }

    private void SyncHexFromHsv()
    {
        if (_projectTagColorSyncing)
            return;

        _projectTagColorSyncing = true;
        try
        {
            _projectTagColorHex = ProjectTagAppearance.HsvToHex(
                _projectTagColorHue,
                _projectTagColorSaturation,
                _projectTagColorValue);
            OnPropertyChanged(nameof(ProjectTagColorHex));
        }
        finally
        {
            _projectTagColorSyncing = false;
        }

        SyncRgbFromHex();
        RefreshProjectTagColorPreview();
    }

    private void RefreshProjectTagColorPreview()
    {
        OnPropertiesChanged(
            nameof(ProjectTagColorPreviewBackground),
            nameof(ProjectTagColorPreviewForeground),
            nameof(ProjectTagColorPreviewBorder));
    }

    private void ApplyProjectTagColorSwatch(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return;

        ProjectTagColorHex = hex.Trim();
    }

    private static List<string> ParseTags(string? tagsCsv)
    {
        if (string.IsNullOrWhiteSpace(tagsCsv))
            return [];

        return [.. tagsCsv
            .Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return string.Empty;

        return string.Join(" ", tag
            .Trim()
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }


    private string? DetectPreset(string projectPath)
    {
        return DetectPresetRecommendation(projectPath)?.PresetId;
    }

    private string ResolveRequiredPreset(ProjectItemViewModel project, string? presetOverride = null)
    {
        var current = presetOverride?.Trim() ?? project.Preset?.Trim() ?? string.Empty;
        var recommended = DetectPreset(project.Path);
        if (!string.IsNullOrWhiteSpace(recommended) &&
            (string.IsNullOrWhiteSpace(current) ||
             string.Equals(current, NoPresetId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(current, GenericPresetId, StringComparison.OrdinalIgnoreCase)))
        {
            return recommended;
        }

        if (!string.IsNullOrWhiteSpace(current) &&
            !string.Equals(current, NoPresetId, StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        return ResolveGenericPreset();
    }

    private string ResolveGenericPreset()
    {
        return PresetAvailable(GenericPresetId)
               ?? PresetAvailable("documents")
               ?? AvailablePresets.FirstOrDefault(p => !string.Equals(p, NoPresetId, StringComparison.OrdinalIgnoreCase))
               ?? NoPresetId;
    }

    private string? PresetAvailable(string presetName)
    {
        return AvailablePresets.Any(p => p.Equals(presetName, StringComparison.OrdinalIgnoreCase))
            ? presetName
            : null;
    }

    private void ApplyPresetRecommendation()
    {
        var project = SelectedProject;
        if (project is null || string.IsNullOrWhiteSpace(project.RecommendedPreset))
            return;

        project.Preset = project.RecommendedPreset;
        ShowNotification(
            Lf("Projects.Preset.Recommendation.Applied", "Applied recommended preset '{0}'.", project.RecommendedPreset),
            NotificationSeverity.Info);
        _applyPresetRecommendationCommand.RaiseCanExecuteChanged();
    }

    private void UpdateProjectPresetRecommendation(ProjectItemViewModel? vm)
    {
        if (vm is null || string.IsNullOrWhiteSpace(vm.Path))
            return;

        if (!_presetRecommendationCache.TryGetValue(vm.Path, out var recommendation))
        {
            recommendation = DetectPresetRecommendation(vm.Path);
            _presetRecommendationCache[vm.Path] = recommendation;
        }

        if (recommendation is null ||
            string.Equals(vm.Preset, recommendation.PresetId, StringComparison.OrdinalIgnoreCase))
        {
            vm.RecommendedPreset = string.Empty;
            vm.RecommendedPresetReason = string.Empty;
        }
        else
        {
            vm.RecommendedPreset = recommendation.PresetId;
            vm.RecommendedPresetReason = recommendation.Reason;
        }

        if (ReferenceEquals(vm, SelectedProject))
            _applyPresetRecommendationCommand.RaiseCanExecuteChanged();
    }

    private PresetRecommendation? DetectPresetRecommendation(string projectPath)
    {
        var probe = new ProjectPathProbe(projectPath);
        foreach (var rule in GetPresetRecommendationRules())
        {
            if (rule.Matches(probe))
                return BuildPresetRecommendation(rule);
        }

        return null;
    }

    private PresetRecommendation? BuildPresetRecommendation(PresetRecommendationRule rule)
    {
        var availablePreset = PresetAvailable(rule.PresetName);
        return string.IsNullOrWhiteSpace(availablePreset)
            ? null
            : new PresetRecommendation(availablePreset, L(rule.ReasonKey, rule.ReasonFallback));
    }

    private static IEnumerable<PresetRecommendationRule> GetPresetRecommendationRules()
    {
        yield return new("unity", "Projects.Preset.Recommendation.Reason.Unity", "Detected Unity project layout (Assets + ProjectSettings).",
            probe => probe.HasDir("Assets") && probe.HasDir("ProjectSettings"));
        yield return new("godot", "Projects.Preset.Recommendation.Reason.Godot", "Detected Godot project marker (project.godot).",
            probe => probe.Has("project.godot"));
        yield return new("unreal", "Projects.Preset.Recommendation.Reason.Unreal", "Detected Unreal project file (*.uproject).",
            probe => probe.HasAny("*.uproject"));
        yield return new("rust", "Projects.Preset.Recommendation.Reason.Rust", "Detected Rust project marker (Cargo.toml).",
            probe => probe.Has("Cargo.toml"));
        yield return new("node", "Projects.Preset.Recommendation.Reason.Node", "Detected JavaScript/Node project markers (package.json + lock/build config).",
            probe => probe.Has("package.json") && probe.HasAnyPath("package-lock.json", "yarn.lock", "pnpm-lock.yaml", "tsconfig.json", "vite.config.ts", "vite.config.js"));
        yield return new("python", "Projects.Preset.Recommendation.Reason.Python", "Detected Python project markers (dependency file + project config).",
            probe => (probe.Has("pyproject.toml") || probe.Has("requirements.txt")) && probe.HasAnyPath("setup.py", "poetry.lock", "Pipfile", "tox.ini"));
        yield return new("avalonia", "Projects.Preset.Recommendation.Reason.Avalonia", "Detected Avalonia UI files (*.axaml).",
            probe => probe.HasAny("*.axaml"));
        yield return new("dotnet", "Projects.Preset.Recommendation.Reason.DotNet", "Detected .NET solution/project files (*.sln/*.csproj).",
            probe => (probe.HasAny("*.csproj") || probe.HasAny("*.sln")) && (probe.HasAnyPath("global.json", "Directory.Build.props", "Directory.Packages.props") || probe.HasAny("*.cs")));
        yield return new("blender", "Projects.Preset.Recommendation.Reason.Blender", "Detected Blender files (*.blend).",
            probe => probe.HasAny("*.blend"));
        yield return new(VideoPresetId, "Projects.Preset.Recommendation.Reason.Video", "Detected video editing project files (*.prproj).",
            probe => probe.HasAny("*.prproj"));
    }


    private void OpenFolder()
    {
        if (SelectedProject is null)
            return;

        var path = SelectedProject.Path;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            SystemFileLauncher.OpenPath(path);
        }
        catch (Exception)
        {
            ShowNotification(Lf("Projects.Notification.OpenFolderFailed", "Failed to open folder for '{0}'.", SelectedProject?.Name ?? string.Empty), NotificationSeverity.Error);
        }
    }

    private void BeginRemoveProjectPreview()
    {
        ProjectItemViewModel? project = SelectedProject;
        if (project is null)
            return;

        RemoveProjectPreviewTitle = Lf(
            "Projects.Remove.PreviewTitle",
            "Remove {0} from VaultSync?",
            project.Name);
        RemoveProjectPreviewDetail = L(
            "Projects.Remove.PreviewLoading",
            "Checking indexed backups and stored data...");
        IsRemoveProjectPreviewOpen = true;

        _ = Task.Run(() => BuildRemoveProjectPreview(project.Name)).ContinueWith(
            task => Dispatcher.UIThread.Post(() =>
            {
                if (!IsRemoveProjectPreviewOpen ||
                    SelectedProject is null ||
                    !string.Equals(SelectedProject.Name, project.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                RemoveProjectPreviewDetail = task.IsCompletedSuccessfully
                    ? task.Result
                    : L(
                        "Projects.Remove.PreviewFallback",
                        "The project registration and local history index will be removed. Stored backup files are kept unless you delete them from Backups first.");
            }),
            TaskScheduler.Default);
    }

    private string BuildRemoveProjectPreview(string projectName)
    {
        AppConfig config = _configStore.GetSnapshot();
        SqliteRepository repo = CreateRepository(config);
        Project? project = repo.GetProjectByName(projectName);
        if (project is null)
        {
            return L(
                "Projects.Remove.PreviewUnregistered",
                "This discovered folder will be hidden from Projects. No source files will be deleted.");
        }

        List<Backup> backups = repo.GetBackupsForProject(project.Id).ToList();
        long bytes = backups.Sum(backup => Math.Max(0, backup.TotalBytes));
        return Lf(
            "Projects.Remove.PreviewDetail",
            "VaultSync will remove the registration and local history index for {0} backup(s) ({1}). Stored backup files remain on their destinations. Review and delete them from Backups first if that is your intent. Source files are never deleted.",
            backups.Count,
            UiFormat.FormatBytes(bytes, "0.#"));
    }

    private void RemoveProject()
    {
        if (SelectedProject is null)
            return;

        IsRemoveProjectPreviewOpen = false;

        var removedProjectName = SelectedProject.Name;
        var removedProjectPath = SelectedProject.Path;

        _ = Task.Run(() =>
        {
            try
            {
                var config = _configStore.GetSnapshot();
                var repo = CreateRepository(config);
                var existing = repo.GetProjectByName(removedProjectName);
                if (existing is null)
                {
                    HideProjectPathInConfig(removedProjectPath);
                    Dispatcher.UIThread.Post(() => ShowNotification(
                        Lf("Projects.Notification.RemoveMissing", "Project '{0}' was not registered in the backup database.", removedProjectName),
                        NotificationSeverity.Warning));
                }
                else
                {
                    var projectExternalId = existing.ExternalId;
                    if (string.IsNullOrWhiteSpace(projectExternalId))
                    {
                        projectExternalId = Guid.NewGuid().ToString("N");
                        repo.UpdateProjectExternalId(existing.Id, projectExternalId);
                    }

                    repo.RemoveProject(existing.Id);
                    HideProjectPathInConfig(removedProjectPath);
                    ProjectRemovedFromDatabase?.Invoke(existing.Id, projectExternalId);
                    Dispatcher.UIThread.Post(() =>
                        ShowNotification(Lf("Projects.Notification.RemoveSuccess", "Removed project '{0}' from the backup database.", removedProjectName), NotificationSeverity.Info));
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (SelectedProject is not null &&
                        string.Equals(SelectedProject.Name, removedProjectName, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectedProject.LastSnapshot = default;
                        SelectedProject.SizeBytes = 0;
                        SelectedProject.SetSnapshots([]);
                        SelectedProject.Health = ProjectHealthStatus.OutOfDate;
                        SelectedProject.HealthTag = L("Projects.Health.NotBackedUp", "Not backed up");
                        SelectedProject.IsRegistered = false;
                    }

                    RemoveProjectFromCurrentList(removedProjectPath);
                    DetachedTask.Run(
                        () => RefreshAsync(forceDiscovery: false),
                        "refresh-projects-after-removal");
                });
            }
            catch (Exception)
            {
                Dispatcher.UIThread.Post(() =>
                    ShowNotification(Lf("Projects.Notification.RemoveFailed", "Failed to remove project '{0}' from the backup database.", removedProjectName), NotificationSeverity.Error));
            }
        });

    }

    private void TakeSnapshot()
    {
        _ = DetachedTask.RunAsync(TakeSnapshotCoreAsync, nameof(TakeSnapshotCoreAsync));
    }

    private async Task TakeSnapshotCoreAsync()
    {
        var selectedProject = SelectedProject;
        if (selectedProject is null)
            return;

        try
        {
            var config = await Task.Run(_configStore.GetSnapshot);
            var repo = CreateRepository(config);

            var existing = repo.GetProjectByName(selectedProject.Name);
            if (existing is null)
            {
                RegisterProjectForSnapshots(repo, selectedProject);
                return;
            }

            ShowRestoreWarningIfNeeded(config, existing);
            await CreateSnapshotForProjectAsync(config, repo, existing);
            ShowSnapshotSuccessIfSelected();
        }
        catch (Exception)
        {
            var msg = L("Projects.Notification.SnapshotFailure", "Snapshot failed. Check logs for details.");
            ShowNotification(msg, NotificationSeverity.Error);
            NotifySnapshotOutcome(msg, success: false);
        }

        // Refresh label/state after the operation.
        RefreshSelectedProjectRegistration();
    }

    private void RegisterProjectForSnapshots(SqliteRepository repo, ProjectItemViewModel selectedProject)
    {
        if (string.IsNullOrWhiteSpace(selectedProject.Preset))
        {
            ShowNotification(L("Projects.Preset.Required", "Please select a preset (or 'no preset') before adding this project."), NotificationSeverity.Error);
            return;
        }

        var project = new Project
        {
            Name = selectedProject.Name,
            RootPath = selectedProject.Path,
            Preset = selectedProject.Preset,
            Tags = selectedProject.TagsCsv,
            CreatedUtc = DateTime.UtcNow,
            PreferredDestinationId = selectedProject.PreferredDestinationId,
            EncryptionPolicy = selectedProject.EncryptionPolicy
        };

        repo.AddProject(project);
        UnhideProjectPathInConfig(project.RootPath);
        ShowNotification(Lf("Projects.Notification.Registered", "Project '{0}' registered. Next click will create a snapshot.", project.Name), NotificationSeverity.Info);

        SnapshotActionLabel = L(DefaultSnapshotActionKey, DefaultSnapshotActionFallback);
        selectedProject.IsRegistered = true;
    }

    private void ShowRestoreWarningIfNeeded(AppConfig config, Project existing)
    {
        if (config.Backups.PromptRestoreAfterImport && existing.NeedsRestore)
        {
            ShowNotification(L("Projects.Notification.RestoreRequired", "Imported history is newer. Consider restoring before creating new snapshots."), NotificationSeverity.Warning);
        }
    }

    private async Task CreateSnapshotForProjectAsync(AppConfig config, SqliteRepository repo, Project existing)
    {
        var hashService = new HashService();
        var snapshotService = new SnapshotService(repo, hashService);

        await snapshotService.CreateSnapshotAsync(
            existing,
            fullHash: config.Backups.UseFullSnapshotHash,
            hashNow: true,
            maxSnapshotsToKeep: config.Backups.MaxSnapshotsPerProject,
            ct: CancellationToken.None,
            progressCallback: null,
            useScanCache: config.Backups.EnableScanCache,
            aggressiveScanCache: config.Backups.AggressiveScanCache);

        if (SnapshotService.LastOutcome != null)
            RefreshSelectedProjectSnapshotStats(repo, existing);
    }

    private void RefreshSelectedProjectSnapshotStats(SqliteRepository repo, Project existing)
    {
        if (SelectedProject is null)
            return;

        try
        {
            var snapshotsFromDb = repo.GetSnapshotsForProject(existing.Name)?.ToList() ?? [];
            ApplySelectedProjectSnapshotStats(snapshotsFromDb);
            SelectedProject.Health = ProjectHealthStatus.Healthy;
            SelectedProject.HealthTag = L("Projects.Health.Healthy", "Healthy");
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Record($"Project snapshot UI refresh failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    private void ApplySelectedProjectSnapshotStats(IReadOnlyList<Snapshot> snapshotsFromDb)
    {
        if (SelectedProject is null)
            return;

        if (snapshotsFromDb.Count == 0)
        {
            SelectedProject.LastSnapshot = default;
            SelectedProject.SizeBytes = 0;
            SelectedProject.SetSnapshots([]);
            return;
        }

        var latest = snapshotsFromDb[0];
        SelectedProject.LastSnapshot = latest.CreatedUtc;
        SelectedProject.SizeBytes = latest.TotalBytes;

        var history = snapshotsFromDb
            .Take(10)
            .Select(CreateProjectSnapshotViewModel);

        SelectedProject.SetSnapshots(history);
    }

    private void ShowSnapshotSuccessIfSelected()
    {
        if (SelectedProject is null)
            return;

        var msg = Lf("Projects.Notification.SnapshotSuccess", "Snapshot created for '{0}'.", SelectedProject.Name);
        ShowNotification(msg, NotificationSeverity.Info);
        NotifySnapshotOutcome(msg, success: true);
    }

    private static string NormalizeProjectPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full;
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static HashSet<string> GetHiddenProjectPathSet(AppConfig config)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = config.Behavior.HiddenProjectPaths ?? [];
        foreach (var value in values)
        {
            var normalized = NormalizeProjectPath(value);
            if (!string.IsNullOrWhiteSpace(normalized))
                set.Add(normalized);
        }

        return set;
    }

    private void HideProjectPathInConfig(string? projectPath)
    {
        var normalized = NormalizeProjectPath(projectPath);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var cfg = _configStore.Load();
        cfg.Behavior.HiddenProjectPaths ??= [];
        var exists = cfg.Behavior.HiddenProjectPaths
            .Any(path => string.Equals(NormalizeProjectPath(path), normalized, StringComparison.OrdinalIgnoreCase));
        if (exists)
            return;

        cfg.Behavior.HiddenProjectPaths.Add(normalized);
        _configStore.Save(cfg);
    }

    private void UnhideProjectPathInConfig(string? projectPath)
    {
        var normalized = NormalizeProjectPath(projectPath);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var cfg = _configStore.Load();
        cfg.Behavior.HiddenProjectPaths ??= [];
        var originalCount = cfg.Behavior.HiddenProjectPaths.Count;
        cfg.Behavior.HiddenProjectPaths = [.. cfg.Behavior.HiddenProjectPaths.Where(path => !string.Equals(NormalizeProjectPath(path), normalized, StringComparison.OrdinalIgnoreCase))];
        if (cfg.Behavior.HiddenProjectPaths.Count == originalCount)
            return;

        _configStore.Save(cfg);
    }

    private void RemoveProjectFromCurrentList(string? projectPath)
    {
        var normalized = NormalizeProjectPath(projectPath);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var removed = _allProjects.RemoveAll(project =>
            string.Equals(NormalizeProjectPath(project.Path), normalized, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
            return;

        ApplyFilterAndSort();
        if (SelectedProject is not null &&
            string.Equals(NormalizeProjectPath(SelectedProject.Path), normalized, StringComparison.OrdinalIgnoreCase))
        {
            SelectedProject = Projects.FirstOrDefault();
        }
    }

    /// <summary>
    /// Tray helper: create a snapshot for a specific project by name,
    /// reusing the existing TakeSnapshot() pipeline.
    /// </summary>
    public Task TakeSnapshotForProjectFromTrayAsync(string projectName)
    {
        var project = Projects.FirstOrDefault(p => p.Name == projectName);
        if (project is null)
            return Task.CompletedTask;

        var previous = SelectedProject;

        try
        {
            // Temporarily select the project so existing code works.
            SelectedProject = project;

            // Fire the existing snapshot pipeline via detached task wrapper.
            TakeSnapshot();
        }
        finally
        {
            // Restore UI selection so the UI does not jump unexpectedly.
            SelectedProject = previous;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Tray helper: create snapshots for all projects, sequentially,
    /// reusing the existing TakeSnapshot() pipeline.
    /// </summary>
    public Task TakeSnapshotAllFromTrayAsync()
    {
        var previous = SelectedProject;

        try
        {
            foreach (var project in Projects.ToList())
            {
                SelectedProject = project;
                TakeSnapshot();
            }
        }
        finally
        {
            SelectedProject = previous;
        }

        return Task.CompletedTask;
    }

    private void RefreshSelectedProjectRegistration()
    {
        if (SelectedProject is null)
        {
            SnapshotActionLabel = L(DefaultSnapshotActionKey, DefaultSnapshotActionFallback);
            return;
        }

        var refreshToken = Interlocked.Increment(ref _selectedProjectRefreshToken);
        var projectName = SelectedProject.Name;

        _ = RefreshSelectedProjectRegistrationAsync(refreshToken, projectName);
    }

    private async Task RefreshSelectedProjectRegistrationAsync(int refreshToken, string projectName)
    {
        try
        {
            ProjectRegistrationSnapshot snapshot = await Task.Run(() => LoadProjectRegistrationSnapshot(projectName)).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyProjectRegistrationSnapshot(refreshToken, projectName, snapshot));
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Record($"Project registration refresh failed for '{projectName}': {ex.GetType().Name} - {ex.Message}");
        }
    }

    private ProjectRegistrationSnapshot LoadProjectRegistrationSnapshot(string projectName)
    {
        try
        {
            var config = _configStore.GetSnapshot();
            var repo = CreateRepository(config);
            var existing = repo.GetProjectByName(projectName);
            return new ProjectRegistrationSnapshot(
                existing is null,
                existing?.Id ?? 0,
                existing?.Preset ?? string.Empty,
                existing?.Tags ?? string.Empty,
                existing?.GroupId ?? ProjectGroupOption.UngroupedId,
                existing?.PreferredDestinationId ?? string.Empty,
                ProjectEncryptionPolicy.Normalize(existing?.EncryptionPolicy),
                existing?.EncryptionKeyRef ?? string.Empty);
        }
        catch
        {
            return new ProjectRegistrationSnapshot(true, 0, string.Empty, string.Empty, string.Empty, string.Empty, ProjectEncryptionPolicy.Inherit, string.Empty);
        }
    }

    private void ApplyProjectRegistrationSnapshot(int refreshToken, string projectName, ProjectRegistrationSnapshot snapshot)
    {
        if (refreshToken != _selectedProjectRefreshToken)
            return;

        if (SelectedProject is null ||
            !string.Equals(SelectedProject.Name, projectName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Interlocked.Increment(ref _suppressProjectPersistence);
        try
        {
            if (snapshot.Missing)
            {
                SnapshotActionLabel = L("Snapshots.Action.AddProject", "Add project");
                SelectedProject.IsRegistered = false;
                SelectedProject.ProjectId = 0;
                SelectedProject.GroupId = ProjectGroupOption.UngroupedId;
                SetProjectGroupOption(SelectedProject);
                SelectedProject.EncryptionKeyRef = string.Empty;

                SelectedProject.Preset = ResolveRequiredPreset(SelectedProject);
                if (string.IsNullOrWhiteSpace(SelectedProject.TagsCsv))
                {
                    SelectedProject.TagsCsv = string.Empty;
                }
            }
            else
            {
                SnapshotActionLabel = L(DefaultSnapshotActionKey, DefaultSnapshotActionFallback);
                SelectedProject.IsRegistered = true;
                SelectedProject.ProjectId = snapshot.ProjectId;
                SelectedProject.Preset = ResolveRequiredPreset(SelectedProject, snapshot.Preset);
                SelectedProject.TagsCsv = snapshot.TagsCsv;
                SelectedProject.GroupId = snapshot.GroupId;
                SetProjectGroupOption(SelectedProject);
                SelectedProject.PreferredDestinationId = snapshot.PreferredDestinationId;
                SelectedProject.EncryptionPolicy = snapshot.EncryptionPolicy;
                SelectedProject.EncryptionKeyRef = snapshot.EncryptionKeyRef;
                var cfg = _configStore.GetSnapshot();
                SelectedProject.IsAutoBackupEnabled = !cfg.Backups.AutoBackupDisabledProjects.Contains(snapshot.ProjectId);
                UpdateProjectDestinationDisplay(SelectedProject, cfg);
                UpdateProjectEncryptionDisplay(SelectedProject, cfg);
                UpdateProjectPresetDisplay(SelectedProject);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _suppressProjectPersistence);
        }
    }

    private void OnProjectItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ProjectItemViewModel vm)
            return;

        if (string.Equals(e.PropertyName, nameof(ProjectItemViewModel.SelectedGroupOption), StringComparison.Ordinal) &&
            ReferenceEquals(vm, SelectedProject))
        {
            _moveSelectedProjectToFolderCommand.RaiseCanExecuteChanged();
        }

        var change = ProjectItemChange.FromProperty(e.PropertyName);
        if (change.ChangedPreset)
        {
            UpdateProjectPresetDisplay(vm);
            UpdateProjectPresetRecommendation(vm);
            if (ReferenceEquals(vm, SelectedProject))
                LoadPresetEditorForSelectedProject();
        }

        if (change.ChangedRecommendedPreset && ReferenceEquals(vm, SelectedProject))
            _applyPresetRecommendationCommand.RaiseCanExecuteChanged();

        if (!change.ShouldPersist)
            return;

        if (Volatile.Read(ref _suppressProjectPersistence) > 0)
            return;

        try
        {
            var config = _configStore.GetSnapshot();
            var repo = CreateRepository(config);
            var project = repo.GetProjectByName(vm.Name);
            if (project is null)
                return;

            PersistProjectItemChange(change, vm, project, repo, config);

            if (change.ChangedPreset || change.ChangedDestination || change.ChangedTags || change.ChangedGroup)
                ProjectSettingsMetadataChanged?.Invoke(project.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Projects] Failed to persist project settings for '{vm.Name}': {ex.Message}");
        }
    }

    private void PersistProjectItemChange(ProjectItemChange change, ProjectItemViewModel vm, Project project, SqliteRepository repo, AppConfig config)
    {
        if (change.ChangedPreset)
            repo.UpdateProjectPreset(project.Id, vm.Preset);

        if (change.ChangedDestination)
            PersistProjectDestination(vm, project.Id, repo, config);

        if (change.ChangedEncryption)
            PersistProjectEncryption(vm, project.Id, repo, config);

        if (change.ChangedTags)
            PersistProjectTags(vm, project.Id, repo);

        if (change.ChangedGroup)
        {
            repo.SetProjectGroup(project.Id, vm.GroupId);
            ApplyFilterAndSort(autoSelectIfNone: false);
        }

        if (change.ChangedAutoBackup)
            PersistProjectAutoBackup(vm, project.Id, config);
    }

    private void PersistProjectAutoBackup(ProjectItemViewModel vm, int projectId, AppConfig config)
    {
        List<int> disabled = config.Backups.AutoBackupDisabledProjects ?? [];
        config.Backups.AutoBackupDisabledProjects = vm.IsAutoBackupEnabled
            ? [.. disabled.Where(id => id != projectId).Distinct()]
            : [.. disabled.Append(projectId).Distinct()];
        _configStore.Save(config);
        RefreshGroupAutoBackupStateFromConfig(config);
        AutoBackupGroupPreferenceChanged?.Invoke([projectId], vm.IsAutoBackupEnabled);
    }

    private void PersistProjectDestination(ProjectItemViewModel vm, int projectId, SqliteRepository repo, AppConfig config)
    {
        repo.UpdateProjectPreferredDestination(projectId, vm.PreferredDestinationId);
        UpdateProjectDestinationDisplay(vm, config);
    }

    private void PersistProjectEncryption(ProjectItemViewModel vm, int projectId, SqliteRepository repo, AppConfig config)
    {
        repo.UpdateProjectEncryptionSettings(
            projectId,
            vm.EncryptionPolicy,
            string.IsNullOrWhiteSpace(vm.EncryptionKeyRef) ? null : vm.EncryptionKeyRef);
        UpdateProjectEncryptionDisplay(vm, config);
        ProjectEncryptionPolicyChanged?.Invoke(projectId, vm.EncryptionPolicy);
    }

    private void PersistProjectTags(ProjectItemViewModel vm, int projectId, SqliteRepository repo)
    {
        repo.UpdateProjectTags(projectId, vm.TagsCsv);
        RefreshReusableProjectTags();
        ApplyFilterAndSort(autoSelectIfNone: false);
        if (ReferenceEquals(vm, SelectedProject))
            RefreshSelectedProjectTags();
    }

    private void LoadSnapshotHistoryForSelectedProject()
    {
        if (SelectedProject is null || !SelectedProject.IsRegistered || SelectedProject.SnapshotHistoryLoaded)
            return;

        var refreshToken = Interlocked.Increment(ref _selectedProjectHistoryToken);
        var projectName = SelectedProject.Name;

        _ = LoadSnapshotHistoryForSelectedProjectAsync(refreshToken, projectName);
    }

    private async Task LoadSnapshotHistoryForSelectedProjectAsync(int refreshToken, string projectName)
    {
        try
        {
            List<ProjectSnapshotViewModel> history = await Task.Run(async () =>
            {
                try
                {
                    var config = _configStore.GetSnapshot();
                    var repo = CreateRepository(config);
                    var snapshots = await repo.GetSnapshotsForProjectAsync(projectName);
                    return snapshots
                        .Take(40)
                        .Select(CreateProjectSnapshotViewModel)
                        .ToList();
                }
                catch
                {
                    return [];
                }
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() => ApplySnapshotHistory(refreshToken, projectName, history));
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Record($"Project snapshot history load failed for '{projectName}': {ex.GetType().Name} - {ex.Message}");
        }
    }

    private void ApplySnapshotHistory(int refreshToken, string projectName, List<ProjectSnapshotViewModel> history)
    {
        if (refreshToken != _selectedProjectHistoryToken)
            return;

        if (SelectedProject is null ||
            !string.Equals(SelectedProject.Name, projectName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (history.Count > 0)
        {
            var latest = history[0];
            if (SelectedProject.LastSnapshot == default ||
                latest.Timestamp > SelectedProject.LastSnapshot)
            {
                SelectedProject.LastSnapshot = latest.Timestamp;
                SelectedProject.SizeBytes = latest.SizeBytes;
            }
        }

        SelectedProject.SetSnapshots(history);
        SelectedProject.SnapshotHistoryLoaded = true;
        ApplyProjectHealth(
            SelectedProject,
            SelectedProject.LastSnapshot == default ? null : SelectedProject.LastSnapshot,
            SelectedProject.IsRegistered);
    }

    private static void ApplyProjectHealth(ProjectItemViewModel vm, DateTime? lastSnapshotTime, bool isRegistered)
    {
        if (lastSnapshotTime.HasValue)
        {
            var age = DateTime.UtcNow - lastSnapshotTime.Value;

            if (age.TotalDays < 1)
            {
                vm.Health = ProjectHealthStatus.Healthy;
                vm.HealthTag = L("Projects.Health.HealthyRecent", "Healthy (<1d)");
            }
            else if (age.TotalDays < 7)
            {
                vm.Health = ProjectHealthStatus.Warning;
                vm.HealthTag = L("Projects.Health.OutOfDateShort", "Out of date (>1d)");
            }
            else
            {
                vm.Health = ProjectHealthStatus.OutOfDate;
                vm.HealthTag = L("Projects.Health.Stale", "Stale (>7d)");
            }

            return;
        }

        vm.Health = ProjectHealthStatus.OutOfDate;
        vm.HealthTag = isRegistered
            ? L("Projects.Health.NoSnapshots", "No snapshots yet")
            : L("Projects.Health.NotAdded", "Not added");
    }

    private static ProjectSnapshotViewModel CreateProjectSnapshotViewModel(Snapshot snapshot)
    {
        var topPaths = SnapshotDiffSummary.ParseTopChangedPaths(snapshot.DiffTopPathsJson);
        return new ProjectSnapshotViewModel(
            snapshot.CreatedUtc,
            snapshot.TotalBytes,
            snapshot.DiffAdded,
            snapshot.DiffModified,
            snapshot.DiffDeleted,
            snapshot.DiffNetBytes,
            topPaths);
    }

    public void RefreshLocalization()
    {
        SnapshotActionLabel = SelectedProject is null || SelectedProject.IsRegistered
            ? L(DefaultSnapshotActionKey, DefaultSnapshotActionFallback)
            : L("Snapshots.Action.AddProject", "Add project");
        OnPropertyChanged(nameof(SortModeLabel));
        var config = _configStore.GetSnapshot();
        LoadGroupOptions();
        OnPropertyChanged(nameof(ProjectTagColorToggleLabel));
        RefreshEncryptionPolicyOptions();
        RefreshDestinationOptionsInternal(config);
        foreach (var project in _allProjects)
        {
            UpdateProjectDestinationDisplay(project, config);
            UpdateProjectEncryptionDisplay(project, config);
            UpdateProjectPresetDisplay(project);

        }
        RefreshHealthTags();
        RefreshSnapshotText();
        if (SelectedProject is not null)
        {
            SelectedProject.SnapshotHistoryLoaded = false;
            LoadSnapshotHistoryForSelectedProject();
        }
        OnPropertyChanged(nameof(SelectedProject));
        OnPropertyChanged(string.Empty);
    }

    private void RequestProjectEncryptionPasswordEdit(ProjectItemViewModel? project)
    {
        if (project is null || !project.IsRegistered)
            return;

        EditProjectEncryptionRequested?.Invoke(project);
    }

    private void RefreshHealthTags()
    {
        foreach (var project in _allProjects)
        {
            project.HealthTag = GetHealthTag(project);
        }
    }

    private void RefreshSnapshotText()
    {
        foreach (var project in _allProjects)
        {
            project.NotifySnapshotTextChanged();
        }
    }

    private static string GetHealthTag(ProjectItemViewModel project)
    {
        if (project.LastSnapshot == default)
        {
            return project.IsRegistered
                ? L("Projects.Health.NoSnapshots", "No snapshots yet")
                : L("Projects.Health.NotAdded", "Not added");
        }

        if (!project.IsRegistered)
            return L("Projects.Health.NotBackedUp", "Not backed up");

        var age = DateTime.UtcNow - project.LastSnapshot;
        if (age.TotalDays < 1)
            return L("Projects.Health.HealthyRecent", "Healthy (<1d)");

        if (age.TotalDays < 7)
            return L("Projects.Health.OutOfDateShort", "Out of date (>1d)");

        return L("Projects.Health.Stale", "Stale (>7d)");
    }

    private void LoadAvailablePresets()
    {
        try
        {
            AvailablePresets.Clear();
            _presetCatalogById.Clear();
            _presetRecommendationCache.Clear();

            foreach (var preset in GetPresetInfos())
            {
                if (string.IsNullOrWhiteSpace(preset.Id))
                    continue;

                AvailablePresets.Add(preset.Id);
                _presetCatalogById[preset.Id] = preset;
            }

            // Keep the explicit "no preset" option for existing configs and power users, but
            // normal project flows now fall back to a real preset instead of staying blank.
            if (!AvailablePresets.Contains(NoPresetId))
                AvailablePresets.Add(NoPresetId);

            foreach (var project in Projects)
            {
                UpdateProjectPresetDisplay(project);
            }
        }
        catch (Exception)
        {

            // Fallback to a minimal hard-coded set so the UI stays usable.
            AvailablePresets.Clear();
            AvailablePresets.Add(GenericPresetId);
            AvailablePresets.Add("unity");
            AvailablePresets.Add("dotnet");
            AvailablePresets.Add("blender");
                AvailablePresets.Add(VideoPresetId);
            AvailablePresets.Add(NoPresetId);
            _presetCatalogById.Clear();
            _presetRecommendationCache.Clear();
        }
    }

    private static IEnumerable<PresetInfo> GetPresetInfos()
    {
        var presets = new List<PresetInfo>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = ResolvePresetsDirForUi();

        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return presets;

        try
        {
            LoadPresetInfos(dir, presets, ids);
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Record($"Project preset catalog load failed: {ex.GetType().Name} - {ex.Message}");
        }

        return presets.OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase);
    }

    private static void LoadPresetInfos(string dir, List<PresetInfo> presets, HashSet<string> ids)
    {
        var indexPath = Path.Combine(dir, "presets.index.json");
        if (File.Exists(indexPath))
        {
            AddIndexedPresetInfos(indexPath, presets, ids);
            return;
        }

        AddDirectoryPresetInfos(dir, presets, ids);
    }

    private static void AddIndexedPresetInfos(string indexPath, List<PresetInfo> presets, HashSet<string> ids)
    {
        var json = File.ReadAllText(indexPath);
        var index = JsonSerializer.Deserialize<PresetIndex>(json);
        foreach (var preset in index?.Presets ?? [])
        {
            AddPresetInfo(preset, presets, ids);
        }
    }

    private static void AddPresetInfo(PresetInfo preset, List<PresetInfo> presets, HashSet<string> ids)
    {
        if (!string.IsNullOrWhiteSpace(preset.Id))
        {
            if (ids.Add(preset.Id))
                presets.Add(preset);
            return;
        }

        if (string.IsNullOrWhiteSpace(preset.File))
            return;

        var id = Path.GetFileNameWithoutExtension(preset.File);
        if (ids.Add(id))
        {
            presets.Add(new PresetInfo
            {
                Id = id,
                File = preset.File
            });
        }
    }

    private static void AddDirectoryPresetInfos(string dir, List<PresetInfo> presets, HashSet<string> ids)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.vaultsyncignore"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrWhiteSpace(name) && ids.Add(name))
            {
                presets.Add(new PresetInfo
                {
                    Id = name,
                    File = Path.GetFileName(file)
                });
            }
        }
    }

    private void UpdateProjectPresetDisplay(ProjectItemViewModel vm)
    {
        if (vm is null)
            return;

        if (string.IsNullOrWhiteSpace(vm.Preset) ||
            string.Equals(vm.Preset, NoPresetId, StringComparison.OrdinalIgnoreCase))
        {
            vm.PresetDescription = L("Projects.Preset.NoPresetDescription", "No exclusion preset is active for this project.");
            vm.PresetExample = string.Empty;
            return;
        }

        if (_presetCatalogById.TryGetValue(vm.Preset, out var info))
        {
            vm.PresetDescription = string.IsNullOrWhiteSpace(info.Description)
                ? L("Projects.Preset.DescriptionFallback", "Preset rules are applied from .vaultsyncignore patterns.")
                : info.Description;
            vm.PresetExample = info.Example ?? string.Empty;
            return;
        }

        vm.PresetDescription = Lf("Projects.Preset.DescriptionUnknown", "Preset '{0}' is active.", vm.Preset);
        vm.PresetExample = string.Empty;
    }

    private void TogglePresetEditor()
    {
        if (!HasPresetEditorTarget)
            return;

        IsPresetEditorVisible = !IsPresetEditorVisible;
        if (IsPresetEditorVisible && string.IsNullOrWhiteSpace(PresetEditorContent))
            _ = ReloadPresetEditorAsync();
    }

    private async Task ReloadPresetEditorAsync()
    {
        if (!TryResolveSelectedPresetPath(out var presetPath, out _))
            return;

        try
        {
            PresetEditorStatus = L("Projects.Preset.Editor.Status.Reloading", "Loading preset rules...");
            var result = await Task.Run(() =>
            {
                bool exists = File.Exists(presetPath);
                string content = exists ? File.ReadAllText(presetPath) : string.Empty;
                return (exists, content);
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!string.Equals(PresetEditorPath, presetPath, StringComparison.OrdinalIgnoreCase))
                    return;

                PresetEditorContent = result.content;
                PresetEditorStatus = result.exists
                    ? L("Projects.Preset.Editor.Status.Reloaded", "Preset rules reloaded.")
                    : L("Projects.Preset.Editor.Status.NewFile", "Preset file does not exist yet. Save to create it.");
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PresetEditorStatus = Lf("Projects.Preset.Editor.Status.ReloadFailed", "Failed to reload preset rules: {0}", ex.Message);
            });
        }
    }

    private void SavePresetEditor()
    {
        if (!TryResolveSelectedPresetPath(out var presetPath, out var presetId))
            return;

        try
        {
            var parent = Path.GetDirectoryName(presetPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            File.WriteAllText(presetPath, PresetEditorContent ?? string.Empty);
            PresetEditorStatus = Lf("Projects.Preset.Editor.Status.Saved", "Saved preset '{0}'.", presetId);
        }
        catch (Exception ex)
        {
            PresetEditorStatus = Lf("Projects.Preset.Editor.Status.SaveFailed", "Failed to save preset rules: {0}", ex.Message);
        }
    }

    private void PreviewPresetEditor()
    {
        var project = SelectedProject;
        if (project is null || string.IsNullOrWhiteSpace(project.Path) || !Directory.Exists(project.Path))
        {
            PresetEditorStatus = L("Projects.Preset.Editor.Status.PreviewNoProject", "Select a valid project path to preview rules.");
            return;
        }

        try
        {
            var lines = (PresetEditorContent ?? string.Empty)
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToList();

            var filter = new FilterService(lines);
            const int maxFiles = 10000;
            var scanned = 0;
            var excluded = 0;
            var included = 0;

            foreach (var file in Directory.EnumerateFiles(project.Path, "*", SearchOption.AllDirectories))
            {
                scanned++;
                if (filter.ShouldExclude(project.Path, file))
                    excluded++;
                else
                    included++;

                if (scanned >= maxFiles)
                    break;
            }

            PresetEditorStatus = Lf(
                "Projects.Preset.Editor.Status.PreviewResult",
                "Preview: scanned {0} files - included {1}, excluded {2}.",
                scanned,
                included,
                excluded);
        }
        catch (Exception ex)
        {
            PresetEditorStatus = Lf("Projects.Preset.Editor.Status.PreviewFailed", "Preview failed: {0}", ex.Message);
        }
    }

    private void ClonePresetEditor()
    {
        if (!HasPresetEditorTarget)
            return;

        var cloneId = (PresetEditorCloneId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cloneId))
        {
            PresetEditorStatus = L("Projects.Preset.Editor.Status.CloneIdRequired", "Enter a preset id before cloning.");
            return;
        }

        if (!IsValidPresetId(cloneId))
        {
            PresetEditorStatus = L("Projects.Preset.Editor.Status.CloneIdInvalid", "Preset id can contain letters, numbers, '-', '_' and '.'.");
            return;
        }

        var presetsDir = ResolvePresetsDirForUi();
        if (string.IsNullOrWhiteSpace(presetsDir))
        {
            PresetEditorStatus = L("Projects.Preset.Editor.Status.PresetsDirMissing", "Could not resolve presets directory.");
            return;
        }

        try
        {
            Directory.CreateDirectory(presetsDir);
            var clonePath = Path.Combine(presetsDir, $"{cloneId}.vaultsyncignore");
            File.WriteAllText(clonePath, PresetEditorContent ?? string.Empty);

            if (!_presetCatalogById.ContainsKey(cloneId))
            {
                _presetCatalogById[cloneId] = new PresetInfo
                {
                    Id = cloneId,
                    File = $"{cloneId}.vaultsyncignore",
                    Description = Lf("Projects.Preset.DescriptionUnknown", "Preset '{0}' is active.", cloneId),
                    Example = string.Empty
                };
            }

            if (!AvailablePresets.Any(p => string.Equals(p, cloneId, StringComparison.OrdinalIgnoreCase)))
                AvailablePresets.Add(cloneId);

            if (SelectedProject is not null)
            {
                SelectedProject.Preset = cloneId;
                UpdateProjectPresetDisplay(SelectedProject);
                UpdateProjectPresetRecommendation(SelectedProject);
            }

            PresetEditorStatus = Lf("Projects.Preset.Editor.Status.Cloned", "Cloned preset as '{0}' and selected it for this project.", cloneId);
        }
        catch (Exception ex)
        {
            PresetEditorStatus = Lf("Projects.Preset.Editor.Status.CloneFailed", "Failed to clone preset: {0}", ex.Message);
        }
    }

    private void ExportPresetEditor()
    {
        if (!TryResolveSelectedPresetPath(out _, out var presetId))
            return;

        try
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var exportDir = Path.Combine(docs, "VaultSync", "Exports", "Presets");
            Directory.CreateDirectory(exportDir);
            var safeId = SanitizePresetIdForFileName(string.IsNullOrWhiteSpace(presetId) ? "preset" : presetId);
            var fileName = $"{safeId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.vaultsyncignore";
            var exportPath = Path.Combine(exportDir, fileName);
            File.WriteAllText(exportPath, PresetEditorContent ?? string.Empty);
            PresetEditorStatus = Lf("Projects.Preset.Editor.Status.Exported", "Exported preset rules to '{0}'.", exportPath);
        }
        catch (Exception ex)
        {
            PresetEditorStatus = Lf("Projects.Preset.Editor.Status.ExportFailed", "Failed to export preset rules: {0}", ex.Message);
        }
    }

    private void ImportPresetEditor()
    {
        var importPath = (PresetEditorImportPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(importPath))
        {
            PresetEditorStatus = L("Projects.Preset.Editor.Status.ImportPathRequired", "Enter a file path to import preset rules.");
            return;
        }

        try
        {
            if (!File.Exists(importPath))
            {
                PresetEditorStatus = Lf("Projects.Preset.Editor.Status.ImportMissing", "Preset file not found: {0}", importPath);
                return;
            }

            PresetEditorContent = File.ReadAllText(importPath);
            PresetEditorStatus = Lf("Projects.Preset.Editor.Status.Imported", "Imported preset rules from '{0}'.", importPath);
        }
        catch (Exception ex)
        {
            PresetEditorStatus = Lf("Projects.Preset.Editor.Status.ImportFailed", "Failed to import preset rules: {0}", ex.Message);
        }
    }

    private void LoadPresetEditorForSelectedProject()
    {
        var project = SelectedProject;
        if (project is null)
        {
            HasPresetEditorTarget = false;
            IsPresetEditorVisible = false;
            PresetEditorContent = string.Empty;
            PresetEditorPath = string.Empty;
            PresetEditorPathDisplay = string.Empty;
            PresetEditorStatus = string.Empty;
            OnPropertyChanged(nameof(PresetEditorToggleLabel));
            RaisePresetEditorCanExecuteChanged();
            return;
        }

        if (string.IsNullOrWhiteSpace(project.Preset) ||
            string.Equals(project.Preset, NoPresetId, StringComparison.OrdinalIgnoreCase))
        {
            HasPresetEditorTarget = false;
            IsPresetEditorVisible = false;
            PresetEditorContent = string.Empty;
            PresetEditorPath = string.Empty;
            PresetEditorPathDisplay = string.Empty;
            PresetEditorStatus = L("Projects.Preset.Editor.Status.NoPreset", "Select a preset to edit its rules.");
            OnPropertyChanged(nameof(PresetEditorToggleLabel));
            RaisePresetEditorCanExecuteChanged();
            return;
        }

        if (!TryResolveSelectedPresetPath(out var presetPath, out var presetId))
        {
            HasPresetEditorTarget = false;
            IsPresetEditorVisible = false;
            PresetEditorContent = string.Empty;
            PresetEditorPath = string.Empty;
            PresetEditorPathDisplay = string.Empty;
            PresetEditorStatus = Lf("Projects.Preset.Editor.Status.ResolveFailed", "Could not resolve preset file for '{0}'.", presetId);
            OnPropertyChanged(nameof(PresetEditorToggleLabel));
            RaisePresetEditorCanExecuteChanged();
            return;
        }

        HasPresetEditorTarget = true;
        PresetEditorPath = presetPath;
        PresetEditorPathDisplay = $"{presetId}.vaultsyncignore";
        PresetEditorContent = string.Empty;
        PresetEditorStatus = IsPresetEditorVisible
            ? L("Projects.Preset.Editor.Status.ReadyToLoad", "Open or reload the preset editor to load rules.")
            : string.Empty;
        OnPropertyChanged(nameof(PresetEditorToggleLabel));
        RaisePresetEditorCanExecuteChanged();
        if (IsPresetEditorVisible)
            _ = ReloadPresetEditorAsync();
    }

    private bool TryResolveSelectedPresetPath(out string presetPath, out string presetId)
    {
        presetPath = string.Empty;
        presetId = SelectedProject?.Preset?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(presetId) ||
            string.Equals(presetId, NoPresetId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var dir = ResolvePresetsDirForUi();
        if (string.IsNullOrWhiteSpace(dir))
            return false;

        if (_presetCatalogById.TryGetValue(presetId, out var info))
        {
            var fileName = string.IsNullOrWhiteSpace(info.File)
                ? $"{presetId}.vaultsyncignore"
                : info.File;
            presetPath = Path.Combine(dir, fileName);
            return true;
        }

        presetPath = Path.Combine(dir, $"{presetId}.vaultsyncignore");
        return true;
    }

    private void RaisePresetEditorCanExecuteChanged()
    {
        _togglePresetEditorCommand.RaiseCanExecuteChanged();
        _reloadPresetEditorCommand.RaiseCanExecuteChanged();
        _savePresetEditorCommand.RaiseCanExecuteChanged();
        _previewPresetEditorCommand.RaiseCanExecuteChanged();
        _clonePresetEditorCommand.RaiseCanExecuteChanged();
        _exportPresetEditorCommand.RaiseCanExecuteChanged();
    }

    private static bool IsValidPresetId(string id)
    {
        foreach (var ch in id)
        {
            if (char.IsLetterOrDigit(ch))
                continue;
            if (ch is '-' or '_' or '.')
                continue;
            return false;
        }

        return true;
    }

    private static string SanitizePresetIdForFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value
            .Select(ch => invalid.Contains(ch) ? '-' : ch)
            .ToArray();
        return new string(chars);
    }

    private static string ResolvePresetsDirForUi()
    {
        // 1) Environment override for power users / testing
        var env = Environment.GetEnvironmentVariable("VAULTSYNC_PRESETS_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        // 2) Installed / published app: <app>/presets
        var appPresets = Path.Combine(AppContext.BaseDirectory, "presets");
        if (Directory.Exists(appPresets))
            return appPresets;

        // 3) Dev tree: walk up to find src/presets (current repo layout)
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, "src", "presets");
            if (Directory.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null)
                break;

            dir = parent;
        }

        // 4) Fallback to app presets path (may or may not exist)
        return appPresets;
    }

    private sealed class PresetIndex
    {
        public List<PresetInfo> Presets { get; set; } = [];
    }

    private sealed class PresetInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Example { get; set; } = string.Empty;
    }

    private sealed record PresetRecommendation(string PresetId, string Reason);

    private sealed record PresetRecommendationRule(
        string PresetName,
        string ReasonKey,
        string ReasonFallback,
        Func<ProjectPathProbe, bool> Matches);

    private sealed class ProjectPathProbe(string projectPath)
    {
        private static readonly EnumerationOptions RecursiveFileOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false
        };

        public bool Has(string relativePath)
        {
            try
            {
                return File.Exists(Path.Combine(projectPath, relativePath));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool HasDir(string relativePath)
        {
            try
            {
                return Directory.Exists(Path.Combine(projectPath, relativePath));
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool HasAny(string pattern)
        {
            try
            {
                return Directory.Exists(projectPath) &&
                    Directory.EnumerateFiles(projectPath, pattern, RecursiveFileOptions).Any();
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool HasAnyPath(params string[] relativePaths) =>
            relativePaths.Any(Has);
    }

    private sealed record ProjectItemChange(
        bool ChangedPreset,
        bool ChangedTags,
        bool ChangedGroup,
        bool ChangedRecommendedPreset,
        bool ChangedDestination,
        bool ChangedEncryption,
        bool ChangedAutoBackup)
    {
        public bool ShouldPersist => ChangedPreset || ChangedDestination || ChangedEncryption || ChangedTags || ChangedGroup || ChangedAutoBackup;

        public static ProjectItemChange FromProperty(string? propertyName) =>
            new(
                string.Equals(propertyName, nameof(ProjectItemViewModel.Preset), StringComparison.Ordinal),
                string.Equals(propertyName, nameof(ProjectItemViewModel.TagsCsv), StringComparison.Ordinal),
                string.Equals(propertyName, nameof(ProjectItemViewModel.GroupId), StringComparison.Ordinal),
                string.Equals(propertyName, nameof(ProjectItemViewModel.RecommendedPreset), StringComparison.Ordinal),
                string.Equals(propertyName, nameof(ProjectItemViewModel.PreferredDestinationId), StringComparison.Ordinal),
                string.Equals(propertyName, nameof(ProjectItemViewModel.EncryptionPolicy), StringComparison.Ordinal),
                string.Equals(propertyName, nameof(ProjectItemViewModel.IsAutoBackupEnabled), StringComparison.Ordinal));
    }

    private sealed record ProjectBuildContext(
        SqliteRepository? Repository,
        IReadOnlyList<Project> RegisteredProjects,
        IReadOnlyDictionary<string, Project> ProjectsByName,
        IReadOnlyDictionary<int, (DateTime CreatedUtc, long TotalBytes)> LatestSnapshotsByProject,
        IReadOnlyDictionary<int, Backup> LatestBackupsByProject)
    {
        public static ProjectBuildContext Empty { get; } =
            new(null, [], new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase), new Dictionary<int, (DateTime, long)>(), new Dictionary<int, Backup>());
    }

    private sealed record ProjectStats(DateTime? LastSnapshotTime, long? LastSnapshotBytes)
    {
        public ProjectStats WithRepositoryData(
            int projectId,
            IReadOnlyDictionary<int, (DateTime CreatedUtc, long TotalBytes)> latestSnapshotsByProject,
            IReadOnlyDictionary<int, Backup> latestBackupsByProject)
        {
            DateTime? lastSnapshotTime = LastSnapshotTime;
            long? lastSnapshotBytes = LastSnapshotBytes;

            if (latestSnapshotsByProject.TryGetValue(projectId, out var latestSnapshot))
            {
                lastSnapshotTime = latestSnapshot.CreatedUtc;
                lastSnapshotBytes = latestSnapshot.TotalBytes;
            }

            if (latestBackupsByProject.TryGetValue(projectId, out var latestBackup) &&
                (!lastSnapshotTime.HasValue || latestBackup.CreatedUtc > lastSnapshotTime.Value))
            {
                lastSnapshotTime = latestBackup.CreatedUtc;
                lastSnapshotBytes = latestBackup.TotalBytes;
            }

            return new ProjectStats(lastSnapshotTime, lastSnapshotBytes);
        }
    }

    private SqliteRepository CreateRepository(AppConfig? config = null)
    {
        return _repositoryFactory.Create(config);
    }

    public void SetAvatarForSelectedProject(string avatarPath)
    {
        if (SelectedProject is null)
            return;

        SelectedProject.SetCustomAvatar(avatarPath);
        AvatarStore.SetAvatarForProject(SelectedProject.Path, avatarPath);
    }

    public void ClearAvatarForSelectedProject()
    {
        if (SelectedProject is null)
            return;

        SelectedProject.ClearAvatar();
        AvatarStore.ClearAvatarForProject(SelectedProject.Path);
    }
}

public enum ProjectHealthStatus
{
    Healthy,
    Warning,
    OutOfDate
}

/// <summary>
/// One project entry in the list + details panel.
/// </summary>
public class ProjectItemViewModel : ViewModelBase
{
    private static string L(string key, string fallback)
    {
        string? value = LocalizationProvider.Service?.GetString(key);
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
            ? fallback
            : value;
    }

    private static string Lf(string key, string fallback, params object[] args)
    {
        var fmt = L(key, fallback);
        return string.Format(CultureInfo.CurrentCulture, fmt, args);
    }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    private string _path = string.Empty;
    public string Path
    {
        get => _path;
        set => SetField(ref _path, value);
    }

    private string _externalId = string.Empty;
    public string ExternalId
    {
        get => _externalId;
        set => SetField(ref _externalId, value ?? string.Empty);
    }

    private int _projectId;
    public int ProjectId
    {
        get => _projectId;
        set => SetField(ref _projectId, value);
    }

    private string _groupId = ProjectGroupOption.UngroupedId;
    public string GroupId
    {
        get => _groupId;
        set => SetField(ref _groupId, value?.Trim() ?? ProjectGroupOption.UngroupedId);
    }

    private ProjectGroupOption? _selectedGroupOption;
    private ProjectGroupOption? _assignedGroupOption;
    public ProjectGroupOption? SelectedGroupOption
    {
        get => _selectedGroupOption;
        set
        {
            // Ignore transient null selection events while the shared option list refreshes.
            if (value is null)
                return;

            if (SetField(ref _selectedGroupOption, value))
                OnPropertiesChanged(nameof(HasPendingGroupChange), nameof(FolderMovePreview));
        }
    }

    public bool HasPendingGroupChange =>
        _selectedGroupOption is not null &&
        !string.Equals(_selectedGroupOption.Id, GroupId, StringComparison.OrdinalIgnoreCase);

    public string FolderLocationText => string.IsNullOrWhiteSpace(GroupId)
        ? L("Projects.Folder.Location.Main", "Currently shown in the main project list.")
        : Lf(
            "Projects.Folder.Location.Inside",
            "Currently shown only inside “{0}”.",
            _assignedGroupOption?.Label ?? L("Projects.Folder.Ungrouped", "Ungrouped"));

    public string FolderMovePreview
    {
        get
        {
            if (!HasPendingGroupChange)
                return FolderLocationText;

            if (string.IsNullOrWhiteSpace(_selectedGroupOption!.Id))
                return L("Projects.Folder.MoveToMain", "Move this project back to the main project list.");

            return Lf(
                "Projects.Folder.MoveInside",
                "Move this project into “{0}”. It will appear only when that folder is open.",
                _selectedGroupOption.Label);
        }
    }

    public string MoveProjectLabel => string.IsNullOrWhiteSpace(_selectedGroupOption?.Id)
        ? L("Projects.Folder.MoveToMainButton", "Move to main list")
        : Lf(
            "Projects.Folder.MoveToFolderButton",
            "Move to {0}",
            _selectedGroupOption.Label);

    public void SetGroupOption(ProjectGroupOption? option)
    {
        _assignedGroupOption = option;
        _selectedGroupOption = option;
        OnPropertiesChanged(
            nameof(SelectedGroupOption),
            nameof(HasPendingGroupChange),
            nameof(FolderLocationText),
            nameof(FolderMovePreview));
    }

    public void CommitGroupOption(ProjectGroupOption option)
    {
        _assignedGroupOption = option;
        _selectedGroupOption = option;
        GroupId = option.Id;
        OnPropertiesChanged(
            nameof(SelectedGroupOption),
            nameof(HasPendingGroupChange),
            nameof(FolderLocationText),
            nameof(FolderMovePreview));
    }

    private ProjectHealthStatus _health;
    public ProjectHealthStatus Health
    {
        get => _health;
        set
        {
            if (SetField(ref _health, value))
            {
                OnPropertyChanged(nameof(HealthBackground));
                OnPropertyChanged(nameof(HealthForeground));
            }
        }
    }

    private string _healthTag = string.Empty;
    public string HealthTag
    {
        get => _healthTag;
        set => SetField(ref _healthTag, value);
    }

    private DateTime _lastSnapshot;
    public DateTime LastSnapshot
    {
        get => _lastSnapshot;
        set
        {
            if (SetField(ref _lastSnapshot, value))
            {
                OnPropertyChanged(nameof(LastSnapshotSummary));
                OnPropertyChanged(nameof(LastSnapshotShort));
                OnPropertyChanged(nameof(LatestSnapshotSizeDisplay));
                OnPropertyChanged(nameof(DaysSinceLastSnapshotDisplay));
            }
        }
    }

    private long _sizeBytes;
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetField(ref _sizeBytes, value))
            {
                OnPropertyChanged(nameof(SizeDisplay));
                OnPropertyChanged(nameof(LatestSnapshotSizeDisplay));
            }
        }
    }

    private string _preset = string.Empty;
    public string Preset
    {
        get => _preset;
        set => SetField(ref _preset, value);
    }

    private string _presetDescription = string.Empty;
    public string PresetDescription
    {
        get => _presetDescription;
        set => SetField(ref _presetDescription, value ?? string.Empty);
    }

    private string _presetExample = string.Empty;
    public string PresetExample
    {
        get => _presetExample;
        set => SetField(ref _presetExample, value ?? string.Empty);
    }

    private string _tagsCsv = string.Empty;
    public string TagsCsv
    {
        get => _tagsCsv;
        set
        {
            if (!SetField(ref _tagsCsv, value ?? string.Empty))
                return;

            RebuildTagChips();
            OnPropertyChanged(nameof(TagsDisplay));
            OnPropertyChanged(nameof(HasTags));
        }
    }

    public string TagsDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TagsCsv))
                return string.Empty;

            var tags = TagsCsv
                .Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();

            return tags.Length == 0 ? string.Empty : string.Join(" - ", tags);
        }
    }

    public bool HasTags => !string.IsNullOrWhiteSpace(TagsDisplay);
    public ObservableCollection<ProjectTagChip> TagChips { get; } = [];

    private string _recommendedPreset = string.Empty;
    public string RecommendedPreset
    {
        get => _recommendedPreset;
        set => SetField(ref _recommendedPreset, value ?? string.Empty);
    }

    private string _recommendedPresetReason = string.Empty;
    public string RecommendedPresetReason
    {
        get => _recommendedPresetReason;
        set => SetField(ref _recommendedPresetReason, value ?? string.Empty);
    }

    private string _preferredDestinationId = string.Empty;
    public string PreferredDestinationId
    {
        get => _preferredDestinationId;
        set => SetField(ref _preferredDestinationId, value ?? string.Empty);
    }

    private DestinationOption? _preferredDestinationOption;
    public DestinationOption? PreferredDestinationOption
    {
        get => _preferredDestinationOption;
        set
        {
            // Ignore transient null selection events fired while option sources refresh.
            // Real auto selection is represented by a non-null option with empty Id.
            if (value is null)
                return;

            if (SetField(ref _preferredDestinationOption, value))
                PreferredDestinationId = value.Id;
        }
    }

    private string _preferredDestinationDisplay = string.Empty;
    public string PreferredDestinationDisplay
    {
        get => _preferredDestinationDisplay;
        set => SetField(ref _preferredDestinationDisplay, value ?? string.Empty);
    }

    public void SetPreferredDestinationOption(DestinationOption? option)
    {
        if (ReferenceEquals(_preferredDestinationOption, option))
            return;

        _preferredDestinationOption = option;
        OnPropertyChanged(nameof(PreferredDestinationOption));
    }

    private string _encryptionPolicy = ProjectEncryptionPolicy.Inherit;
    public string EncryptionPolicy
    {
        get => _encryptionPolicy;
        set => SetField(ref _encryptionPolicy, ProjectEncryptionPolicy.Normalize(value));
    }

    private string _encryptionKeyRef = string.Empty;
    public string EncryptionKeyRef
    {
        get => _encryptionKeyRef;
        set => SetField(ref _encryptionKeyRef, value ?? string.Empty);
    }

    private EncryptionPolicyOption? _encryptionPolicyOption;
    public EncryptionPolicyOption? EncryptionPolicyOption
    {
        get => _encryptionPolicyOption;
        set
        {
            if (SetField(ref _encryptionPolicyOption, value))
            {
                // Ignore transient null selection events fired while option sources refresh.
                // Real "inherit" selection is represented by a non-null option with Id="inherit".
                if (value is null)
                    return;

                EncryptionPolicy = value.Id;
            }
        }
    }

    private string _effectiveEncryptionDisplay = string.Empty;
    public string EffectiveEncryptionDisplay
    {
        get => _effectiveEncryptionDisplay;
        set => SetField(ref _effectiveEncryptionDisplay, value ?? string.Empty);
    }

    private bool _hasEncryptionSecret;
    public bool HasEncryptionSecret
    {
        get => _hasEncryptionSecret;
        set => SetField(ref _hasEncryptionSecret, value);
    }

    private string _encryptionSecretStatus = string.Empty;
    public string EncryptionSecretStatus
    {
        get => _encryptionSecretStatus;
        set => SetField(ref _encryptionSecretStatus, value ?? string.Empty);
    }

    private string _encryptionBadgeText = string.Empty;
    public string EncryptionBadgeText
    {
        get => _encryptionBadgeText;
        set => SetField(ref _encryptionBadgeText, value ?? string.Empty);
    }

    private string _encryptionBadgeBackground = ProjectsViewModel.NeutralBadgeBackground;
    public string EncryptionBadgeBackground
    {
        get => _encryptionBadgeBackground;
        set => SetField(ref _encryptionBadgeBackground, value ?? ProjectsViewModel.NeutralBadgeBackground);
    }

    private string _encryptionBadgeForeground = "#C7D2FE";
    public string EncryptionBadgeForeground
    {
        get => _encryptionBadgeForeground;
        set => SetField(ref _encryptionBadgeForeground, value ?? "#C7D2FE");
    }

    public void SetEncryptionPolicyOption(EncryptionPolicyOption? option)
    {
        if (ReferenceEquals(_encryptionPolicyOption, option))
            return;

        _encryptionPolicyOption = option;
        OnPropertyChanged(nameof(EncryptionPolicyOption));
    }

    private bool _isRegistered;
    public bool IsRegistered
    {
        get => _isRegistered;
        set => SetField(ref _isRegistered, value);
    }

    private bool _isAutoBackupEnabled = true;
    public bool IsAutoBackupEnabled
    {
        get => _isAutoBackupEnabled;
        set => SetField(ref _isAutoBackupEnabled, value);
    }

    public bool SnapshotHistoryLoaded { get; set; }

    // Avatar
    public string AvatarInitials { get; private set; } = string.Empty;
    public string AvatarColor { get; private set; } = "#33405A";
    public string? AvatarImagePath { get; private set; }
    public bool HasCustomAvatar => !string.IsNullOrWhiteSpace(AvatarImagePath);

    // ---- Snapshot history for per-project statistics ----
    public ObservableCollection<ProjectSnapshotViewModel> SnapshotHistory { get; } =
        [];

    public int SnapshotCount => SnapshotHistory.Count;

    public string TotalSnapshotSizeDisplay =>
        SnapshotHistory.Count == 0
            ? "0 B"
            : ProjectSnapshotViewModel.FormatSize(SnapshotHistory.Sum(s => s.SizeBytes));

    public string AverageSnapshotSizeDisplay =>
        SnapshotHistory.Count == 0
            ? "-"
            : ProjectSnapshotViewModel.FormatSize((long)SnapshotHistory.Average(s => (double)s.SizeBytes));

    /// <summary>
    /// Sets the snapshot history used by the Projects view and stats panel.
    /// Also computes relative sizes and trend colors for the mini chart.
    /// </summary>
    public void SetSnapshots(IEnumerable<ProjectSnapshotViewModel> snapshots)
    {
        SnapshotHistory.Clear();
        foreach (var snapshot in snapshots)
        {
            SnapshotHistory.Add(snapshot);
        }

        var max = SnapshotHistory.Count == 0 ? 0 : SnapshotHistory.Max(s => s.SizeBytes);

        if (max <= 0)
        {
            // No snapshots or all zero size: clear aggregate stats.
            OnPropertyChanged(nameof(SnapshotCount));
            OnPropertyChanged(nameof(TotalSnapshotSizeDisplay));
            OnPropertyChanged(nameof(AverageSnapshotSizeDisplay));
            OnPropertyChanged(nameof(DaysSinceLastSnapshotDisplay));
            return;
        }

        for (int i = 0; i < SnapshotHistory.Count; i++)
        {
            var s = SnapshotHistory[i];
            s.RelativeSize = (double)s.SizeBytes / max;
            s.TrendColor = GetSnapshotTrendColor(i);
            s.ShowDayLabel = ShouldShowSnapshotDayLabel(i);

            if (s.ShowDayLabel)
            {
                s.DayLabel = s.Timestamp.ToString("dd/MM", CultureInfo.CurrentCulture);
            }
        }

        var last = SnapshotHistory[^1];
        if (!last.ShowDayLabel)
        {
            last.ShowDayLabel = true;
            last.DayLabel = last.Timestamp.ToString("dd/MM", CultureInfo.CurrentCulture);
        }

        // Notify that aggregate snapshot stats have changed.
        OnPropertiesChanged(
            nameof(SnapshotCount),
            nameof(TotalSnapshotSizeDisplay),
            nameof(AverageSnapshotSizeDisplay),
            nameof(DaysSinceLastSnapshotDisplay));
    }

    private string GetSnapshotTrendColor(int index)
    {
        if (index == 0)
            return ProjectsViewModel.NeutralBadgeBackground;

        var current = SnapshotHistory[index];
        var previous = SnapshotHistory[index - 1];
        if (current.SizeBytes > previous.SizeBytes)
            return "#6A2E2E";
        if (current.SizeBytes < previous.SizeBytes)
            return "#2E6A3E";

        return ProjectsViewModel.NeutralBadgeBackground;
    }

    private bool ShouldShowSnapshotDayLabel(int index)
    {
        if (index == 0)
            return true;

        var previousDate = SnapshotHistory[index - 1].Timestamp.Date;
        return SnapshotHistory[index].Timestamp.Date != previousDate;
    }

    // ---- Convenience / formatted properties ----

    public string LastSnapshotSummary =>
        LastSnapshot == default
            ? (IsRegistered
                ? L("Projects.LastSnapshot.None", "No snapshots yet")
                : L("Projects.Health.NotAdded", "Not added"))
            : LastSnapshot.ToString("g", CultureInfo.CurrentCulture);

    public string LastSnapshotShort =>
        LastSnapshot == default
            ? (IsRegistered
                ? L("Projects.LastSnapshot.NoneShort", "No snapshots yet")
                : L("Projects.Health.NotAdded", "Not added"))
            : LastSnapshot.ToString("ddd - HH:mm", CultureInfo.CurrentCulture);

    public string DaysSinceLastSnapshotDisplay
    {
        get
        {
            if (LastSnapshot == default)
                return L("Projects.TimeSinceLast.Never", "Never");

            var diff = DateTime.Today - LastSnapshot.Date;
            if (diff.TotalDays < 1)
                return L("Projects.TimeSinceLast.LessThanDay", "< 1 day");

            if (Math.Abs(diff.TotalDays - 1) < 0.1)
                return L("Projects.TimeSinceLast.OneDay", "1 day");

            return Lf("Projects.TimeSinceLast.ManyDays", "{0} days", (int)diff.TotalDays);
        }
    }

    public void NotifySnapshotTextChanged()
    {
        OnPropertiesChanged(
            nameof(LastSnapshotSummary),
            nameof(LastSnapshotShort),
            nameof(LatestSnapshotSizeDisplay),
            nameof(DaysSinceLastSnapshotDisplay));
    }

    public string SizeDisplay
    {
        get
        {
            return UiFormat.FormatBytes(SizeBytes, "0.#");
        }
    }

    public string LatestSnapshotSizeDisplay
    {
        get
        {
            if (LastSnapshot == default)
                return L("Projects.SnapshotSize.NoSnapshots", "No snapshots yet");

            if (SizeBytes <= 0)
                return L("Projects.SnapshotSize.Unavailable", "Size unavailable");

            return UiFormat.FormatBytes(SizeBytes, "0.#");
        }
    }

    public string HealthBackground =>
        Health switch
        {
            ProjectHealthStatus.Healthy => "#1C4730",
            ProjectHealthStatus.Warning => "#473F1C",
            ProjectHealthStatus.OutOfDate => "#471C1C",
            _ => "#181B23"
        };

    public static string HealthForeground => "#F4F8FF";

    public void SetCustomAvatar(string path)
    {
        AvatarImagePath = path;
        OnPropertyChanged(nameof(AvatarImagePath));
        OnPropertyChanged(nameof(HasCustomAvatar));
    }

    public void ClearAvatar()
    {
        AvatarImagePath = null;
        OnPropertyChanged(nameof(AvatarImagePath));
        OnPropertyChanged(nameof(HasCustomAvatar));
    }

    public void SetAvatarFromNameAndStore(string projectPath, string? customPath, string? externalId)
    {
        AvatarInitials = ComputeInitials(Name);
        AvatarColor = AvatarColorProvider.GetColor(Name, projectPath, externalId);
        AvatarImagePath = customPath;
        OnPropertyChanged(nameof(AvatarInitials));
        OnPropertyChanged(nameof(AvatarColor));
        OnPropertyChanged(nameof(AvatarImagePath));
        OnPropertyChanged(nameof(HasCustomAvatar));
    }

    private static string ComputeInitials(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "??";

        var parts = name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return (parts[0][0].ToString() + parts[1][0].ToString()).ToUpperInvariant();
        }

        var trimmed = name.Trim();
        if (trimmed.Length >= 2)
            return trimmed[..2].ToUpperInvariant();

        return trimmed[..1].ToUpperInvariant();
    }

    private void RebuildTagChips()
    {
        TagChips.Clear();

        if (string.IsNullOrWhiteSpace(TagsCsv))
            return;

        var tags = TagsCsv
            .Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4);

        var config = ProjectTagAppearance.TryLoadConfig();
        foreach (var tag in tags)
            TagChips.Add(ProjectTagChip.Create(tag, config));
    }

    public void RefreshTagChips(AppConfig? config = null)
    {
        TagChips.Clear();

        if (string.IsNullOrWhiteSpace(TagsCsv))
            return;

        var tags = TagsCsv
            .Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4);

        config ??= ProjectTagAppearance.TryLoadConfig();
        foreach (var tag in tags)
            TagChips.Add(ProjectTagChip.Create(tag, config));
    }

}
