#nullable enable
using System;
using System.Linq;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoveryObservationSelectionServiceTests
{
    private static readonly Guid Subject = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static RecoveryObservation Observation() => new(Guid.NewGuid(), Subject, "generation", "bytes",
        "fixture", RecoveryEvidenceBasis.Measured, RecoveryObservationOutcome.Passed, Now.AddDays(-1), null, true);
    private static RecoveryObservationSelectionAssessment Assess(params RecoveryObservation[] observations) =>
        RecoveryObservationSelectionService.Assess(observations, Subject, "generation", "bytes", Now, TimeSpan.FromDays(7));

    [Fact]
    public void OneCurrentObservationIsSelectedWithHistoricalCandidatesPreserved()
    {
        RecoveryObservation current = Observation();
        RecoveryObservation stale = Observation() with { ObservedUtc = Now.AddDays(-8) };
        RecoveryObservationSelectionAssessment result = Assess(stale, current);
        Assert.True(result.HasCurrentPassedMeasurement);
        Assert.Same(current, result.Selected!.Observation);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Theory]
    [InlineData(RecoveryObservationOutcome.Passed)]
    [InlineData(RecoveryObservationOutcome.Failed)]
    [InlineData(RecoveryObservationOutcome.Interrupted)]
    public void MultipleCurrentRecordsAreAmbiguousRegardlessOfOrderOrNewestOutcome(RecoveryObservationOutcome outcome)
    {
        RecoveryObservation older = Observation();
        RecoveryObservation newer = Observation() with { Outcome = outcome, ObservedUtc = Now };
        foreach (RecoveryObservation[] ordered in new[] { new[] { older, newer }, new[] { newer, older } })
        {
            RecoveryObservationSelectionAssessment result = Assess(ordered);
            Assert.Equal(RecoveryObservationSelectionState.Ambiguous, result.State);
            Assert.False(result.HasCurrentPassedMeasurement);
            Assert.Null(result.Selected);
        }
    }

    [Theory]
    [InlineData(RecoveryEvidenceBasis.Simulated)]
    [InlineData(RecoveryEvidenceBasis.Inferred)]
    [InlineData(RecoveryEvidenceBasis.UserConfirmed)]
    public void SelectingOneNonMeasuredRecordDoesNotTurnItIntoAMeasurement(RecoveryEvidenceBasis basis)
    {
        RecoveryObservationSelectionAssessment result = Assess(Observation() with { Basis = basis });
        Assert.Equal(RecoveryObservationSelectionState.Selected, result.State);
        Assert.False(result.HasCurrentPassedMeasurement);
    }

    [Fact]
    public void NoCurrentEvidenceAndInvalidCandidatesCannotFallBackToSuccess()
    {
        Assert.Equal(RecoveryObservationSelectionState.NoCurrentEvidence, Assess().State);
        Assert.Equal(RecoveryObservationSelectionState.NoCurrentEvidence, Assess(Observation() with { Generation = "old" }).State);
        Assert.Equal(RecoveryObservationSelectionState.Invalid,
            Assess(Observation(), Observation() with { ObservedUtc = Now.AddTicks(1) }).State);
        RecoveryObservation duplicate = Observation();
        Assert.Equal(RecoveryObservationSelectionState.Invalid, Assess(duplicate, duplicate).State);
        Assert.Equal(RecoveryObservationSelectionState.Invalid, Assess([null!]).State);
    }

    [Fact]
    public void CandidateLimitIsInclusiveAndRetainedCollectionsOwnTheirSnapshot()
    {
        RecoveryObservation[] observations = Enumerable.Range(0, 256).Select(_ => Observation()).ToArray();
        Assert.Equal(RecoveryObservationSelectionState.Ambiguous, Assess(observations).State);
        Assert.Equal("selection.candidate-limit", Assess(observations.Append(Observation()).ToArray()).Reason);
        RecoveryObservation original = observations[0];
        RecoveryObservationSelectionAssessment result = Assess(observations);
        observations[0] = Observation();
        Assert.Same(original, result.Candidates[0].Observation);
    }
}
