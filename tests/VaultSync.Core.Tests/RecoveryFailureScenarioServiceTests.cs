#nullable enable

using System;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoveryFailureScenarioServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Failed = Guid.Parse("2c92e8d0-bcdb-41cc-8f4d-6f9c0d4df4a0");
    private static readonly Guid Other = Guid.Parse("5890dc7b-53e9-4662-a9ad-bb2f903055ab");
    private const RecoveryFailureDomainKind Device = RecoveryFailureDomainKind.PhysicalDevice;

    [Theory]
    [InlineData(RecoveryFailureDomainKind.PhysicalDevice)]
    [InlineData(RecoveryFailureDomainKind.ProviderAccount)]
    public void CopiesInOneDiskOrAccountAreRemovedTogether(RecoveryFailureDomainKind kind)
    {
        RecoveryDependencyNode first = Node();
        RecoveryDependencyNode second = Node();
        RecoveryDependencyNode root = Node(first.SubjectId, second.SubjectId);
        RecoveryFailureScenarioAssessment result = Assess([root, first, second], root,
            [Bind(root, Other, kind), Bind(first, Failed, kind), Bind(second, Failed, kind)], kind);
        Assert.Equal(RecoveryFailureScenarioOutcome.CoverageLost, result.Outcome);
        Assert.Equal(2, result.RemovedSubjects.Count);
        Assert.True(result.Baseline.HasCurrentMeasuredCoverage);
        Assert.Equal(RecoveryEvidenceBasis.Simulated, result.Basis);
        Assert.All(result.Baseline.Nodes, node => Assert.True(node.Observation.HasCurrentPassedMeasurement));
    }

    [Fact]
    public void ExplicitSeparateCopyCanRetainModeledCoverage()
    {
        RecoveryDependencyNode first = Node();
        RecoveryDependencyNode second = Node();
        RecoveryDependencyNode root = Node(first.SubjectId, second.SubjectId);
        RecoveryFailureScenarioAssessment result = Assess([root, first, second], root,
            [Bind(root, Other), Bind(first, Failed), Bind(second, Other)]);
        Assert.Equal(RecoveryFailureScenarioOutcome.CoverageRetained, result.Outcome);
        Assert.Equal(first.SubjectId, Assert.Single(result.RemovedSubjects));
        Assert.Empty(result.UnknownSubjects);
        Assert.Equal(RecoveryEvidenceBasis.Simulated, result.Basis);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("explicit-unknown")]
    [InlineData("other-dimension")]
    [InlineData("partially-known")]
    public void UnknownMembershipCannotEstablishIndependence(string membership)
    {
        RecoveryDependencyNode payload = Node();
        RecoveryDependencyNode root = Node(payload.SubjectId);
        RecoveryFailureDomainBinding[] bindings = membership switch
        {
            "absent" => [Bind(root, Other)],
            "other-dimension" => [Bind(root, Other), Bind(payload, Other, RecoveryFailureDomainKind.Site)],
            "partially-known" => [Bind(root, Other), Bind(payload, Other), Bind(payload, null)],
            _ => [Bind(root, Other), Bind(payload, null)]
        };
        RecoveryFailureScenarioAssessment result = Assess([root, payload], root, bindings);
        Assert.Equal(RecoveryFailureScenarioOutcome.UnknownCorrelation, result.Outcome);
        Assert.Equal(payload.SubjectId, Assert.Single(result.UnknownSubjects));
        Assert.Empty(result.RemovedSubjects);
    }

    [Fact]
    public void UnknownUnusedCopyRemainsVisibleWithoutBlockingAKnownAlternative()
    {
        RecoveryDependencyNode known = Node();
        RecoveryDependencyNode unknown = Node();
        RecoveryDependencyNode root = Node(unknown.SubjectId, known.SubjectId);
        RecoveryFailureScenarioAssessment result = Assess([root, known, unknown], root,
            [Bind(root, Other), Bind(known, Other)]);
        Assert.Equal(RecoveryFailureScenarioOutcome.CoverageRetained, result.Outcome);
        Assert.Equal(unknown.SubjectId, Assert.Single(result.UnknownSubjects));
    }

    [Fact]
    public void AnIndependentPayloadCannotReplaceAKeyLostWithTheOriginalDisk()
    {
        RecoveryDependencyNode original = Node();
        RecoveryDependencyNode mirror = Node();
        RecoveryDependencyNode key = Node();
        RecoveryDependencyNode root = Node(original.SubjectId, mirror.SubjectId);
        root = root with { Requirements = [root.Requirements[0],
            new("key", RecoveryDependencyRelation.Key, [key.SubjectId])] };
        RecoveryFailureScenarioAssessment result = Assess([root, original, mirror, key], root,
            [Bind(root, Other), Bind(original, Failed), Bind(mirror, Other), Bind(key, Failed)]);
        Assert.Equal(RecoveryFailureScenarioOutcome.CoverageLost, result.Outcome);
        Assert.Contains(key.SubjectId, result.RemovedSubjects);
    }

    [Fact]
    public void KnownLossTakesPrecedenceOverUnknownSurvivingMembership()
    {
        RecoveryDependencyNode payload = Node();
        RecoveryDependencyNode root = Node(payload.SubjectId);
        RecoveryFailureScenarioAssessment result = Assess([root, payload], root, [Bind(root, Failed)]);
        Assert.Equal(RecoveryFailureScenarioOutcome.CoverageLost, result.Outcome);
        Assert.Contains(payload.SubjectId, result.UnknownSubjects);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("simulation")]
    [InlineData("failed")]
    [InlineData("unsupported")]
    public void IncompleteBaselineIsNotPresentedAsLossCausedByTheScenario(string limitation)
    {
        RecoveryDependencyNode root = Node();
        RecoveryObservation observation = root.Observation!;
        observation = limitation switch
        {
            "stale" => observation with { ObservedUtc = Now.AddDays(-8) },
            "simulation" => observation with { Basis = RecoveryEvidenceBasis.Simulated },
            "unsupported" => observation with { IsSupported = false },
            _ => observation with { Outcome = RecoveryObservationOutcome.Failed }
        };
        root = root with { Observation = observation };
        RecoveryFailureScenarioAssessment result = Assess([root], root, [Bind(root, Other)]);
        Assert.Equal(RecoveryFailureScenarioOutcome.InsufficientBaselineEvidence, result.Outcome);
        Assert.Same(observation, Assert.Single(result.Baseline.Nodes).Observation.Observation);
    }

    [Theory]
    [InlineData("unknown-subject")]
    [InlineData("empty-domain")]
    [InlineData("unsupported-kind")]
    [InlineData("blank-generation")]
    [InlineData("duplicate")]
    [InlineData("null")]
    public void InvalidBindingsNeverProduceAScenarioConclusion(string defect)
    {
        RecoveryDependencyNode root = Node();
        RecoveryFailureDomainBinding binding = Bind(root, Other);
        RecoveryFailureDomainBinding[] bindings = defect switch
        {
            "unknown-subject" => [binding with { SubjectId = Guid.NewGuid() }],
            "empty-domain" => [binding with { DomainId = Guid.Empty }],
            "unsupported-kind" => [binding with { Kind = (RecoveryFailureDomainKind)99 }],
            "blank-generation" => [binding with { Generation = " " }],
            "duplicate" => [binding, binding],
            _ => [null!]
        };
        RecoveryFailureScenarioAssessment result = Assess([root], root, bindings);
        Assert.Equal(RecoveryFailureScenarioOutcome.InvalidInput, result.Outcome);
        Assert.Equal("scenario.invalid-binding", result.Reason);
    }

    [Fact]
    public void MembershipFromAnotherGenerationCannotEstablishIndependence()
    {
        RecoveryDependencyNode root = Node();
        RecoveryFailureScenarioAssessment result = Assess([root], root,
            [Bind(root, Other) with { Generation = "previous-generation" }]);
        Assert.Equal(RecoveryFailureScenarioOutcome.InvalidInput, result.Outcome);
        Assert.Equal("scenario.binding-generation-changed", result.Reason);
        Assert.True(result.Baseline.HasCurrentMeasuredCoverage);
    }

    [Fact]
    public void GraphCyclesAndUnsupportedScenariosRemainExplicitRefusals()
    {
        RecoveryDependencyNode root = Node();
        root = root with { Requirements = [new("parent", RecoveryDependencyRelation.ParentImage, [root.SubjectId])] };
        Assert.Equal("dependencies.cycle", Assess([root], root, []).Reason);
        root = root with { Requirements = [] };
        Assert.Equal("scenario.invalid-domain", Assess([root], root, [], (RecoveryFailureDomainKind)99).Reason);
        Assert.Equal("scenario.invalid-domain", RecoveryFailureScenarioService.Assess([root], root.SubjectId,
            [], new(Device, Guid.Empty), Now, TimeSpan.FromDays(7)).Reason);
    }

    [Fact]
    public void BindingBudgetIsInclusiveAndRejectsExcessBeforeEvaluation()
    {
        RecoveryDependencyNode root = Node();
        RecoveryFailureDomainBinding[] bindings = Enumerable.Range(0, 1024)
            .Select(_ => Bind(root, Guid.NewGuid())).ToArray();
        Assert.Equal(RecoveryFailureScenarioOutcome.CoverageRetained, Assess([root], root, bindings).Outcome);
        Assert.Equal("scenario.binding-limit", Assess([root], root, bindings.Append(Bind(root, Other)).ToArray()).Reason);
    }

    [Fact]
    public void DisconnectedUnknownSubjectsDoNotAffectTheRequestedRoot()
    {
        RecoveryDependencyNode root = Node();
        RecoveryDependencyNode unrelated = Node();
        RecoveryFailureScenarioAssessment result = Assess([root, unrelated], root, [Bind(root, Other)]);
        Assert.Equal(RecoveryFailureScenarioOutcome.CoverageRetained, result.Outcome);
        Assert.Empty(result.UnknownSubjects);
    }

    [Fact]
    public void ResultsKeepHistoricalEvidenceAndOwnTheirRemovedSubjectLists()
    {
        RecoveryDependencyNode root = Node();
        RecoveryFailureDomainBinding[] bindings = [Bind(root, Failed)];
        RecoveryObservation original = root.Observation!;
        RecoveryFailureScenarioAssessment result = Assess([root], root, bindings);
        bindings[0] = Bind(root, Other);
        Assert.Equal(root.SubjectId, Assert.Single(result.RemovedSubjects));
        Assert.Same(original, Assert.Single(result.Baseline.Nodes).Observation.Observation);
        Assert.Same(original, root.Observation);
        Assert.Equal(RecoveryObservationOutcome.Passed, original.Outcome);
    }

    private static RecoveryFailureScenarioAssessment Assess(RecoveryDependencyNode[] nodes, RecoveryDependencyNode root,
        RecoveryFailureDomainBinding[] bindings, RecoveryFailureDomainKind kind = Device) =>
        RecoveryFailureScenarioService.Assess(nodes, root.SubjectId, bindings, new(kind, Failed), Now, TimeSpan.FromDays(7));

    private static RecoveryFailureDomainBinding Bind(RecoveryDependencyNode node, Guid? domain,
        RecoveryFailureDomainKind kind = Device) => new(node.SubjectId, node.Generation, kind, domain);

    private static RecoveryDependencyNode Node(params Guid[] alternatives)
    {
        Guid id = Guid.NewGuid();
        RecoveryDependencyRequirement[] requirements = alternatives.Length == 0 ? [] :
            [new("payload", RecoveryDependencyRelation.Payload, alternatives)];
        return new(id, "generation-1", "fixture-coverage", new(Guid.NewGuid(), id, "generation-1", "fixture-coverage",
            "synthetic-fixture", RecoveryEvidenceBasis.Measured, RecoveryObservationOutcome.Passed, Now.AddDays(-1), null, true), requirements);
    }
}
