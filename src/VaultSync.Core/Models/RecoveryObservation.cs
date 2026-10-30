namespace VaultSync.Core.Models;

// VS-1919 review-only in-memory types, not a serialized format or restore authorization.
public enum RecoveryObservationOutcome { Passed, Failed, Interrupted }
public enum RecoveryObservationFreshness { Missing, Current, Stale, Invalid }

public sealed record RecoveryObservation(
    Guid EvidenceId,
    Guid SubjectId,
    string Generation,
    string Scope,
    string ProducerBuild,
    RecoveryEvidenceBasis Basis,
    RecoveryObservationOutcome Outcome,
    DateTimeOffset ObservedUtc,
    DateTimeOffset? ExpiresUtc,
    bool IsSupported);

public sealed record RecoveryObservationAssessment(
    RecoveryObservation? Observation,
    RecoveryObservationFreshness Freshness,
    string Reason)
{
    // Only describes this named observation; does not establish overall recoverability.
    public bool HasCurrentPassedMeasurement =>
        Freshness == RecoveryObservationFreshness.Current &&
        Observation is { IsSupported: true, Basis: RecoveryEvidenceBasis.Measured, Outcome: RecoveryObservationOutcome.Passed };
}
