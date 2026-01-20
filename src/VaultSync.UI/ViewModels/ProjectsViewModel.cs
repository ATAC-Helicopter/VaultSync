using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
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
    private readonly IProjectDiscoveryService _discovery = new ProjectDiscoveryService();
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
    public ObservableCollection<ProjectItemViewModel> Projects { get; } =
        new ObservableCollection<ProjectItemViewModel>();

    private ProjectItemViewModel? _selectedProject;
    public ProjectItemViewModel? SelectedProject
    {
        get => _selectedProject;
        set
        {
            SetProperty(ref _selectedProject, value);
            RefreshSelectedProjectRegistration();
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
    public ICommand NewProjectCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RemoveProjectCommand { get; }
    public ICommand SnapshotCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand TakeSnapshotCommand => SnapshotCommand;
    public ICommand ToggleSortCommand { get; }
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
        RefreshCommand       = new RelayCommand(_ => Refresh());
        NewProjectCommand    = new RelayCommand(_ => NewProject());
        OpenFolderCommand    = new RelayCommand(_ => OpenFolder(),    _ => SelectedProject is not null);
        RemoveProjectCommand = new RelayCommand(_ => RemoveProject(), _ => SelectedProject is not null);
        SnapshotCommand      = new RelayCommand(_ => TakeSnapshot());
        SyncCommand          = new RelayCommand(_ => SyncProject(),   _ => SelectedProject is not null);
        ToggleSortCommand    = new RelayCommand(_ => ToggleSortMode());

        LoadAvailablePresets();

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
        var title    = success
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
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (IsLoading)
            return;

        try
        {
            IsLoading = true;

            var config     = AppConfigStore.Load();
            ShowProjectAvatars = config.Appearance.ShowProjectAvatars;
            OnPropertyChanged(nameof(ShowProjectAvatars));
            var projectItems = await Task.Run(() => BuildProjectItems(config));

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
        }
    }

    private List<ProjectItemViewModel> BuildProjectItems(AppConfig config)
    {
        IReadOnlyList<DiscoveredProject> discovered;
        try
        {
            discovered = _discovery.DiscoverAsync(config).GetAwaiter().GetResult();
        }
        catch
        {
            discovered = Array.Empty<DiscoveredProject>();
        }

        // Try to open the shared DB so we can enrich projects with real snapshot data.
        SqliteRepository? repo = null;
        try
        {
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();

            repo = new SqliteRepository(dbPath);
        }
        catch
        {
        }

        if (discovered.Count == 0)
            return new List<ProjectItemViewModel>();

        var items = new List<ProjectItemViewModel>();
        foreach (var p in discovered)
        {
            DateTime? lastSnapshotTime  = p.LastSnapshotTime;
            long?     lastSnapshotBytes = p.LastSnapshotSizeBytes;
            List<ProjectSnapshotViewModel>? snapshotVms = null;
            Project? existingProject = null;

            if (repo != null)
            {
                try
                {
                    // Use DB snapshot history if the project is registered.
                    existingProject = repo.GetProjectByName(p.Name);
                    if (existingProject != null)
                    {
                        var snapshots = repo.GetSnapshotsForProject(existingProject.Name)?.ToList();
                        if (snapshots != null && snapshots.Count > 0)
                        {
                            // Assume snapshots are returned newest-first.
                            var latest = snapshots[0];
                            lastSnapshotTime  = latest.CreatedUtc;
                            lastSnapshotBytes = latest.TotalBytes;

                            snapshotVms = snapshots
                                .Select(s => new ProjectSnapshotViewModel(s.CreatedUtc, s.TotalBytes))
                                .ToList();
                        }
                    }
                }
                catch
                {
                }
            }

            var vm = new ProjectItemViewModel
            {
                Name         = p.Name,
                Path         = p.Path,
                LastSnapshot = lastSnapshotTime ?? default,
                SizeBytes    = lastSnapshotBytes ?? 0,
                Preset       = existingProject?.Preset ?? string.Empty
            };
            vm.SetAvatarFromNameAndStore(p.Path, AvatarStore.GetAvatarForProject(p.Path));

            var isRegistered = existingProject is not null;

            // Compute health based on how old the last snapshot is (if any).
            if (lastSnapshotTime.HasValue)
            {
                var age = DateTime.UtcNow - lastSnapshotTime.Value;

                if (age.TotalDays < 1)
                {
                    vm.Health    = ProjectHealthStatus.Healthy;
                    vm.HealthTag = L("Projects.Health.HealthyRecent", "Healthy (<1d)");
                }
                else if (age.TotalDays < 7)
                {
                    vm.Health    = ProjectHealthStatus.Warning;
                    vm.HealthTag = L("Projects.Health.OutOfDateShort", "Out of date (>1d)");
                }
                else
                {
                    vm.Health    = ProjectHealthStatus.OutOfDate;
                    vm.HealthTag = L("Projects.Health.Stale", "Stale (>7d)");
                }
            }
            else
            {
                vm.Health = ProjectHealthStatus.OutOfDate;
                vm.HealthTag = isRegistered
                    ? L("Projects.Health.NoSnapshots", "No snapshots yet")
                    : L("Projects.Health.NotAdded", "Not added");
            }

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
            }
            else
            {
                vm.SetSnapshots(Array.Empty<ProjectSnapshotViewModel>());
            }

            // Mark whether this project is registered in the backup DB.
            vm.IsRegistered = isRegistered;

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

    private void NewProject()
    {
        // TODO: open "Add project" flow.
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

        try
        {
            // Resolve DB path (shared with CLI).
            var config = AppConfigStore.Load();
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();

            // Open repository (schema already initialized at app startup).
            var repo = new SqliteRepository(dbPath);

            // Look up the project in the DB by name.
            var existing = repo.GetProjectByName(removedProjectName);
            if (existing is null)
            {
                ShowNotification(Lf("Projects.Notification.RemoveMissing", "Project '{0}' was not registered in the backup database.", removedProjectName), NotificationSeverity.Warning);
            }
            else
            {
                repo.RemoveProject(existing.Id);
                ShowNotification(Lf("Projects.Notification.RemoveSuccess", "Removed project '{0}' from the backup database.", removedProjectName), NotificationSeverity.Info);
            }
        }
        catch (Exception ex)
        {
            ShowNotification(Lf("Projects.Notification.RemoveFailed", "Failed to remove project '{0}' from the backup database.", removedProjectName), NotificationSeverity.Error);
        }

        // Reset the selected project's details so the right panel no longer shows stale data.
        if (SelectedProject != null && SelectedProject.Name == removedProjectName)
        {
            SelectedProject.LastSnapshot = default;
            SelectedProject.SizeBytes    = 0;
            SelectedProject.SetSnapshots(Array.Empty<ProjectSnapshotViewModel>());
            SelectedProject.Health    = ProjectHealthStatus.OutOfDate;
            SelectedProject.HealthTag = L("Projects.Health.NotBackedUp", "Not backed up");
            SelectedProject.IsRegistered = false;
        }

        // After removing from DB, keep the project visible in the list but mark it as unregistered
        // so the primary action becomes "Add project" again.
        RefreshSelectedProjectRegistration();
    }

    private async void TakeSnapshot()
    {
        if (SelectedProject is null)
            return;

        try
        {
            // 1. Resolve DB path from shared AppConfig (with a sensible default).
            var config = AppConfigStore.Load();
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();
            var maxSnapshotsToKeep = config.Backups.MaxSnapshotsPerProject;
            var fullHash = config.Backups.UseFullSnapshotHash;

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
                    Name       = SelectedProject.Name,
                    RootPath   = SelectedProject.Path,
                    Preset     = SelectedProject.Preset,
                    CreatedUtc = DateTime.UtcNow
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
            var hashService     = new HashService();
            var snapshotService = new SnapshotService(repo, hashService);

            var snapshotId = await snapshotService.CreateSnapshotAsync(
                existing,
                fullHash: fullHash,
                maxSnapshotsToKeep: maxSnapshotsToKeep);
            var outcome    = SnapshotService.LastOutcome;

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
                        SelectedProject.SizeBytes    = latest.TotalBytes;

                        var history = snapshotsFromDb
                            .Take(10)
                            .Select(s => new ProjectSnapshotViewModel(s.CreatedUtc, s.TotalBytes));

                        SelectedProject.SetSnapshots(history);
                    }
                    else
                    {
                        // No snapshots remaining (should be rare, but handle it).
                        SelectedProject.LastSnapshot = default;
                        SelectedProject.SizeBytes    = 0;
                        SelectedProject.SetSnapshots(Array.Empty<ProjectSnapshotViewModel>());
                    }

                    SelectedProject.Health    = ProjectHealthStatus.Healthy;
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

            // Fire the existing snapshot pipeline (async void).
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

    private void SyncProject()
    {
        if (SelectedProject is null) return;
        // TODO: trigger sync pipeline for SelectedProject.
    }

    private void RefreshSelectedProjectRegistration()
    {
        if (SelectedProject is null)
        {
            SnapshotActionLabel = L("Snapshots.Action.Default", "Snapshot now");
            return;
        }

        try
        {
            var config = AppConfigStore.Load();
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();

            var repo = new SqliteRepository(dbPath);

            var existing = repo.GetProjectByName(SelectedProject.Name);
            if (existing is null)
            {
                SnapshotActionLabel = L("Snapshots.Action.AddProject", "Add project");
                SelectedProject.IsRegistered = false;

                // When not registered yet, force the user to choose a preset explicitly.
                if (string.IsNullOrWhiteSpace(SelectedProject.Preset))
                {
                    SelectedProject.Preset = string.Empty;
                }
            }
            else
            {
                SnapshotActionLabel = L("Snapshots.Action.Default", "Snapshot now");
                SelectedProject.IsRegistered = true;

                // Keep the UI in sync with the DB-stored preset.
                SelectedProject.Preset = existing.Preset;
            }
        }
        catch (Exception ex)
        {
            SnapshotActionLabel = L("Snapshots.Action.Default", "Snapshot now");
            ShowNotification(L("Projects.Notification.RefreshFailed", "Could not refresh project registration state. Using default actions."), NotificationSeverity.Warning);
        }
    }

    public void RefreshLocalization()
    {
        SnapshotActionLabel = SelectedProject is null || SelectedProject.IsRegistered
            ? L("Snapshots.Action.Default", "Snapshot now")
            : L("Snapshots.Action.AddProject", "Add project");
        OnPropertyChanged(nameof(SortModeLabel));
        RefreshHealthTags();
        RefreshSnapshotText();
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
        var dir   = ResolvePresetsDirForUi();

        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
        {
            try
            {
                var indexPath = Path.Combine(dir, "presets.index.json");
                if (File.Exists(indexPath))
                {
                    var json  = File.ReadAllText(indexPath);
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
        var dir     = Path.Combine(appData, "VaultSync");
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

    private ProjectHealthStatus _health;
    public ProjectHealthStatus Health
    {
        get => _health;
        set
        {
            if (SetProperty(ref _health, value))
            {
                OnPropertyChanged(nameof(HealthBackground));
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

    private bool _isRegistered;
    public bool IsRegistered
    {
        get => _isRegistered;
        set => SetProperty(ref _isRegistered, value);
    }

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
            ? L("Projects.LastSnapshot.None", "No snapshots yet")
            : LastSnapshot.ToString("g", CultureInfo.CurrentCulture);

    public string LastSnapshotShort =>
        LastSnapshot == default
            ? L("Projects.LastSnapshot.NoneShort", "No snapshots yet")
            : LastSnapshot.ToString("ddd · HH:mm", CultureInfo.CurrentCulture);

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
            ProjectHealthStatus.Healthy   => "#1C4730",
            ProjectHealthStatus.Warning   => "#473F1C",
            ProjectHealthStatus.OutOfDate => "#471C1C",
            _                             => "#181B23"
        };

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

    public void SetAvatarFromNameAndStore(string projectPath, string? customPath)
    {
        AvatarInitials = ComputeInitials(Name);
        AvatarColor    = AvatarColorProvider.GetColor(Name, projectPath);
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

public sealed class ProjectSnapshotViewModel
{
    public ProjectSnapshotViewModel(DateTime timestamp, long sizeBytes)
    {
        Timestamp = timestamp;
        SizeBytes = sizeBytes;
    }

    public DateTime Timestamp { get; }
    public long SizeBytes { get; }

    // Mini-chart data
    public double RelativeSize { get; set; }

    /// <summary>
    /// 24-80px bar height, based on RelativeSize.
    /// </summary>
    public double RelativeBarHeight => 24 + RelativeSize * 56;

    /// <summary>
    /// Color used for the bar: neutral, up (red), down (green).
    /// </summary>
    public string TrendColor { get; set; } = "#2F3650";

    public string DateDisplay => Timestamp.ToString("dd/MM/yyyy · HH:mm");

    public string SizeDisplay => FormatSize(SizeBytes);

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
}
