using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Conservative review prototype: multiple current records require explicit review, never implicit precedence.</summary>
public static class RecoveryObservationSelectionService
{
    public const int MaximumCandidates = 256;

    public static RecoveryObservationSelectionAssessment Assess(
        IReadOnlyList<RecoveryObservation> observations, Guid subjectId, string generation,
        string scope, DateTimeOffset nowUtc, TimeSpan maximumAge)
    {
        ArgumentNullException.ThrowIfNull(observations);
        // Validate caller policy even for empty or excessive input.
        RecoveryObservationService.Assess(null, subjectId, generation, scope, nowUtc, maximumAge);
        if (observations.Count > MaximumCandidates)
            return new(RecoveryObservationSelectionState.Invalid, "selection.candidate-limit", [], null);
        var candidates = new List<RecoveryObservationAssessment>();
        var identities = new HashSet<Guid>();
        bool invalid = false;
        foreach (RecoveryObservation observation in observations)
        {
            RecoveryObservationAssessment assessment = RecoveryObservationService.Assess(
                observation, subjectId, generation, scope, nowUtc, maximumAge);
            candidates.Add(assessment);
            if (observation is null || !identities.Add(observation.EvidenceId) ||
                assessment.Freshness == RecoveryObservationFreshness.Invalid)
                invalid = true;
        }
        IReadOnlyList<RecoveryObservationAssessment> snapshot = candidates.AsReadOnly();
        if (invalid)
            return new(RecoveryObservationSelectionState.Invalid, "selection.invalid-candidate", snapshot, null);
        RecoveryObservationAssessment[] current = candidates.Where(
            candidate => candidate.Freshness == RecoveryObservationFreshness.Current).ToArray();
        return current.Length switch
        {
            0 => new(RecoveryObservationSelectionState.NoCurrentEvidence, "selection.no-current-evidence", snapshot, null),
            1 => new(RecoveryObservationSelectionState.Selected, "selection.selected", snapshot, current[0]),
            _ => new(RecoveryObservationSelectionState.Ambiguous, "selection.ambiguous", snapshot, null)
        };
    }
}
