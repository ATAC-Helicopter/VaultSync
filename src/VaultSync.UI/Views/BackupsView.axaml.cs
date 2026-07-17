using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views
{
    public partial class BackupsView : UserControl
    {
        private BackupsViewModel? _viewModel;

        public BackupsView()
        {
            AvaloniaXamlLoader.Load(this);
            SizeChanged += (_, _) => UpdateSummaryLayout();
            DataContextChanged += OnDataContextChanged;
            AttachedToVisualTree += (_, _) => OnDataContextChanged(this, EventArgs.Empty);
            DetachedFromVisualTree += (_, _) => DetachViewModel();
            OnDataContextChanged(this, EventArgs.Empty);
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            DetachViewModel();

            _viewModel = DataContext as BackupsViewModel;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateSummaryLayout();
        }

        private void DetachViewModel()
        {
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = null;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(BackupsViewModel.DiffFileContentLines))
                return;

            Dispatcher.UIThread.Post(() =>
            {
                ScrollViewer? scrollViewer = this.FindControl<ScrollViewer>("DiffContentScrollViewer");
                if (scrollViewer is not null)
                    scrollViewer.Offset = new Vector(0, 0);
            }, DispatcherPriority.Loaded);
        }

        private void UpdateSummaryLayout()
        {
            if (DataContext is not BackupsViewModel vm)
                return;

            double width = Bounds.Width > 0 ? Bounds.Width : Width;
            if (width <= 0)
                return;

            vm.UpdateSummaryLayout(width);
        }
    }
}
