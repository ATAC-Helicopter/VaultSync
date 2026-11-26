using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
            if (DataContext is not DiskRecoveryViewModel { CanPrepareUsb: true }) return;
            if (OperatingSystem.IsWindows())
            {
                var executable = Environment.ProcessPath;
                if (executable is null || !Path.GetFileName(executable).Equals("VaultSync.UI.exe", StringComparison.OrdinalIgnoreCase))
                    return;
                var launch = new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory
                };
                launch.ArgumentList.Add(Program.PrepareRecoveryUsbArgument);
                launch.ArgumentList.Add(LocalizationProvider.Service?.CurrentLanguage ?? "en");
                try { using var child = Process.Start(launch); }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { }
            }
            else if (TopLevel.GetTopLevel(this) is Window owner)
            {
                await new RecoveryUsbWindow().ShowDialog(owner);
            }
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
