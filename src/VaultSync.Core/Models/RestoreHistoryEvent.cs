using System;

namespace VaultSync.Core.Models;

public sealed record RestoreHistoryEvent
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int BackupId { get; init; }
    public int SnapshotId { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public string RestoreMode { get; init; } = ProjectRestoreMode.Direct;
    public string TargetPath { get; init; } = string.Empty;
    public string Status { get; init; } = RestoreHistoryEventStatus.Completed;
    public string Note { get; init; } = string.Empty;
}

public static class RestoreHistoryEventStatus
{
    public const string Completed = "completed";
    public const string Failed = "failed";

    public static string Normalize(string? status) =>
        string.Equals(status, Failed, StringComparison.OrdinalIgnoreCase)
            ? Failed
            : Completed;
}
