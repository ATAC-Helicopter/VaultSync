using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace VaultSync.UI.ViewModels
{
    public partial class ProjectsViewModel : ObservableObject
    {
        public ObservableCollection<string> Projects { get; } = new() { "Vault (~/Vault)", "Photos (~/Pictures/Photos)" };

        [RelayCommand] private void AddProject() => Projects.Add("New Project...");
        [RelayCommand] private void RemoveProject(string? name)
        {
            if (name != null && Projects.Contains(name)) Projects.Remove(name);
        }
    }
}