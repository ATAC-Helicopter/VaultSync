namespace VaultSync.Core.Models;

public record Snapshot
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public long FileCount { get; init; }
    public long TotalBytes { get; init; }
}