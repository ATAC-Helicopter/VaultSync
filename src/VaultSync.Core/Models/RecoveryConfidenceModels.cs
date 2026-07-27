namespace VaultSync.Core.Models;

public enum RecoveryConfidenceState
{
    NotMeasured,
    NoRecoveryPoint,
    DestinationUnavailable,
    CredentialUnavailable,
    VerificationFailed,
    VerificationPending,
    RestorePlanInvalid,
    DrillFailed,
    DrillNotRun,
    DrillOverdue,
    RecoverableWithWarnings,
    FullyVerified
}

public enum RecoveryEvidenceKind
{
    RecoveryPoint,
    Destination,
    Credential,
    IntegrityVerification,
    RestorePlan,
    RestoreDrill,
    OffsiteCopy
}

public enum RecoveryEvidenceBasis
{
    Measured,
    Simulated,
    Inferred,
    UserConfirmed,
    Unsupported
}

public enum RecoveryEvidenceStatus
{
    Passed,
    Warning,
    Failed,
    Missing,
    Stale,
    Unsupported
}

public enum RecoveryVerificationStatus
{
    NotRun,
    Passed,
    Limited,
    Failed,
    Unsupported
}

public sealed record RecoveryConfidenceEvidence(
    RecoveryEvidenceKind Kind,
    RecoveryEvidenceBasis Basis,
    RecoveryEvidenceStatus Status,
    string Code,
    DateTime? ObservedUtc = null);

public sealed record RecoveryConfidenceInput
{
    public int ProjectId { get; init; }
    public bool HasRecoveryPoint { get; init; }
    public bool IsDestinationReachable { get; init; }
    public bool IsEncrypted { get; init; }
    public bool? IsCredentialAvailable { get; init; }
    public RecoveryVerificationStatus VerificationStatus { get; init; }
    public DateTime? VerificationUtc { get; init; }
    public bool? IsRestorePlanValid { get; init; }
    public RecoveryDrillStatus? DrillStatus { get; init; }
    public DateTime? DrillUtc { get; init; }
    public bool HasOffsiteCopy { get; init; }
}

public sealed record ProjectRecoveryConfidence
{
    public int ProjectId { get; init; }
    public RecoveryConfidenceState State { get; init; }
    public string DecisiveEvidenceCode { get; init; } = string.Empty;
    public string RecommendedActionCode { get; init; } = string.Empty;
    public IReadOnlyList<RecoveryConfidenceEvidence> Evidence { get; init; } = [];

    public bool IsBlocked => State is
        RecoveryConfidenceState.NoRecoveryPoint or
        RecoveryConfidenceState.DestinationUnavailable or
        RecoveryConfidenceState.CredentialUnavailable or
        RecoveryConfidenceState.VerificationFailed or
        RecoveryConfidenceState.RestorePlanInvalid or
        RecoveryConfidenceState.DrillFailed;
}
