using CommunityToolkit.Mvvm.ComponentModel;

namespace VaultSync.UI.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        public string Theme => "Light";
        public string Version => "0.1.0";
    }
}