#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoveryDependencyServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PayloadAlternativesCannotReplaceARequiredKey()
    {
        RecoveryDependencyNode payload = Node();
        RecoveryDependencyNode key = Node() with { Observation = null };
        RecoveryDependencyNode root = Node(Need("payload", payload.SubjectId), Need("key", key.SubjectId));
        RecoveryDependencyGraphAssessment result = Assess(root, payload, key);
        Assert.True(result.IsValid);
        Assert.False(result.HasCurrentMeasuredCoverage);
        RecoveryDependencyNodeAssessment assessedRoot = result.Nodes.Single(node => node.SubjectId == root.SubjectId);
        Assert.True(assessedRoot.Requirements[0].HasCurrentMeasuredCoverage);
        Assert.False(assessedRoot.Requirements[1].HasCurrentMeasuredCoverage);
        Assert.Equal("key", assessedRoot.Requirements[1].Name);
    }

    [Fact]
    public void ExplicitAlternativeCanSupplyCoverageWhileKeepingFailedEvidence()
    {
        RecoveryDependencyNode failed = Node();
        failed = failed with { Observation = failed.Observation! with { Outcome = RecoveryObservationOutcome.Failed } };
        RecoveryDependencyNode mirror = Node();
        RecoveryDependencyNode root = Node(Need("payload", failed.SubjectId, mirror.SubjectId));
        RecoveryDependencyGraphAssessment result = Assess(root, failed, mirror);
        Assert.True(result.HasCurrentMeasuredCoverage);
        Assert.Equal(3, result.Nodes.Count);
        Assert.Equal(RecoveryObservationOutcome.Failed,
            result.Nodes.Single(node => node.SubjectId == failed.SubjectId).Observation.Observation!.Outcome);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("generation")]
    [InlineData("unsupported")]
    [InlineData("simulation")]
    [InlineData("confirmation")]
    [InlineData("interrupted")]
    [InlineData("future")]
    public void DependencyLimitationsNeverBecomeMeasuredCoverage(string limitation)
    {
        RecoveryDependencyNode dependency = Node();
        RecoveryObservation observation = dependency.Observation!;
        dependency = limitation switch
        {
            "missing" => dependency with { Observation = null },
            "stale" => dependency with { Observation = observation with { ObservedUtc = Now.AddDays(-8) } },
            "generation" => dependency with { Generation = "replacement" },
            "unsupported" => dependency with { Observation = observation with { IsSupported = false } },
            "simulation" => dependency with { Observation = observation with { Basis = RecoveryEvidenceBasis.Simulated } },
            "confirmation" => dependency with { Observation = observation with { Basis = RecoveryEvidenceBasis.UserConfirmed } },
            "future" => dependency with { Observation = observation with { ObservedUtc = Now.AddTicks(1) } },
            _ => dependency with { Observation = observation with { Outcome = RecoveryObservationOutcome.Interrupted } }
        };
        RecoveryDependencyNode root = Node(Need("reader", dependency.SubjectId));
        RecoveryDependencyGraphAssessment result = Assess(root, dependency);
        Assert.True(result.IsValid);
        Assert.False(result.HasCurrentMeasuredCoverage);
        Assert.Same(dependency.Observation, result.Nodes.Single(node => node.SubjectId == dependency.SubjectId).Observation.Observation);
    }

    [Fact]
    public void MissingParentAndEmptyAlternativesRemainNamedUnsatisfiedRequirements()
    {
        Guid missing = Guid.NewGuid();
        RecoveryDependencyNode root = Node(Need("parent-image", missing), Need("key"));
        RecoveryDependencyGraphAssessment result = Assess(root);
        Assert.True(result.IsValid);
        Assert.False(result.HasCurrentMeasuredCoverage);
        Assert.Equal(missing, result.Nodes[0].Requirements[0].Alternatives[0]);
        Assert.All(result.Nodes[0].Requirements, requirement => Assert.False(requirement.HasCurrentMeasuredCoverage));
        RecoveryDependencyGraphAssessment absentRoot = RecoveryDependencyService.Assess([], Guid.NewGuid(), Now, TimeSpan.FromDays(7));
        Assert.Equal("dependencies.root-missing", absentRoot.Reason);
        Assert.False(absentRoot.HasCurrentMeasuredCoverage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CyclesAreRejectedEvenWhenAnotherAlternativePasses(bool disconnected)
    {
        RecoveryDependencyNode a = Node();
        RecoveryDependencyNode b = Node(Need("parent", a.SubjectId));
        a = a with { Requirements = [Need("parent", b.SubjectId)] };
        RecoveryDependencyNode passing = Node();
        RecoveryDependencyNode root = Node(Need("payload", passing.SubjectId, disconnected ? passing.SubjectId : a.SubjectId));
        if (disconnected)
            root = root with { Requirements = [Need("payload", passing.SubjectId)] };
        RecoveryDependencyGraphAssessment result = Assess(root, a, b, passing);
        Assert.False(result.IsValid);
        Assert.Equal("dependencies.cycle", result.Reason);
        Assert.Empty(result.Nodes);
    }

    [Theory]
    [InlineData("duplicate-node")]
    [InlineData("duplicate-name")]
    [InlineData("unknown-relation")]
    [InlineData("empty-identity")]
    [InlineData("duplicate-alternative")]
    [InlineData("blank-generation")]
    public void AmbiguousOrUnsupportedGraphsFailBeforeEvaluation(string defect)
    {
        RecoveryDependencyNode leaf = Node();
        RecoveryDependencyRequirement requirement = Need("payload", leaf.SubjectId);
        RecoveryDependencyNode root = Node(requirement);
        root = defect switch
        {
            "duplicate-name" => root with { Requirements = [requirement, requirement] },
            "unknown-relation" => root with { Requirements = [requirement with { Relation = (RecoveryDependencyRelation)99 }] },
            "empty-identity" => root with { Requirements = [Need("payload", Guid.Empty)] },
            "duplicate-alternative" => root with { Requirements = [Need("payload", leaf.SubjectId, leaf.SubjectId)] },
            "blank-generation" => root with { Generation = " " },
            _ => root
        };
        RecoveryDependencyGraphAssessment result = defect == "duplicate-node" ? Assess(root, leaf, leaf) : Assess(root, leaf);
        Assert.False(result.IsValid);
        Assert.False(result.HasCurrentMeasuredCoverage);
        Assert.Empty(result.Nodes);
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void DependencyChainsRespectTheLongestPathRegardlessOfInputOrder(int length, bool valid)
    {
        var chain = new List<RecoveryDependencyNode> { Node() };
        for (int index = 1; index < length; index++)
            chain.Add(Node(Need("parent", chain[^1].SubjectId)));
        RecoveryDependencyNode root = chain[^1];
        foreach (RecoveryDependencyNode[] ordered in new[] { chain.ToArray(), chain.AsEnumerable().Reverse().ToArray() })
        {
            RecoveryDependencyGraphAssessment result = RecoveryDependencyService.Assess(ordered, root.SubjectId, Now, TimeSpan.FromDays(7));
            Assert.Equal(valid, result.IsValid);
            Assert.Equal(valid, result.HasCurrentMeasuredCoverage);
            if (!valid)
                Assert.Equal("dependencies.depth-limit", result.Reason);
        }
    }

    [Fact]
    public void AShortAlternativeCannotHideAnExcessivePathThroughSharedNodes()
    {
        RecoveryDependencyNode shared = Node();
        var chain = new List<RecoveryDependencyNode> { shared };
        for (int index = 1; index < 64; index++)
            chain.Add(Node(Need("parent", chain[^1].SubjectId)));
        RecoveryDependencyNode root = Node(Need("payload", shared.SubjectId, chain[^1].SubjectId));
        RecoveryDependencyGraphAssessment result = RecoveryDependencyService.Assess(
            chain.Append(root).ToArray(), root.SubjectId, Now, TimeSpan.FromDays(7));
        Assert.False(result.IsValid);
        Assert.Equal("dependencies.depth-limit", result.Reason);
    }

    [Fact]
    public void NodeRequirementAndAlternativeBudgetsRejectExcessInput()
    {
        RecoveryDependencyNode root = Node();
        Assert.Equal("dependencies.node-limit", Assess(root, Enumerable.Range(0, 256).Select(_ => Node()).ToArray()).Reason);
        root = root with { Requirements = Enumerable.Range(0, 1025).Select(index => Need($"need-{index}")).ToArray() };
        Assert.Equal("dependencies.requirement-limit", Assess(root).Reason);
        root = root with { Requirements = [Need("payload", Enumerable.Range(0, 1025).Select(_ => Guid.NewGuid()).ToArray())] };
        Assert.Equal("dependencies.alternative-limit", Assess(root).Reason);
    }

    [Fact]
    public void AssessmentOwnsItsAlternativeListAndDoesNotEditInput()
    {
        RecoveryDependencyNode leaf = Node();
        Guid[] alternatives = [leaf.SubjectId];
        RecoveryDependencyNode root = Node(Need("payload", alternatives));
        RecoveryDependencyGraphAssessment result = Assess(root, leaf);
        alternatives[0] = Guid.Empty;
        Assert.True(result.HasCurrentMeasuredCoverage);
        Assert.Equal(leaf.SubjectId, result.Nodes.Single(node => node.SubjectId == root.SubjectId).Requirements[0].Alternatives[0]);
        Assert.Equal(Guid.Empty, root.Requirements[0].Alternatives[0]);
    }

    private static RecoveryDependencyGraphAssessment Assess(RecoveryDependencyNode root, params RecoveryDependencyNode[] others) =>
        RecoveryDependencyService.Assess(new[] { root }.Concat(others).ToArray(), root.SubjectId, Now, TimeSpan.FromDays(7));

    private static RecoveryDependencyRequirement Need(string name, params Guid[] alternatives) =>
        new(name, name == "key" ? RecoveryDependencyRelation.Key : RecoveryDependencyRelation.Payload, alternatives);

    private static RecoveryDependencyNode Node(params RecoveryDependencyRequirement[] requirements)
    {
        Guid id = Guid.NewGuid();
        return new(id, "generation-1", "fixture-coverage", new(Guid.NewGuid(), id, "generation-1", "fixture-coverage",
            "synthetic-fixture", RecoveryEvidenceBasis.Measured, RecoveryObservationOutcome.Passed, Now.AddDays(-1), null, true), requirements);
    }
}
