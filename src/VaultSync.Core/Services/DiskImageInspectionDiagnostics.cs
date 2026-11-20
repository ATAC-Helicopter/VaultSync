using System.Diagnostics;
using System.Globalization;

namespace VaultSync.Core.Services;

// Local capture-host evidence only. Never sent to product diagnostics or the
// worker; no strings, image facts, paths, process identities or stderr payloads.
internal enum DiskImageInspectionPhase { Installation = 1, Start, Peer, Exchange, Result, Sandbox, Acknowledgment, End, Exit }
internal sealed record DiskImageInspectionDiagnostic(DiskImageInspectionPhase Phase,
    DiskImageInspectionWorkerOutcome Outcome, LinuxRecoveryStartupDiagnostics.Error Error,
    long ElapsedMilliseconds, int? WorkerExitCode, long ReadCalls, long ReadBytes);

internal sealed class DiskImageInspectionDiagnostics
{
    private readonly long started = Stopwatch.GetTimestamp();
    internal DiskImageInspectionPhase Phase = DiskImageInspectionPhase.Installation;
    private long calls, bytes;
    internal void ReadServed(int count) { calls++; bytes += count; }
    internal DiskImageInspectionDiagnostic Snapshot(DiskImageInspectionWorkerOutcome outcome,
        LinuxRecoveryStartupDiagnostics.Error error, int? exit) => new(Phase, outcome, error,
            Math.Clamp((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, 86400000),
            exit is >= 0 and <= 255 ? exit : null, Math.Clamp(calls, 0, 9000000000),
            Math.Clamp(bytes, 0, 3L * (long)DiskImageRawProfileVerificationService.MaximumCapacity + 68L * 1024 * 1024));

    internal static void Write(DiskImageInspectionDiagnostic? value, Action<string> sink)
    {
        if (value is null || !Enum.IsDefined(value.Phase) || !Enum.IsDefined(value.Outcome) ||
            !Enum.IsDefined(value.Error) || value.ElapsedMilliseconds is < 0 or > 86400000 ||
            value.WorkerExitCode is < 0 or > 255 || value.ReadCalls is < 0 or > 9000000000 ||
            value.ReadBytes < 0 || value.ReadBytes > 3L * (long)DiskImageRawProfileVerificationService.MaximumCapacity + 68L * 1024 * 1024)
            return;
        try
        {
            // One bounded terminal line. -1 means no exit was observed BEFORE
            // cleanup; a parent's subsequent kill is never called a worker exit.
            sink(string.Create(CultureInfo.InvariantCulture,
                $"VSINS1|{(int)value.Phase}|{(int)value.Outcome}|{(int)value.Error}|{value.ElapsedMilliseconds}|{value.WorkerExitCode ?? -1}|{value.ReadCalls}|{value.ReadBytes}\n"));
        }
        catch (Exception) { /* Diagnostics cannot replace any operation outcome. */ }
    }
}
