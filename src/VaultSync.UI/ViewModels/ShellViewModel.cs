using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VaultSync.UI.ViewModels
{
    public partial class ShellViewModel : ObservableObject
    {
        private readonly Dictionary<string, Func<ObservableObject>> _routes;

        [ObservableProperty] private ObservableObject? currentViewModel;

        public ShellViewModel()
        {
            _routes = new()
            {
                ["Dashboard"] = () => new DashboardViewModel(),
                ["Projects"]  = () => new ProjectsViewModel(),
                ["Sync"]      = () => new SyncViewModel(),
                ["History"]   = () => new HistoryViewModel(),
                ["Backup"]    = () => new BackupViewModel(),
                ["Settings"]  = () => new SettingsViewModel(),
            };

            // Default page
            CurrentViewModel = _routes["Dashboard"]();
        }

        [RelayCommand]
        private void Navigate(string? route)
        {
            if (string.IsNullOrWhiteSpace(route)) return;
            if (_routes.TryGetValue(route, out var factory))
                CurrentViewModel = factory();
        }
    }
}