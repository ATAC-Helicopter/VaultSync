namespace VaultSync.Core.Models;

public enum DiskImageFileCaptureState { Invalid, Unsupported, Incomplete, Interrupted, Failed, FileFixtureCaptured }

public sealed record DiskImageFileCaptureRequest(
    Guid ImageId, Guid CaptureId, Guid PartId, ulong Generation, ulong Capacity, uint LogicalSectorBytes);

/// <summary>Temporary synthetic capture only; no source consistency, device or filesystem qualification.</summary>
public sealed record DiskImageFileCaptureAssessment(
    DiskImageFileCaptureState State, string Reason, ulong WrittenBytes = 0,
    string? Workspace = null, string? ManifestRoot = null);
