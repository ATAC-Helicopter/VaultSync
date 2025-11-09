using CommunityToolkit.Mvvm.ComponentModel;

namespace VaultSync.UI.ViewModels
{
    public partial class DashboardViewModel : ObservableObject
    {
        public string Greeting => "Welcome to VaultSync";
        public string StatusSummary => "No syncs yet. Configure a project to get started.";
    }
}