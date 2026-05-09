using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views
{
    public partial class BackupsView : UserControl
    {
        public BackupsView()
        {
            AvaloniaXamlLoader.Load(this);
            SizeChanged += (_, _) => UpdateSummaryLayout();
            DataContextChanged += (_, _) => UpdateSummaryLayout();
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
