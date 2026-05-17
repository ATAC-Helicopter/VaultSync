using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.Services
{
    public sealed record LogLine(DateTimeOffset Timestamp, string Source, string Message)
    {
        private static readonly IBrush DiagnosticsBackground = new ImmutableSolidColorBrush(Color.Parse("#23304A"));
        private static readonly IBrush DiagnosticsForeground = new ImmutableSolidColorBrush(Color.Parse("#BFD1FF"));
        private static readonly IBrush OutputBackground = new ImmutableSolidColorBrush(Color.Parse("#173B34"));
        private static readonly IBrush OutputForeground = new ImmutableSolidColorBrush(Color.Parse("#A7F3D0"));
        private static readonly IBrush ErrorBackground = new ImmutableSolidColorBrush(Color.Parse("#4A1F28"));
        private static readonly IBrush ErrorForeground = new ImmutableSolidColorBrush(Color.Parse("#FEB2B2"));
        private static readonly IBrush TraceBackground = new ImmutableSolidColorBrush(Color.Parse("#3A314A"));
        private static readonly IBrush TraceForeground = new ImmutableSolidColorBrush(Color.Parse("#DDD6FE"));

        public string TimeText => Timestamp.ToString("HH:mm:ss");

        public string SourceText => SourceKind switch
        {
            "diagnostics" => "DIAG",
            "stderr" => "ERR",
            "stdout" => "OUT",
            "trace" => "TRACE",
            _ => Source.ToUpperInvariant()
        };

        public IBrush SourceBackground => SourceKind switch
        {
            "stderr" => ErrorBackground,
            "stdout" => OutputBackground,
            "trace" => TraceBackground,
            _ => DiagnosticsBackground
        };

        public IBrush SourceForeground => SourceKind switch
        {
            "stderr" => ErrorForeground,
            "stdout" => OutputForeground,
            "trace" => TraceForeground,
            _ => DiagnosticsForeground
        };

        public string MessageText => SimplifyMessage(Message);

        public string Display => $"[{TimeText}] {SourceText}: {MessageText}";

        public string RawDisplay => $"[{Timestamp:O}] {Source}: {Message}";

        private string SourceKind
        {
            get
            {
                if (!string.Equals(Source, "diagnostics", StringComparison.Ordinal))
                    return Source;

                string text = Message.TrimStart();
                if (text.Contains("CONSOLE[stderr]", StringComparison.Ordinal))
                    return "stderr";
                if (text.Contains("CONSOLE[stdout]", StringComparison.Ordinal))
                    return "stdout";
                if (text.Contains("TRACE ", StringComparison.Ordinal) ||
                    text.Contains("TRACE[", StringComparison.Ordinal))
                {
                    return "trace";
                }

                return Source;
            }
        }

        private static string SimplifyMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            string text = message.Trim();
            int threadMarker = text.IndexOf(" [t", StringComparison.Ordinal);
            if (threadMarker > 0 && text.Length > 24 && char.IsDigit(text[0]))
            {
                int threadEnd = text.IndexOf("] ", threadMarker, StringComparison.Ordinal);
                if (threadEnd >= 0 && threadEnd + 2 < text.Length)
                {
                    text = text[(threadEnd + 2)..].TrimStart();
                }
            }

            if (text.StartsWith("CONSOLE[", StringComparison.Ordinal))
            {
                int sourceEnd = text.IndexOf("] ", StringComparison.Ordinal);
                if (sourceEnd > 8 && sourceEnd + 2 < text.Length)
                {
                    string consoleSource = text[8..sourceEnd].ToUpperInvariant();
                    text = $"{consoleSource}: {text[(sourceEnd + 2)..]}";
                }
            }

            return text;
        }
    }

    public sealed class LogConsoleService
    {
        private const int DefaultMaxLines = 2000;
        private const int ReducedMaxLines = 200;
        private readonly ObservableCollection<LogLine> _lines = [];
        private readonly ReadOnlyObservableCollection<LogLine> _readOnlyLines;
        private readonly object _snapshotGate = new();
        private readonly List<LogLine> _snapshotLines = [];
        private readonly ConcurrentQueue<LogLine> _pending = new();
        private int _pendingCount;
        private int _suppressNextIbusTraceStackFrames;
        private readonly object _fileGate = new();
        private readonly StringBuilder _fileBuffer = new();
        private int _uiCaptureEnabled;
        private bool _captureInstalled;
        private TextWriter? _originalOut;
        private TextWriter? _originalErr;
        private int _flushScheduled;
        private int _flushDelayed;
        private DateTime _lastFlushUtc = DateTime.MinValue;
        private int _maxFlushBatch = 200;
        private Timer? _fileFlushTimer;
        private int _fileFlushIntervalMs = 2000;
        private int _maxFileBufferChars = 32 * 1024;
        private int _fileFlushQueued;

        public LogConsoleService()
        {
            _readOnlyLines = new ReadOnlyObservableCollection<LogLine>(_lines);
            SeedDiagnosticsSnapshot();
            DiagnosticsLogger.Recorded += OnDiagnosticsRecorded;
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
                ApplyMaxLines();
                if (!_enabled)
                {
                    lock (_snapshotGate)
                    {
                        if (_snapshotLines.Count > MaxLines)
                        {
                            int toRemove = _snapshotLines.Count - MaxLines;
                            _snapshotLines.RemoveRange(0, toRemove);
                        }
                    }
                }
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
                _fileFlushIntervalMs = _saveToFile ? 5000 : 2000;
                _maxFileBufferChars = _saveToFile ? 128 * 1024 : 32 * 1024;
                if (!_saveToFile)
                {
                    StopFileCapture();
                }
                StateChanged?.Invoke();
            }
        }
        public int MaxLines { get; set; } = DefaultMaxLines;

        public void SetUiCaptureEnabled(bool enabled, bool loadSnapshot = false)
        {
            int value = enabled ? 1 : 0;
            Interlocked.Exchange(ref _uiCaptureEnabled, value);
            _maxFlushBatch = enabled
                ? (OperatingSystem.IsMacOS() ? 20 : 50)
                : 200;
            ApplyMaxLines();

            if (enabled && loadSnapshot)
            {
                List<LogLine> snapshot;
                lock (_snapshotGate)
                {
                    snapshot = [.. _snapshotLines];
                }

                Dispatcher.UIThread.Post(() =>
                {
                    _lines.Clear();
                    foreach (LogLine line in snapshot)
                        _lines.Add(line);
                });
            }
            else if (!enabled)
            {
                while (_pending.TryDequeue(out _))
                {
                }

                Interlocked.Exchange(ref _pendingCount, 0);
                Interlocked.Exchange(ref _flushScheduled, 0);
                Interlocked.Exchange(ref _flushDelayed, 0);
                TrimSnapshotIfNeeded();
            }
        }

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
                string exportsDir = Path.Combine(GetLogRoot(), "exports");
                Directory.CreateDirectory(exportsDir);

                string path = Path.Combine(exportsDir, $"vaultsync-ui-log-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt");
                List<LogLine> snapshot;
                lock (_snapshotGate)
                {
                    snapshot = [.. _snapshotLines];
                }

                var sb = new StringBuilder();
                foreach (LogLine line in snapshot)
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
                    snapshot = [.. _snapshotLines];
                }

                if (snapshot.Count == 0)
                    return null;

                int start = Math.Max(0, snapshot.Count - maxLines);
                var sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(header))
                {
                    sb.AppendLine(header);
                }

                for (int i = start; i < snapshot.Count; i++)
                {
                    LogLine line = snapshot[i];
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
            if (!ShouldCapture() || string.IsNullOrWhiteSpace(message))
                return;

            if (Dispatcher.UIThread.CheckAccess())
            {
                string captured = message;
                ThreadPool.QueueUserWorkItem(_ => AppendCore(captured, source));
                return;
            }

            AppendCore(message, source);
        }

        private void AppendCore(string message, string source)
        {
            foreach (string line in message.Replace("\r", string.Empty).Split('\n'))
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
                        int toRemove = _snapshotLines.Count - MaxLines;
                        _snapshotLines.RemoveRange(0, toRemove);
                    }
                }

                if (Interlocked.CompareExchange(ref _uiCaptureEnabled, 0, 0) == 1)
                {
                    if (OperatingSystem.IsMacOS())
                    {
                        int queued = Interlocked.Increment(ref _pendingCount);
                        if (queued > 200)
                        {
                            Interlocked.Decrement(ref _pendingCount);
                            continue;
                        }
                    }

                    _pending.Enqueue(entry);
                    ScheduleFlush();
                }

                if (SaveToFile)
                {
                    AppendToFile(entry);
                }
            }
        }

        private void OnDiagnosticsRecorded(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            if (_captureInstalled &&
                (line.Contains("CONSOLE[", StringComparison.Ordinal) ||
                 line.Contains("TRACE ", StringComparison.Ordinal) ||
                 line.Contains("TRACE[", StringComparison.Ordinal)))
            {
                return;
            }

            AppendDiagnostics(line);
        }

        private void AppendDiagnostics(string line)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                string captured = line;
                ThreadPool.QueueUserWorkItem(_ => AppendCore(captured, "diagnostics"));
                return;
            }

            AppendCore(line, "diagnostics");
        }

        private void SeedDiagnosticsSnapshot()
        {
            string? recent = DiagnosticsLogger.GetRecentLog();
            if (string.IsNullOrWhiteSpace(recent))
                return;

            foreach (string line in recent.Replace("\r", string.Empty).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                _snapshotLines.Add(new LogLine(DateTimeOffset.Now, "diagnostics", line));
                if (_snapshotLines.Count > MaxLines)
                {
                    _snapshotLines.RemoveAt(0);
                }
            }
        }

        private bool ShouldCapture() =>
            Enabled ||
            SaveToFile ||
            Interlocked.CompareExchange(ref _uiCaptureEnabled, 0, 0) == 1;

        private void ApplyMaxLines()
        {
            MaxLines = Enabled || Interlocked.CompareExchange(ref _uiCaptureEnabled, 0, 0) == 1
                ? DefaultMaxLines
                : ReducedMaxLines;
        }

        private void TrimSnapshotIfNeeded()
        {
            lock (_snapshotGate)
            {
                if (_snapshotLines.Count <= MaxLines)
                    return;

                _snapshotLines.RemoveRange(0, _snapshotLines.Count - MaxLines);
            }
        }

        private bool IsNoisyTrace(string line)
        {
            if (Interlocked.CompareExchange(ref _suppressNextIbusTraceStackFrames, 0, 0) > 0)
            {
                if (line.Contains("Tmds.DBus.Protocol", StringComparison.Ordinal) ||
                    line.Contains("Avalonia.FreeDesktop.DBusIme", StringComparison.Ordinal) ||
                    line.Contains("IValueTaskSource", StringComparison.Ordinal) ||
                    line.Contains("CallMethodAsync", StringComparison.Ordinal) ||
                    line.StartsWith("at ", StringComparison.Ordinal) ||
                    line.StartsWith("   at ", StringComparison.Ordinal))
                {
                    Interlocked.Decrement(ref _suppressNextIbusTraceStackFrames);
                    return true;
                }

                Interlocked.Exchange(ref _suppressNextIbusTraceStackFrames, 0);
            }

            if (line.Contains("[IME] Error while destroying the context", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("org.freedesktop.DBus.Error.UnknownMethod: Method Destroy is not implemented on interface org.freedesktop.IBus.Service", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Exchange(ref _suppressNextIbusTraceStackFrames, 8);
                return true;
            }

            return IsNoisyTraceLine(line);
        }

        private static bool IsNoisyTraceLine(string line)
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

            int toRemove = _lines.Count - MaxLines;
            for (int i = 0; i < toRemove; i++)
                _lines.RemoveAt(0);
        }

        private void ScheduleFlush()
        {
            if (Interlocked.Exchange(ref _flushScheduled, 1) == 1)
                return;

            var minInterval = TimeSpan.FromMilliseconds(200);
            DateTime now = DateTime.UtcNow;
            if ((now - _lastFlushUtc) < minInterval)
            {
                if (Interlocked.Exchange(ref _flushDelayed, 1) == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(minInterval).ConfigureAwait(false);
                        Interlocked.Exchange(ref _flushDelayed, 0);
                        Dispatcher.UIThread.Post(FlushPending);
                    });
                }
                return;
            }

            Dispatcher.UIThread.Post(FlushPending);
        }

        private void FlushPending()
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            _lastFlushUtc = DateTime.UtcNow;

            if (_pending.IsEmpty)
                return;

            var batch = new List<LogLine>(_maxFlushBatch);
            while (batch.Count < _maxFlushBatch && _pending.TryDequeue(out LogLine? entry))
            {
                Interlocked.Decrement(ref _pendingCount);
                batch.Add(entry);
            }

            foreach (LogLine entry in batch)
                _lines.Add(entry);

            TrimIfNeeded();

            if (!_pending.IsEmpty)
                ScheduleFlush();
        }

        private void AppendToFile(LogLine entry)
        {
            try
            {
                string path = GetDailyLogPath();
                lock (_fileGate)
                {
                    _fileBuffer.Append('[')
                        .Append(entry.Timestamp.ToString("O"))
                        .Append("] ")
                        .Append(entry.Source)
                        .Append(": ")
                        .Append(entry.Message)
                        .Append(Environment.NewLine);

                    if (_fileBuffer.Length >= _maxFileBufferChars)
                    {
                        QueueFileFlush(path);
                    }
                    else if (_fileFlushTimer is null)
                    {
                        _fileFlushTimer = new Timer(_ => QueueFileFlush(path), null, _fileFlushIntervalMs, _fileFlushIntervalMs);
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
                string path = GetDailyLogPath();
                lock (_fileGate)
                {
                    _fileFlushTimer?.Dispose();
                    _fileFlushTimer = null;
                }
                FlushFileBuffer(path);
            }
            catch
            {
                // Never throw from logger.
            }
        }

        private void QueueFileFlush(string path)
        {
            if (Interlocked.Exchange(ref _fileFlushQueued, 1) == 1)
                return;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    FlushFileBuffer(path);
                }
                finally
                {
                    Interlocked.Exchange(ref _fileFlushQueued, 0);
                }
            });
        }

        private void FlushFileBuffer(string path)
        {
            string? payload = null;
            lock (_fileGate)
            {
                if (_fileBuffer.Length == 0)
                    return;

                payload = _fileBuffer.ToString();
                _fileBuffer.Clear();
            }

            if (string.IsNullOrEmpty(payload))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, payload);
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
                if (!OperatingSystem.IsMacOS() && !Dispatcher.UIThread.CheckAccess())
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
                if (!OperatingSystem.IsMacOS() && !Dispatcher.UIThread.CheckAccess())
                    _passthrough?.Write(value);
                if (string.IsNullOrEmpty(value))
                    return;

                foreach (char ch in value)
                {
                    Write(ch);
                }
            }

            public override void WriteLine(string? value)
            {
                if (!OperatingSystem.IsMacOS() && !Dispatcher.UIThread.CheckAccess())
                    _passthrough?.WriteLine(value);
                if (!string.IsNullOrEmpty(value))
                {
                    _buffer.Append(value);
                }
                FlushBuffer();
            }

            private void FlushBuffer()
            {
                string line = _buffer.ToString();
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
