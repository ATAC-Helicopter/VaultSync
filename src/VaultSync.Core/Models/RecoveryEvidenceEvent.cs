namespace VaultSync.Core.Models;

public sealed record RecoveryEvidenceEvent
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int BackupId { get; init; }
    public int SnapshotId { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public string Kind { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string EvidenceId { get; init; } = string.Empty;
    public string SourceIdentity { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
}
