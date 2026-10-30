using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>
/// Read-only VS-1919 review prototype. Requirements are conjunctive; explicitly
/// listed alternatives are disjunctive. No observation discovery, trust validation,
/// failure-domain simulation, storage, secret acquisition or device access.
/// </summary>
public static class RecoveryDependencyService
{
    public const int MaximumNodes = 256;
    public const int MaximumRequirements = 1024;
    public const int MaximumAlternatives = 1024;
    public const int MaximumDepth = 64;

    public static RecoveryDependencyGraphAssessment Assess(
        IReadOnlyList<RecoveryDependencyNode> nodes,
        Guid rootSubjectId,
        DateTimeOffset nowUtc,
        TimeSpan maximumAge)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (rootSubjectId == Guid.Empty)
            throw new ArgumentException("Root identity is required.", nameof(rootSubjectId));
        if (maximumAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));

        RecoveryDependencyGraphAssessment Invalid(string reason) => new(false, reason, rootSubjectId, []);
        if (nodes.Count > MaximumNodes)
            return Invalid("dependencies.node-limit");

        var graph = new Dictionary<Guid, RecoveryDependencyNode>();
        int requirementCount = 0;
        int alternativeCount = 0;
        foreach (RecoveryDependencyNode node in nodes)
        {
            if (node is null || node.SubjectId == Guid.Empty ||
                string.IsNullOrWhiteSpace(node.Generation) || string.IsNullOrWhiteSpace(node.Scope) ||
                node.Requirements is null || graph.ContainsKey(node.SubjectId))
                return Invalid("dependencies.invalid-node");
            if (node.Requirements.Count > MaximumRequirements - requirementCount)
                return Invalid("dependencies.requirement-limit");
            requirementCount += node.Requirements.Count;
            var requirements = new List<RecoveryDependencyRequirement>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (RecoveryDependencyRequirement requirement in node.Requirements)
            {
                if (requirement is null || string.IsNullOrWhiteSpace(requirement.Name) ||
                    !names.Add(requirement.Name) || !Enum.IsDefined(requirement.Relation) ||
                    requirement.Alternatives is null)
                    return Invalid("dependencies.invalid-requirement");
                if (requirement.Alternatives.Count > MaximumAlternatives - alternativeCount)
                    return Invalid("dependencies.alternative-limit");
                alternativeCount += requirement.Alternatives.Count;
                Guid[] alternatives = requirement.Alternatives.ToArray();
                if (alternatives.Any(id => id == Guid.Empty) || alternatives.Distinct().Count() != alternatives.Length)
                    return Invalid("dependencies.invalid-alternative");
                requirements.Add(requirement with { Alternatives = Array.AsReadOnly(alternatives) });
            }
            graph.Add(node.SubjectId, node with { Requirements = requirements.AsReadOnly() });
        }

        // Validate the entire supplied graph, including alternatives which would
        // not be selected. Memoized subtree height also bounds shared DAG paths.
        var heights = new Dictionary<Guid, int>();
        var visiting = new HashSet<Guid>();
        string? graphError = null;
        int Height(Guid id, int depth)
        {
            if (!graph.TryGetValue(id, out RecoveryDependencyNode? node))
                return 0; // Missing dependencies remain named unsatisfied requirements.
            if (depth > MaximumDepth)
            {
                graphError = "dependencies.depth-limit";
                return 0;
            }
            if (heights.TryGetValue(id, out int knownHeight))
                return knownHeight;
            if (!visiting.Add(id))
            {
                graphError = "dependencies.cycle";
                return 0;
            }
            int height = 1;
            foreach (Guid alternative in node.Requirements.SelectMany(requirement => requirement.Alternatives))
            {
                height = Math.Max(height, 1 + Height(alternative, depth + 1));
                if (graphError is not null)
                    return 0;
            }
            visiting.Remove(id);
            heights.Add(id, height);
            if (height > MaximumDepth)
                graphError = "dependencies.depth-limit";
            return height;
        }
        foreach (Guid id in graph.Keys)
        {
            Height(id, 1);
            if (graphError is not null)
                return Invalid(graphError);
        }

        var assessments = new Dictionary<Guid, RecoveryDependencyNodeAssessment>();
        bool Evaluate(Guid id)
        {
            if (!graph.TryGetValue(id, out RecoveryDependencyNode? node))
                return false;
            if (assessments.TryGetValue(id, out RecoveryDependencyNodeAssessment? existing))
                return existing.HasCurrentMeasuredCoverage;
            var requirements = new List<RecoveryDependencyRequirementAssessment>();
            foreach (RecoveryDependencyRequirement requirement in node.Requirements)
            {
                bool covered = false;
                foreach (Guid alternative in requirement.Alternatives)
                    covered |= Evaluate(alternative); // Retain failed alternatives' evidence too.
                requirements.Add(new(requirement.Name, requirement.Relation, requirement.Alternatives, covered));
            }
            var assessment = new RecoveryDependencyNodeAssessment(id,
                RecoveryObservationService.Assess(node.Observation, id, node.Generation, node.Scope, nowUtc, maximumAge),
                requirements.AsReadOnly());
            assessments.Add(id, assessment);
            return assessment.HasCurrentMeasuredCoverage;
        }

        Evaluate(rootSubjectId);
        return new(true, graph.ContainsKey(rootSubjectId) ? "dependencies.assessed" : "dependencies.root-missing",
            rootSubjectId, Array.AsReadOnly(assessments.Values.OrderBy(node => node.SubjectId).ToArray()));
    }
}
