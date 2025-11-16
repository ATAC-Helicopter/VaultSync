using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using VaultSync.Core.Config;

namespace VaultSync.UI
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        // Browse for Projects root
        private async void OnBrowseProjectsRoot(object? sender, RoutedEventArgs e)
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window is null)
                return;

            var storageProvider = window.StorageProvider;
            if (storageProvider is null)
                return;

            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose projects root",
                AllowMultiple = false
            });

            var folder = folders?.FirstOrDefault();
            if (folder is null)
                return;

            var path = folder.Path?.LocalPath;
            if (string.IsNullOrWhiteSpace(path))
                return;

            // Save into Core AppConfig (ProjectsRoot exists in AppConfig)
            var config = AppConfigStore.Load();
            config.ProjectsRoot = path;
            AppConfigStore.Save(config);

            // Update ViewModel binding if present
            if (DataContext is SettingsViewModel vm)
            {
                vm.ProjectsRootPath = path;
            }
        }

        // Browse for Backup location
        private async void OnBrowseBackupLocation(object? sender, RoutedEventArgs e)
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window is null)
                return;

            var storageProvider = window.StorageProvider;
            if (storageProvider is null)
                return;

            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose backup location",
                AllowMultiple = false
            });

            var folder = folders?.FirstOrDefault();
            if (folder is null)
                return;

            var path = folder.Path?.LocalPath;
            if (string.IsNullOrWhiteSpace(path))
                return;

            // Let SettingsViewModel handle persistence for backup location
            if (DataContext is SettingsViewModel vm)
            {
                vm.BackupLocationPath = path;
            }
        }
    }
}