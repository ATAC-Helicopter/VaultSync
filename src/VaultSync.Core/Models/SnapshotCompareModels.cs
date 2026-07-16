using System.Collections.Generic;

namespace VaultSync.Core.Models;

public enum SnapshotFileChangeKind
{
    Added,
    Modified,
    Deleted
}

public sealed record SnapshotFileChange(
    string Path,
    SnapshotFileChangeKind Kind,
    long PreviousSizeBytes,
    long CurrentSizeBytes)
{
    public long SizeDeltaBytes => CurrentSizeBytes - PreviousSizeBytes;
}

public enum SnapshotChangeSignalKind
{
    MassDeletion,
    SignificantGrowth,
    HighChurn
}

public sealed record SnapshotChangeSignal(
    SnapshotChangeSignalKind Kind,
    int AffectedFiles,
    double Ratio,
    long SizeDeltaBytes);

public sealed record SnapshotCompareResult(
    int Added,
    int Modified,
    int Deleted,
    int Unchanged,
    long PreviousTotalBytes,
    long CurrentTotalBytes,
    long ChangedBytes,
    IReadOnlyList<SnapshotFileChange> Changes,
    IReadOnlyList<SnapshotDiffPathStat> TopChangedPaths,
    IReadOnlyList<SnapshotChangeSignal> Signals)
{
    public int ChangedCount => Added + Modified + Deleted;
    public long NetSizeBytes => CurrentTotalBytes - PreviousTotalBytes;
}

public sealed record SnapshotCompareOptions(
    int MassDeletionMinimumFiles = 10,
    double MassDeletionRatio = 0.25,
    long SignificantGrowthMinimumBytes = 100L * 1024L * 1024L,
    double SignificantGrowthRatio = 0.25,
    int HighChurnMinimumFiles = 100,
    double HighChurnRatio = 0.50)
{
    public static SnapshotCompareOptions Default { get; } = new();
}
