namespace VaultSync.UI.ViewModels;

public sealed class DiskCloningViewModel : ViewModelBase
{
    public DiskRecoveryViewModel DiskRecovery { get; } = new();
}
