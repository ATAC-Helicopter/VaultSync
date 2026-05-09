using System.Collections.Generic;

namespace VaultSync.Core.Models;

public enum RestoreReadinessState
{
    Ready,
    Attention,
    Risk,
    Unavailable
}

public sealed class ProjectRestoreReadiness
{
    public int ProjectId { get; init; }
    public string ProjectName { get; init; } = string.Empty;
    public RestoreReadinessState State { get; init; } = RestoreReadinessState.Unavailable;
    public int Score { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

public sealed class RestoreReadinessSummary
{
    public int ReadyCount { get; init; }
    public int AttentionCount { get; init; }
    public int RiskCount { get; init; }
    public int UnavailableCount { get; init; }
    public int ProjectCount { get; init; }
    public string Headline { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public IReadOnlyList<ProjectRestoreReadiness> Projects { get; init; } = [];
}
