namespace VaultSync.Core.Models;

public sealed record ProjectGroup
{
    public const int MaxNameLength = 80;

    public required string Id
    {
        get; init;
    }
    public required string Name
    {
        get; init;
    }
    public int SortOrder
    {
        get; init;
    }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    public static string NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string normalized = string.Join(
            " ",
            value.Trim().Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

        return normalized.Length <= MaxNameLength
            ? normalized
            : normalized[..MaxNameLength].TrimEnd();
    }
}
