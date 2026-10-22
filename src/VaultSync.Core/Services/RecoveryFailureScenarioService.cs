using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>
/// Simulates one explicitly identified failure domain over supplied observations.
/// Never infers independence from paths, labels or providers and performs no I/O.
/// </summary>
public static class RecoveryFailureScenarioService
{
    public const int MaximumBindings = 1024;
    public const int MaximumFailedDomains = 32;

    public static RecoveryFailureScenarioAssessment Assess(
        IReadOnlyList<RecoveryDependencyNode> nodes,
        Guid rootSubjectId,
        IReadOnlyList<RecoveryFailureDomainBinding> bindings,
        RecoveryFailureDomain failedDomain,
        DateTimeOffset nowUtc,
        TimeSpan maximumAge)
    {
        ArgumentNullException.ThrowIfNull(failedDomain);
        RecoveryMultiFailureScenarioAssessment result = AssessMultiple(nodes, rootSubjectId, bindings,
            [failedDomain], nowUtc, maximumAge);
        return new(result.Outcome, result.Reason, failedDomain, result.Baseline,
            result.RemovedSubjects, result.UnknownSubjects);
    }

    public static RecoveryMultiFailureScenarioAssessment AssessMultiple(
        IReadOnlyList<RecoveryDependencyNode> nodes,
        Guid rootSubjectId,
        IReadOnlyList<RecoveryFailureDomainBinding> bindings,
        IReadOnlyList<RecoveryFailureDomain> failedDomains,
        DateTimeOffset nowUtc,
        TimeSpan maximumAge)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(failedDomains);
        RecoveryDependencyGraphAssessment baseline = RecoveryDependencyService.Assess(nodes, rootSubjectId, nowUtc, maximumAge);
        if (failedDomains.Count > MaximumFailedDomains)
            return new(RecoveryFailureScenarioOutcome.InvalidInput, "scenario.domain-limit", [], baseline, [], []);
        IReadOnlyList<RecoveryFailureDomain> domainSnapshot = Array.AsReadOnly(failedDomains.ToArray());
        RecoveryMultiFailureScenarioAssessment Invalid(string reason) => new(
            RecoveryFailureScenarioOutcome.InvalidInput, reason, domainSnapshot, baseline, [], []);
        if (!baseline.IsValid)
            return Invalid(baseline.Reason);
        if (domainSnapshot.Count == 0 || domainSnapshot.Any(domain => domain is null ||
            !Enum.IsDefined(domain.Kind) || domain.DomainId == Guid.Empty) ||
            domainSnapshot.Distinct().Count() != domainSnapshot.Count)
            return Invalid("scenario.invalid-domain");
        if (bindings.Count > MaximumBindings)
            return Invalid("scenario.binding-limit");

        var subjects = nodes.ToDictionary(node => node.SubjectId);
        var distinct = new HashSet<RecoveryFailureDomainBinding>();
        var selectedDomains = domainSnapshot.ToHashSet();
        var selectedKinds = domainSnapshot.Select(domain => domain.Kind).ToHashSet();
        var domainSubjects = new HashSet<(Guid Subject, RecoveryFailureDomainKind Kind)>();
        var removed = new HashSet<Guid>();
        var unknown = new HashSet<Guid>();
        foreach (RecoveryFailureDomainBinding binding in bindings)
        {
            if (binding is null || !subjects.TryGetValue(binding.SubjectId, out RecoveryDependencyNode? subject) ||
                string.IsNullOrWhiteSpace(binding.Generation) ||
                !Enum.IsDefined(binding.Kind) || binding.DomainId == Guid.Empty || !distinct.Add(binding))
                return Invalid("scenario.invalid-binding");
            if (!string.Equals(binding.Generation, subject.Generation, StringComparison.Ordinal))
                return Invalid("scenario.binding-generation-changed");
            if (!selectedKinds.Contains(binding.Kind))
                continue;
            domainSubjects.Add((binding.SubjectId, binding.Kind));
            if (binding.DomainId is null)
                unknown.Add(binding.SubjectId);
            else if (selectedDomains.Contains(new(binding.Kind, binding.DomainId.Value)))
                removed.Add(binding.SubjectId);
        }

        // Only reachable observations participate in the scenario, but all supplied
        // bindings and the whole dependency graph have already been validated.
        var observations = baseline.Nodes.ToDictionary(node => node.SubjectId);
        removed.IntersectWith(observations.Keys);
        unknown.UnionWith(observations.Keys.Where(id => selectedKinds.Any(kind => !domainSubjects.Contains((id, kind)))));
        unknown.IntersectWith(observations.Keys);
        unknown.ExceptWith(removed);

        RecoveryMultiFailureScenarioAssessment Result(RecoveryFailureScenarioOutcome outcome, string reason) => new(
            outcome, reason, domainSnapshot, baseline,
            Array.AsReadOnly(removed.Order().ToArray()), Array.AsReadOnly(unknown.Order().ToArray()));

        if (!baseline.HasCurrentMeasuredCoverage)
            return Result(RecoveryFailureScenarioOutcome.InsufficientBaselineEvidence, "scenario.baseline-incomplete");

        bool Coverage(HashSet<Guid> excluded)
        {
            var memo = new Dictionary<Guid, bool>();
            bool Visit(Guid id)
            {
                if (excluded.Contains(id) || !observations.TryGetValue(id, out RecoveryDependencyNodeAssessment? node))
                    return false;
                if (memo.TryGetValue(id, out bool known))
                    return known;
                bool covered = node.Observation.HasCurrentPassedMeasurement && node.Requirements.All(
                    requirement => requirement.Alternatives.Any(Visit));
                memo.Add(id, covered);
                return covered;
            }
            return Visit(rootSubjectId);
        }

        if (!Coverage(removed))
            return Result(RecoveryFailureScenarioOutcome.CoverageLost, "scenario.coverage-lost");
        var pessimistic = new HashSet<Guid>(removed);
        pessimistic.UnionWith(unknown);
        if (!Coverage(pessimistic))
            return Result(RecoveryFailureScenarioOutcome.UnknownCorrelation, "scenario.unknown-correlation");
        return Result(RecoveryFailureScenarioOutcome.CoverageRetained, "scenario.coverage-retained");
    }
}
