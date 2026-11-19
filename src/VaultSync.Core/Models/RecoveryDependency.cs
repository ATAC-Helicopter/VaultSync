namespace VaultSync.Core.Models;

// VS-1919 review-only in-memory graph; no wire format or restore authorization.
public enum RecoveryDependencyRelation { Payload, ParentImage, Key, Credential, Reader, BootDriver, Network, Service, Permission }

public sealed record RecoveryDependencyRequirement(
    string Name,
    RecoveryDependencyRelation Relation,
    IReadOnlyList<Guid> Alternatives);

public sealed record RecoveryDependencyNode(
    Guid SubjectId,
    string Generation,
    string Scope,
    RecoveryObservation? Observation,
    IReadOnlyList<RecoveryDependencyRequirement> Requirements);

public sealed record RecoveryDependencyRequirementAssessment(
    string Name,
    RecoveryDependencyRelation Relation,
    IReadOnlyList<Guid> Alternatives,
    bool HasCurrentMeasuredCoverage);

public sealed record RecoveryDependencyNodeAssessment(
    Guid SubjectId,
    RecoveryObservationAssessment Observation,
    IReadOnlyList<RecoveryDependencyRequirementAssessment> Requirements)
{
    // Coverage is confined to supplied observations, never overall recoverability.
    public bool HasCurrentMeasuredCoverage => Observation.HasCurrentPassedMeasurement &&
        Requirements.All(requirement => requirement.HasCurrentMeasuredCoverage);
}

public sealed record RecoveryDependencyGraphAssessment(
    bool IsValid,
    string Reason,
    Guid RootSubjectId,
    IReadOnlyList<RecoveryDependencyNodeAssessment> Nodes)
{
    public bool HasCurrentMeasuredCoverage => IsValid &&
        Nodes.Any(node => node.SubjectId == RootSubjectId && node.HasCurrentMeasuredCoverage);
}
