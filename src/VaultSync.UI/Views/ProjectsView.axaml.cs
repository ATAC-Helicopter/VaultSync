using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views
{
    public partial class ProjectsView : UserControl
    {
        public ProjectsView()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnProjectTagPillDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (DataContext is not ProjectsViewModel vm)
                return;

            if (sender is not Control control || control.DataContext is not ProjectTagChip chip)
                return;

            vm.BeginEditProjectTag(chip.Value);
            e.Handled = true;
        }

        private void OnProjectTagInputKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            if (DataContext is not ProjectsViewModel vm)
                return;

            if (!vm.CommitProjectTagInputCommand.CanExecute(null))
                return;

            vm.CommitProjectTagInputCommand.Execute(null);
            e.Handled = true;
        }
    }
}
