using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>
/// Read-only VS-1919 applicability prototype for one explicitly supplied observation.
/// Does not select between conflicting observations, evaluate dependencies or perform I/O.
/// </summary>
public static class RecoveryObservationService
{
    public static RecoveryObservationAssessment Assess(
        RecoveryObservation? observation,
        Guid subjectId,
        string generation,
        string scope,
        DateTimeOffset nowUtc,
        TimeSpan maximumAge)
    {
        if (subjectId == Guid.Empty)
            throw new ArgumentException("Subject identity is required.", nameof(subjectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(generation);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        if (maximumAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));

        if (observation is null)
            return new(null, RecoveryObservationFreshness.Missing, "observation.missing");
        if (observation.EvidenceId == Guid.Empty || observation.SubjectId == Guid.Empty ||
            string.IsNullOrWhiteSpace(observation.Generation) || string.IsNullOrWhiteSpace(observation.Scope) ||
            string.IsNullOrWhiteSpace(observation.ProducerBuild) ||
            !Enum.IsDefined(observation.Basis) || !Enum.IsDefined(observation.Outcome))
            return new(observation, RecoveryObservationFreshness.Invalid, "observation.invalid-provenance");
        if (observation.ObservedUtc > nowUtc || observation.ExpiresUtc < observation.ObservedUtc)
            return new(observation, RecoveryObservationFreshness.Invalid, "observation.invalid-time");
        if (observation.SubjectId != subjectId)
            return new(observation, RecoveryObservationFreshness.Stale, "observation.subject-changed");
        if (!string.Equals(observation.Generation, generation, StringComparison.Ordinal))
            return new(observation, RecoveryObservationFreshness.Stale, "observation.generation-changed");
        if (!string.Equals(observation.Scope, scope, StringComparison.Ordinal))
            return new(observation, RecoveryObservationFreshness.Stale, "observation.scope-changed");
        if (observation.ExpiresUtc <= nowUtc || nowUtc - observation.ObservedUtc > maximumAge)
            return new(observation, RecoveryObservationFreshness.Stale, "observation.expired");
        return new(observation, RecoveryObservationFreshness.Current, "observation.current");
    }
}
