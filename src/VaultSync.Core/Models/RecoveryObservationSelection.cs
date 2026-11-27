namespace VaultSync.Core.Models;

public enum RecoveryObservationSelectionState { Invalid, NoCurrentEvidence, Selected, Ambiguous }
public sealed record RecoveryObservationSelectionAssessment(
    RecoveryObservationSelectionState State,
    string Reason,
    IReadOnlyList<RecoveryObservationAssessment> Candidates,
    RecoveryObservationAssessment? Selected)
{
    public bool HasCurrentPassedMeasurement => State == RecoveryObservationSelectionState.Selected &&
        Selected is { HasCurrentPassedMeasurement: true };
}
