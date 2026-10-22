namespace VaultSync.Core.Models;

public enum DiskImageGptInspectionState { Invalid, Unsupported, Incomplete, LayoutInspected }
public enum DiskImageGptInspectionReason
{
    InvalidGeometry, SeekableSourceRequired, SourceLengthMismatch, TruncatedMetadata,
    ProtectiveMbrRequired, HybridMbrUnsupported, InvalidProtectiveMbr,
    UnsupportedHeader, InvalidHeader, HeaderChecksumMismatch, MetadataOverlap,
    HeaderCopiesDisagree, UnsupportedEntryFormat, EntryChecksumMismatch,
    EntryCopiesDisagree, InvalidPartition, OverlappingPartitions, LayoutOnly
}

public sealed record DiskImageGptPartition(int EntryNumber, Guid TypeId, Guid PartitionId,
    ulong Offset, ulong Length, ulong Attributes);

/// <summary>Read-only layout observations; neither physical identity nor restore authorization.</summary>
public sealed record DiskImageGptLayout(Guid DiskId, ulong Capacity, uint LogicalSectorBytes,
    ulong FirstUsableLba, ulong LastUsableLba, IReadOnlyList<DiskImageGptPartition> Partitions,
    string MetadataSha256);

public sealed record DiskImageGptInspectionAssessment(DiskImageGptInspectionState State,
    DiskImageGptInspectionReason Reason, DiskImageGptLayout? Layout = null);
