// File: src/VaultSync.UI/ViewModels/AppViewModel.cs
using System;
using System.Windows.Input;

namespace VaultSync.UI.ViewModels
{
    public class AppViewModel : ViewModelBase
    {
        private object? _currentView;
        private string _headerTitle = "Dashboard";
        private string _headerKicker = "Overview";

        public object? CurrentView
        {
            get => _currentView;
            set
            {
                if (!Equals(_currentView, value))
                {
                    _currentView = value;
                    OnPropertyChanged(nameof(CurrentView));
                }
            }
        }

        public string HeaderTitle
        {
            get => _headerTitle;
            set
            {
                if (_headerTitle != value)
                {
                    _headerTitle = value;
                    OnPropertyChanged(nameof(HeaderTitle));
                }
            }
        }

        public string HeaderKicker
        {
            get => _headerKicker;
            set
            {
                if (_headerKicker != value)
                {
                    _headerKicker = value;
                    OnPropertyChanged(nameof(HeaderKicker));
                }
            }
        }

        // Commands that MainWindow.axaml binds to
        public ICommand NavigateDashboard { get; }
        public ICommand NavigateProjects  { get; }
        public ICommand NavigateBackups   { get; }
        public ICommand NavigateSettings  { get; }

        public AppViewModel()
        {
            // default route
            CurrentView = new DashboardViewModel();
            HeaderTitle  = "Dashboard";
            HeaderKicker = "Overview";

            NavigateDashboard = new RelayCommand(_ =>
            {
                CurrentView  = new DashboardViewModel();
                HeaderTitle  = "Dashboard";
                HeaderKicker = "Overview";
            });

            NavigateProjects = new RelayCommand(_ =>
            {
                CurrentView  = new ProjectsViewModel();
                HeaderTitle  = "Projects";
                HeaderKicker = "All repositories";
            });

            NavigateBackups = new RelayCommand(_ =>
            {
                CurrentView  = new BackupsViewModel();
                HeaderTitle  = "Backups";
                HeaderKicker = "Snapshots & history";
            });

            NavigateSettings = new RelayCommand(_ =>
            {
                CurrentView  = new SettingsViewModel();
                HeaderTitle  = "Settings";
                HeaderKicker = "Preferences";
            });
        }

        // tiny ICommand implementation to avoid extra deps
        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object?> _execute;
            private readonly Func<object?, bool>? _canExecute;

            public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

            public void Execute(object? parameter) => _execute(parameter);

            public event EventHandler? CanExecuteChanged
            {
                add { CommandManagerRequerySuggested.Add(value); }
                remove { CommandManagerRequerySuggested.Remove(value); }
            }
        }

        // Minimal requery suggested hook (since we don't reference WPF)
        private static class CommandManagerRequerySuggested
        {
            public static void Add(EventHandler? handler) { }
            public static void Remove(EventHandler? handler) { }
        }
    }

    // Stub view models so the compiler can resolve types if you haven't created them yet.
    // If you already have these, keep yours and remove these stubs.
    public class BackupsViewModel  : ViewModelBase { }
    public class SettingsViewModel : ViewModelBase { }
}