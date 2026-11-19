using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class DiskRecoveryViewModel : ViewModelBase
{
    private CancellationTokenSource? _inspection;
    private string? _folder;
    private string _statusKey = "DiskRecovery.Empty";
    private DiskImageInspectionResult? _receipt;

    public DiskRecoveryViewModel()
    {
        VerifyCommand = new AsyncRelayCommand(_ => VerifyAsync(), _ => _folder is not null && !IsBusy && Supported,
            "inspect-recovery-image");
        CancelCommand = new RelayCommand(_ => _inspection?.Cancel(), _ => IsBusy);
    }

    public AsyncRelayCommand VerifyCommand { get; }
    public RelayCommand CancelCommand { get; }
    public bool Supported => OperatingSystem.IsLinux() &&
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;
    public bool IsBusy => _inspection is not null;
    public bool CanChoose => !IsBusy;
    public string Folder => _folder ?? string.Empty;
    public string Status
    {
        get
        {
            if (!Supported) return Text("DiskRecovery.Unsupported");
            if (_receipt is { State: DiskImageInspectionState.Verified } receipt)
                return string.Format(CultureInfo.GetCultureInfo(LocalizationProvider.Service?.CurrentLanguage ?? "en"),
                    Text("DiskRecovery.Verified"), receipt.VerifiedBytes, receipt.LogicalSectorBytes);
            return Text(_statusKey);
        }
    }

    public void SelectFolder(string folder)
    {
        if (IsBusy) return;
        _folder = folder;
        _receipt = null;
        _statusKey = "DiskRecovery.Ready";
        Refresh();
    }

    public void Cancel() => _inspection?.Cancel();
    public void ReapplyLocalization() => OnPropertyChanged(nameof(Status));

    private async Task VerifyAsync()
    {
        if (_folder is not { } folder || IsBusy || !Supported) return;
        using var cancellation = new CancellationTokenSource();
        _inspection = cancellation;
        _receipt = null;
        _statusKey = "DiskRecovery.Checking";
        await Dispatcher.UIThread.InvokeAsync(Refresh);
        try
        {
            await using var metadata = OpenReadOnly(Path.Combine(folder, "image.metadata"));
            await using var payload = OpenReadOnly(Path.Combine(folder, "payload.raw"));
            var receipt = await DiskImageInspectionService.InspectAsync(metadata, payload, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _receipt = receipt;
                _statusKey = receipt.State switch
                {
                    DiskImageInspectionState.Verified => "DiskRecovery.Verified",
                    DiskImageInspectionState.Rejected => "DiskRecovery.Rejected",
                    DiskImageInspectionState.UnsupportedPlatform => "DiskRecovery.Unsupported",
                    DiskImageInspectionState.Unavailable => "DiskRecovery.Unavailable",
                    _ => "DiskRecovery.Failed"
                };
            });
        }
        catch (OperationCanceledException) { await SetStatusAsync("DiskRecovery.Cancelled"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Do not expose raw exception text, paths or parser output as UI errors.
            await SetStatusAsync("DiskRecovery.Failed");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => { _inspection = null; Refresh(); });
        }
    }

    private static FileStream OpenReadOnly(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
    private async Task SetStatusAsync(string key) =>
        await Dispatcher.UIThread.InvokeAsync(() => { _receipt = null; _statusKey = key; });
    private static string Text(string key) => LocalizationProvider.Service?.GetString(key) ?? key;
    private void Refresh()
    {
        OnPropertyChanged(string.Empty);
        VerifyCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }
}
