namespace VaultSync.Core.Models;

public enum RecoveryDrillStatus
{
    Passed,
    Attention,
    Failed
}

public enum RecoveryDrillCheckStatus
{
    Passed,
    Attention,
    Failed
}

public sealed record RecoveryDrillCheck(
    string Code,
    RecoveryDrillCheckStatus Status,
    string Detail,
    string? EvidenceId = null,
    string? Path = null);

public sealed record RecoveryDrillResult
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int BackupId { get; init; }
    public int SnapshotId { get; init; }
    public DateTime RunUtc { get; init; } = DateTime.UtcNow;
    public RecoveryDrillStatus Status { get; init; }
    public int ChecksPassed { get; init; }
    public int ChecksTotal { get; init; }
    public int FilesExamined { get; init; }
    public bool IsLimited { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string ChecksJson { get; init; } = "[]";
}

public enum ProtectionRecommendationKind
{
    ReleaseMarker,
    LargeDeletion,
    SignificantChange,
    Baseline
}

public sealed record ProtectionRecommendation(
    int ProjectId,
    int SnapshotId,
    int BackupId,
    ProtectionRecommendationKind Kind,
    string Reason);

public sealed record ProjectProtectionAssessment
{
    public int ProjectId { get; init; }
    public int CopyCount { get; init; }
    public int MediaCount { get; init; }
    public bool HasOffsiteCopy { get; init; }
    public bool MeetsThreeTwoOne { get; init; }
    public int ProtectedPointCount { get; init; }
    public RecoveryDrillResult? LastDrill { get; init; }
    public ProtectionRecommendation? Recommendation { get; init; }
}

public sealed record DisasterRecoverySummary
{
    public int ProjectCount { get; init; }
    public int ThreeTwoOneReadyCount { get; init; }
    public int DrilledProjectCount { get; init; }
    public int PassedDrillCount { get; init; }
    public int ProtectedPointCount { get; init; }
    public IReadOnlyList<ProjectProtectionAssessment> Projects { get; init; } = [];
}
