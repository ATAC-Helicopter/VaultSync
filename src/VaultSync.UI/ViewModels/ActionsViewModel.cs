using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VaultSync.Core.Repositories;
using VaultSync.UI.Services;
using System.Threading.Tasks;

namespace VaultSync.UI.ViewModels;

public partial class ActionsViewModel : ObservableObject
{
    private readonly SqliteRepository _repo;
    private readonly UiEventBus _bus;

    [ObservableProperty] private string? destinationPath;
    [ObservableProperty] private bool fullVerify;
    [ObservableProperty] private int samplePercent = 100;

    public ActionsViewModel(SqliteRepository repo, UiEventBus bus)
    {
        _repo = repo;
        _bus = bus;
    }

    [RelayCommand]
    private async Task SnapshotAsync(string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            _bus.Warn("Select a project before running a snapshot.");
            return;
        }

        _bus.Info($"Creating snapshot for '{projectName}'...");
        await Task.Run(() => Task.Delay(500).Wait()); // TODO: SnapshotService
        _bus.Success($"Snapshot completed for '{projectName}'.");
    }

    [RelayCommand]
    private async Task SyncAsync(string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            _bus.Warn("Select a project before syncing.");
            return;
        }

        if (string.IsNullOrWhiteSpace(DestinationPath))
        {
            _bus.Warn("Enter destination path before syncing.");
            return;
        }

        _bus.Info($"Syncing '{projectName}' to '{DestinationPath}'...");
        await Task.Run(() => Task.Delay(500).Wait()); // TODO: SyncService
        _bus.Success($"Sync completed for '{projectName}'.");
    }

    [RelayCommand]
    private async Task VerifyAsync(string? projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            _bus.Warn("Select a project before verification.");
            return;
        }

        _bus.Info($"Verifying snapshot for '{projectName}'...");
        await Task.Run(() => Task.Delay(500).Wait()); // TODO: VerifyService
        _bus.Success($"Verification completed for '{projectName}'.");
    }
}