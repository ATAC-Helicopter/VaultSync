namespace VaultSync.Core.Models;

public record Backup
{
    public int Id           { get; init; }
    public string ExternalId { get; init; } = string.Empty;
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

    /// <summary>
    /// When true, this backup was imported from another machine.
    /// </summary>
    public bool IsImported { get; init; }

    /// <summary>
    /// When true, backup payload was written as encrypted artifact.
    /// </summary>
    public bool IsEncrypted { get; init; }

    /// <summary>
    /// Non-secret crypto descriptor JSON for metadata sync/export.
    /// </summary>
    public string CryptoDescriptorJson { get; init; } = BackupCryptoDescriptor.PlainMetadataJson;

    /// <summary>
    /// Absolute path to the destination root that stored this backup.
    /// </summary>
    public string DestinationPath { get; init; } = string.Empty;

    /// <summary>
    /// User-friendly label for the destination used when the backup was created.
    /// </summary>
    public string DestinationAlias { get; init; } = string.Empty;

    /// <summary>
    /// Machine name that created this backup (from metadata sync).
    /// </summary>
    public string OriginMachineName { get; init; } = string.Empty;
}
