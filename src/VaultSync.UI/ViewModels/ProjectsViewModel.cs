using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Collections.Generic;
using System.Linq;
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

namespace VaultSync.UI.ViewModels;

/// <summary>
/// Projects page view model – drives the list on the left and the
/// details / actions panel on the right.
/// </summary>
public class ProjectsViewModel : ViewModelBase
{
    private readonly IProjectDiscoveryService _discovery = new ProjectDiscoveryService();
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

    private string _snapshotActionLabel = "Snapshot now";
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

    public ICommand RefreshCommand { get; }
    public ICommand NewProjectCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RemoveProjectCommand { get; }
    public ICommand SnapshotCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand TakeSnapshotCommand => SnapshotCommand;

    public ProjectsViewModel()
    {
        RefreshCommand       = new RelayCommand(_ => Refresh());
        NewProjectCommand    = new RelayCommand(_ => NewProject());
        OpenFolderCommand    = new RelayCommand(_ => OpenFolder(),    _ => SelectedProject is not null);
        RemoveProjectCommand = new RelayCommand(_ => RemoveProject(), _ => SelectedProject is not null);
        SnapshotCommand      = new RelayCommand(_ => TakeSnapshot());
        SyncCommand          = new RelayCommand(_ => SyncProject(),   _ => SelectedProject is not null);

        LoadAvailablePresets();

        _ = RefreshAsync();
    }

    private void SeedDesignProjects()
    {
        Projects.Clear();

        var vaultSync = new ProjectItemViewModel
        {
            Name = "VaultSync",
            Path = "/Users/flavio/Desktop/Dev/VaultSync",
            Health = ProjectHealthStatus.Healthy,
            HealthTag = "Healthy",
            LastSnapshot = DateTime.Today.AddMinutes(-20),
            SizeBytes = 1_800_000_000,
            Preset = "unity"
        };
        vaultSync.SetSnapshots(CreateDesignSnapshots(1.8));
        Projects.Add(vaultSync);

        var dumpsterFire = new ProjectItemViewModel
        {
            Name = "Dumpster Fire Royale",
            Path = "/Volumes/Projects/DumpsterFireRoyale",
            Health = ProjectHealthStatus.Warning,
            HealthTag = "Warning",
            LastSnapshot = DateTime.Today.AddDays(-1).AddHours(23),
            SizeBytes = 46_200_000_000,
            Preset = "unity"
        };
        dumpsterFire.SetSnapshots(CreateDesignSnapshots(46.2));
        Projects.Add(dumpsterFire);

        var overSteer = new ProjectItemViewModel
        {
            Name = "OverSteer",
            Path = "/Volumes/Projects/OverSteer",
            Health = ProjectHealthStatus.OutOfDate,
            HealthTag = "Out of date",
            LastSnapshot = DateTime.Today.AddDays(-3),
            SizeBytes = 32_900_000_000,
            Preset = "unity"
        };
        overSteer.SetSnapshots(CreateDesignSnapshots(32.9));
        Projects.Add(overSteer);
    }

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

    private async Task RefreshAsync()
    {
        if (IsLoading)
            return;

        try
        {
            IsLoading = true;

            var config     = AppConfigStore.Load();
            var discovered = await _discovery.DiscoverAsync(config);

            // Try to open the shared DB so we can enrich projects with real snapshot data.
            SqliteRepository? repo = null;
            try
            {
                var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                    ? config.DbPath
                    : GetDefaultDbPath();

                repo = new SqliteRepository(dbPath);
                repo.EnsureSchema();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ProjectsViewModel] Could not open DB during refresh: " + ex);
            }

            Projects.Clear();

            if (discovered.Count == 0)
            {
                // Design-time / fallback sample data if nothing is found yet.
                SeedDesignProjects();
            }
            else
            {
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
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ProjectsViewModel] Failed to load snapshot data for '{p.Name}': {ex}");
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

                    // Compute health based on how old the last snapshot is (if any).
                    if (lastSnapshotTime.HasValue)
                    {
                        var age = DateTime.UtcNow - lastSnapshotTime.Value;

                        if (age.TotalDays < 1)
                        {
                            vm.Health    = ProjectHealthStatus.Healthy;
                            vm.HealthTag = "Healthy";
                        }
                        else if (age.TotalDays < 7)
                        {
                            vm.Health    = ProjectHealthStatus.Warning;
                            vm.HealthTag = "Out of date";
                        }
                        else
                        {
                            vm.Health    = ProjectHealthStatus.OutOfDate;
                            vm.HealthTag = "Stale";
                        }
                    }
                    else
                    {
                        vm.Health    = ProjectHealthStatus.OutOfDate;
                        vm.HealthTag = "Not backed up";
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

                    Projects.Add(vm);
                }
            }

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
            Console.WriteLine("[ProjectsViewModel] Error refreshing projects: " + ex);
        }
        finally
        {
            IsLoading = false;
        }
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
            Console.WriteLine($"[ProjectsViewModel] Failed to open folder '{path}': {ex}");
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

            // Open repository and ensure schema exists.
            var repo = new SqliteRepository(dbPath);
            repo.EnsureSchema();

            // Look up the project in the DB by name.
            var existing = repo.GetProjectByName(removedProjectName);
            if (existing is null)
            {
                Console.WriteLine($"[ProjectsViewModel] Project '{removedProjectName}' not found in DB.");
            }
            else
            {
                repo.RemoveProject(existing.Id);
                Console.WriteLine($"[ProjectsViewModel] Removed project '{removedProjectName}' from DB.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProjectsViewModel] Failed to remove project '{removedProjectName}' from DB: {ex}");
        }

        // Reset the selected project's details so the right panel no longer shows stale data.
        if (SelectedProject != null && SelectedProject.Name == removedProjectName)
        {
            SelectedProject.LastSnapshot = default;
            SelectedProject.SizeBytes    = 0;
            SelectedProject.SetSnapshots(Array.Empty<ProjectSnapshotViewModel>());
            SelectedProject.Health    = ProjectHealthStatus.OutOfDate;
            SelectedProject.HealthTag = "Not backed up";
        }

        // After removing from DB, keep the project visible in the list but mark it as unregistered
        // so the primary action becomes "Add project" again.
        RefreshSelectedProjectRegistration();
    }

    private async void TakeSnapshot()
    {
        if (SelectedProject is null)
            return;

        Console.WriteLine(
            $"[ProjectsViewModel] Snapshot requested for project '{SelectedProject.Name}' at '{SelectedProject.Path}'.");

        try
        {
            // 1. Resolve DB path from shared AppConfig (with a sensible default).
            var config = AppConfigStore.Load();
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();

            // 2. Open repository and ensure schema exists.
            var repo = new SqliteRepository(dbPath);
            repo.EnsureSchema();

            // 3. Check if project is already registered.
            var existing = repo.GetProjectByName(SelectedProject.Name);
            if (existing is null)
            {
                // Require a preset (or explicit "no preset") before registering the project.
                if (string.IsNullOrWhiteSpace(SelectedProject.Preset))
                {
                    Console.WriteLine("[ProjectsViewModel] Cannot register project without a preset. Please select a preset first.");
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
                Console.WriteLine($"[ProjectsViewModel] Registered project '{project.Name}' with id={id}.");

                // Update UI label so next click becomes a real snapshot.
                SnapshotActionLabel = "Snapshot now";
                return;
            }

            // 4. Run snapshot via Core engine.
            var hashService     = new HashService();
            var snapshotService = new SnapshotService(repo, hashService);

            var snapshotId = await snapshotService.CreateSnapshotAsync(existing, fullHash: true);
            var outcome    = SnapshotService.LastOutcome;

            Console.WriteLine(
                $"[ProjectsViewModel] Snapshot #{snapshotId} for '{existing.Name}': " +
                $"Added={outcome?.Added}, Modified={outcome?.Modified}, Deleted={outcome?.Deleted}, " +
                $"Unchanged={outcome?.Unchanged}, TotalFiles={outcome?.TotalFiles}, Bytes={outcome?.TotalBytes}");

            // Update the selected project's stats in the UI immediately.
            if (SelectedProject != null && outcome != null)
            {
                var snapshotTime = DateTime.UtcNow;
                SelectedProject.LastSnapshot = snapshotTime;
                SelectedProject.SizeBytes    = outcome.TotalBytes;

                var history = SelectedProject.SnapshotHistory?.ToList()
                              ?? new List<ProjectSnapshotViewModel>();

                history.Insert(0, new ProjectSnapshotViewModel(snapshotTime, outcome.TotalBytes));

                if (history.Count > 10)
                    history = history.Take(10).ToList();

                SelectedProject.SetSnapshots(history);

                SelectedProject.Health    = ProjectHealthStatus.Healthy;
                SelectedProject.HealthTag = "Healthy";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProjectsViewModel] Snapshot failed: {ex}");
        }

        // Refresh label/state after the operation.
        RefreshSelectedProjectRegistration();
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
            SnapshotActionLabel = "Snapshot now";
            return;
        }

        try
        {
            var config = AppConfigStore.Load();
            var dbPath = !string.IsNullOrWhiteSpace(config.DbPath)
                ? config.DbPath
                : GetDefaultDbPath();

            var repo = new SqliteRepository(dbPath);
            repo.EnsureSchema();

            var existing = repo.GetProjectByName(SelectedProject.Name);
            if (existing is null)
            {
                SnapshotActionLabel = "Add project";
                // When not registered yet, force the user to choose a preset explicitly.
                if (string.IsNullOrWhiteSpace(SelectedProject.Preset))
                {
                    SelectedProject.Preset = string.Empty;
                }
            }
            else
            {
                SnapshotActionLabel = "Snapshot now";
                // Keep the UI in sync with the DB-stored preset.
                SelectedProject.Preset = existing.Preset;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ProjectsViewModel] Failed to refresh registration state: {ex}");
            SnapshotActionLabel = "Snapshot now";
        }
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
            Console.WriteLine("[ProjectsViewModel] Failed to load presets from index/files: " + ex);

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
                Console.WriteLine("[ProjectsViewModel] Error reading preset index/files: " + ex);
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
            ? "–"
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
            ? "Never"
            : $"{LastSnapshot:g}";

    public string LastSnapshotShort =>
        LastSnapshot == default
            ? "Never"
            : LastSnapshot.ToString("ddd · HH:mm");

    public string DaysSinceLastSnapshotDisplay
    {
        get
        {
            if (LastSnapshot == default)
                return "Never";

            var diff = DateTime.Today - LastSnapshot.Date;
            if (diff.TotalDays < 1)
                return "< 1 day";

            if (Math.Abs(diff.TotalDays - 1) < 0.1)
                return "1 day";

            return $"{(int)diff.TotalDays} days";
        }
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
    /// 24–80px bar height, based on RelativeSize.
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