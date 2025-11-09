using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VaultSync.UI.ViewModels
{
    public partial class SyncViewModel : ObservableObject
    {
        [ObservableProperty]
        private bool isRunning;

        public ObservableCollection<string> Output { get; } = new();

        [RelayCommand]
        private async Task Run()
        {
            if (IsRunning) return;
            IsRunning = true;
            Output.Clear();

            // Simulated pipeline; replace with VaultSync.Core service calls
            await Task.Delay(600); Output.Add("Scanning projects...");
            await Task.Delay(600); Output.Add("Detected 12 changes.");
            await Task.Delay(600); Output.Add("Pushing to remote...");
            await Task.Delay(600); Output.Add("Sync complete.");

            IsRunning = false;
        }
    }
}