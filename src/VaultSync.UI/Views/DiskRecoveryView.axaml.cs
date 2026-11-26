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

    private void PrepareUsb(object? sender, RoutedEventArgs args) =>
        _ = DetachedTask.RunAsync(async () =>
        {
            if (DataContext is DiskRecoveryViewModel { CanPrepareUsb: true } &&
                TopLevel.GetTopLevel(this) is Window owner)
                await new RecoveryUsbWindow().ShowDialog(owner);
        }, "prepare-recovery-usb");

    private void ChooseImage(object? sender, RoutedEventArgs args) =>
        _ = DetachedTask.RunAsync(async () =>
        {
            if (DataContext is not DiskRecoveryViewModel model || !model.CanChoose ||
                TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage) return;
            using var startFolder = model.HasFolder
                ? await storage.TryGetFolderFromPathAsync(model.Folder)
                : null;
            var selected = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LocalizationProvider.Service?.GetString("DiskRecovery.Choose") ?? "DiskRecovery.Choose",
                AllowMultiple = false,
                SuggestedStartLocation = startFolder
            });
            if (selected.Count == 1 && selected[0].TryGetLocalPath() is { } folder)
                await model.SelectFolderAsync(folder);
        }, "choose-recovery-image");
}
