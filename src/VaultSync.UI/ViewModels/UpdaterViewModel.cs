using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;

namespace VaultSync.UI.ViewModels;

public sealed class UpdaterViewModel : ViewModelBase
{
    private const int MaxLogLines = 400;
    private readonly PatchApplyRequest _request;
    private readonly Queue<string> _logLines = new();
    private readonly StringBuilder _logBuilder = new();
    private readonly RelayCommand _closeCommand;
    private bool _canClose;
    private bool _isBusy = true;
    private string _status;
    private string _logText = string.Empty;

    public UpdaterViewModel(PatchApplyRequest request)
    {
        _request = request;
        _status = L("Updater.Status.Preparing", "Preparing update...");
        _closeCommand = new RelayCommand(_ => RequestClose?.Invoke(), _ => CanClose);
    }

    public event Action? RequestClose;

    public static string Title => L("Updater.Title", "Updating VaultSync");

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetField(ref _logText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public bool CanClose
    {
        get => _canClose;
        private set
        {
            if (SetField(ref _canClose, value))
            {
                _closeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public static string CloseButtonText => L("Updater.Close", "Close");

    public ICommand CloseCommand => _closeCommand;

    public void Start()
    {
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        AppendLog(L("Updater.Log.Starting", "Starting updater helper."));
        Status = L("Updater.Status.Applying", "Applying update...");

        PatchApplyResult result = await PatchInstallService.ApplyPatchAsync(
            _request,
            AppendLog,
            CancellationToken.None);

        if (result.Success)
        {
            AppendLog(L("Updater.Log.Success", "Update applied successfully."));
            Status = L("Updater.Status.Success", "Update installed. Launching VaultSync...");
            IsBusy = false;
            CanClose = true;
            await Task.Delay(1500);
            RequestClose?.Invoke();
            return;
        }

        AppendLog(L("Updater.Log.Failed", "Update failed."));
        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            AppendLog(result.ErrorMessage);
        }
        AppendLog($"Log: {result.LogPath}");
        Status = L("Updater.Status.Failed", "Update failed. Check the log and try again.");
        IsBusy = false;
        CanClose = true;
    }

    private void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            _logLines.Enqueue(line);
            while (_logLines.Count > MaxLogLines)
            {
                _logLines.Dequeue();
            }

            _logBuilder.Clear();
            foreach (string item in _logLines)
            {
                _logBuilder.AppendLine(item);
            }

            LogText = _logBuilder.ToString();
        });
    }

    private static string L(string key, string fallback)
    {
        string? value = LocalizationProvider.Service?.GetString(key);
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.OrdinalIgnoreCase))
            return fallback;

        return value;
    }
}
