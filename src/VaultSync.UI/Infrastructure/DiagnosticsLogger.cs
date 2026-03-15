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
    private static readonly ConcurrentQueue<string> Recent = new();
    private static readonly ConcurrentDictionary<string, int> FirstChanceCounts = new(StringComparer.Ordinal);
    private static int _recentCount;
    private static readonly object FileGate = new();
    private static string? _sessionPath;
    private static string? _heartbeatPath;
    private static Timer? _heartbeatTimer;
    private static int _dumpInFlight;
    private static int _dumpAttempted;
    private static int _firstChanceTotal;
    private static int _consoleMirroringInstalled;
    private static int _traceListenerInstalled;

    public static string? SessionLogPath => _sessionPath;

    public static void Initialize()
    {
        if (_sessionPath is not null)
            return;

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VaultSync",
                "diagnostics");
            Directory.CreateDirectory(dir);
            PruneDiagnostics(dir);
            _sessionPath = Path.Combine(dir, $"session-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log");
            _heartbeatPath = Path.Combine(dir, "heartbeat.txt");
            LogPreviousHeartbeat();
            InstallTraceListener();
            InstallConsoleMirroring();
            StartHeartbeat();
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

        var line = $"{DateTimeOffset.UtcNow:O} [t{Thread.CurrentThread.ManagedThreadId}] {message}";
        Recent.Enqueue(line);
        var count = Interlocked.Increment(ref _recentCount);
        while (count > MaxRecent && Recent.TryDequeue(out _))
        {
            count = Interlocked.Decrement(ref _recentCount);
        }

        var path = _sessionPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                lock (FileGate)
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Best-effort diagnostics.
            }
        });
    }

    public static void RecordWithStack(string message, int maxLines = 10)
    {
        Record(message);
        try
        {
            var stack = Environment.StackTrace;
            if (string.IsNullOrWhiteSpace(stack))
                return;

            var lines = stack.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var take = Math.Min(maxLines, lines.Length);
            var sb = new StringBuilder();
            for (var i = 0; i < take; i++)
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
            var osDescription = RuntimeInformation.OSDescription;
            var framework = RuntimeInformation.FrameworkDescription;
            var processPath = Environment.ProcessPath ?? string.Empty;
            var baseDir = AppContext.BaseDirectory;
            var envKeys = new[]
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

            foreach (var key in envKeys)
            {
                var value = Environment.GetEnvironmentVariable(key);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (string.Equals(key, "PATH", StringComparison.Ordinal))
                {
                    var segments = value
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

        var total = Interlocked.Increment(ref _firstChanceTotal);
        if (total > MaxFirstChanceTotal)
            return;

        var signature = $"{source}|{ex.GetType().FullName}|{ex.Message}";
        var count = FirstChanceCounts.AddOrUpdate(signature, 1, static (_, current) => current + 1);
        if (count > MaxFirstChancePerSignature)
            return;

        var topFrame = ex.StackTrace?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "no-stack";

        Record($"FirstChance[{source}] #{count}: {ex.GetType().Name} - {ex.Message} @ {topFrame}");
    }

    public static string? GetRecentLog()
    {
        if (Recent.IsEmpty)
            return null;

        var sb = new StringBuilder();
        foreach (var line in Recent)
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
            var content = File.ReadAllText(_heartbeatPath).Trim();
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
                var line = $"pid={Environment.ProcessId} utc={DateTimeOffset.UtcNow:O}";
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

    private static void PruneDiagnostics(string dir)
    {
        try
        {
            PruneByPattern(dir, "session-*.log", keep: 5);
            PruneByPattern(dir, "sample-*.txt", keep: 5);
            PruneByPattern(dir, "hangdump-*.dmp", keep: 5);
            PruneByPattern(dir, "trace-*.log", keep: 5);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private static void PruneByPattern(string dir, string pattern, int keep)
    {
        if (keep <= 0)
            return;

        var files = Directory.GetFiles(dir, pattern)
            .Select(path => new FileInfo(path))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .ToList();

        if (files.Count <= keep)
            return;

        foreach (var fi in files.Skip(keep))
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

    public static void TryCollectDump(string reason)
    {
        if (Interlocked.Exchange(ref _dumpAttempted, 1) == 1)
            return;

        if (Interlocked.Exchange(ref _dumpInFlight, 1) == 1)
            return;

        _ = Task.Run(() =>
        {
            var shouldSample = false;
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VaultSync",
                    "diagnostics");
                Directory.CreateDirectory(dir);
                var output = Path.Combine(dir, $"hangdump-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.dmp");
                var dotnetDump = ResolveExecutablePath("dotnet-dump");
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
                psi.ArgumentList.Add("full");
                psi.ArgumentList.Add("--output");
                psi.ArgumentList.Add(output);

                Record($"Attempting dump collection: reason='{reason}', output='{output}'.");
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null)
                {
                    Record("dotnet-dump failed to start.");
                    return;
                }
                proc.WaitForExit(20_000);
                var stderr = proc.StandardError.ReadToEnd().Trim();
                var stdout = proc.StandardOutput.ReadToEnd().Trim();
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

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var isWindows = OperatingSystem.IsWindows();
        var hasExtension = Path.HasExtension(fileName);
        var windowsExtensions = isWindows
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                    return candidate;

                if (!hasExtension && isWindows)
                {
                    foreach (var ext in windowsExtensions)
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
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VaultSync",
                "diagnostics");
            Directory.CreateDirectory(dir);
            var output = Path.Combine(dir, $"sample-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt");
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
            var stderr = proc.StandardError.ReadToEnd().Trim();
            var stdout = proc.StandardOutput.ReadToEnd().Trim();
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
                foreach (var ch in value)
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
