using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;
using VaultSync.UI.Notifications;
using VaultSync.UI.ViewModels.Notifications;

namespace VaultSync.UI.ViewModels
{
    public sealed class LogConsoleViewModel : ViewModelBase
    {
        private readonly LogConsoleService _service;
        private string _statusMessage = string.Empty;

        public LogConsoleViewModel(LogConsoleService service)
        {
            _service = service;
            _service.StateChanged += OnServiceStateChanged;

            ClearCommand = new RelayCommand(_ => _service.Clear());
            ExportCommand = new RelayCommand(async _ => await ExportLogsAsync());
            OpenFolderCommand = new RelayCommand(_ => OpenLogFolder());
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

        public ICommand ClearCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand OpenFolderCommand { get; }

        private async System.Threading.Tasks.Task ExportLogsAsync()
        {
            var path = await System.Threading.Tasks.Task.Run(() => _service.ExportBuffer());
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
                var folder = LogConsoleService.GetLogDirectory();
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

        private void OnServiceStateChanged()
        {
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(IsSaving));
        }

        private static string L(string key, string fallback) =>
            LocalizationProvider.Service?.GetString(key) ?? fallback;

        private static string Lf(string key, string fallback, params object[] args)
        {
            var text = L(key, fallback);
            return args is { Length: > 0 }
                ? string.Format(text, args)
                : text;
        }
    }
}
