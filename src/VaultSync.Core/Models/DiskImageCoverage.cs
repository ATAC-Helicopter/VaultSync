namespace VaultSync.Core.Models;

// VS-1917 logical review fixtures, not a disk-image wire format.
public enum DiskImageExtentKind { Stored, Zero }
public enum DiskImageCoverageState { Invalid, Unsupported, Incomplete, CompleteUnverified }
public sealed record DiskImagePayloadPart(Guid PartId, ulong StoredLength);
public sealed record DiskImageExtent(ulong Offset, ulong Length, DiskImageExtentKind Kind,
    Guid? PartId = null, ulong PartOffset = 0);
public sealed record DiskImageCoveragePlan(ulong Capacity, uint LogicalSectorBytes,
    IReadOnlyList<DiskImagePayloadPart> Parts, IReadOnlyList<DiskImageExtent> Extents, bool HasCompletion);
public sealed record DiskImageCoverageAssessment(DiskImageCoverageState State, string Reason, ulong CoveredBytes);
