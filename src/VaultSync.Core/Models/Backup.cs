namespace VaultSync.Core.Models;

public record Backup
{
    public int Id           { get; init; }
    public int ProjectId    { get; init; }
    public int SnapshotId   { get; init; }
    public DateTime CreatedUtc { get; init; }

    /// <summary>
    /// "auto" or "manual" (could be extended later).
    /// </summary>
    public string Type      { get; init; } = string.Empty;

    /// <summary>
    /// Size of the backup archive on disk (folder or zip), in bytes.
    /// </summary>
    public long TotalBytes  { get; init; }

    /// <summary>
    /// Path to the backup relative to the backup root (from AppConfig.Backups).
    /// </summary>
    public string Path      { get; init; } = string.Empty;

    /// <summary>
    /// When true, this backup is protected from automatic retention pruning.
    /// </summary>
    public bool IsProtected { get; init; }
}
