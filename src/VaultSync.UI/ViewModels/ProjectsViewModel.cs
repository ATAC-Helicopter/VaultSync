using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
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
public class ProjectsViewModel : ViewModelBase
{
    private const string BackupEncryptionSecretUsername = "vaultsync-backup-encryption";
    private const string GenericPresetId = "generic";
    private const string NoPresetId = "no preset";
    private static readonly string[] DefaultReusableTags = ["Work", "Games", "Media", "Critical", "Archive"];
    private sealed record ProjectRegistrationSnapshot(
        bool Missing,
        int ProjectId,
        string Preset,
        string TagsCsv,
        string PreferredDestinationId,
        string EncryptionPolicy,
        string EncryptionKeyRef);

    private readonly ProjectDiscoveryService _discovery = new();
    private IReadOnlyList<DiscoveredProject> _cachedDiscovery = [];
    private string? _cachedDiscoveryRoot;
    private DateTime _cachedDiscoveryUtc;
    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromSeconds(10);
    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

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
    private int _refreshInFlight;
    private int _refreshQueued;
    private readonly RelayCommand _openFolderCommand;
    private readonly RelayCommand _removeProjectCommand;
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
    private readonly RelayCommand _commitProjectTagInputCommand;
    private readonly RelayCommand _removeProjectTagCommand;
    private readonly RelayCommand _addExistingTagToSelectedProjectCommand;
    private readonly RelayCommand _toggleProjectTagColorEditorCommand;
    private readonly RelayCommand _applyProjectTagColorCommand;
    private readonly RelayCommand _resetProjectTagColorCommand;
    private readonly RelayCommand _applyProjectTagColorSwatchCommand;
    private readonly RelayCommand _applyTagToGroupCommand;
    private readonly RelayCommand _removeTagFromGroupCommand;
    private readonly RelayCommand _selectGroupTagCommand;
    private readonly RelayCommand _removeGroupTagCommand;
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
            if (SetProperty(ref _selectedProject, value))
            {
                if (value is not null && !string.IsNullOrWhiteSpace(value.Name))
                    _lastSelectedProjectName = value.Name;

                OnPropertyChanged(nameof(HasSelectedProject));
                OnPropertyChanged(nameof(ShowSelectedProjectEmptyState));

                _openFolderCommand.RaiseCanExecuteChanged();
                _removeProjectCommand.RaiseCanExecuteChanged();
                _applyPresetRecommendationCommand.RaiseCanExecuteChanged();
                _snapshotGroupCommand.RaiseCanExecuteChanged();
                _backupGroupCommand.RaiseCanExecuteChanged();
                _disableAutoBackupGroupCommand.RaiseCanExecuteChanged();
                _enableAutoBackupGroupCommand.RaiseCanExecuteChanged();
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
    public bool ShowProjectsEmptyState => !HasProjects;
    public bool HasSelectedProject => SelectedProject is not null;
    public bool ShowSelectedProjectEmptyState => !HasSelectedProject;

    public bool ShowProjectAvatars { get; private set; } = true;

    private string _snapshotActionLabel = L("Snapshots.Action.Default", "Snapshot now");
    public string SnapshotActionLabel
    {
        get => _snapshotActionLabel;
        set => SetProperty(ref _snapshotActionLabel, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    // Reusable notification state for the Projects view.
    public NotificationState Notification { get; } = new NotificationState();

    public ICommand RefreshCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RemoveProjectCommand { get; }
    public ICommand SnapshotCommand { get; }
    public ICommand SnapshotGroupCommand { get; }
    public ICommand BackupGroupCommand { get; }
    public ICommand DisableAutoBackupGroupCommand { get; }
    public ICommand EnableAutoBackupGroupCommand { get; }
    public ICommand CommitProjectTagInputCommand { get; }
    public ICommand RemoveProjectTagCommand { get; }
    public ICommand AddExistingTagToSelectedProjectCommand { get; }
    public ICommand ToggleProjectTagColorEditorCommand { get; }
    public ICommand ApplyProjectTagColorCommand { get; }
    public ICommand ResetProjectTagColorCommand { get; }
    public ICommand ApplyProjectTagColorSwatchCommand { get; }
    public ICommand ApplyTagToGroupCommand { get; }
    public ICommand RemoveTagFromGroupCommand { get; }
    public ICommand SelectGroupTagCommand { get; }
    public ICommand RemoveGroupTagCommand { get; }
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
    public ObservableCollection<ProjectTagChip> SelectedGroupTags { get; } = [];
    public ObservableCollection<ProjectTagChip> ReusableProjectTags { get; } = [];
    private string _groupTagInput = string.Empty;
    public string GroupTagInput
    {
        get => _groupTagInput;
        set
        {
            if (!SetProperty(ref _groupTagInput, value ?? string.Empty))
                return;

            ConsumeGroupTagInputDelimiters();
            _applyTagToGroupCommand.RaiseCanExecuteChanged();
            _removeTagFromGroupCommand.RaiseCanExecuteChanged();
            _addExistingTagToSelectedProjectCommand.RaiseCanExecuteChanged();
        }
    }
    private string _projectTagInput = string.Empty;
    public string ProjectTagInput
    {
        get => _projectTagInput;
        set
        {
            if (!SetProperty(ref _projectTagInput, value ?? string.Empty))
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
            if (!SetProperty(ref _isProjectTagColorEditorOpen, value))
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
            if (!SetProperty(ref _projectTagColorHex, normalized))
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
            if (!SetProperty(ref _projectTagColorRed, clamped))
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
            if (!SetProperty(ref _projectTagColorGreen, clamped))
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
            if (!SetProperty(ref _projectTagColorBlue, clamped))
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
            if (!SetProperty(ref _projectTagColorHue, normalized))
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
            if (!SetProperty(ref _projectTagColorSaturation, normalized))
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
            if (!SetProperty(ref _projectTagColorValue, normalized))
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
        set => SetProperty(ref _presetEditorContent, value ?? string.Empty);
    }

    private string _presetEditorStatus = string.Empty;
    public string PresetEditorStatus
    {
        get => _presetEditorStatus;
        set => SetProperty(ref _presetEditorStatus, value ?? string.Empty);
    }

    private string _presetEditorPath = string.Empty;
    public string PresetEditorPath
    {
        get => _presetEditorPath;
        set => SetProperty(ref _presetEditorPath, value ?? string.Empty);
    }

    private string _presetEditorPathDisplay = string.Empty;
    public string PresetEditorPathDisplay
    {
        get => _presetEditorPathDisplay;
        set => SetProperty(ref _presetEditorPathDisplay, value ?? string.Empty);
    }

    private bool _hasPresetEditorTarget;
    public bool HasPresetEditorTarget
    {
        get => _hasPresetEditorTarget;
        set => SetProperty(ref _hasPresetEditorTarget, value);
    }

    private bool _isPresetEditorVisible;
    public bool IsPresetEditorVisible
    {
        get => _isPresetEditorVisible;
        set
        {
            if (!SetProperty(ref _isPresetEditorVisible, value))
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
        set => SetProperty(ref _presetEditorCloneId, value ?? string.Empty);
    }

    private string _presetEditorImportPath = string.Empty;
    public string PresetEditorImportPath
    {
        get => _presetEditorImportPath;
        set => SetProperty(ref _presetEditorImportPath, value ?? string.Empty);
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
    private ProjectGroupOption? _selectedGroup;
    public ProjectGroupOption? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                ApplyFilterAndSort();
                _snapshotGroupCommand.RaiseCanExecuteChanged();
                _backupGroupCommand.RaiseCanExecuteChanged();
                _disableAutoBackupGroupCommand.RaiseCanExecuteChanged();
                _enableAutoBackupGroupCommand.RaiseCanExecuteChanged();
                _applyTagToGroupCommand.RaiseCanExecuteChanged();
                _removeTagFromGroupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ProjectsViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Refresh());
        _openFolderCommand = new RelayCommand(_ => OpenFolder(), _ => SelectedProject is not null);
        _removeProjectCommand = new RelayCommand(_ => RemoveProject(), _ => SelectedProject is not null);
        _applyPresetRecommendationCommand = new RelayCommand(_ => ApplyPresetRecommendation(), _ =>
            SelectedProject is { RecommendedPreset.Length: > 0 });
        _togglePresetEditorCommand = new RelayCommand(_ => TogglePresetEditor(), _ => HasPresetEditorTarget);
        _reloadPresetEditorCommand = new RelayCommand(_ => ReloadPresetEditor(), _ => HasPresetEditorTarget);
        _savePresetEditorCommand = new RelayCommand(_ => SavePresetEditor(), _ => HasPresetEditorTarget);
        _previewPresetEditorCommand = new RelayCommand(_ => PreviewPresetEditor(), _ => HasPresetEditorTarget);
        _clonePresetEditorCommand = new RelayCommand(_ => ClonePresetEditor(), _ => HasPresetEditorTarget);
        _exportPresetEditorCommand = new RelayCommand(_ => ExportPresetEditor(), _ => HasPresetEditorTarget);
        _importPresetEditorCommand = new RelayCommand(_ => ImportPresetEditor());
        _snapshotGroupCommand = new RelayCommand(
            _ => _ = DetachedTask.RunAsync(SnapshotSelectedGroupAsync, "snapshot-selected-group"),
            _ => CanSnapshotSelectedGroup());
        _backupGroupCommand = new RelayCommand(
            _ => _ = DetachedTask.RunAsync(BackupSelectedGroupAsync, "backup-selected-group"),
            _ => CanBackupSelectedGroup());
        _disableAutoBackupGroupCommand = new RelayCommand(
            _ => _ = DetachedTask.RunAsync(() => SetAutoBackupForSelectedGroupAsync(false), "disable-auto-backup-selected-group"),
            _ => CanDisableAutoBackupForSelectedGroup());
        _enableAutoBackupGroupCommand = new RelayCommand(
            _ => _ = DetachedTask.RunAsync(() => SetAutoBackupForSelectedGroupAsync(true), "enable-auto-backup-selected-group"),
            _ => CanEnableAutoBackupForSelectedGroup());
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
        _applyTagToGroupCommand = new RelayCommand(
            _ => _ = DetachedTask.RunAsync(() => SetTagForSelectedGroupAsync(add: true), "apply-tag-selected-group"),
            _ => CanSetTagForSelectedGroup());
        _removeTagFromGroupCommand = new RelayCommand(
            _ => _ = DetachedTask.RunAsync(() => SetTagForSelectedGroupAsync(add: false), "remove-tag-selected-group"),
            _ => CanSetTagForSelectedGroup());
        _selectGroupTagCommand = new RelayCommand(tag => SelectGroupTag(tag as string));
        _removeGroupTagCommand = new RelayCommand(tag => RemoveGroupTag(tag as string), _ => true);
        OpenFolderCommand = _openFolderCommand;
        RemoveProjectCommand = _removeProjectCommand;
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
        CommitProjectTagInputCommand = _commitProjectTagInputCommand;
        RemoveProjectTagCommand = _removeProjectTagCommand;
        AddExistingTagToSelectedProjectCommand = _addExistingTagToSelectedProjectCommand;
        ToggleProjectTagColorEditorCommand = _toggleProjectTagColorEditorCommand;
        ApplyProjectTagColorCommand = _applyProjectTagColorCommand;
        ResetProjectTagColorCommand = _resetProjectTagColorCommand;
        ApplyProjectTagColorSwatchCommand = _applyProjectTagColorSwatchCommand;
        ApplyTagToGroupCommand = _applyTagToGroupCommand;
        RemoveTagFromGroupCommand = _removeTagFromGroupCommand;
        SelectGroupTagCommand = _selectGroupTagCommand;
        RemoveGroupTagCommand = _removeGroupTagCommand;
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
        if (_allProjects.Count > 0 || IsLoading)
            return;

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

    private static void NotifySnapshotOutcome(string message, bool success)
    {
        var cfg = AppConfigStore.GetSnapshot();

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
        if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
        {
            Interlocked.Exchange(ref _refreshQueued, 1);
            return;
        }

        try
        {
            IsLoading = true;

            var config = await Task.Run(AppConfigStore.GetSnapshot);
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

        // Try to open the shared DB so we can enrich projects with real snapshot data.
        SqliteRepository? repo = null;
        List<Project>? registeredProjects = null;
        Dictionary<string, Project>? projectsByName = null;
        IReadOnlyDictionary<int, (DateTime CreatedUtc, long TotalBytes)>? latestSnapshotsByProject = null;
        Dictionary<int, Backup>? latestBackupsByProject = null;
        try
        {
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();

            repo = new SqliteRepository(dbPath);
            registeredProjects = [.. repo.GetAllProjects()];
            projectsByName = registeredProjects
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            latestSnapshotsByProject = repo.GetLatestSnapshotInfoByProject();
            latestBackupsByProject = repo.GetLatestBackupsPerProject()
                .GroupBy(b => b.ProjectId)
                .ToDictionary(g => g.Key, g => g.First());
        }
        catch
        {
        }

        var projectSources = new List<DiscoveredProject>(discovered);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in discovered)
        {
            var normalized = NormalizeProjectPath(item.Path);
            if (!string.IsNullOrWhiteSpace(normalized))
                seenPaths.Add(normalized);
        }

        if (registeredProjects is { Count: > 0 })
        {
            foreach (var project in registeredProjects)
            {
                var rootPath = project.RootPath?.Trim();
                if (string.IsNullOrWhiteSpace(rootPath))
                    continue;

                var normalizedRoot = NormalizeProjectPath(rootPath);
                if (!string.IsNullOrWhiteSpace(normalizedRoot) && seenPaths.Contains(normalizedRoot))
                    continue;

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
        }

        if (projectSources.Count == 0)
            return [];

        var items = new List<ProjectItemViewModel>();
        foreach (var p in projectSources)
        {
            var normalizedPath = NormalizeProjectPath(p.Path);
            if (!string.IsNullOrWhiteSpace(normalizedPath) && hiddenPaths.Contains(normalizedPath))
                continue;

            DateTime? lastSnapshotTime = p.LastSnapshotTime;
            long? lastSnapshotBytes = p.LastSnapshotSizeBytes;
            List<ProjectSnapshotViewModel>? snapshotVms = null;
            Project? existingProject = null;

            if (repo != null)
            {
                try
                {
                    // Use DB snapshot history if the project is registered.
                    projectsByName?.TryGetValue(p.Name, out existingProject);

                    if (existingProject != null)
                    {
                        if (latestSnapshotsByProject?.TryGetValue(existingProject.Id, out var latestSnapshot) == true)
                        {
                            lastSnapshotTime = latestSnapshot.CreatedUtc;
                            lastSnapshotBytes = latestSnapshot.TotalBytes;
                        }

                        if (latestBackupsByProject?.TryGetValue(existingProject.Id, out var latestBackup) == true)
                        {
                            if (!lastSnapshotTime.HasValue || latestBackup.CreatedUtc > lastSnapshotTime.Value)
                            {
                                lastSnapshotTime = latestBackup.CreatedUtc;
                                lastSnapshotBytes = latestBackup.TotalBytes;
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            var vm = new ProjectItemViewModel
            {
                Name = p.Name,
                Path = p.Path,
                ProjectId = existingProject?.Id ?? 0,
                ExternalId = existingProject?.ExternalId ?? string.Empty,
                LastSnapshot = lastSnapshotTime ?? default,
                SizeBytes = lastSnapshotBytes ?? 0,
                Preset = existingProject?.Preset ?? string.Empty,
                TagsCsv = existingProject?.Tags ?? string.Empty,
                PreferredDestinationId = existingProject?.PreferredDestinationId ?? string.Empty,
                EncryptionPolicy = ProjectEncryptionPolicy.Normalize(existingProject?.EncryptionPolicy),
                EncryptionKeyRef = existingProject?.EncryptionKeyRef ?? string.Empty
            };
            vm.SetAvatarFromNameAndStore(p.Path, AvatarStore.GetAvatarForProject(p.Path), vm.ExternalId);
            UpdateProjectDestinationDisplay(vm, config);
            UpdateProjectEncryptionDisplay(vm, config);
            UpdateProjectPresetDisplay(vm);
            vm.PropertyChanged += OnProjectItemPropertyChanged;

            // Populate snapshot history from DB if available; otherwise fall back to discovery values.
            if (snapshotVms?.Count > 0)
            {
                vm.SetSnapshots(snapshotVms);
            }
            else if (p.LastSnapshotTime.HasValue && p.LastSnapshotSizeBytes.HasValue)
            {
                var snapshotVm = new ProjectSnapshotViewModel(
                    p.LastSnapshotTime.Value,
                    p.LastSnapshotSizeBytes.Value);

                vm.SetSnapshots([snapshotVm]);
                vm.SnapshotHistoryLoaded = true;
            }
            else
            {
                vm.SetSnapshots([]);
            }

            // Mark whether this project is registered in the backup DB.
            var isRegistered = existingProject is not null;
            vm.IsRegistered = isRegistered;
            if (!isRegistered)
            {
                vm.SnapshotHistoryLoaded = true;
            }

            ApplyProjectHealth(vm, lastSnapshotTime, isRegistered);

            var resolvedPreset = ResolveRequiredPreset(vm);
            if (!string.Equals(vm.Preset, resolvedPreset, StringComparison.Ordinal))
            {
                vm.Preset = resolvedPreset;
                if (isRegistered && existingProject is not null && repo is not null)
                {
                    try
                    {
                        repo.UpdateProjectPreset(existingProject.Id, resolvedPreset);
                    }
                    catch (Exception ex)
                    {
                        DiagnosticsLogger.Record($"Project preset fallback persist failed for '{vm.Name}': {ex.GetType().Name} - {ex.Message}");
                    }
                }
            }
            UpdateProjectPresetRecommendation(vm);

            items.Add(vm);
        }

        return items;
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

        var hasSecret = !string.IsNullOrWhiteSpace(CredentialVault.Instance.GetSecret(
            string.IsNullOrWhiteSpace(vm.EncryptionKeyRef) ? null : vm.EncryptionKeyRef,
            BackupEncryptionSecretUsername,
            preferKeychain: true,
            fallbackPlaintext: null));
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
            vm.EncryptionBadgeBackground = "#2F3650";
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
        IEnumerable<ProjectItemViewModel> filtered = _allProjects;

        var selectedGroupId = SelectedGroup?.Id ?? ProjectGroupOption.AllId;
        if (!string.Equals(selectedGroupId, ProjectGroupOption.AllId, StringComparison.OrdinalIgnoreCase))
        {
            filtered = filtered.Where(p => ProjectMatchesGroup(p, selectedGroupId));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var terms = SearchText
                .Split((char[])null!, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            filtered = filtered.Where(p => MatchesSearchTerms(p, terms));
        }

        IOrderedEnumerable<ProjectItemViewModel> ordered = SortMode switch
        {
            ProjectSortMode.LastSnapshot => filtered
                .OrderByDescending(p => p.LastSnapshot == default ? DateTime.MinValue : p.LastSnapshot)
                .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
        };

        var newList = ordered.ToList();

        // Sync Projects collection to newList
        for (int i = 0; i < newList.Count; i++)
        {
            var item = newList[i];
            if (i < Projects.Count && ReferenceEquals(Projects[i], item))
                continue;

            var currentIndex = Projects.IndexOf(item);
            if (currentIndex >= 0)
            {
                Projects.Move(currentIndex, i);
            }
            else
            {
                Projects.Insert(i, item);
            }
        }

        // Remove any extra items not in newList
        for (int i = Projects.Count - 1; i >= newList.Count; i--)
        {
            Projects.RemoveAt(i);
        }

        // Keep selection valid
        if (SelectedProject != null && !Projects.Contains(SelectedProject))
        {
            SelectedProject = autoSelectIfNone && Projects.Count > 0 ? Projects[0] : null;
        }
        else if (SelectedProject == null)
        {
            var restore = !string.IsNullOrWhiteSpace(_lastSelectedProjectName)
                ? Projects.FirstOrDefault(p => string.Equals(p.Name, _lastSelectedProjectName, StringComparison.OrdinalIgnoreCase))
                : null;

            if (restore is not null)
            {
                SelectedProject = restore;
            }
            else if (autoSelectIfNone && Projects.Count > 0)
            {
                SelectedProject = Projects[0];
            }
        }

        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(ShowProjectsEmptyState));
        OnPropertyChanged(nameof(HasSelectedProject));
        OnPropertyChanged(nameof(ShowSelectedProjectEmptyState));
    }

    private void LoadGroupOptions()
    {
        GroupOptions.Clear();
        GroupOptions.Add(new ProjectGroupOption(ProjectGroupOption.AllId, L("Projects.Group.All", "All projects")));
        GroupOptions.Add(new ProjectGroupOption("work", L("Projects.Group.Work", "Work")));
        GroupOptions.Add(new ProjectGroupOption("games", L("Projects.Group.Games", "Games")));
        GroupOptions.Add(new ProjectGroupOption("media", L("Projects.Group.Media", "Media")));
        GroupOptions.Add(new ProjectGroupOption("critical", L("Projects.Group.Critical", "Critical")));
        GroupOptions.Add(new ProjectGroupOption("archive", L("Projects.Group.Archive", "Archive")));
        SelectedGroup = GroupOptions.FirstOrDefault();
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
        var selected = GroupTagInput;
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

        if (string.IsNullOrWhiteSpace(selected) ||
            allTags.Any(t => string.Equals(t, selected, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        GroupTagInput = string.Empty;
    }

    private void ConsumeGroupTagInputDelimiters()
    {
        var input = GroupTagInput;
        if (string.IsNullOrWhiteSpace(input))
            return;

        var separators = new[] { ',', '\n', '\r', ';' };
        if (input.IndexOfAny(separators) < 0)
            return;

        var trailingDelimiter = separators.Contains(input[^1]);
        var parts = input.Split(separators, StringSplitOptions.None);
        var completeCount = trailingDelimiter ? parts.Length : Math.Max(parts.Length - 1, 0);

        for (var i = 0; i < completeCount; i++)
            TryAddGroupTagChip(parts[i]);

        var remainder = trailingDelimiter ? string.Empty : parts.LastOrDefault()?.Trim() ?? string.Empty;
        if (!string.Equals(GroupTagInput, remainder, StringComparison.Ordinal))
            GroupTagInput = remainder;
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

        var cfg = AppConfigStore.Load();
        cfg.Appearance.TagColors ??= new Dictionary<string, TagColorConfig>(StringComparer.OrdinalIgnoreCase);
        cfg.Appearance.TagColors[tag] = ProjectTagAppearance.BuildConfigFromAccent(ProjectTagColorHex);
        AppConfigStore.Save(cfg);
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

        var cfg = AppConfigStore.Load();
        cfg.Appearance.TagColors ??= new Dictionary<string, TagColorConfig>(StringComparer.OrdinalIgnoreCase);
        cfg.Appearance.TagColors.Remove(tag);
        AppConfigStore.Save(cfg);
        RefreshProjectTagAppearance(cfg);
        ShowNotification(Lf("Projects.Tags.ColorReset", "Reset tag '{0}' to the default palette.", tag));
    }

    private void RefreshProjectTagAppearance(AppConfig? config = null)
    {
        config ??= AppConfigStore.GetSnapshot();
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
        var cfg = AppConfigStore.GetSnapshot();
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
                OnPropertyChanged(nameof(ProjectTagColorRed));
                OnPropertyChanged(nameof(ProjectTagColorGreen));
                OnPropertyChanged(nameof(ProjectTagColorBlue));

                ProjectTagAppearance.RgbToHsv(red, green, blue, out var hue, out var saturation, out var value);
                _projectTagColorHue = hue;
                _projectTagColorSaturation = saturation;
                _projectTagColorValue = value;
                OnPropertyChanged(nameof(ProjectTagColorHue));
                OnPropertyChanged(nameof(ProjectTagColorSaturation));
                OnPropertyChanged(nameof(ProjectTagColorValue));
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
            OnPropertyChanged(nameof(ProjectTagColorRed));
            OnPropertyChanged(nameof(ProjectTagColorGreen));
            OnPropertyChanged(nameof(ProjectTagColorBlue));

            ProjectTagAppearance.RgbToHsv(red, green, blue, out var hue, out var saturation, out var value);
            _projectTagColorHue = hue;
            _projectTagColorSaturation = saturation;
            _projectTagColorValue = value;
            OnPropertyChanged(nameof(ProjectTagColorHue));
            OnPropertyChanged(nameof(ProjectTagColorSaturation));
            OnPropertyChanged(nameof(ProjectTagColorValue));
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
        OnPropertyChanged(nameof(ProjectTagColorPreviewBackground));
        OnPropertyChanged(nameof(ProjectTagColorPreviewForeground));
        OnPropertyChanged(nameof(ProjectTagColorPreviewBorder));
    }

    private void ApplyProjectTagColorSwatch(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return;

        ProjectTagColorHex = hex.Trim();
    }

    private void SelectGroupTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;

        TryAddGroupTagChip(tag);
        if (!string.IsNullOrWhiteSpace(GroupTagInput))
            GroupTagInput = string.Empty;
    }

    private void RemoveGroupTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;

        var existing = SelectedGroupTags.FirstOrDefault(t =>
            string.Equals(t.Value, tag, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            return;

        SelectedGroupTags.Remove(existing);
        _applyTagToGroupCommand.RaiseCanExecuteChanged();
        _removeTagFromGroupCommand.RaiseCanExecuteChanged();
    }

    private bool TryAddGroupTagChip(string? token)
    {
        token = NormalizeTag(token);
        if (string.IsNullOrWhiteSpace(token))
            return false;

        if (SelectedGroupTags.Any(t => string.Equals(t.Value, token, StringComparison.OrdinalIgnoreCase)))
            return false;

        SelectedGroupTags.Add(ProjectTagChip.Create(token, ProjectTagAppearance.TryLoadConfig()));
        _applyTagToGroupCommand.RaiseCanExecuteChanged();
        _removeTagFromGroupCommand.RaiseCanExecuteChanged();
        return true;
    }

    private List<string> GetPendingGroupTags()
    {
        var tags = SelectedGroupTags
            .Select(t => NormalizeTag(t.Value))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        var input = GroupTagInput;
        if (!string.IsNullOrWhiteSpace(input))
        {
            tags.AddRange(input
                .Split([',', ';', '|', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeTag)
                .Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        return [.. tags.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static bool ProjectMatchesGroup(ProjectItemViewModel project, string groupId)
    {
        var tagSet = ParseTags(project.TagsCsv);
        var preset = project.Preset ?? string.Empty;

        bool Tagged(params string[] tags) =>
            tags.Any(tag => tagSet.Contains(tag, StringComparer.OrdinalIgnoreCase));

        return groupId.ToLowerInvariant() switch
        {
            "work" => Tagged("work", "client", "business", "job", "office"),
            "games" => Tagged("games", "game", "mod", "steam") ||
                       preset is "unity" or "unreal" or "godot" or "gamemaker" or "steam_mods",
            "media" => Tagged("media", "photo", "photos", "video", "music", "creative") ||
                       preset is "blender" or "video" or "premiere" or "after_effects" or "davinci" or "creative_suite" or "photos",
            "critical" => Tagged("critical", "important", "prod", "production") ||
                          project.Health == ProjectHealthStatus.OutOfDate,
            "archive" => Tagged("archive", "legacy", "cold", "old"),
            _ => true
        };
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

    private bool CanSnapshotSelectedGroup()
    {
        return GetSelectedGroupRegisteredProjectIds().Count > 0;
    }

    private bool CanBackupSelectedGroup() => GetSelectedGroupRegisteredProjectIds().Count > 0;

    private bool CanSetTagForSelectedGroup()
    {
        return GetSelectedGroupRegisteredProjectIds().Count > 0 &&
               GetPendingGroupTags().Count > 0;
    }

    private bool CanDisableAutoBackupForSelectedGroup()
    {
        var ids = GetSelectedGroupRegisteredProjectIds();
        if (ids.Count == 0)
            return false;

        return ids.Any(id => !_autoBackupDisabledProjectIds.Contains(id));
    }

    private bool CanEnableAutoBackupForSelectedGroup()
    {
        var ids = GetSelectedGroupRegisteredProjectIds();
        if (ids.Count == 0)
            return false;

        return ids.Any(_autoBackupDisabledProjectIds.Contains);
    }

    private void RefreshGroupAutoBackupStateFromConfig(AppConfig? config = null)
    {
        config ??= AppConfigStore.GetSnapshot();
        _autoBackupDisabledProjectIds = [.. config.Backups.AutoBackupDisabledProjects ?? []];
        _disableAutoBackupGroupCommand.RaiseCanExecuteChanged();
        _enableAutoBackupGroupCommand.RaiseCanExecuteChanged();
    }

    private List<int> GetSelectedGroupRegisteredProjectIds()
    {
        var selectedGroupId = SelectedGroup?.Id ?? ProjectGroupOption.AllId;
        return [.. _allProjects
            .Where(p =>
                p.IsRegistered &&
                (string.Equals(selectedGroupId, ProjectGroupOption.AllId, StringComparison.OrdinalIgnoreCase) ||
                 ProjectMatchesGroup(p, selectedGroupId)))
            .Select(p => p.ProjectId)
            .Distinct()];
    }

    private async Task SetTagForSelectedGroupAsync(bool add)
    {
        var ids = GetSelectedGroupRegisteredProjectIds();
        var tagsToProcess = GetPendingGroupTags();
        if (ids.Count == 0 || tagsToProcess.Count == 0)
            return;

        await Task.Run(() =>
        {
            var config = AppConfigStore.GetSnapshot();
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();

            var repo = new SqliteRepository(dbPath);
            var projectsById = repo.GetAllProjects().ToDictionary(p => p.Id);
            foreach (var projectId in ids)
            {
                if (!projectsById.TryGetValue(projectId, out var project))
                    continue;

                var tags = ParseTags(project.Tags);
                var changed = false;
                foreach (var tag in tagsToProcess)
                {
                    if (add)
                    {
                        if (tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                            continue;
                        tags.Add(tag);
                        changed = true;
                    }
                    else
                    {
                        var removed = tags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                        if (removed > 0)
                            changed = true;
                    }
                }

                if (!changed)
                    continue;

                var csv = string.Join(", ", tags);
                repo.UpdateProjectTags(projectId, csv);
                var vm = _allProjects.FirstOrDefault(p => p.ProjectId == projectId);
                if (vm is not null)
                    vm.TagsCsv = csv;
            }
        }).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SelectedGroupTags.Clear();
            GroupTagInput = string.Empty;
            _applyTagToGroupCommand.RaiseCanExecuteChanged();
            _removeTagFromGroupCommand.RaiseCanExecuteChanged();
            RefreshReusableProjectTags();
            ApplyFilterAndSort(autoSelectIfNone: false);
            ShowNotification(
                add
                    ? Lf("Projects.Group.TagApplied", "Applied {0} tag(s) to {1} projects.", tagsToProcess.Count, ids.Count)
                    : Lf("Projects.Group.TagRemoved", "Removed {0} tag(s) from {1} projects.", tagsToProcess.Count, ids.Count),
                NotificationSeverity.Info);
        });
    }

    private async Task SnapshotSelectedGroupAsync()
    {
        if (!CanSnapshotSelectedGroup())
            return;

        try
        {
            var config = await Task.Run(AppConfigStore.GetSnapshot).ConfigureAwait(false);
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();
            var maxSnapshotsToKeep = config.Backups.MaxSnapshotsPerProject;
            var fullHash = config.Backups.UseFullSnapshotHash;
            var enableScanCache = config.Backups.EnableScanCache;
            var aggressiveScanCache = config.Backups.AggressiveScanCache;
            var selectedGroupId = SelectedGroup?.Id ?? ProjectGroupOption.AllId;

            var targets = _allProjects
                .Where(p =>
                    p.IsRegistered &&
                    (string.Equals(selectedGroupId, ProjectGroupOption.AllId, StringComparison.OrdinalIgnoreCase) ||
                     ProjectMatchesGroup(p, selectedGroupId)))
                .ToList();

            if (targets.Count == 0)
                return;

            var repo = new SqliteRepository(dbPath);
            var existingByName = repo.GetAllProjects()
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var hashService = new HashService();
            var snapshotService = new SnapshotService(repo, hashService);

            var success = 0;
            var failure = 0;

            foreach (var target in targets)
            {
                if (!existingByName.TryGetValue(target.Name, out var existing))
                    continue;

                try
                {
                    await snapshotService.CreateSnapshotAsync(
                        existing,
                        fullHash: fullHash,
                        hashNow: true,
                        maxSnapshotsToKeep: maxSnapshotsToKeep,
                        ct: CancellationToken.None,
                        progressCallback: null,
                        useScanCache: enableScanCache,
                        aggressiveScanCache: aggressiveScanCache).ConfigureAwait(false);
                    success++;
                }
                catch
                {
                    failure++;
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (success > 0)
                {
                    ShowNotification(
                        Lf("Projects.Group.SnapshotSuccess", "Created snapshots for {0} projects.", success),
                        NotificationSeverity.Info);
                }

                if (failure > 0)
                {
                    ShowNotification(
                        Lf("Projects.Group.SnapshotFailure", "Failed to create snapshots for {0} projects.", failure),
                        NotificationSeverity.Warning);
                }
            });

            await RefreshAsync(forceDiscovery: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ShowNotification(
                    Lf("Projects.Group.SnapshotError", "Failed to run grouped snapshot operation: {0}", ex.Message),
                    NotificationSeverity.Error);
            });
        }
    }

    private async Task BackupSelectedGroupAsync()
    {
        var ids = GetSelectedGroupRegisteredProjectIds();
        if (ids.Count == 0)
            return;

        BackupGroupRequested?.Invoke(ids);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ShowNotification(
                Lf("Projects.Group.BackupQueued", "Queued backup for {0} projects.", ids.Count),
                NotificationSeverity.Info);
        });
    }

    private async Task SetAutoBackupForSelectedGroupAsync(bool enabled)
    {
        var ids = GetSelectedGroupRegisteredProjectIds();
        if (ids.Count == 0)
            return;

        await Task.Run(() =>
        {
            var cfg = AppConfigStore.Load();
            var disabled = cfg.Backups.AutoBackupDisabledProjects ?? [];
            disabled = enabled
                ? [.. disabled.Except(ids).Distinct()]
                : [.. disabled.Concat(ids).Distinct()];
            cfg.Backups.AutoBackupDisabledProjects = disabled;
            AppConfigStore.Save(cfg);
        }).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (enabled)
                _autoBackupDisabledProjectIds.ExceptWith(ids);
            else
                _autoBackupDisabledProjectIds.UnionWith(ids);

            ShowNotification(
                enabled
                    ? Lf("Projects.Group.AutoBackupEnabled", "Enabled auto backups for {0} projects.", ids.Count)
                    : Lf("Projects.Group.AutoBackupDisabled", "Disabled auto backups for {0} projects.", ids.Count),
                NotificationSeverity.Info);
            _disableAutoBackupGroupCommand.RaiseCanExecuteChanged();
            _enableAutoBackupGroupCommand.RaiseCanExecuteChanged();
            AutoBackupGroupPreferenceChanged?.Invoke(ids, enabled);
        });
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
        bool Has(string relativePath)
        {
            try
            {
                return File.Exists(Path.Combine(projectPath, relativePath));
            }
            catch
            {
                return false;
            }
        }

        bool HasDir(string relativePath)
        {
            try
            {
                return Directory.Exists(Path.Combine(projectPath, relativePath));
            }
            catch
            {
                return false;
            }
        }

        bool HasAny(string pattern)
        {
            try
            {
                if (!Directory.Exists(projectPath))
                {
                    return false;
                }

                return Directory.EnumerateFiles(
                        projectPath,
                        pattern,
                        new EnumerationOptions
                        {
                            RecurseSubdirectories = true,
                            IgnoreInaccessible = true,
                            ReturnSpecialDirectories = false
                        })
                    .Take(1)
                    .Any();
            }
            catch
            {
                return false;
            }
        }

        bool HasAnyPath(params string[] relativePaths)
        {
            foreach (var path in relativePaths)
            {
                if (Has(path))
                    return true;
            }

            return false;
        }

        PresetRecommendation? Build(string presetName, string reasonKey, string reasonFallback)
        {
            var availablePreset = PresetAvailable(presetName);
            if (string.IsNullOrWhiteSpace(availablePreset))
                return null;

            return new PresetRecommendation(availablePreset, L(reasonKey, reasonFallback));
        }

        if (HasDir("Assets") && HasDir("ProjectSettings"))
        {
            return Build(
                "unity",
                "Projects.Preset.Recommendation.Reason.Unity",
                "Detected Unity project layout (Assets + ProjectSettings).");
        }

        if (Has("project.godot"))
        {
            return Build(
                "godot",
                "Projects.Preset.Recommendation.Reason.Godot",
                "Detected Godot project marker (project.godot).");
        }

        if (HasAny("*.uproject"))
        {
            return Build(
                "unreal",
                "Projects.Preset.Recommendation.Reason.Unreal",
                "Detected Unreal project file (*.uproject).");
        }

        if (Has("Cargo.toml"))
        {
            return Build(
                "rust",
                "Projects.Preset.Recommendation.Reason.Rust",
                "Detected Rust project marker (Cargo.toml).");
        }

        var hasNodeMarker = Has("package.json");
        var hasNodeConfidence = HasAnyPath("package-lock.json", "yarn.lock", "pnpm-lock.yaml", "tsconfig.json", "vite.config.ts", "vite.config.js");
        if (hasNodeMarker && hasNodeConfidence)
        {
            return Build(
                "node",
                "Projects.Preset.Recommendation.Reason.Node",
                "Detected JavaScript/Node project markers (package.json + lock/build config).");
        }

        var hasPythonMarker = Has("pyproject.toml") || Has("requirements.txt");
        var hasPythonConfidence = HasAnyPath("setup.py", "poetry.lock", "Pipfile", "tox.ini");
        if (hasPythonMarker && hasPythonConfidence)
        {
            return Build(
                "python",
                "Projects.Preset.Recommendation.Reason.Python",
                "Detected Python project markers (dependency file + project config).");
        }

        if (HasAny("*.axaml"))
        {
            return Build(
                "avalonia",
                "Projects.Preset.Recommendation.Reason.Avalonia",
                "Detected Avalonia UI files (*.axaml).");
        }

        var hasDotNetMarker = HasAny("*.csproj") || HasAny("*.sln");
        var hasDotNetConfidence = HasAnyPath("global.json", "Directory.Build.props", "Directory.Packages.props") || HasAny("*.cs");
        if (hasDotNetMarker && hasDotNetConfidence)
        {
            return Build(
                "dotnet",
                "Projects.Preset.Recommendation.Reason.DotNet",
                "Detected .NET solution/project files (*.sln/*.csproj).");
        }

        if (HasAny("*.blend"))
        {
            return Build(
                "blender",
                "Projects.Preset.Recommendation.Reason.Blender",
                "Detected Blender files (*.blend).");
        }

        if (HasAny("*.prproj"))
        {
            return Build(
                "video",
                "Projects.Preset.Recommendation.Reason.Video",
                "Detected video editing project files (*.prproj).");
        }

        return null;
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
            if (OperatingSystem.IsMacOS())
            {
                // macOS: use the 'open' command
                Process.Start("open", path);
            }
            else if (OperatingSystem.IsWindows())
            {
                // Windows: use explorer
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsLinux())
            {
                // Linux: try xdg-open
                Process.Start("xdg-open", path);
            }
            else
            {
                // Fallback: try default shell execute
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            ShowNotification(Lf("Projects.Notification.OpenFolderFailed", "Failed to open folder for '{0}'.", SelectedProject?.Name ?? string.Empty), NotificationSeverity.Error);
        }
    }

    private void RemoveProject()
    {
        if (SelectedProject is null)
            return;

        var removedProjectName = SelectedProject.Name;
        var removedProjectPath = SelectedProject.Path;

        _ = Task.Run(() =>
        {
            try
            {
                var config = AppConfigStore.GetSnapshot();
                var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                    ? config.DbPath
                    : GetDefaultDbPath();

                var repo = new SqliteRepository(dbPath);
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
            }
            catch (Exception)
            {
                Dispatcher.UIThread.Post(() =>
                    ShowNotification(Lf("Projects.Notification.RemoveFailed", "Failed to remove project '{0}' from the backup database.", removedProjectName), NotificationSeverity.Error));
            }
        });

        // Reset the selected project's details so the right panel no longer shows stale data.
        if (SelectedProject != null && SelectedProject.Name == removedProjectName)
        {
            SelectedProject.LastSnapshot = default;
            SelectedProject.SizeBytes = 0;
            SelectedProject.SetSnapshots([]);
            SelectedProject.Health = ProjectHealthStatus.OutOfDate;
            SelectedProject.HealthTag = L("Projects.Health.NotBackedUp", "Not backed up");
            SelectedProject.IsRegistered = false;
        }

        // After removing from DB, keep the project visible in the list but mark it as unregistered
        // so the primary action becomes "Add project" again.
        RemoveProjectFromCurrentList(removedProjectPath);
        _ = RefreshAsync(forceDiscovery: false);
    }

    private void TakeSnapshot()
    {
        _ = DetachedTask.RunAsync(TakeSnapshotCoreAsync, nameof(TakeSnapshotCoreAsync));
    }

    private async Task TakeSnapshotCoreAsync()
    {
        if (SelectedProject is null)
            return;

        try
        {
            // 1. Resolve DB path from shared AppConfig (with a sensible default).
            var config = await Task.Run(AppConfigStore.GetSnapshot);
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();
            var maxSnapshotsToKeep = config.Backups.MaxSnapshotsPerProject;
            var fullHash = config.Backups.UseFullSnapshotHash;
            var enableScanCache = config.Backups.EnableScanCache;
            var aggressiveScanCache = config.Backups.AggressiveScanCache;

            // 2. Open repository (schema already initialized at app startup).
            var repo = new SqliteRepository(dbPath);

            // 3. Check if project is already registered.
            var existing = repo.GetProjectByName(SelectedProject.Name);
            if (existing is null)
            {
                // Require a preset (or explicit "no preset") before registering the project.
                if (string.IsNullOrWhiteSpace(SelectedProject.Preset))
                {
                    ShowNotification(L("Projects.Preset.Required", "Please select a preset (or 'no preset') before adding this project."), NotificationSeverity.Error);
                    return;
                }
                // Register project instead of snapshot.
                var project = new Project
                {
                    Name = SelectedProject.Name,
                    RootPath = SelectedProject.Path,
                    Preset = SelectedProject.Preset,
                    Tags = SelectedProject.TagsCsv,
                    CreatedUtc = DateTime.UtcNow,
                    PreferredDestinationId = SelectedProject.PreferredDestinationId,
                    EncryptionPolicy = SelectedProject.EncryptionPolicy
                };

                var id = repo.AddProject(project);
                UnhideProjectPathInConfig(project.RootPath);
                ShowNotification(Lf("Projects.Notification.Registered", "Project '{0}' registered. Next click will create a snapshot.", project.Name), NotificationSeverity.Info);

                // Update UI label so next click becomes a real snapshot.
                SnapshotActionLabel = L("Snapshots.Action.Default", "Snapshot now");
                if (SelectedProject != null)
                {
                    SelectedProject.IsRegistered = true;
                }
                return;
            }

            if (config.Backups.PromptRestoreAfterImport && existing.NeedsRestore)
            {
                ShowNotification(L("Projects.Notification.RestoreRequired", "Imported history is newer. Consider restoring before creating new snapshots."), NotificationSeverity.Warning);
            }

            // 4. Run snapshot via Core engine.
            var hashService = new HashService();
            var snapshotService = new SnapshotService(repo, hashService);

            var snapshotId = await snapshotService.CreateSnapshotAsync(
                existing,
                fullHash: fullHash,
                hashNow: true,
                maxSnapshotsToKeep: maxSnapshotsToKeep,
                ct: CancellationToken.None,
                progressCallback: null,
                useScanCache: enableScanCache,
                aggressiveScanCache: aggressiveScanCache);
            var outcome = SnapshotService.LastOutcome;

            // Update the selected project's stats in the UI immediately, based on the DB state
            // after snapshot creation and retention have run.
            if (SelectedProject != null && outcome != null)
            {
                try
                {
                    var snapshotsFromDb = repo.GetSnapshotsForProject(existing.Name)?.ToList()
                                          ?? [];

                    if (snapshotsFromDb.Count > 0)
                    {
                        // Assume snapshots are returned newest-first, consistent with RefreshAsync.
                        var latest = snapshotsFromDb[0];
                        SelectedProject.LastSnapshot = latest.CreatedUtc;
                        SelectedProject.SizeBytes = latest.TotalBytes;

                        var history = snapshotsFromDb
                            .Take(10)
                            .Select(CreateProjectSnapshotViewModel);

                        SelectedProject.SetSnapshots(history);
                    }
                    else
                    {
                        // No snapshots remaining (should be rare, but handle it).
                        SelectedProject.LastSnapshot = default;
                        SelectedProject.SizeBytes = 0;
                        SelectedProject.SetSnapshots([]);
                    }

                    SelectedProject.Health = ProjectHealthStatus.Healthy;
                    SelectedProject.HealthTag = L("Projects.Health.Healthy", "Healthy");
                }
                catch (Exception ex)
                {
                    DiagnosticsLogger.Record($"Project snapshot UI refresh failed: {ex.GetType().Name} - {ex.Message}");
                }
            }
            if (SelectedProject != null)
            {
                var msg = Lf("Projects.Notification.SnapshotSuccess", "Snapshot created for '{0}'.", SelectedProject.Name);
                ShowNotification(msg, NotificationSeverity.Info);
                NotifySnapshotOutcome(msg, success: true);
            }
        }
        catch (Exception ex)
        {
            var msg = L("Projects.Notification.SnapshotFailure", "Snapshot failed. Check logs for details.");
            ShowNotification(msg, NotificationSeverity.Error);
            NotifySnapshotOutcome(msg, success: false);
        }

        // Refresh label/state after the operation.
        RefreshSelectedProjectRegistration();
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

    private static void HideProjectPathInConfig(string? projectPath)
    {
        var normalized = NormalizeProjectPath(projectPath);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var cfg = AppConfigStore.Load();
        cfg.Behavior.HiddenProjectPaths ??= [];
        var exists = cfg.Behavior.HiddenProjectPaths
            .Any(path => string.Equals(NormalizeProjectPath(path), normalized, StringComparison.OrdinalIgnoreCase));
        if (exists)
            return;

        cfg.Behavior.HiddenProjectPaths.Add(normalized);
        AppConfigStore.Save(cfg);
    }

    private static void UnhideProjectPathInConfig(string? projectPath)
    {
        var normalized = NormalizeProjectPath(projectPath);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        var cfg = AppConfigStore.Load();
        cfg.Behavior.HiddenProjectPaths ??= [];
        var originalCount = cfg.Behavior.HiddenProjectPaths.Count;
        cfg.Behavior.HiddenProjectPaths = [.. cfg.Behavior.HiddenProjectPaths.Where(path => !string.Equals(NormalizeProjectPath(path), normalized, StringComparison.OrdinalIgnoreCase))];
        if (cfg.Behavior.HiddenProjectPaths.Count == originalCount)
            return;

        AppConfigStore.Save(cfg);
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
            SnapshotActionLabel = L("Snapshots.Action.Default", "Snapshot now");
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

    private static ProjectRegistrationSnapshot LoadProjectRegistrationSnapshot(string projectName)
    {
        try
        {
            var config = AppConfigStore.GetSnapshot();
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();

            var repo = new SqliteRepository(dbPath);
            var existing = repo.GetProjectByName(projectName);
            return new ProjectRegistrationSnapshot(
                existing is null,
                existing?.Id ?? 0,
                existing?.Preset ?? string.Empty,
                existing?.Tags ?? string.Empty,
                existing?.PreferredDestinationId ?? string.Empty,
                ProjectEncryptionPolicy.Normalize(existing?.EncryptionPolicy),
                existing?.EncryptionKeyRef ?? string.Empty);
        }
        catch
        {
            return new ProjectRegistrationSnapshot(true, 0, string.Empty, string.Empty, string.Empty, ProjectEncryptionPolicy.Inherit, string.Empty);
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

        if (snapshot.Missing)
        {
            SnapshotActionLabel = L("Snapshots.Action.AddProject", "Add project");
            SelectedProject.IsRegistered = false;
            SelectedProject.ProjectId = 0;
            SelectedProject.EncryptionKeyRef = string.Empty;

            SelectedProject.Preset = ResolveRequiredPreset(SelectedProject);
            if (string.IsNullOrWhiteSpace(SelectedProject.TagsCsv))
            {
                SelectedProject.TagsCsv = string.Empty;
            }
        }
        else
        {
            SnapshotActionLabel = L("Snapshots.Action.Default", "Snapshot now");
            SelectedProject.IsRegistered = true;
            SelectedProject.ProjectId = snapshot.ProjectId;
            SelectedProject.Preset = ResolveRequiredPreset(SelectedProject, snapshot.Preset);
            SelectedProject.TagsCsv = snapshot.TagsCsv;
            SelectedProject.PreferredDestinationId = snapshot.PreferredDestinationId;
            SelectedProject.EncryptionPolicy = snapshot.EncryptionPolicy;
            SelectedProject.EncryptionKeyRef = snapshot.EncryptionKeyRef;
            var cfg = AppConfigStore.GetSnapshot();
            UpdateProjectDestinationDisplay(SelectedProject, cfg);
            UpdateProjectEncryptionDisplay(SelectedProject, cfg);
            UpdateProjectPresetDisplay(SelectedProject);
        }
    }

    private void OnProjectItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ProjectItemViewModel vm)
            return;

        var changedPreset = string.Equals(e.PropertyName, nameof(ProjectItemViewModel.Preset), StringComparison.Ordinal);
        var changedTags = string.Equals(e.PropertyName, nameof(ProjectItemViewModel.TagsCsv), StringComparison.Ordinal);
        var changedRecommendedPreset = string.Equals(e.PropertyName, nameof(ProjectItemViewModel.RecommendedPreset), StringComparison.Ordinal);
        var changedDestination = string.Equals(e.PropertyName, nameof(ProjectItemViewModel.PreferredDestinationId), StringComparison.Ordinal);
        var changedEncryption = string.Equals(e.PropertyName, nameof(ProjectItemViewModel.EncryptionPolicy), StringComparison.Ordinal);
        if (changedPreset)
        {
            UpdateProjectPresetDisplay(vm);
            UpdateProjectPresetRecommendation(vm);
            if (ReferenceEquals(vm, SelectedProject))
                LoadPresetEditorForSelectedProject();
        }

        if (changedRecommendedPreset && ReferenceEquals(vm, SelectedProject))
            _applyPresetRecommendationCommand.RaiseCanExecuteChanged();

        if (!changedPreset && !changedDestination && !changedEncryption && !changedTags)
            return;

        try
        {
            var config = AppConfigStore.GetSnapshot();
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();

            var repo = new SqliteRepository(dbPath);
            var project = repo.GetProjectByName(vm.Name);
            if (project is null)
                return;

            if (changedPreset)
            {
                repo.UpdateProjectPreset(project.Id, vm.Preset);
            }

            if (changedDestination)
            {
                repo.UpdateProjectPreferredDestination(project.Id, vm.PreferredDestinationId);
                UpdateProjectDestinationDisplay(vm, config);
            }

            if (changedEncryption)
            {
                repo.UpdateProjectEncryptionSettings(
                    project.Id,
                    vm.EncryptionPolicy,
                    string.IsNullOrWhiteSpace(vm.EncryptionKeyRef) ? null : vm.EncryptionKeyRef);
                UpdateProjectEncryptionDisplay(vm, config);
                ProjectEncryptionPolicyChanged?.Invoke(project.Id, vm.EncryptionPolicy);
            }

            if (changedTags)
            {
                repo.UpdateProjectTags(project.Id, vm.TagsCsv);
                RefreshReusableProjectTags();
                ApplyFilterAndSort(autoSelectIfNone: false);
                if (ReferenceEquals(vm, SelectedProject))
                    RefreshSelectedProjectTags();
            }

            if (changedPreset || changedDestination || changedTags)
                ProjectSettingsMetadataChanged?.Invoke(project.Id);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Projects] Failed to persist project settings for '{vm.Name}': {ex.Message}");
        }
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
                    var config = AppConfigStore.GetSnapshot();
                    var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                        ? config.DbPath
                        : GetDefaultDbPath();

                    var repo = new SqliteRepository(dbPath);
                    var snapshots = await repo.GetSnapshotsForProjectAsync(projectName);
                    return snapshots.ConvertAll(CreateProjectSnapshotViewModel);
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
            ? L("Snapshots.Action.Default", "Snapshot now")
            : L("Snapshots.Action.AddProject", "Add project");
        OnPropertyChanged(nameof(SortModeLabel));
        var config = AppConfigStore.GetSnapshot();
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
        catch (Exception ex)
        {

            // Fallback to a minimal hard-coded set so the UI stays usable.
            AvailablePresets.Clear();
            AvailablePresets.Add(GenericPresetId);
            AvailablePresets.Add("unity");
            AvailablePresets.Add("dotnet");
            AvailablePresets.Add("blender");
            AvailablePresets.Add("video");
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

        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            try
            {
                var indexPath = Path.Combine(dir, "presets.index.json");
                if (File.Exists(indexPath))
                {
                    var json = File.ReadAllText(indexPath);
                    var index = JsonSerializer.Deserialize<PresetIndex>(json);

                    if (index?.Presets != null)
                    {
                        foreach (var p in index.Presets)
                        {
                            if (!string.IsNullOrWhiteSpace(p.Id))
                            {
                                if (ids.Add(p.Id))
                                {
                                    presets.Add(p);
                                }
                            }
                            else if (!string.IsNullOrWhiteSpace(p.File))
                            {
                                var id = Path.GetFileNameWithoutExtension(p.File);
                                if (ids.Add(id))
                                {
                                    presets.Add(new PresetInfo
                                    {
                                        Id = id,
                                        File = p.File
                                    });
                                }
                            }
                        }
                    }
                }
                else
                {
                    // No index file: just enumerate preset files.
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
            }
            catch (Exception ex)
            {
                DiagnosticsLogger.Record($"Project preset catalog load failed: {ex.GetType().Name} - {ex.Message}");
            }
        }

        return presets.OrderBy(n => n.Id, StringComparer.OrdinalIgnoreCase);
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
            ReloadPresetEditor();
    }

    private void ReloadPresetEditor()
    {
        if (!TryResolveSelectedPresetPath(out var presetPath, out _))
            return;

        try
        {
            PresetEditorContent = File.Exists(presetPath)
                ? File.ReadAllText(presetPath)
                : string.Empty;
            PresetEditorStatus = File.Exists(presetPath)
                ? L("Projects.Preset.Editor.Status.Reloaded", "Preset rules reloaded.")
                : L("Projects.Preset.Editor.Status.NewFile", "Preset file does not exist yet. Save to create it.");
        }
        catch (Exception ex)
        {
            PresetEditorStatus = Lf("Projects.Preset.Editor.Status.ReloadFailed", "Failed to reload preset rules: {0}", ex.Message);
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
        OnPropertyChanged(nameof(PresetEditorToggleLabel));
        RaisePresetEditorCanExecuteChanged();
        ReloadPresetEditor();
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

    private static string GetDefaultDbPath()
    {
        // Fallback DB location when AppConfig.DbPath is not set.
        // Later this will be fully unified with the CLI DB resolution logic.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "VaultSync");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "vaultsync.db");
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
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
    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private static string Lf(string key, string fallback, params object[] args)
    {
        var fmt = L(key, fallback);
        return string.Format(CultureInfo.CurrentCulture, fmt, args);
    }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    private string _path = string.Empty;
    public string Path
    {
        get => _path;
        set => SetProperty(ref _path, value);
    }

    private string _externalId = string.Empty;
    public string ExternalId
    {
        get => _externalId;
        set => SetProperty(ref _externalId, value ?? string.Empty);
    }

    private int _projectId;
    public int ProjectId
    {
        get => _projectId;
        set => SetProperty(ref _projectId, value);
    }

    private ProjectHealthStatus _health;
    public ProjectHealthStatus Health
    {
        get => _health;
        set
        {
            if (SetProperty(ref _health, value))
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
        set => SetProperty(ref _healthTag, value);
    }

    private DateTime _lastSnapshot;
    public DateTime LastSnapshot
    {
        get => _lastSnapshot;
        set
        {
            if (SetProperty(ref _lastSnapshot, value))
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
            if (SetProperty(ref _sizeBytes, value))
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
        set => SetProperty(ref _preset, value);
    }

    private string _presetDescription = string.Empty;
    public string PresetDescription
    {
        get => _presetDescription;
        set => SetProperty(ref _presetDescription, value ?? string.Empty);
    }

    private string _presetExample = string.Empty;
    public string PresetExample
    {
        get => _presetExample;
        set => SetProperty(ref _presetExample, value ?? string.Empty);
    }

    private string _tagsCsv = string.Empty;
    public string TagsCsv
    {
        get => _tagsCsv;
        set
        {
            if (!SetProperty(ref _tagsCsv, value ?? string.Empty))
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
        set => SetProperty(ref _recommendedPreset, value ?? string.Empty);
    }

    private string _recommendedPresetReason = string.Empty;
    public string RecommendedPresetReason
    {
        get => _recommendedPresetReason;
        set => SetProperty(ref _recommendedPresetReason, value ?? string.Empty);
    }

    private string _preferredDestinationId = string.Empty;
    public string PreferredDestinationId
    {
        get => _preferredDestinationId;
        set => SetProperty(ref _preferredDestinationId, value ?? string.Empty);
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

            if (SetProperty(ref _preferredDestinationOption, value))
                PreferredDestinationId = value.Id;
        }
    }

    private string _preferredDestinationDisplay = string.Empty;
    public string PreferredDestinationDisplay
    {
        get => _preferredDestinationDisplay;
        set => SetProperty(ref _preferredDestinationDisplay, value ?? string.Empty);
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
        set => SetProperty(ref _encryptionPolicy, ProjectEncryptionPolicy.Normalize(value));
    }

    private string _encryptionKeyRef = string.Empty;
    public string EncryptionKeyRef
    {
        get => _encryptionKeyRef;
        set => SetProperty(ref _encryptionKeyRef, value ?? string.Empty);
    }

    private EncryptionPolicyOption? _encryptionPolicyOption;
    public EncryptionPolicyOption? EncryptionPolicyOption
    {
        get => _encryptionPolicyOption;
        set
        {
            if (SetProperty(ref _encryptionPolicyOption, value))
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
        set => SetProperty(ref _effectiveEncryptionDisplay, value ?? string.Empty);
    }

    private bool _hasEncryptionSecret;
    public bool HasEncryptionSecret
    {
        get => _hasEncryptionSecret;
        set => SetProperty(ref _hasEncryptionSecret, value);
    }

    private string _encryptionSecretStatus = string.Empty;
    public string EncryptionSecretStatus
    {
        get => _encryptionSecretStatus;
        set => SetProperty(ref _encryptionSecretStatus, value ?? string.Empty);
    }

    private string _encryptionBadgeText = string.Empty;
    public string EncryptionBadgeText
    {
        get => _encryptionBadgeText;
        set => SetProperty(ref _encryptionBadgeText, value ?? string.Empty);
    }

    private string _encryptionBadgeBackground = "#2F3650";
    public string EncryptionBadgeBackground
    {
        get => _encryptionBadgeBackground;
        set => SetProperty(ref _encryptionBadgeBackground, value ?? "#2F3650");
    }

    private string _encryptionBadgeForeground = "#C7D2FE";
    public string EncryptionBadgeForeground
    {
        get => _encryptionBadgeForeground;
        set => SetProperty(ref _encryptionBadgeForeground, value ?? "#C7D2FE");
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
        set => SetProperty(ref _isRegistered, value);
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
            : ProjectSnapshotViewModel.FormatSize(
                SnapshotHistory.Count == 0
                    ? 0
                    : (long)SnapshotHistory.Average(s => (double)s.SizeBytes));

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

            if (i == 0)
            {
                // first bar: neutral
                s.TrendColor = "#2F3650";
            }
            else
            {
                var prev = SnapshotHistory[i - 1];
                if (s.SizeBytes > prev.SizeBytes)
                {
                    // snapshot grew -> red warning
                    s.TrendColor = "#6A2E2E";
                }
                else if (s.SizeBytes < prev.SizeBytes)
                {
                    // snapshot shrank -> nice green
                    s.TrendColor = "#2E6A3E";
                }
                else
                {
                    s.TrendColor = "#2F3650";
                }
            }

            if (i == 0)
            {
                s.ShowDayLabel = true;
            }
            else
            {
                var prevDate = SnapshotHistory[i - 1].Timestamp.Date;
                s.ShowDayLabel = s.Timestamp.Date != prevDate;
            }

            if (s.ShowDayLabel)
            {
                s.DayLabel = s.Timestamp.ToString("dd/MM", CultureInfo.CurrentCulture);
            }
        }

        if (SnapshotHistory.Count > 0)
        {
            var last = SnapshotHistory[^1];
            if (!last.ShowDayLabel)
            {
                last.ShowDayLabel = true;
                last.DayLabel = last.Timestamp.ToString("dd/MM", CultureInfo.CurrentCulture);
            }
        }

        // Notify that aggregate snapshot stats have changed.
        OnPropertyChanged(nameof(SnapshotCount));
        OnPropertyChanged(nameof(TotalSnapshotSizeDisplay));
        OnPropertyChanged(nameof(AverageSnapshotSizeDisplay));
        OnPropertyChanged(nameof(DaysSinceLastSnapshotDisplay));
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
        OnPropertyChanged(nameof(LastSnapshotSummary));
        OnPropertyChanged(nameof(LastSnapshotShort));
        OnPropertyChanged(nameof(LatestSnapshotSizeDisplay));
        OnPropertyChanged(nameof(DaysSinceLastSnapshotDisplay));
    }

    public string SizeDisplay
    {
        get
        {
            double gb = SizeBytes / (1024d * 1024d * 1024d);
            if (gb < 0.01) return $"{SizeBytes / (1024d * 1024d):0.#} MB";
            return $"{gb:0.0} GB";
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

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public sealed class DestinationOption(string id, string label)
{
    public string Id { get; } = id ?? string.Empty;
    public string Label { get; } = label ?? string.Empty;

    public override string ToString() => Label;

    public override bool Equals(object? obj)
    {
        return obj is DestinationOption other &&
               string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
    }
}

public sealed class ProjectGroupOption(string id, string label)
{
    public const string AllId = "all";
    public string Id { get; } = string.IsNullOrWhiteSpace(id) ? AllId : id.Trim();
    public string Label { get; } = label ?? string.Empty;

    public override string ToString() => Label;

    public override bool Equals(object? obj)
    {
        return obj is ProjectGroupOption other &&
               string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
    }
}

public sealed class EncryptionPolicyOption(string id, string label)
{
    public string Id { get; } = ProjectEncryptionPolicy.Normalize(id);
    public string Label { get; } = label ?? string.Empty;

    public override string ToString() => Label;

    public override bool Equals(object? obj)
    {
        return obj is EncryptionPolicyOption other &&
               string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
    }
}

public sealed class RestoreModeOption(string id, string label)
{
    public string Id { get; } = ProjectRestoreMode.Normalize(id);
    public string Label { get; } = label ?? string.Empty;

    public override string ToString() => Label;

    public override bool Equals(object? obj)
    {
        return obj is RestoreModeOption other &&
               string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
    }
}

public sealed class VerificationPolicyOption(string id, string label)
{
    public string Id { get; } = ProjectVerificationPolicy.Normalize(id);
    public string Label { get; } = label ?? string.Empty;

    public override string ToString() => Label;

    public override bool Equals(object? obj)
    {
        return obj is VerificationPolicyOption other &&
               string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
    }

    public override int GetHashCode()
    {
        return StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
    }
}

public sealed class ProjectSnapshotViewModel(
    DateTime timestamp,
    long sizeBytes,
    int diffAdded = 0,
    int diffModified = 0,
    int diffDeleted = 0,
    long diffNetBytes = 0,
    IReadOnlyList<SnapshotDiffPathStat>? topChangedPaths = null)
{
    public DateTime Timestamp { get; } = timestamp;
    public long SizeBytes { get; } = sizeBytes;
    public int DiffAdded { get; } = Math.Max(0, diffAdded);
    public int DiffModified { get; } = Math.Max(0, diffModified);
    public int DiffDeleted { get; } = Math.Max(0, diffDeleted);
    public long DiffNetBytes { get; } = diffNetBytes;
    public IReadOnlyList<SnapshotDiffPathStat> TopChangedPaths { get; } = topChangedPaths ?? [];

    // Mini-chart data
    public double RelativeSize { get; set; }

    /// <summary>
    /// 24-80px bar height, based on RelativeSize.
    /// </summary>
    public double RelativeBarHeight => 24 + RelativeSize * 56;

    public double RelativeBarHeightCapped => Math.Max(16, RelativeBarHeight);

    /// <summary>
    /// Color used for the bar: neutral, up (red), down (green).
    /// </summary>
    public string TrendColor { get; set; } = "#2F3650";

    public bool ShowDayLabel { get; set; }

    public string DayLabel { get; set; } = string.Empty;

    public string DateDisplay => Timestamp.ToString("dd/MM/yyyy - HH:mm", CultureInfo.CurrentCulture);

    public string SizeDisplay => FormatSize(SizeBytes);

    public string DiffSummaryDisplay
    {
        get
        {
            var hasChanges = (DiffAdded > 0) || (DiffModified > 0) || (DiffDeleted > 0);
            if (!hasChanges && DiffNetBytes == 0)
                return L("Projects.DiffSummary.NoChanges", "No file changes detected or diff data is unavailable for this snapshot");

            return Lf(
                "Projects.DiffSummary.Compact",
                "+{0} / ~{1} / -{2}  Δ {3}",
                DiffAdded,
                DiffModified,
                DiffDeleted,
                FormatSignedSize(DiffNetBytes));
        }
    }

    public bool HasDiffTopPaths => TopChangedPaths.Count > 0;

    public string DiffTopPathsDisplay
    {
        get
        {
            if (TopChangedPaths.Count == 0)
                return L("Projects.DiffSummary.TopPaths.None", "Top paths: none");

            var preview = string.Join(
                ", ",
                TopChangedPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path.Path))
                    .Take(2)
                    .Select(path => $"{path.Path} ({path.Changes})"));

            return string.IsNullOrWhiteSpace(preview)
                ? L("Projects.DiffSummary.TopPaths.None", "Top paths: none")
                : Lf("Projects.DiffSummary.TopPaths.Compact", "Top paths: {0}", preview);
        }
    }

    // Used by tooltip: date + size in one string
    public string TooltipText => $"{DateDisplay}\n{SizeDisplay}";

    public static string FormatSize(long bytes) =>
        UiFormat.FormatBytes(bytes, "0.0");

    private static string FormatSignedSize(long value)
        => UiFormat.FormatSignedBytes(value, "0.0");

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private static string Lf(string key, string fallback, params object[] args)
    {
        var fmt = L(key, fallback);
        return string.Format(CultureInfo.CurrentCulture, fmt, args);
    }
}
