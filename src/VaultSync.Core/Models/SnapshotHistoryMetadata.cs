using System;

namespace VaultSync.Core.Models;

public sealed record SnapshotHistoryMetadata
{
    public int SnapshotId { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Note { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
    public bool IsProtected { get; init; }
    public bool IsKnownGood { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; init; } = DateTime.UtcNow;
}
