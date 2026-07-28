using Avalonia.Controls;
using Avalonia;
using System;

namespace VaultSync.UI.Views.Controls;

public partial class OnboardingTourOverlay : UserControl
{
    public OnboardingTourOverlay()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window window)
                return;
            ApplyResponsiveBounds(window.Bounds.Size);
            window.SizeChanged += (_, args) => ApplyResponsiveBounds(args.NewSize);
        };
    }

    private void ApplyResponsiveBounds(Size size)
    {
        Width = Math.Min(430, Math.Max(280, size.Width - 32));
        MaxHeight = Math.Max(320, size.Height - 32);
        Margin = size.Width < 760
            ? new Thickness(16)
            : new Thickness(24);
    }
}
