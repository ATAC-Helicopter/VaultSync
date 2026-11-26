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
    private bool _checkingFolder;
    private bool _hasImageFiles;
    private bool _savingRestore;
    private string? _restoreStatusKey;

    public DiskRecoveryViewModel()
    {
        VerifyCommand = new AsyncRelayCommand(_ => VerifyAsync(), _ => CanVerify,
            "inspect-recovery-image");
        CancelCommand = new RelayCommand(_ => _inspection?.Cancel(), _ => IsBusy);
        PrepareRestoreCommand = new AsyncRelayCommand(_ => PrepareRestoreAsync(), _ => CanPrepareRestore,
            "prepare-offline-restore");
    }

    public AsyncRelayCommand VerifyCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncRelayCommand PrepareRestoreCommand { get; }
    public bool Supported => OperatingSystem.IsLinux() &&
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;
    public bool CanPrepareUsb => Supported;
    public bool IsBusy => _inspection is not null;
    public bool CanChoose => !IsBusy && !_checkingFolder && !_savingRestore;
    public bool CanVerify => Supported && _hasImageFiles && CanChoose;
    public bool HasFolder => _folder is not null;
    public bool HasImageFiles => _hasImageFiles;
    public string Folder => _folder ?? string.Empty;
    public bool CanPrepareRestore => CanChoose && HasVerifiedExport;
    public bool HasVerifiedExport => _folder is { } folder &&
        _receipt is { State: DiskImageInspectionState.Verified } receipt &&
        DiskRecoveryRestoreRequest.CanSave(folder, receipt.ImageId);
    public string RestoreImageId => _receipt is { ImageId: var id } && id != Guid.Empty ? id.ToString("N") : string.Empty;
    public bool HasRestoreStatus => _restoreStatusKey is not null;
    public string RestoreStatus => _restoreStatusKey is { } key ? Text(key) : string.Empty;
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

    public async Task SelectFolderAsync(string folder)
    {
        if (!CanChoose) return;
        _checkingFolder = true;
        _hasImageFiles = false;
        _folder = folder;
        _receipt = null;
        _restoreStatusKey = null;
        _statusKey = "DiskRecovery.Choose";
        Refresh();
        try
        {
            _statusKey = await Task.Run(() => CheckImageFolder(folder), CancellationToken.None);
            _hasImageFiles = _statusKey == "DiskRecovery.Ready";
        }
        finally
        {
            _checkingFolder = false;
            Refresh();
        }
    }

    private static string CheckImageFolder(string folder)
    {
        try
        {
            foreach (string name in new[] { "image.metadata", "payload.raw" })
            {
                if ((File.GetAttributes(Path.Combine(folder, name)) & FileAttributes.Directory) != 0)
                    return "DiskRecovery.MissingFiles";
            }
            return "DiskRecovery.Ready";
        }
        catch (FileNotFoundException) { return "DiskRecovery.MissingFiles"; }
        catch (DirectoryNotFoundException) { return "DiskRecovery.MissingFiles"; }
        catch (UnauthorizedAccessException) { return "DiskRecovery.FolderUnreadable"; }
        catch (IOException) { return "DiskRecovery.FolderUnreadable"; }
    }

    public void Cancel() => _inspection?.Cancel();
    public void ReapplyLocalization()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(RestoreStatus));
    }

    private async Task PrepareRestoreAsync()
    {
        if (!CanPrepareRestore || _folder is not { } folder || _receipt is not { } receipt) return;
        _savingRestore = true;
        _restoreStatusKey = null;
        Refresh();
        try
        {
            await Task.Run(() => DiskRecoveryRestoreRequest.Save(folder, receipt.ImageId));
            _restoreStatusKey = "DiskRecovery.RestorePrepared";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _restoreStatusKey = "DiskRecovery.RestorePrepareFailed";
        }
        finally
        {
            _savingRestore = false;
            Refresh();
        }
    }

    private async Task VerifyAsync()
    {
        if (_folder is not { } folder || !CanVerify) return;
        using var cancellation = new CancellationTokenSource();
        _inspection = cancellation;
        _receipt = null;
        _restoreStatusKey = null;
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
        catch (FileNotFoundException) { await SetMissingFilesAsync(); }
        catch (DirectoryNotFoundException) { await SetMissingFilesAsync(); }
        catch (UnauthorizedAccessException) { await SetStatusAsync("DiskRecovery.FolderUnreadable"); }
        catch (IOException)
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
    private async Task SetMissingFilesAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _hasImageFiles = false;
            _statusKey = "DiskRecovery.MissingFiles";
        });
    private static string Text(string key) => LocalizationProvider.Service?.GetString(key) ?? key;
    private void Refresh()
    {
        OnPropertyChanged(string.Empty);
        VerifyCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        PrepareRestoreCommand.RaiseCanExecuteChanged();
    }
}
