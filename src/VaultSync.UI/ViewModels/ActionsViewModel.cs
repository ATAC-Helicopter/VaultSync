using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public partial class ActionsViewModel : ObservableObject
{
    private readonly SqliteRepository _repo;
    private readonly UiEventBus _bus;

    [ObservableProperty] private string destinationPath = "";
    [ObservableProperty] private bool fullVerify = false;
    [ObservableProperty] private int samplePercent = 15;

    public ActionsViewModel(SqliteRepository repo, UiEventBus bus)
    {
        _repo = repo;
        _bus = bus;
    }

    [RelayCommand]
    private async Task SnapshotAsync(string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName)) { _bus.Warn("Select a project first."); return; }
        var proj = _repo.GetProjectByName(projectName);
        if (proj is null) { _bus.Error($"Project '{projectName}' not found."); return; }

        var svc = new SnapshotService(_repo, new HashService());
        _bus.Info($"Snapshotting {proj.Name}…");
        var id = await svc.CreateSnapshotAsync(proj, fullHash: true, CancellationToken.None);
        var outcome = SnapshotService.LastOutcome;
        _bus.Success($"Snapshot {id} created (added {outcome?.Added}, modified {outcome?.Modified}, deleted {outcome?.Deleted}).");
    }

    [RelayCommand]
    private async Task SyncAsync(string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName)) { _bus.Warn("Select a project first."); return; }
        if (string.IsNullOrWhiteSpace(DestinationPath)) { _bus.Warn("Set a destination path."); return; }
        var proj = _repo.GetProjectByName(projectName);
        if (proj is null) { _bus.Error($"Project '{projectName}' not found."); return; }

        var svc = new SyncService();
        _bus.Info($"Sync → {DestinationPath}");
        var code = await svc.SyncAsync(proj, DestinationPath, dryRun: false, CancellationToken.None);
        if (code == 0) _bus.Success("Sync complete.");
        else _bus.Error($"Sync failed (exit {code}).");
    }

    [RelayCommand]
    private async Task VerifyAsync(string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName)) { _bus.Warn("Select a project first."); return; }
        if (string.IsNullOrWhiteSpace(DestinationPath)) { _bus.Warn("Set a destination path."); return; }

        var proj = _repo.GetProjectByName(projectName)!;
        var svc = new VerifyService(_repo, new HashService());
        var result = await svc.VerifyAsync(proj, DestinationPath, percent: FullVerify ? 100 : SamplePercent, full: FullVerify, CancellationToken.None);
        if (result.Failures.Any()) _bus.Error($"Verify failed: {result.Failures.Count} issue(s).");
        else _bus.Success("Verify OK.");
    }
}