using System.Reflection;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Application-owned collector. No connection to legacy telemetry/log payloads.</summary>
public static class ProductDiagnostics
{
    private static ProductDiagnosticReporter? reporter;
    private static Timer? timer;
    private static Assembly? productAssembly;
    public static bool IsBeta => reporter?.IsBeta == true;
    public static bool Enabled => reporter?.Enabled == true;
    public static bool HasDecision => reporter?.HasDecision == true;
    public static event Action? ConsentChanged;
    internal static void InitializeForTests(ProductDiagnosticReporter value, Assembly? assembly = null)
    {
        timer?.Dispose();
        reporter?.Dispose();
        reporter = value;
        productAssembly = assembly;
        timer = null;
    }
    public static void Initialize(string directory, Assembly? assembly = null)
    {
        try
        {
            if (reporter is not null) return;
            productAssembly = assembly;
            reporter = new ProductDiagnosticReporter(directory,
                ProductDiagnosticReport.Channel(assembly ?? typeof(ProductDiagnostics).Assembly) == "beta");
            timer = new Timer(_state => { _ = FlushSafely(); }, null, TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1));
        }
        catch { /* Local storage failure cannot block the application. */ }
    }
    public static bool SetConsent(bool enabled)
    {
        bool saved = reporter?.SetConsent(enabled) == true;
        try { ConsentChanged?.Invoke(); } catch { }
        return saved;
    }
    public static void Record(DiagnosticOperation operation, DiagnosticOutcome outcome, Exception? exception = null,
        DiagnosticPhase phase = DiagnosticPhase.None, DiagnosticMode mode = DiagnosticMode.None,
        TimeSpan duration = default, long quantity = 0, DiagnosticFailure? failure = null)
    {
        if (reporter is null) return;
        try { var report = ProductDiagnosticReport.Create(operation, outcome, exception, phase, mode, duration, quantity, productAssembly);
            reporter?.Record(failure is null ? report : report with { Failure = failure.Value }); }
        catch { }
    }
    public static Task TraceBetaAsync(DiagnosticOperation operation, Func<Task> action) =>
        TraceBetaAsync(operation, async () => { await action().ConfigureAwait(false); return true; });

    // Fixed-code operation boundaries only; no delegate arguments/results are inspected or serialized.
    public static async Task<T> TraceBetaAsync<T>(DiagnosticOperation operation, Func<Task<T>> action,
        DiagnosticMode mode = DiagnosticMode.None, bool recordCompletion = true)
    {
        if (!IsBeta) return await action().ConfigureAwait(false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Record(operation, DiagnosticOutcome.Started, mode: mode);
        try
        {
            T result = await action().ConfigureAwait(false);
            if (recordCompletion) Record(operation, DiagnosticOutcome.Completed, mode: mode, duration: clock.Elapsed);
            return result;
        }
        catch (OperationCanceledException)
        {
            Record(operation, DiagnosticOutcome.Cancelled, mode: mode, duration: clock.Elapsed);
            throw;
        }
        catch (Exception ex)
        {
            Record(operation, DiagnosticOutcome.Failed, ex, mode: mode, duration: clock.Elapsed);
            throw;
        }
    }

    public static string Review() { try { return reporter?.Review() ?? "[]"; } catch { return "[]"; } }
    public static void DeleteReports() => reporter?.DeleteReports();
    private static async Task FlushSafely() { try { if (reporter is not null) await reporter.FlushAsync().ConfigureAwait(false); } catch { } }
    public static void Shutdown() { timer?.Dispose(); reporter?.Dispose(); }
}
