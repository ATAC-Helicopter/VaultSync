using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.Views;

internal sealed class RecoveryUsbWindow : Window
{
    private readonly ComboBox _disks = new();
    private readonly TextBox _digest = new() { MaxLength = 64 };
    private readonly TextBox _confirmation = new() { MaxLength = 64 };
    private readonly Button _imageButton = new(), _refresh = new(), _write = new(), _cancel = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _imageLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _deviceLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, IsVisible = false };
    private readonly StackPanel _choices;
    private CancellationTokenSource? _operation;
    private string? _image;
    private bool _choosing;

    internal RecoveryUsbWindow()
    {
        Title = Text("Title");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _imageButton.Content = Text("Choose");
        _refresh.Content = Text("Refresh");
        _write.Content = Text("Write");
        _cancel.Content = Common("Common.Close");
        AutomationProperties.SetName(_digest, Text("Hash"));
        AutomationProperties.SetName(_disks, Common("Recovery.Wizard.Target"));
        AutomationProperties.SetName(_confirmation, Text("Confirm"));
        _choices = new StackPanel { Spacing = 8 };
        _choices.Children.AddRange([
            _imageButton, _imageLabel, new TextBlock { Text = Text("Hash"), TextWrapping = TextWrapping.Wrap }, _digest,
            new TextBlock { Text = Common("Recovery.Wizard.Target") }, _disks, _refresh, _deviceLabel,
            new TextBlock { Text = Text("Confirm"), TextWrapping = TextWrapping.Wrap }, _confirmation, _write
        ]);
        Content = new ScrollViewer { Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 12,
            Children = { _choices, _progress, _status, _cancel }
        } };
        _imageButton.Click += (_, _) => Run(ChooseAsync);
        _refresh.Click += (_, _) => Run(RefreshAsync);
        _write.Click += (_, _) => Run(WriteAsync);
        _cancel.Click += (_, _) =>
        {
            if (_operation is { } operation) operation.Cancel();
            else Close();
        };
        _digest.TextChanged += (_, _) => UpdateButtons();
        _confirmation.TextChanged += (_, _) => UpdateButtons();
        _disks.SelectionChanged += (_, _) =>
        {
            _deviceLabel.Text = _disks.SelectedItem?.ToString() ?? "";
            _confirmation.Text = "";
            UpdateButtons();
        };
        Opened += (_, _) => Run(RefreshAsync);
        Closing += (_, args) =>
        {
            if (_operation is null) return;
            args.Cancel = true;
            _operation.Cancel();
        };
        UpdateButtons();
    }

    private static void Run(Func<Task> action) => _ = DetachedTask.RunAsync(action, "recovery-usb");

    private async Task ChooseAsync()
    {
        if (_operation is not null || _choosing) return;
        _choosing = true;
        UpdateButtons();
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Text("Choose"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("RAW") { Patterns = ["*.raw", "*.img"] }]
            });
            if (files.Count == 1)
            {
                using var file = files[0];
                _image = file.TryGetLocalPath();
                _imageLabel.Text = file.Name;
                _digest.Text = "";
                _confirmation.Text = "";
            }
        }
        finally
        {
            _choosing = false;
            UpdateButtons();
        }
    }

    private Task RefreshAsync() => OperateAsync(async token =>
    {
        _disks.SelectedItem = null;
        _disks.ItemsSource = null;
        if (!LinuxRecoveryUsbMedia.Supported)
        {
            _status.Text = Text("NoDevices");
            return;
        }
        var disks = await LinuxRecoveryUsbDevices.ListAsync(token);
        _disks.ItemsSource = disks;
        _status.Text = disks.Count == 0 ? Text("NoDevices") : "";
    }, false);

    private Task WriteAsync()
    {
        if (!Ready || _image is not { } image || _digest.Text is not { } digest ||
            _disks.SelectedItem is not LinuxRecoveryUsbDevices.Target target) return Task.CompletedTask;

        return OperateAsync(async token =>
        {
            _status.Text = Text("Working");
            _progress.Value = 0;
            _progress.IsVisible = true;
            var progress = new Progress<double>(value => _progress.Value = value);
            await LinuxRecoveryUsbMedia.WriteAsync(image, digest, target.Disk,
                (_, cancellation) => LinuxRecoveryUsbDevices.OpenAsync(target, cancellation), progress, token);
            _status.Text = Text("Done");
        }, true);
    }

    private async Task OperateAsync(Func<CancellationToken, Task> work, bool write)
    {
        if (_operation is not null || _choosing) return;
        using var stop = new CancellationTokenSource();
        _operation = stop;
        _cancel.Content = Common("Common.Cancel");
        _status.Text = "";
        UpdateButtons();
        try { await work(stop.Token); }
        catch (OperationCanceledException)
        {
            _status.Text = write ? Common("Recovery.Wizard.Cancelled") : "";
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or
            System.IO.InvalidDataException or TimeoutException or Tmds.DBus.Protocol.DBusExceptionBase or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _status.Text = Text(write ? "Failed" : "NoDevices");
        }
        finally
        {
            _operation = null;
            _progress.IsVisible = false;
            if (write)
            {
                _disks.ItemsSource = null;
                _confirmation.Text = "";
            }
            _cancel.Content = Common("Common.Close");
            UpdateButtons();
        }
    }

    private bool Ready => _image is not null && _digest.Text is { Length: 64 } hash &&
        hash.All(char.IsAsciiHexDigit) && _disks.SelectedItem is LinuxRecoveryUsbDevices.Target target &&
        _confirmation.Text == target.KernelPath;

    private void UpdateButtons()
    {
        _choices.IsEnabled = _operation is null && !_choosing;
        _write.IsEnabled = Ready;
    }

    private static string Text(string key) => Common("DiskRecovery.Usb." + key);
    private static string Common(string key) => LocalizationProvider.Service?.GetString(key) ?? key;
}
