using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
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
        private readonly object _snapshotGate = new();
        private readonly List<LogLine> _snapshotLines = new();
        private readonly ConcurrentQueue<LogLine> _pending = new();
        private readonly object _fileGate = new();
        private readonly StringBuilder _fileBuffer = new();
        private bool _captureInstalled;
        private TextWriter? _originalOut;
        private TextWriter? _originalErr;
        private int _flushScheduled;
        private const int MaxFlushBatch = 200;
        private Timer? _fileFlushTimer;
        private const int FileFlushIntervalMs = 2000;
        private const int MaxFileBufferChars = 32 * 1024;

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
                if (!_saveToFile)
                {
                    StopFileCapture();
                }
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
            lock (_snapshotGate)
            {
                _snapshotLines.Clear();
            }
        }

        public string? ExportBuffer()
        {
            try
            {
                var exportsDir = Path.Combine(GetLogRoot(), "exports");
                Directory.CreateDirectory(exportsDir);

                var path = Path.Combine(exportsDir, $"vaultsync-ui-log-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt");
                List<LogLine> snapshot;
                lock (_snapshotGate)
                {
                    snapshot = _snapshotLines.ToList();
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

        public string? GetRecentSnippet(int maxLines, string? header = null)
        {
            try
            {
                if (maxLines <= 0)
                    return null;

                List<LogLine> snapshot;
                lock (_snapshotGate)
                {
                    snapshot = _snapshotLines.ToList();
                }

                if (snapshot.Count == 0)
                    return null;

                var start = Math.Max(0, snapshot.Count - maxLines);
                var sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(header))
                {
                    sb.AppendLine(header);
                }

                for (var i = start; i < snapshot.Count; i++)
                {
                    var line = snapshot[i];
                    sb.AppendLine($"[{line.Timestamp:O}] {line.Source}: {line.Message}");
                }

                return sb.ToString().TrimEnd();
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
                lock (_snapshotGate)
                {
                    _snapshotLines.Add(entry);
                    if (_snapshotLines.Count > MaxLines)
                    {
                        var toRemove = _snapshotLines.Count - MaxLines;
                        _snapshotLines.RemoveRange(0, toRemove);
                    }
                }

                _pending.Enqueue(entry);
                ScheduleFlush();

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
                _lines.RemoveAt(0);
        }

        private void ScheduleFlush()
        {
            if (Interlocked.Exchange(ref _flushScheduled, 1) == 1)
                return;

            Dispatcher.UIThread.Post(FlushPending);
        }

        private void FlushPending()
        {
            Interlocked.Exchange(ref _flushScheduled, 0);

            if (_pending.IsEmpty)
                return;

            var batch = new List<LogLine>(MaxFlushBatch);
            while (batch.Count < MaxFlushBatch && _pending.TryDequeue(out var entry))
            {
                batch.Add(entry);
            }

            foreach (var entry in batch)
                _lines.Add(entry);

            TrimIfNeeded();

            if (!_pending.IsEmpty)
                ScheduleFlush();
        }

        private void AppendToFile(LogLine entry)
        {
            try
            {
                var path = GetDailyLogPath();
                lock (_fileGate)
                {
                    _fileBuffer.Append('[')
                        .Append(entry.Timestamp.ToString("O"))
                        .Append("] ")
                        .Append(entry.Source)
                        .Append(": ")
                        .Append(entry.Message)
                        .Append(Environment.NewLine);

                    if (_fileBuffer.Length >= MaxFileBufferChars)
                    {
                        FlushFileBuffer(path);
                    }
                    else if (_fileFlushTimer is null)
                    {
                        _fileFlushTimer = new Timer(_ => FlushFileBuffer(path), null, FileFlushIntervalMs, FileFlushIntervalMs);
                    }
                }
            }
            catch
            {
                // Never throw from logger.
            }
        }

        private void StopFileCapture()
        {
            try
            {
                var path = GetDailyLogPath();
                lock (_fileGate)
                {
                    _fileFlushTimer?.Dispose();
                    _fileFlushTimer = null;
                    FlushFileBuffer(path);
                }
            }
            catch
            {
                // Never throw from logger.
            }
        }

        private void FlushFileBuffer(string path)
        {
            lock (_fileGate)
            {
                if (_fileBuffer.Length == 0)
                    return;

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, _fileBuffer.ToString());
                _fileBuffer.Clear();
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
