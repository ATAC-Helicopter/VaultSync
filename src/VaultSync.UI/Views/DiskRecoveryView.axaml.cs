using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;
using VaultSync.UI.ViewModels;

namespace VaultSync.UI.Views;

public partial class DiskRecoveryView : UserControl
{
    public DiskRecoveryView()
    {
        InitializeComponent();
        DetachedFromVisualTree += (_, _) => (DataContext as DiskRecoveryViewModel)?.Cancel();
    }

    private void ChooseImage(object? sender, RoutedEventArgs args) =>
        _ = DetachedTask.RunAsync(async () =>
        {
            if (DataContext is not DiskRecoveryViewModel model || !model.CanChoose ||
                TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage) return;
            var selected = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LocalizationProvider.Service?.GetString("DiskRecovery.Choose") ?? "DiskRecovery.Choose",
                AllowMultiple = false
            });
            if (selected.Count == 1 && selected[0].TryGetLocalPath() is { } folder)
                model.SelectFolder(folder);
        }, "choose-recovery-image");
}
