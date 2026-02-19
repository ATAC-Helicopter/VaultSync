using System.Text.Json;
using System.Text.Json.Serialization;

namespace VaultSync.Core.Models;

public sealed record SnapshotDiffSummary(
    int Added,
    int Modified,
    int Deleted,
    long NetSizeBytes,
    IReadOnlyList<SnapshotDiffPathStat> TopChangedPaths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string TopChangedPathsJson => JsonSerializer.Serialize(TopChangedPaths ?? Array.Empty<SnapshotDiffPathStat>(), JsonOptions);

    public static IReadOnlyList<SnapshotDiffPathStat> ParseTopChangedPaths(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<SnapshotDiffPathStat>();

        try
        {
            var parsed = JsonSerializer.Deserialize<List<SnapshotDiffPathStat>>(json, JsonOptions);
            if (parsed is null || parsed.Count == 0)
                return Array.Empty<SnapshotDiffPathStat>();

            return parsed
                .Where(stat => !string.IsNullOrWhiteSpace(stat.Path))
                .Select(stat => new SnapshotDiffPathStat(stat.Path.Trim(), Math.Max(0, stat.Changes), Math.Max(0L, stat.ChangedBytes)))
                .ToArray();
        }
        catch
        {
            return Array.Empty<SnapshotDiffPathStat>();
        }
    }

    public static SnapshotDiffSummary Empty { get; } =
        new(Added: 0, Modified: 0, Deleted: 0, NetSizeBytes: 0, TopChangedPaths: Array.Empty<SnapshotDiffPathStat>());
}

public sealed record SnapshotDiffPathStat(
    string Path,
    int Changes,
    long ChangedBytes);
