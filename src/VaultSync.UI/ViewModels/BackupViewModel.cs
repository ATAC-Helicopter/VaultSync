using CommunityToolkit.Mvvm.ComponentModel;

namespace VaultSync.UI.ViewModels
{
    public partial class BackupViewModel : ObservableObject
    {
        public string Target => "/Volumes/NAS/Backups/VaultSync";
        public string LastRun => "Never";
    }
}