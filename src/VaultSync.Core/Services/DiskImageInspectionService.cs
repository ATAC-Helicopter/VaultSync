namespace VaultSync.Core.Services;

public enum DiskImageInspectionState { Verified, Rejected, UnsupportedPlatform, Unavailable, Failed, TimedOut }

/// <summary>A read-only inspection receipt, never a staged image or device authorization.</summary>
public sealed record DiskImageInspectionResult(DiskImageInspectionState State,
    ulong VerifiedBytes = 0, uint LogicalSectorBytes = 0);

/// <summary>Desktop entry point for the fixed, authenticated, isolated image reader.</summary>
public static class DiskImageInspectionService
{
    public static async Task<DiskImageInspectionResult> InspectAsync(Stream metadata, Stream payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64)
            return new(DiskImageInspectionState.UnsupportedPlatform);
        return Translate(await DiskImageInspectionWorkerClient.InspectAsync(metadata, payload, cancellationToken)
            .ConfigureAwait(false));
    }

    internal static DiskImageInspectionResult Translate(DiskImageInspectionWorkerResult result) => result.Outcome switch
    {
        DiskImageInspectionWorkerOutcome.Unavailable => new(DiskImageInspectionState.Unavailable),
        DiskImageInspectionWorkerOutcome.TimedOut => new(DiskImageInspectionState.TimedOut),
        DiskImageInspectionWorkerOutcome.Completed when result.Observation is
            { State: DiskImageRawProfileVerificationState.ContentAndLayoutVerified } observation =>
            new(DiskImageInspectionState.Verified, observation.VerifiedBytes, observation.LogicalSectorBytes),
        DiskImageInspectionWorkerOutcome.Completed when result.Observation is not null => new(DiskImageInspectionState.Rejected),
        _ => new(DiskImageInspectionState.Failed)
    };
}
