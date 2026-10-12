namespace VaultSync.Core.Models;

// VS-1919 review-only model assumptions, not measured domain membership or a wire format.
public enum RecoveryFailureDomainKind
{
    PhysicalDevice, Controller, Host, Power, Network, Site, ProviderAccount,
    RepositoryAdministration, CredentialCustody, KeyCustody
}

public sealed record RecoveryFailureDomain(RecoveryFailureDomainKind Kind, Guid DomainId);

public sealed record RecoveryFailureDomainBinding(
    Guid SubjectId,
    string Generation,
    RecoveryFailureDomainKind Kind,
    Guid? DomainId);

public enum RecoveryFailureScenarioOutcome
{
    InvalidInput, InsufficientBaselineEvidence, CoverageLost, UnknownCorrelation, CoverageRetained
}

public sealed record RecoveryFailureScenarioAssessment(
    RecoveryFailureScenarioOutcome Outcome,
    string Reason,
    RecoveryFailureDomain FailedDomain,
    RecoveryDependencyGraphAssessment Baseline,
    IReadOnlyList<Guid> RemovedSubjects,
    IReadOnlyList<Guid> UnknownSubjects)
{
    // All scenario conclusions are simulations, even when baseline observations were measured.
    public RecoveryEvidenceBasis Basis => RecoveryEvidenceBasis.Simulated;
}
