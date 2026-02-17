namespace VaultSync.Core.Models;

public record Snapshot
{
    public int Id { get; init; }
    public string ExternalId { get; init; } = string.Empty;
    public int ProjectId { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public long FileCount { get; init; }
    public long TotalBytes { get; init; }
    public int DiffAdded { get; init; }
    public int DiffModified { get; init; }
    public int DiffDeleted { get; init; }
    public long DiffNetBytes { get; init; }
    public string DiffTopPathsJson { get; init; } = "[]";
}
