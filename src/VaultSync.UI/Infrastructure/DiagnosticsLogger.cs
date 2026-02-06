using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VaultSync.UI.Infrastructure;

internal static class DiagnosticsLogger
{
    private const int MaxRecent = 200;
    private static readonly ConcurrentQueue<string> Recent = new();
    private static int _recentCount;
    private static readonly object FileGate = new();
    private static string? _sessionPath;
    private static string? _heartbeatPath;
    private static Timer? _heartbeatTimer;
    private static int _dumpInFlight;
    private static int _dumpAttempted;

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
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet-dump",
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
}
