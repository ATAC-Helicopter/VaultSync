using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VaultSync.UI.Infrastructure;

internal static class DiagnosticsLogger
{
    private const int MaxRecent = 1000;
    private const int MaxFirstChanceTotal = 250;
    private const int MaxFirstChancePerSignature = 5;
    private const int MaxHangDumpFiles = 2;
    private const long MaxDiagnosticsBytes = 1024L * 1024L * 1024L;
    private static readonly TimeSpan DiagnosticsPruneInterval = TimeSpan.FromHours(6);
    private static readonly string[] RetainedDiagnosticPatterns =
    [
        "session-*.log",
        "sample-*.txt",
        "hangdump-*.dmp",
        "trace-*.log"
    ];
    private static readonly ConcurrentQueue<string> Recent = new();
    private static readonly ConcurrentQueue<string> PendingWrites = new();
    private static readonly ConcurrentDictionary<string, int> FirstChanceCounts = new(StringComparer.Ordinal);
    private static int _recentCount;
    private static readonly object FileGate = new();
    private static string? _sessionPath;
    private static string? _heartbeatPath;
    private static Timer? _heartbeatTimer;
    private static Timer? _retentionTimer;
    private static Task? _writerTask;
    private static CancellationTokenSource? _writerCts;
    private static readonly AutoResetEvent WriterSignal = new(false);
    private static int _dumpInFlight;
    private static int _dumpAttempted;
    private static int _firstChanceTotal;
    private static int _consoleMirroringInstalled;
    private static int _traceListenerInstalled;
    private static int _writerStarted;

    public static string? SessionLogPath => _sessionPath;
    public static event Action<string>? Recorded;

    public static void Initialize()
    {
        if (_sessionPath is not null)
            return;

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VaultSync",
                "diagnostics");
            Directory.CreateDirectory(dir);
            PruneDiagnostics(dir);
            _sessionPath = Path.Combine(dir, $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log");
            _heartbeatPath = Path.Combine(dir, "heartbeat.txt");
            StartWriter();
            LogPreviousHeartbeat();
            InstallTraceListener();
            InstallConsoleMirroring();
            StartHeartbeat();
            StartPeriodicRetention(dir);
            Record("Diagnostics session started.");
        }
        catch
        {
            _sessionPath = null;
        }
    }

    public static void Record(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        string line = $"{DateTimeOffset.UtcNow:O} [t{Thread.CurrentThread.ManagedThreadId}] {message}";
        RaiseRecorded(line);
        Recent.Enqueue(line);
        int count = Interlocked.Increment(ref _recentCount);
        while (count > MaxRecent && Recent.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref _recentCount);
        }

        string? path = _sessionPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        PendingWrites.Enqueue(line);
        WriterSignal.Set();
    }

    public static void Shutdown()
    {
        Timer? heartbeatTimer = Interlocked.Exchange(ref _heartbeatTimer, null);
        heartbeatTimer?.Dispose();

        Timer? retentionTimer = Interlocked.Exchange(ref _retentionTimer, null);
        retentionTimer?.Dispose();

        CancellationTokenSource? writerCts = Interlocked.Exchange(ref _writerCts, null);
        if (writerCts is not null)
        {
            try
            {
                writerCts.Cancel();
                WriterSignal.Set();
                _writerTask?.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Diagnostics shutdown must never fail process cleanup.
            }
            finally
            {
                writerCts.Dispose();
                _writerTask = null;
                Interlocked.Exchange(ref _writerStarted, 0);
            }
        }

        FlushPendingWrites();
    }

    private static void RaiseRecorded(string line)
    {
        Action<string>? handlers = Recorded;
        if (handlers is null)
            return;

        foreach (Delegate callback in handlers.GetInvocationList())
        {
            try
            {
                if (callback is Action<string> handler)
                    handler(line);
            }
            catch
            {
                // Diagnostics must never fail the caller.
            }
        }
    }

    public static void RecordWithStack(string message, int maxLines = 10)
    {
        Record(message);
        try
        {
            string stack = Environment.StackTrace;
            if (string.IsNullOrWhiteSpace(stack))
                return;

            string[] lines = stack.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int take = Math.Min(maxLines, lines.Length);
            var sb = new StringBuilder();
            for (int i = 0; i < take; i++)
            {
                sb.AppendLine(lines[i]);
            }
            Record("Stack (truncated):\n" + sb.ToString().TrimEnd());
        }
        catch
        {
            // Best-effort
        }
    }

    public static void RecordStartupSnapshot(string[] args, bool useSoftwareFallback)
    {
        try
        {
            string osDescription = RuntimeInformation.OSDescription;
            string framework = RuntimeInformation.FrameworkDescription;
            string processPath = Environment.ProcessPath ?? string.Empty;
            string baseDir = AppContext.BaseDirectory;
            string[] envKeys = new[]
            {
                "DOTNET_ENVIRONMENT",
                "DOTNET_ROOT",
                "DOTNET_gcServer",
                "DOTNET_gcConcurrent",
                "DOTNET_DefaultDiagnosticPortSuspend",
                "VAULTSYNC_IGNORE_SIGTERM",
                "AVALONIA_SKIA",
                "AVALONIA_RENDERER",
                "PATH"
            };

            Record(
                $"Startup snapshot: os='{osDescription}', framework='{framework}', arch='{RuntimeInformation.ProcessArchitecture}', " +
                $"pid={Environment.ProcessId}, softwareFallback={useSoftwareFallback}, cwd='{Environment.CurrentDirectory}', " +
                $"baseDir='{baseDir}', processPath='{processPath}', args='{string.Join(' ', args)}'.");

            foreach (string? key in envKeys)
            {
                string? value = Environment.GetEnvironmentVariable(key);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (string.Equals(key, "PATH", StringComparison.Ordinal))
                {
                    System.Collections.Generic.IEnumerable<string> segments = value
                        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Take(8);
                    Record($"Environment {key}={string.Join(Path.PathSeparator, segments)}");
                    continue;
                }

                Record($"Environment {key}={value}");
            }
        }
        catch (Exception ex)
        {
            Record($"Startup snapshot failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    public static void RecordException(string context, Exception ex, bool includeStack = true)
    {
        if (ex is null)
            return;

        Record($"{context}: {ex.GetType().Name} - {ex.Message}");
        if (!includeStack)
            return;

        try
        {
            Record(ex.ToString());
        }
        catch
        {
            // Best-effort.
        }
    }

    public static void RecordFirstChanceException(Exception ex, string source)
    {
        if (ex is null)
            return;

        if (ShouldSuppressFirstChanceException(ex))
            return;

        int total = Interlocked.Increment(ref _firstChanceTotal);
        if (total > MaxFirstChanceTotal)
            return;

        string signature = $"{source}|{ex.GetType().FullName}|{ex.Message}";
        int count = FirstChanceCounts.AddOrUpdate(signature, 1, static (_, current) => current + 1);
        if (count > MaxFirstChancePerSignature)
            return;

        Record($"FirstChance[{source}] #{count}: {ex.GetType().Name} - {ex.Message} @ {FormatFirstChanceLocation(ex)}");
    }

    private static string FormatFirstChanceLocation(Exception ex)
    {
        string topFrame = ex.StackTrace?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "no-stack";

        if (ex is not System.Reflection.TargetInvocationException { InnerException: { } inner })
            return topFrame;

        string innerTopFrame = inner.StackTrace?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "no-inner-stack";

        return $"{topFrame}; inner={inner.GetType().Name} - {inner.Message} @ {innerTopFrame}";
    }

    private static bool ShouldSuppressFirstChanceException(Exception ex)
    {
        if (ex is DirectoryNotFoundException)
            return true;

        if (ex is UnauthorizedAccessException && IsExpectedRetentionDeleteException(ex.StackTrace))
            return true;

        string typeName = ex.GetType().FullName ?? string.Empty;
        return typeName.Contains("DBusException", StringComparison.Ordinal);
    }

    private static bool IsExpectedRetentionDeleteException(string? stackTrace)
    {
        return !string.IsNullOrWhiteSpace(stackTrace)
            && (stackTrace.Contains("VaultSync.Core.Services.BackupService.FallbackDeleteDirectory", StringComparison.Ordinal)
            || stackTrace.Contains("VaultSync.Core.Services.BackupService.TryDeleteBackupFolder", StringComparison.Ordinal)
            || stackTrace.Contains("VaultSync.UI.ViewModels.AppViewModel.BackupHistoryHandlers", StringComparison.Ordinal));
    }

    public static string? GetRecentLog()
    {
        if (Recent.IsEmpty)
            return null;

        var sb = new StringBuilder();
        foreach (string line in Recent)
        {
            sb.AppendLine(line);
        }
        return sb.ToString().TrimEnd();
    }

    private static void LogPreviousHeartbeat()
    {
        if (string.IsNullOrWhiteSpace(_heartbeatPath) || !File.Exists(_heartbeatPath))
            return;

        try
        {
            string content = File.ReadAllText(_heartbeatPath).Trim();
            if (string.IsNullOrWhiteSpace(content))
                return;

            Record($"Previous heartbeat: {content}");
        }
        catch
        {
            // ignore heartbeat read failures
        }
    }

    private static void StartHeartbeat()
    {
        if (string.IsNullOrWhiteSpace(_heartbeatPath))
            return;

        _heartbeatTimer = new Timer(_ =>
        {
            try
            {
                string line = $"pid={Environment.ProcessId} utc={DateTimeOffset.UtcNow:O}";
                lock (FileGate)
                {
                    File.WriteAllText(_heartbeatPath!, line);
                }
            }
            catch
            {
                // best-effort
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    private static void StartPeriodicRetention(string diagnosticsDirectory)
    {
        _retentionTimer = new Timer(
            _ => PruneDiagnostics(diagnosticsDirectory),
            null,
            DiagnosticsPruneInterval,
            DiagnosticsPruneInterval);
    }

    private static void StartWriter()
    {
        if (Interlocked.Exchange(ref _writerStarted, 1) == 1)
            return;

        _writerCts = new CancellationTokenSource();
        _writerTask = Task.Run(() => WriterLoop(_writerCts.Token));
    }

    private static void WriterLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                WriterSignal.WaitOne(TimeSpan.FromMilliseconds(250));
                FlushPendingWrites();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Best-effort diagnostics writer.
            }
        }

        FlushPendingWrites();
    }

    private static void FlushPendingWrites()
    {
        string? path = _sessionPath;
        if (string.IsNullOrWhiteSpace(path) || PendingWrites.IsEmpty)
            return;

        var batch = new StringBuilder();
        while (PendingWrites.TryDequeue(out string? line))
        {
            batch.AppendLine(line);
        }

        if (batch.Length == 0)
            return;

        try
        {
            lock (FileGate)
            {
                File.AppendAllText(path, batch.ToString());
            }
        }
        catch
        {
            // Best-effort diagnostics.
        }
    }

    internal static void PruneDiagnostics(
        string dir,
        int maxHangDumpFiles = MaxHangDumpFiles,
        long maxDiagnosticsBytes = MaxDiagnosticsBytes)
    {
        try
        {
            PruneByPattern(dir, "session-*.log", keep: 5);
            PruneByPattern(dir, "sample-*.txt", keep: 5);
            PruneByPattern(dir, "hangdump-*.dmp", keep: maxHangDumpFiles);
            PruneByPattern(dir, "trace-*.log", keep: 5);
            PruneToTotalSize(dir, maxDiagnosticsBytes);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static void PruneByPattern(string dir, string pattern, int keep)
    {
        var files = Directory.GetFiles(dir, pattern)
            .Select(path => new FileInfo(path))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .ToList();

        if (files.Count <= keep)
            return;

        foreach (FileInfo? fi in files.Skip(keep))
        {
            try
            {
                fi.Delete();
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void PruneToTotalSize(string dir, long maxBytes)
    {
        if (maxBytes < 0)
            return;

        var files = RetainedDiagnosticPatterns
            .SelectMany(pattern => Directory.GetFiles(dir, pattern))
            .Distinct(StringComparer.Ordinal)
            .Select(path => new FileInfo(path))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .ToList();

        long retainedBytes = 0;
        foreach (FileInfo file in files)
        {
            if (file.Length <= maxBytes - retainedBytes)
            {
                retainedBytes += file.Length;
                continue;
            }

            TryDeleteDiagnostic(file);
        }
    }

    private static void TryDeleteDiagnostic(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch
        {
            // Diagnostics retention is best effort.
        }
    }

    public static void TryCollectDump(string reason)
    {
        if (Interlocked.Exchange(ref _dumpAttempted, 1) == 1)
            return;

        if (Interlocked.Exchange(ref _dumpInFlight, 1) == 1)
            return;

        _ = Task.Run(() =>
        {
            bool shouldSample = false;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VaultSync",
                    "diagnostics");
                Directory.CreateDirectory(dir);
                string output = Path.Combine(dir, $"hangdump-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.dmp");
                string dotnetDump = ResolveExecutablePath("dotnet-dump");
                if (string.IsNullOrWhiteSpace(dotnetDump))
                {
                    Record("dotnet-dump not found in PATH; skipping dump collection.");
                    shouldSample = true;
                    return;
                }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dotnetDump,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("collect");
                psi.ArgumentList.Add("--process-id");
                psi.ArgumentList.Add(Environment.ProcessId.ToString());
                psi.ArgumentList.Add("--type");
                psi.ArgumentList.Add("Mini");
                psi.ArgumentList.Add("--output");
                psi.ArgumentList.Add(output);

                Record($"Attempting dump collection: reason='{reason}', output='{output}'.");
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null)
                {
                    Record("dotnet-dump failed to start.");
                    return;
                }
                if (!proc.WaitForExit(20_000))
                {
                    Record("dotnet-dump timed out after 20 seconds; terminating collection.");
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5_000);
                    TryDeleteDiagnostic(new FileInfo(output));
                    shouldSample = true;
                    return;
                }

                string stderr = proc.StandardError.ReadToEnd().Trim();
                string stdout = proc.StandardOutput.ReadToEnd().Trim();
                if (!string.IsNullOrWhiteSpace(stdout))
                    Record($"dotnet-dump stdout: {stdout}");
                if (!string.IsNullOrWhiteSpace(stderr))
                    Record($"dotnet-dump stderr: {stderr}");
                Record($"dotnet-dump exit code: {proc.ExitCode}.");
                shouldSample = proc.ExitCode != 0;
            }
            catch (Exception ex)
            {
                Record($"dotnet-dump failed: {ex.GetType().Name} - {ex.Message}");
                shouldSample = true;
            }
            finally
            {
                Interlocked.Exchange(ref _dumpInFlight, 0);
                PruneDiagnostics(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VaultSync",
                    "diagnostics"));
            }

            if (shouldSample && OperatingSystem.IsMacOS())
            {
                TryCollectSample(reason);
            }
        });
    }

    private static string ResolveExecutablePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        if (Path.IsPathRooted(fileName))
            return File.Exists(fileName) ? fileName : string.Empty;

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        bool isWindows = OperatingSystem.IsWindows();
        bool hasExtension = Path.HasExtension(fileName);
        string[] windowsExtensions = isWindows
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];

        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;

                if (!hasExtension && isWindows)
                {
                    foreach (string ext in windowsExtensions)
                    {
                        candidate = Path.Combine(dir, fileName + ext.ToLowerInvariant());
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return string.Empty;
    }

    private static void TryCollectSample(string reason)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VaultSync",
                "diagnostics");
            Directory.CreateDirectory(dir);
            string output = Path.Combine(dir, $"sample-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/sample",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add("5");
            psi.ArgumentList.Add("-file");
            psi.ArgumentList.Add(output);

            Record($"Attempting sample capture: reason='{reason}', output='{output}'.");
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                Record("sample failed to start.");
                return;
            }
            proc.WaitForExit(10_000);
            string stderr = proc.StandardError.ReadToEnd().Trim();
            string stdout = proc.StandardOutput.ReadToEnd().Trim();
            if (!string.IsNullOrWhiteSpace(stdout))
                Record($"sample stdout: {stdout}");
            if (!string.IsNullOrWhiteSpace(stderr))
                Record($"sample stderr: {stderr}");
            Record($"sample exit code: {proc.ExitCode}.");
        }
        catch (Exception ex)
        {
            Record($"sample failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    private static void InstallTraceListener()
    {
        if (Interlocked.Exchange(ref _traceListenerInstalled, 1) == 1)
            return;

        try
        {
            Trace.Listeners.Add(new DiagnosticsTraceListener());
            Record("Trace listener installed.");
        }
        catch (Exception ex)
        {
            Record($"Trace listener install failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    private static void InstallConsoleMirroring()
    {
        if (Interlocked.Exchange(ref _consoleMirroringInstalled, 1) == 1)
            return;

        try
        {
            Console.SetOut(new DiagnosticsTextWriter(Console.Out, "stdout"));
            Console.SetError(new DiagnosticsTextWriter(Console.Error, "stderr"));
            Record("Console mirroring installed.");
        }
        catch (Exception ex)
        {
            Record($"Console mirroring install failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    private sealed class DiagnosticsTraceListener : TraceListener
    {
        public override void Write(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                Record($"TRACE {message.Trim()}");
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                Record($"TRACE {message.Trim()}");
        }

        public override void TraceEvent(TraceEventCache? eventCache, string? source, TraceEventType eventType, int id, string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            Record($"TRACE[{eventType}][{source ?? "unknown"}] {message.Trim()}");
        }
    }

    private sealed class DiagnosticsTextWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly string _streamName;
        private readonly StringBuilder _buffer = new();
        private readonly object _gate = new();

        public DiagnosticsTextWriter(TextWriter inner, string streamName)
        {
            _inner = inner;
            _streamName = streamName;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _inner.Write(value);
            Append(value);
        }

        public override void Write(string? value)
        {
            _inner.Write(value);
            if (string.IsNullOrEmpty(value))
                return;

            lock (_gate)
            {
                foreach (char ch in value)
                    AppendCore(ch);
            }
        }

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);
            if (!string.IsNullOrEmpty(value))
                Record($"CONSOLE[{_streamName}] {value}");
        }

        private void Append(char value)
        {
            lock (_gate)
            {
                AppendCore(value);
            }
        }

        private void AppendCore(char value)
        {
            if (value == '\r')
                return;

            if (value == '\n')
            {
                FlushBuffer();
                return;
            }

            _buffer.Append(value);
        }

        private void FlushBuffer()
        {
            if (_buffer.Length == 0)
                return;

            Record($"CONSOLE[{_streamName}] {_buffer}");
            _buffer.Clear();
        }
    }
}
