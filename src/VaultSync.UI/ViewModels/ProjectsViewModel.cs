using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Collections.Generic;
using System.Linq;

namespace VaultSync.UI.ViewModels;

/// <summary>
/// Projects page view model – drives the list on the left and the
/// details / actions panel on the right.
/// </summary>
public class ProjectsViewModel : ViewModelBase
{
    public ObservableCollection<ProjectItemViewModel> Projects { get; } =
        new ObservableCollection<ProjectItemViewModel>();

    private ProjectItemViewModel? _selectedProject;
    public ProjectItemViewModel? SelectedProject
    {
        get => _selectedProject;
        set => SetProperty(ref _selectedProject, value);
    }

    public ICommand RefreshCommand { get; }
    public ICommand NewProjectCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RemoveProjectCommand { get; }
    public ICommand SnapshotCommand { get; }
    public ICommand SyncCommand { get; }

    public ProjectsViewModel()
    {
        // TODO: replace with real data from VaultSync.Core
        SeedDesignProjects();

        if (Projects.Count > 0)
            SelectedProject = Projects[0];

        RefreshCommand       = new RelayCommand(_ => Refresh());
        NewProjectCommand    = new RelayCommand(_ => NewProject());
        OpenFolderCommand    = new RelayCommand(_ => OpenFolder(),    _ => SelectedProject is not null);
        RemoveProjectCommand = new RelayCommand(_ => RemoveProject(), _ => SelectedProject is not null);
        SnapshotCommand      = new RelayCommand(_ => TakeSnapshot(),  _ => SelectedProject is not null);
        SyncCommand          = new RelayCommand(_ => SyncProject(),   _ => SelectedProject is not null);
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
            Preset = "Daily snapshot"
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
            Preset = "Weekly + on-demand"
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
            Preset = "Manual only"
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
        // TODO: call into Core to reload projects from config / disk.
        // For now this just re-seeds the sample data.
        SeedDesignProjects();

        if (SelectedProject != null && !Projects.Contains(SelectedProject) && Projects.Count > 0)
            SelectedProject = Projects[0];
    }

    private void NewProject()
    {
        // TODO: open "Add project" flow.
    }

    private void OpenFolder()
    {
        if (SelectedProject is null) return;
        // TODO: use platform-specific shell open helper.
    }

    private void RemoveProject()
    {
        if (SelectedProject is null) return;

        Projects.Remove(SelectedProject);
        SelectedProject = Projects.Count > 0 ? Projects[0] : null;
    }

    private void TakeSnapshot()
    {
        if (SelectedProject is null) return;
        // TODO: trigger snapshot pipeline for SelectedProject.
    }

    private void SyncProject()
    {
        if (SelectedProject is null) return;
        // TODO: trigger sync pipeline for SelectedProject.
    }

    private static bool SetProperty<T>(ref T storage, T value)
    {
        if (Equals(storage, value)) return false;
        storage = value;
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
        set => SetProperty(ref _health, value);
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
        set => SetProperty(ref _lastSnapshot, value);
    }

    private long _sizeBytes;
    public long SizeBytes
    {
        get => _sizeBytes;
        set => SetProperty(ref _sizeBytes, value);
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
            return;

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

    private static bool SetProperty<T>(ref T storage, T value)
    {
        if (Equals(storage, value)) return false;
        storage = value;
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