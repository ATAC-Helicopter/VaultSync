using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VaultSync.Core.Repositories;
using VaultSync.UI.Services;
using System.Windows.Input;

namespace VaultSync.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public ProjectsViewModel Projects { get; }
    public ActionsViewModel Actions { get; }
    public UiEventBus Bus { get; }

    [ObservableProperty] private bool isDarkTheme;

    partial void OnIsDarkThemeChanged(bool value) => ThemeService.SetDark(value);
    [RelayCommand]
    private void ClearLog() => Bus.Clear();
    [RelayCommand]
    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

    public MainViewModel()
    {
        var db = DbPathHelper.Resolve();
        var repo = new SqliteRepository(db);
        Bus = new UiEventBus();

        Projects = new ProjectsViewModel(repo, Bus);
        Actions = new ActionsViewModel(repo, Bus);
    }
}