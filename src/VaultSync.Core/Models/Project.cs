namespace VaultSync.Core.Models;

public record Project
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public required string RootPath { get; init; }
    public required string Preset { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}