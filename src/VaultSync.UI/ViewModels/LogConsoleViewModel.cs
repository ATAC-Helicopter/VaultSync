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
            ExportCommand = new RelayCommand(_ => ExportLogs());
            OpenFolderCommand = new RelayCommand(_ => OpenLogFolder());
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

        private void ExportLogs()
        {
            var path = _service.ExportBuffer();
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusMessage = "Log export failed.";
                GlobalNotificationCenter.Instance.Show(
                    StatusMessage,
                    NotificationSeverity.Warning,
                    "Log export");
                return;
            }

            StatusMessage = $"Exported to {path}";
            GlobalNotificationCenter.Instance.Show(
                "Log export ready. You can share the file.",
                NotificationSeverity.Info,
                "Log export");
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
                    Process.Start("open", folder);
                }
                else if (OperatingSystem.IsLinux())
                {
                    Process.Start("xdg-open", folder);
                }
                else
                {
                    Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
                }
            }
            catch
            {
                StatusMessage = "Failed to open log folder.";
                GlobalNotificationCenter.Instance.Show(
                    StatusMessage,
                    NotificationSeverity.Warning,
                    "Log export");
            }
        }

        private void OnServiceStateChanged()
        {
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(IsSaving));
        }
    }
}
