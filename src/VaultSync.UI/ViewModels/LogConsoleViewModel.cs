using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Input.Platform;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;
using VaultSync.UI.Notifications;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI.ViewModels
{
    public sealed class LogConsoleViewModel : ViewModelBase, IDisposable
    {
        private readonly LogConsoleService _service;
        private Func<string, Task<bool>>? _copyTextAsync;
        private string _statusMessage = string.Empty;
        private bool _autoScrollEnabled = true;
        private LogLine? _selectedLine;

        public LogConsoleViewModel(LogConsoleService service)
        {
            _service = service;
            _service.StateChanged += OnServiceStateChanged;

            ClearCommand = new RelayCommand(_ => _service.Clear());
            ExportCommand = new RelayCommand(async _ => await ExportLogsAsync());
            OpenFolderCommand = new RelayCommand(_ => OpenLogFolder());
            CopySelectedLineCommand = new RelayCommand(async _ => await CopySelectedLineAsync(), _ => SelectedLine is not null);
        }

        public void SetClipboardProvider(Func<IClipboard?> clipboardProvider)
        {
            if (clipboardProvider is null)
                throw new ArgumentNullException(nameof(clipboardProvider));

            SetCopyTextAsync(text => ClipboardHelper.TryCopyAsync(text, clipboardProvider()));
        }

        public void SetCopyTextAsync(Func<string, Task<bool>> copyTextAsync)
        {
            _copyTextAsync = copyTextAsync ?? throw new ArgumentNullException(nameof(copyTextAsync));
        }

        public void SetUiCaptureEnabled(bool enabled)
        {
            _service.SetUiCaptureEnabled(enabled, loadSnapshot: enabled);
        }

        public ReadOnlyObservableCollection<LogLine> Lines => _service.Lines;

        public bool IsEnabled => _service.Enabled;
        public bool IsSaving => _service.SaveToFile;

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetField(ref _statusMessage, value);
        }

        public bool AutoScrollEnabled
        {
            get => _autoScrollEnabled;
            set => SetField(ref _autoScrollEnabled, value);
        }

        public LogLine? SelectedLine
        {
            get => _selectedLine;
            set
            {
                if (SetField(ref _selectedLine, value))
                {
                    if (CopySelectedLineCommand is RelayCommand copyCommand)
                        copyCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public ICommand ClearCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand CopySelectedLineCommand { get; }

        private async System.Threading.Tasks.Task ExportLogsAsync()
        {
            string? path = await System.Threading.Tasks.Task.Run(() => _service.ExportBuffer());
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusMessage = L("LogConsole.ExportFailed", "Log export failed.");
                GlobalNotificationCenter.Instance.Show(
                    StatusMessage,
                    NotificationSeverity.Warning,
                    L("LogConsole.ExportTitle", "Log export"));
                return;
            }

            StatusMessage = Lf("LogConsole.ExportedTo", "Exported to {0}", path);
            GlobalNotificationCenter.Instance.Show(
                L("LogConsole.ExportReady", "Log export ready. You can share the file."),
                NotificationSeverity.Info,
                L("LogConsole.ExportTitle", "Log export"));
        }

        private void OpenLogFolder()
        {
            try
            {
                string folder = LogConsoleService.GetLogDirectory();
                Directory.CreateDirectory(folder);

                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
                }
                else if (OperatingSystem.IsMacOS())
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "open",
                        UseShellExecute = false
                    };
                    psi.ArgumentList.Add(folder);
                    Process.Start(psi);
                }
                else if (OperatingSystem.IsLinux())
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        UseShellExecute = false
                    };
                    psi.ArgumentList.Add(folder);
                    Process.Start(psi);
                }
                else
                {
                    Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
                }
            }
            catch
            {
                StatusMessage = L("LogConsole.OpenFolderFailed", "Failed to open log folder.");
                GlobalNotificationCenter.Instance.Show(
                    StatusMessage,
                    NotificationSeverity.Warning,
                    L("LogConsole.ExportTitle", "Log export"));
            }
        }

        public async System.Threading.Tasks.Task<bool> CopySelectedLineAsync()
        {
            if (SelectedLine is null)
                return false;

            bool copied = _copyTextAsync is { } copyTextAsync
                ? await copyTextAsync(SelectedLine.RawDisplay)
                : await ClipboardHelper.TryCopyAsync(SelectedLine.RawDisplay);
            StatusMessage = copied
                ? L("LogConsole.CopySelectedSuccess", "Selected line copied.")
                : L("LogConsole.CopySelectedFailed", "Failed to copy selected line.");
            return copied;
        }

        public async System.Threading.Tasks.Task<bool> CopyLinesAsync(IEnumerable<LogLine> lines)
        {
            if (lines is null)
                throw new ArgumentNullException(nameof(lines));

            var selectedLines = lines.Where(line => line is not null).ToList();
            if (selectedLines.Count == 0)
                return await CopySelectedLineAsync();

            string text = string.Join(Environment.NewLine, selectedLines.Select(line => line.RawDisplay));
            bool copied = _copyTextAsync is { } copyTextAsync
                ? await copyTextAsync(text)
                : await ClipboardHelper.TryCopyAsync(text);
            StatusMessage = copied
                ? Lf("LogConsole.CopySelectedManySuccess", "Copied {0} selected lines.", selectedLines.Count)
                : L("LogConsole.CopySelectedFailed", "Failed to copy selected line.");
            return copied;
        }

        private void OnServiceStateChanged()
        {
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(IsSaving));
        }

        public void Dispose()
        {
            _service.StateChanged -= OnServiceStateChanged;
        }

        private static string L(string key, string fallback) =>
            LocalizationProvider.Service?.GetString(key) ?? fallback;

        private static string Lf(string key, string fallback, params object[] args)
        {
            string text = L(key, fallback);
            return args is { Length: > 0 }
                ? string.Format(text, args)
                : text;
        }
    }
}
