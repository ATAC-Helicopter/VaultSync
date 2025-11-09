using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VaultSync.Core.Repositories;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public ProjectsViewModel Projects { get; }
    public ActionsViewModel Actions { get; }
    public UiEventBus Bus { get; }

    [ObservableProperty]
    private bool isDarkTheme;

    partial void OnIsDarkThemeChanged(bool value)
    {
        ThemeService.SetDark(value);
    }

    [RelayCommand]
    private void ClearLog() => Bus.Clear();

    public MainViewModel()
    {
        // Local services setup
        Bus = new UiEventBus();

        // Create the repository used by viewmodels that need it
        var db = DbPathHelper.Resolve();
        var repo = new SqliteRepository(db);

        // Projects VM is simple/parameterless for now
        Projects = new ProjectsViewModel();

        // Actions needs services: keep the explicit ctor
        Actions = new ActionsViewModel(repo, Bus);
    }
}