using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
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

/// <summary>
/// Projects page view model - drives the list on the left and the
/// details / actions panel on the right.
/// </summary>
public class ProjectsViewModel : ViewModelBase
{
    private const string BackupEncryptionSecretUsername = "vaultsync-backup-encryption";
    private readonly IProjectDiscoveryService _discovery = new ProjectDiscoveryService();
    private IReadOnlyList<DiscoveredProject> _cachedDiscovery = Array.Empty<DiscoveredProject>();
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
    /// <summary>
    /// Preset options that can be applied to projects. These correspond to
    /// .vaultsyncignore-style profiles (Unity, .NET, etc.) plus an explicit
    /// "no preset" option.
    /// </summary>
    public ObservableCollection<string> AvailablePresets { get; } =
        new ObservableCollection<string>();
    public ObservableCollection<DestinationOption> DestinationOptions { get; } =
        new ObservableCollection<DestinationOption>();
    public ObservableCollection<EncryptionPolicyOption> EncryptionPolicyOptions { get; } =
        new ObservableCollection<EncryptionPolicyOption>();
    public ObservableCollection<ProjectItemViewModel> Projects { get; } =
        new ObservableCollection<ProjectItemViewModel>();

    private ProjectItemViewModel? _selectedProject;
    private int _selectedProjectRefreshToken;
    private int _selectedProjectHistoryToken;
    private int _refreshInFlight;
    private int _refreshQueued;
    public ProjectItemViewModel? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (SetProperty(ref _selectedProject, value))
            {
                RefreshSelectedProjectRegistration();
                LoadSnapshotHistoryForSelectedProject();
            }
        }
    }

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
    public ICommand TakeSnapshotCommand => SnapshotCommand;
    public ICommand ManageProjectEncryptionCommand { get; }
    public ICommand ToggleSortCommand { get; }
    public event Action<ProjectItemViewModel>? EditProjectEncryptionRequested;
    public event Action<int, string>? ProjectEncryptionPolicyChanged;
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

    private readonly List<ProjectItemViewModel> _allProjects = new();
    private string _searchText = string.Empty;

    public ProjectsViewModel()
    {
        RefreshCommand = new RelayCommand(_ => Refresh());
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => SelectedProject is not null);
        RemoveProjectCommand = new RelayCommand(_ => RemoveProject(), _ => SelectedProject is not null);
        SnapshotCommand = new RelayCommand(_ => TakeSnapshot());
        ManageProjectEncryptionCommand = new RelayCommand(p => RequestProjectEncryptionPasswordEdit(p as ProjectItemViewModel ?? SelectedProject));
        ToggleSortCommand = new RelayCommand(_ => ToggleSortMode());

        LoadAvailablePresets();
        RefreshEncryptionPolicyOptions();

        _ = RefreshAsync();
    }

    private void ShowNotification(string message, NotificationSeverity severity = NotificationSeverity.Info)
    {
        Notification.Show(message, severity);
    }

    private void NotifySnapshotOutcome(string message, bool success)
    {
        var cfg = AppConfigStore.Load();

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

    private static ProjectSnapshotViewModel[] CreateDesignSnapshots(double baseGb)
    {
        long Bytes(double gb) => (long)(gb * 1024 * 1024 * 1024);

        return new[]
        {
            new ProjectSnapshotViewModel(DateTime.Today.AddDays(-1).AddHours(23).AddMinutes(40), Bytes(baseGb)),
            new ProjectSnapshotViewModel(DateTime.Today.AddDays(-3).AddHours(23),                Bytes(baseGb * 0.97)),
            new ProjectSnapshotViewModel(DateTime.Today.AddDays(-5).AddHours(22).AddMinutes(50), Bytes(baseGb * 0.94)),
            new ProjectSnapshotViewModel(DateTime.Today.AddDays(-7).AddHours(23),                Bytes(baseGb * 0.90))
        };
    }

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

            var config = await Task.Run(AppConfigStore.Load);
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
            foreach (var item in projectItems)
            {
                _allProjects.Add(item);
            }

            ApplyFilterAndSort();

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
            discovered = Array.Empty<DiscoveredProject>();
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
        // Try to open the shared DB so we can enrich projects with real snapshot data.
        SqliteRepository? repo = null;
        Dictionary<string, Project>? projectsByName = null;
        IReadOnlyDictionary<int, (DateTime CreatedUtc, long TotalBytes)>? latestSnapshotsByProject = null;
        Dictionary<int, Backup>? latestBackupsByProject = null;
        try
        {
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();

            repo = new SqliteRepository(dbPath);
            projectsByName = repo.GetAllProjects()
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

        if (discovered.Count == 0)
            return new List<ProjectItemViewModel>();

        var items = new List<ProjectItemViewModel>();
        foreach (var p in discovered)
        {
            DateTime? lastSnapshotTime = p.LastSnapshotTime;
            long? lastSnapshotBytes = p.LastSnapshotSizeBytes;
            List<ProjectSnapshotViewModel>? snapshotVms = null;
            Project? existingProject = null;

            if (repo != null)
            {
                try
                {
                    // Use DB snapshot history if the project is registered.
                    if (projectsByName != null)
                    {
                        projectsByName.TryGetValue(p.Name, out existingProject);
                    }

                    if (existingProject != null)
                    {
                        if (latestSnapshotsByProject != null &&
                            latestSnapshotsByProject.TryGetValue(existingProject.Id, out var latestSnapshot))
                        {
                            lastSnapshotTime = latestSnapshot.CreatedUtc;
                            lastSnapshotBytes = latestSnapshot.TotalBytes;
                        }

                        if (latestBackupsByProject != null &&
                            latestBackupsByProject.TryGetValue(existingProject.Id, out var latestBackup))
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
                PreferredDestinationId = existingProject?.PreferredDestinationId ?? string.Empty,
                EncryptionPolicy = ProjectEncryptionPolicy.Normalize(existingProject?.EncryptionPolicy),
                EncryptionKeyRef = existingProject?.EncryptionKeyRef ?? string.Empty
            };
            vm.SetAvatarFromNameAndStore(p.Path, AvatarStore.GetAvatarForProject(p.Path), vm.ExternalId);
            UpdateProjectDestinationDisplay(vm, config);
            UpdateProjectEncryptionDisplay(vm, config);
            vm.PropertyChanged += OnProjectItemPropertyChanged;

            // Populate snapshot history from DB if available; otherwise fall back to discovery values.
            if (snapshotVms != null && snapshotVms.Count > 0)
            {
                vm.SetSnapshots(snapshotVms);
            }
            else if (p.LastSnapshotTime.HasValue && p.LastSnapshotSizeBytes.HasValue)
            {
                var snapshotVm = new ProjectSnapshotViewModel(
                    p.LastSnapshotTime.Value,
                    p.LastSnapshotSizeBytes.Value);

                vm.SetSnapshots(new[] { snapshotVm });
                vm.SnapshotHistoryLoaded = true;
            }
            else
            {
                vm.SetSnapshots(Array.Empty<ProjectSnapshotViewModel>());
            }

            // Mark whether this project is registered in the backup DB.
            var isRegistered = existingProject is not null;
            vm.IsRegistered = isRegistered;
            if (!isRegistered)
            {
                vm.SnapshotHistoryLoaded = true;
            }

            ApplyProjectHealth(vm, lastSnapshotTime, isRegistered);

            // Auto-detect preset for unregistered projects if none is set yet.
            if (!isRegistered && string.IsNullOrWhiteSpace(vm.Preset))
            {
                var autoPreset = DetectPreset(p.Path);
                vm.Preset = autoPreset ?? string.Empty;
            }

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

                var id = string.IsNullOrWhiteSpace(dest.Alias) ? dest.Path : dest.Alias;
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
        var id = vm.PreferredDestinationId ?? string.Empty;
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

        var match = config.Backups.Destinations.FirstOrDefault(d =>
            string.Equals(d.Alias ?? string.Empty, id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(d.Path ?? string.Empty, id, StringComparison.OrdinalIgnoreCase));

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

    private void ApplyFilterAndSort()
    {
        IEnumerable<ProjectItemViewModel> filtered = _allProjects;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            filtered = filtered.Where(p =>
                (!string.IsNullOrEmpty(p.Name) && p.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(p.Path) && p.Path.Contains(term, StringComparison.OrdinalIgnoreCase)));
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
            SelectedProject = Projects.Count > 0 ? Projects[0] : null;
        }
        else if (SelectedProject == null && Projects.Count > 0)
        {
            SelectedProject = Projects[0];
        }
    }

    private string? DetectPreset(string projectPath)
    {
        // Simple heuristics: choose the first matching preset from known signals.
        // This does not override an explicitly chosen preset or a DB-stored preset.
        bool Has(string relativePath) => File.Exists(Path.Combine(projectPath, relativePath));
        bool HasDir(string relativePath) => Directory.Exists(Path.Combine(projectPath, relativePath));
        bool HasAny(string pattern) => Directory.EnumerateFiles(projectPath, pattern, SearchOption.AllDirectories).Any();

        // Prefer Avalonia when we see XAML + package references.
        if (HasAny("*.axaml"))
        {
            var csproj = Directory.EnumerateFiles(projectPath, "*.csproj", SearchOption.AllDirectories).FirstOrDefault();
            if (csproj != null)
            {
                try
                {
                    var text = File.ReadAllText(csproj);
                    if (text.IndexOf("Avalonia.", StringComparison.OrdinalIgnoreCase) >= 0)
                        return PresetAvailable("avalonia");
                }
                catch
                {
                }
            }

            // If .axaml exists, lean toward Avalonia even without package check.
            var avaloniaPreset = PresetAvailable("avalonia");
            if (!string.IsNullOrWhiteSpace(avaloniaPreset))
                return avaloniaPreset;
        }

        if (HasDir("Assets") && HasDir("ProjectSettings"))
            return PresetAvailable("unity");
        if (Has("project.godot"))
            return PresetAvailable("godot");
        if (HasAny("*.uproject"))
            return PresetAvailable("unreal");
        if (Has("Cargo.toml"))
            return PresetAvailable("rust");
        if (Has("package.json"))
            return PresetAvailable("node");
        if (HasAny("*.csproj") || HasAny("*.sln"))
            return PresetAvailable("dotnet");
        if (Has("pyproject.toml") || Has("requirements.txt"))
            return PresetAvailable("python");
        if (HasAny("*.blend"))
            return PresetAvailable("blender");
        if (HasAny("*.prproj"))
            return PresetAvailable("video");

        return null;
    }

    private string? PresetAvailable(string presetName)
    {
        return AvailablePresets.Any(p => p.Equals(presetName, StringComparison.OrdinalIgnoreCase))
            ? presetName
            : null;
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

        _ = Task.Run(() =>
        {
            try
            {
                var config = AppConfigStore.Load();
                var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                    ? config.DbPath
                    : GetDefaultDbPath();

                var repo = new SqliteRepository(dbPath);
                var existing = repo.GetProjectByName(removedProjectName);
                if (existing is null)
                {
                    Dispatcher.UIThread.Post(() =>
                        ShowNotification(Lf("Projects.Notification.RemoveMissing", "Project '{0}' was not registered in the backup database.", removedProjectName), NotificationSeverity.Warning));
                }
                else
                {
                    repo.RemoveProject(existing.Id);
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
            SelectedProject.SetSnapshots(Array.Empty<ProjectSnapshotViewModel>());
            SelectedProject.Health = ProjectHealthStatus.OutOfDate;
            SelectedProject.HealthTag = L("Projects.Health.NotBackedUp", "Not backed up");
            SelectedProject.IsRegistered = false;
        }

        // After removing from DB, keep the project visible in the list but mark it as unregistered
        // so the primary action becomes "Add project" again.
        RefreshSelectedProjectRegistration();
    }

    private void TakeSnapshot()
    {
        _ = RunDetachedAsync(TakeSnapshotCoreAsync, nameof(TakeSnapshotCoreAsync));
    }

    private async Task TakeSnapshotCoreAsync()
    {
        if (SelectedProject is null)
            return;

        try
        {
            // 1. Resolve DB path from shared AppConfig (with a sensible default).
            var config = await Task.Run(AppConfigStore.Load);
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
                    CreatedUtc = DateTime.UtcNow,
                    PreferredDestinationId = SelectedProject.PreferredDestinationId,
                    EncryptionPolicy = SelectedProject.EncryptionPolicy
                };

                var id = repo.AddProject(project);
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
                                          ?? new List<Snapshot>();

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
                        SelectedProject.SetSnapshots(Array.Empty<ProjectSnapshotViewModel>());
                    }

                    SelectedProject.Health = ProjectHealthStatus.Healthy;
                    SelectedProject.HealthTag = L("Projects.Health.Healthy", "Healthy");
                }
                catch (Exception ex)
                {
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

        _ = Task.Run(() =>
        {
            try
            {
                var config = AppConfigStore.Load();
                var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                    ? config.DbPath
                    : GetDefaultDbPath();

                var repo = new SqliteRepository(dbPath);
                var existing = repo.GetProjectByName(projectName);
                return (
                    existing is null,
                    existing?.Id ?? 0,
                    existing?.Preset ?? string.Empty,
                    existing?.PreferredDestinationId ?? string.Empty,
                    ProjectEncryptionPolicy.Normalize(existing?.EncryptionPolicy),
                    existing?.EncryptionKeyRef ?? string.Empty);
            }
            catch
            {
                return (true, 0, string.Empty, string.Empty, ProjectEncryptionPolicy.Inherit, string.Empty);
            }
        }).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (refreshToken != _selectedProjectRefreshToken)
                    return;

                if (SelectedProject is null ||
                    !string.Equals(SelectedProject.Name, projectName, StringComparison.OrdinalIgnoreCase))
                    return;

                var (missing, projectId, preset, preferredDestinationId, encryptionPolicy, encryptionKeyRef) = t.Result;
                if (missing)
                {
                    SnapshotActionLabel = L("Snapshots.Action.AddProject", "Add project");
                    SelectedProject.IsRegistered = false;
                    SelectedProject.ProjectId = 0;
                    SelectedProject.EncryptionKeyRef = string.Empty;

                    if (string.IsNullOrWhiteSpace(SelectedProject.Preset))
                    {
                        SelectedProject.Preset = string.Empty;
                    }
                }
                else
                {
                    SnapshotActionLabel = L("Snapshots.Action.Default", "Snapshot now");
                    SelectedProject.IsRegistered = true;
                    SelectedProject.ProjectId = projectId;
                    SelectedProject.Preset = preset;
                    SelectedProject.PreferredDestinationId = preferredDestinationId;
                    SelectedProject.EncryptionPolicy = encryptionPolicy;
                    SelectedProject.EncryptionKeyRef = encryptionKeyRef;
                    var cfg = AppConfigStore.Load();
                    UpdateProjectDestinationDisplay(SelectedProject, cfg);
                    UpdateProjectEncryptionDisplay(SelectedProject, cfg);
                }
            });
        });
    }

    private void OnProjectItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ProjectItemViewModel vm)
            return;

        var changedDestination = string.Equals(e.PropertyName, nameof(ProjectItemViewModel.PreferredDestinationId), StringComparison.Ordinal);
        var changedEncryption = string.Equals(e.PropertyName, nameof(ProjectItemViewModel.EncryptionPolicy), StringComparison.Ordinal);
        if (!changedDestination && !changedEncryption)
            return;

        try
        {
            var config = AppConfigStore.Load();
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();

            var repo = new SqliteRepository(dbPath);
            var project = repo.GetProjectByName(vm.Name);
            if (project is null)
                return;

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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Projects] Failed to persist project settings for '{vm.Name}': {ex.Message}");
        }
    }

    private static async Task RunDetachedAsync(Func<Task> operation, string operationName)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DiagnosticsLogger.Record(
                $"Projects detached operation failed ({operationName}): {ex.GetType().Name} - {ex.Message}");
        }
    }

    private void LoadSnapshotHistoryForSelectedProject()
    {
        if (SelectedProject is null || !SelectedProject.IsRegistered || SelectedProject.SnapshotHistoryLoaded)
            return;

        var refreshToken = Interlocked.Increment(ref _selectedProjectHistoryToken);
        var projectName = SelectedProject.Name;

        _ = Task.Run(async () =>
        {
            try
            {
                var config = AppConfigStore.Load();
                var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                    ? config.DbPath
                    : GetDefaultDbPath();

                var repo = new SqliteRepository(dbPath);
                var snapshots = await repo.GetSnapshotsForProjectAsync(projectName);
                return snapshots
                    .Select(CreateProjectSnapshotViewModel)
                    .ToList();
            }
            catch
            {
                return new List<ProjectSnapshotViewModel>();
            }
        }).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (refreshToken != _selectedProjectHistoryToken)
                    return;

                if (SelectedProject is null ||
                    !string.Equals(SelectedProject.Name, projectName, StringComparison.OrdinalIgnoreCase))
                    return;

                var history = t.Result;
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
            });
        });
    }

    private void ApplyProjectHealth(ProjectItemViewModel vm, DateTime? lastSnapshotTime, bool isRegistered)
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
        var config = AppConfigStore.Load();
        RefreshEncryptionPolicyOptions();
        RefreshDestinationOptionsInternal(config);
        foreach (var project in _allProjects)
        {
            UpdateProjectDestinationDisplay(project, config);
            UpdateProjectEncryptionDisplay(project, config);
        }
        RefreshHealthTags();
        RefreshSnapshotText();
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

    private string GetHealthTag(ProjectItemViewModel project)
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

            foreach (var name in GetPresetNames())
            {
                AvailablePresets.Add(name);
            }

            // Always offer an explicit "no preset" option.
            if (!AvailablePresets.Contains("no preset"))
                AvailablePresets.Add("no preset");
        }
        catch (Exception ex)
        {

            // Fallback to a minimal hard-coded set so the UI stays usable.
            AvailablePresets.Clear();
            AvailablePresets.Add("unity");
            AvailablePresets.Add("dotnet");
            AvailablePresets.Add("blender");
            AvailablePresets.Add("video");
            AvailablePresets.Add("no preset");
        }
    }

    private static IEnumerable<string> GetPresetNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                                names.Add(p.Id);
                            }
                            else if (!string.IsNullOrWhiteSpace(p.File))
                            {
                                names.Add(Path.GetFileNameWithoutExtension(p.File));
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
                        if (!string.IsNullOrWhiteSpace(name))
                            names.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
            }
        }

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
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
        public List<PresetInfo> Presets { get; set; } = new();
    }

    private sealed class PresetInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

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
            }
        }
    }

    private string _preset = string.Empty;
    public string Preset
    {
        get => _preset;
        set => SetProperty(ref _preset, value);
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
            if (SetProperty(ref _preferredDestinationOption, value))
            {
                PreferredDestinationId = value?.Id ?? string.Empty;
            }
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
        new ObservableCollection<ProjectSnapshotViewModel>();

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

    public string HealthBackground =>
        Health switch
        {
            ProjectHealthStatus.Healthy => "#1C4730",
            ProjectHealthStatus.Warning => "#473F1C",
            ProjectHealthStatus.OutOfDate => "#471C1C",
            _ => "#181B23"
        };

    public string HealthForeground => "#F4F8FF";

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

        var parts = name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return (parts[0][0].ToString() + parts[1][0].ToString()).ToUpperInvariant();
        }

        var trimmed = name.Trim();
        if (trimmed.Length >= 2)
            return trimmed.Substring(0, 2).ToUpperInvariant();

        return trimmed.Substring(0, 1).ToUpperInvariant();
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(storage, value)) return false;
        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public sealed class DestinationOption
{
    public string Id { get; }
    public string Label { get; }

    public DestinationOption(string id, string label)
    {
        Id = id ?? string.Empty;
        Label = label ?? string.Empty;
    }

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

public sealed class EncryptionPolicyOption
{
    public string Id { get; }
    public string Label { get; }

    public EncryptionPolicyOption(string id, string label)
    {
        Id = ProjectEncryptionPolicy.Normalize(id);
        Label = label ?? string.Empty;
    }

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

public sealed class ProjectSnapshotViewModel
{
    public ProjectSnapshotViewModel(
        DateTime timestamp,
        long sizeBytes,
        int diffAdded = 0,
        int diffModified = 0,
        int diffDeleted = 0,
        long diffNetBytes = 0,
        IReadOnlyList<SnapshotDiffPathStat>? topChangedPaths = null)
    {
        Timestamp = timestamp;
        SizeBytes = sizeBytes;
        DiffAdded = Math.Max(0, diffAdded);
        DiffModified = Math.Max(0, diffModified);
        DiffDeleted = Math.Max(0, diffDeleted);
        DiffNetBytes = diffNetBytes;
        TopChangedPaths = topChangedPaths ?? Array.Empty<SnapshotDiffPathStat>();
    }

    public DateTime Timestamp { get; }
    public long SizeBytes { get; }
    public int DiffAdded { get; }
    public int DiffModified { get; }
    public int DiffDeleted { get; }
    public long DiffNetBytes { get; }
    public IReadOnlyList<SnapshotDiffPathStat> TopChangedPaths { get; }

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
            var hasChanges = DiffAdded > 0 || DiffModified > 0 || DiffDeleted > 0;
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

    public static string FormatSize(long bytes)
    {
        double size = bytes;
        string unit = "B";

        if (size >= 1024)
        {
            size /= 1024;
            unit = "KB";
        }

        if (size >= 1024)
        {
            size /= 1024;
            unit = "MB";
        }

        if (size >= 1024)
        {
            size /= 1024;
            unit = "GB";
        }

        return $"{size:0.0} {unit}";
    }

    private static string FormatSignedSize(long value)
    {
        var abs = FormatSize(Math.Abs(value));
        if (value > 0)
            return $"+{abs}";
        if (value < 0)
            return $"-{abs}";
        return abs;
    }

    private static string L(string key, string fallback) =>
        LocalizationProvider.Service?.GetString(key) ?? fallback;

    private static string Lf(string key, string fallback, params object[] args)
    {
        var fmt = L(key, fallback);
        return string.Format(CultureInfo.CurrentCulture, fmt, args);
    }
}
