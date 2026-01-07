using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Threading;

namespace VaultSync.UI.Services
{
    public sealed record LogLine(DateTimeOffset Timestamp, string Source, string Message)
    {
        public string Display => $"[{Timestamp:HH:mm:ss}] {Source}: {Message}";
    }

    public sealed class LogConsoleService
    {
        private readonly ObservableCollection<LogLine> _lines = new();
        private readonly ReadOnlyObservableCollection<LogLine> _readOnlyLines;
        private readonly object _fileGate = new();
        private bool _captureInstalled;
        private TextWriter? _originalOut;
        private TextWriter? _originalErr;

        public LogConsoleService()
        {
            _readOnlyLines = new ReadOnlyObservableCollection<LogLine>(_lines);
        }

        public ReadOnlyObservableCollection<LogLine> Lines => _readOnlyLines;

        private bool _enabled;
        private bool _saveToFile;

        public event Action? StateChanged;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                    return;
                _enabled = value;
                StateChanged?.Invoke();
            }
        }

        public bool SaveToFile
        {
            get => _saveToFile;
            set
            {
                if (_saveToFile == value)
                    return;
                _saveToFile = value;
                StateChanged?.Invoke();
            }
        }
        public int MaxLines { get; set; } = 2000;

        public void InstallCapture()
        {
            if (_captureInstalled)
                return;

            _originalOut = Console.Out;
            _originalErr = Console.Error;

            Console.SetOut(new LogTextWriter(this, _originalOut, "stdout"));
            Console.SetError(new LogTextWriter(this, _originalErr, "stderr"));
            Trace.Listeners.Add(new LogTraceListener(this));

            _captureInstalled = true;
        }

        public void Clear()
        {
            Dispatcher.UIThread.Post(() => _lines.Clear());
        }

        public string? ExportBuffer()
        {
            try
            {
                var exportsDir = Path.Combine(GetLogRoot(), "exports");
                Directory.CreateDirectory(exportsDir);

                var path = Path.Combine(exportsDir, $"vaultsync-ui-log-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt");
                List<LogLine> snapshot;
                if (Dispatcher.UIThread.CheckAccess())
                {
                    snapshot = _lines.ToList();
                }
                else
                {
                    snapshot = Dispatcher.UIThread.InvokeAsync(() => _lines.ToList())
                        .GetAwaiter()
                        .GetResult();
                }

                var sb = new StringBuilder();
                foreach (var line in snapshot)
                {
                    sb.AppendLine($"[{line.Timestamp:O}] {line.Source}: {line.Message}");
                }

                File.WriteAllText(path, sb.ToString());
                return path;
            }
            catch
            {
                return null;
            }
        }

        internal void Append(string? message, string source)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(message))
                return;

            foreach (var line in message.Replace("\r", string.Empty).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (source == "trace" && IsNoisyTrace(line))
                    continue;

                var entry = new LogLine(DateTimeOffset.Now, source, line);

                Dispatcher.UIThread.Post(() =>
                {
                    _lines.Add(entry);
                    TrimIfNeeded();
                });

                if (SaveToFile)
                {
                    AppendToFile(entry);
                }
            }
        }

        private static bool IsNoisyTrace(string line)
        {
            return line.Contains("Layout cycle detected", StringComparison.OrdinalIgnoreCase)
                || line.Contains("PlatformImpl is null, couldn't handle input", StringComparison.OrdinalIgnoreCase)
                || line.Contains("RenderTargetCorruptedException", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Exception in render loop", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Resize failed", StringComparison.OrdinalIgnoreCase);
        }

        private void TrimIfNeeded()
        {
            if (_lines.Count <= MaxLines)
                return;

            var toRemove = _lines.Count - MaxLines;
            for (var i = 0; i < toRemove; i++)
            {
                _lines.RemoveAt(0);
            }
        }

        private void AppendToFile(LogLine entry)
        {
            try
            {
                var path = GetDailyLogPath();
                var line = $"[{entry.Timestamp:O}] {entry.Source}: {entry.Message}{Environment.NewLine}";
                lock (_fileGate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.AppendAllText(path, line);
                }
            }
            catch
            {
                // Never throw from logger.
            }
        }

        private static string GetLogRoot()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VaultSync",
                "logs");
        }

        public static string GetLogDirectory()
        {
            return GetLogRoot();
        }

        private static string GetDailyLogPath()
        {
            return Path.Combine(GetLogRoot(), $"ui-{DateTimeOffset.UtcNow:yyyy-MM-dd}.log");
        }

        private sealed class LogTextWriter : TextWriter
        {
            private readonly LogConsoleService _service;
            private readonly TextWriter? _passthrough;
            private readonly string _source;
            private readonly StringBuilder _buffer = new();

            public LogTextWriter(LogConsoleService service, TextWriter? passthrough, string source)
            {
                _service = service;
                _passthrough = passthrough;
                _source = source;
            }

            public override Encoding Encoding => Encoding.UTF8;

            public override void Write(char value)
            {
                _passthrough?.Write(value);
                if (value == '\n')
                {
                    FlushBuffer();
                }
                else if (value != '\r')
                {
                    _buffer.Append(value);
                }
            }

            public override void Write(string? value)
            {
                _passthrough?.Write(value);
                if (string.IsNullOrEmpty(value))
                    return;

                foreach (var ch in value)
                {
                    Write(ch);
                }
            }

            public override void WriteLine(string? value)
            {
                _passthrough?.WriteLine(value);
                if (!string.IsNullOrEmpty(value))
                {
                    _buffer.Append(value);
                }
                FlushBuffer();
            }

            private void FlushBuffer()
            {
                var line = _buffer.ToString();
                _buffer.Clear();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _service.Append(line, _source);
                }
            }
        }

        private sealed class LogTraceListener : TraceListener
        {
            private readonly LogConsoleService _service;

            public LogTraceListener(LogConsoleService service)
            {
                _service = service;
            }

            public override void Write(string? message)
            {
                _service.Append(message, "trace");
            }

            public override void WriteLine(string? message)
            {
                _service.Append(message, "trace");
            }
        }
    }

    public static class LogConsoleProvider
    {
        public static LogConsoleService? Service { get; private set; }

        public static void Initialize(LogConsoleService service)
        {
            Service = service;
        }
    }
}
