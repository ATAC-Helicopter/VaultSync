namespace VaultSync.Core.Models;

public record Project
{
    public const string DestinationAllId = "__ALL__";
    public int Id { get; init; }
    public string ExternalId { get; init; } = string.Empty;
    public required string Name { get; init; }
    public required string RootPath { get; init; }
    public required string Preset { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public bool NeedsRestore { get; init; }
    public string? PreferredDestinationId { get; init; }
    public string EncryptionPolicy { get; init; } = ProjectEncryptionPolicy.Inherit;
    public string? EncryptionKeyRef { get; init; }
    public string RestoreMode { get; init; } = ProjectRestoreMode.Direct;
}
