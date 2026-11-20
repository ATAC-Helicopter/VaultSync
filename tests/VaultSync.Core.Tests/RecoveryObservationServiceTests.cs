#nullable enable

using System;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoveryObservationServiceTests
{
    private static readonly Guid Subject = Guid.Parse("c4f76e94-8377-4951-8cf3-decfca660e61");
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Age = TimeSpan.FromDays(7);

    [Fact]
    public void MissingEvidenceRemainsMissing()
    {
        RecoveryObservationAssessment assessment = Assess(null);
        Assert.Equal(RecoveryObservationFreshness.Missing, assessment.Freshness);
        Assert.Equal("observation.missing", assessment.Reason);
        Assert.False(assessment.HasCurrentPassedMeasurement);
    }

    [Theory]
    [InlineData(RecoveryEvidenceBasis.Measured, true)]
    [InlineData(RecoveryEvidenceBasis.Simulated, false)]
    [InlineData(RecoveryEvidenceBasis.Inferred, false)]
    [InlineData(RecoveryEvidenceBasis.UserConfirmed, false)]
    [InlineData(RecoveryEvidenceBasis.Unsupported, false)]
    public void CurrentSimulationsAndConfirmationsNeverBecomeMeasurements(RecoveryEvidenceBasis basis, bool measured)
    {
        RecoveryObservation observation = Observation() with { Basis = basis };
        RecoveryObservationAssessment assessment = Assess(observation);
        Assert.Equal(RecoveryObservationFreshness.Current, assessment.Freshness);
        Assert.Equal(observation, assessment.Observation);
        Assert.Equal(measured, assessment.HasCurrentPassedMeasurement);
    }

    [Theory]
    [InlineData(RecoveryObservationOutcome.Failed)]
    [InlineData(RecoveryObservationOutcome.Interrupted)]
    public void FreshnessDoesNotHideFailedOrInterruptedExecution(RecoveryObservationOutcome outcome)
    {
        RecoveryObservationAssessment assessment = Assess(Observation() with { Outcome = outcome });
        Assert.Equal(RecoveryObservationFreshness.Current, assessment.Freshness);
        Assert.Equal(outcome, assessment.Observation!.Outcome);
        Assert.False(assessment.HasCurrentPassedMeasurement);
    }

    [Fact]
    public void UnsupportedCheckerKeepsItsRecordedOutcomeButDoesNotSupplyAPassedMeasurement()
    {
        RecoveryObservationAssessment assessment = Assess(Observation() with { IsSupported = false });
        Assert.Equal(RecoveryObservationFreshness.Current, assessment.Freshness);
        Assert.Equal(RecoveryObservationOutcome.Passed, assessment.Observation!.Outcome);
        Assert.False(assessment.HasCurrentPassedMeasurement);
    }

    [Theory]
    [InlineData("subject", "observation.subject-changed")]
    [InlineData("generation", "observation.generation-changed")]
    [InlineData("scope", "observation.scope-changed")]
    public void ChangedBindingsInvalidateApplicabilityWithoutEditingHistoricalFacts(string field, string reason)
    {
        RecoveryObservation observation = field switch
        {
            "subject" => Observation() with { SubjectId = Guid.NewGuid() },
            "generation" => Observation() with { Generation = "generation-2" },
            _ => Observation() with { Scope = "byte-coverage-different" }
        };
        RecoveryObservationAssessment assessment = Assess(observation);
        Assert.Equal(RecoveryObservationFreshness.Stale, assessment.Freshness);
        Assert.Equal(reason, assessment.Reason);
        Assert.Same(observation, assessment.Observation);
        Assert.Equal(RecoveryObservationOutcome.Passed, assessment.Observation!.Outcome);
        Assert.False(assessment.HasCurrentPassedMeasurement);
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("subject")]
    [InlineData("generation")]
    [InlineData("scope")]
    [InlineData("producer")]
    [InlineData("basis")]
    [InlineData("outcome")]
    public void InvalidProvenanceNeverProducesApplicableEvidence(string field)
    {
        RecoveryObservation observation = field switch
        {
            "evidence" => Observation() with { EvidenceId = Guid.Empty },
            "subject" => Observation() with { SubjectId = Guid.Empty },
            "generation" => Observation() with { Generation = " " },
            "scope" => Observation() with { Scope = "" },
            "producer" => Observation() with { ProducerBuild = null! },
            "basis" => Observation() with { Basis = (RecoveryEvidenceBasis)99 },
            _ => Observation() with { Outcome = (RecoveryObservationOutcome)99 }
        };
        RecoveryObservationAssessment assessment = Assess(observation);
        Assert.Equal(RecoveryObservationFreshness.Invalid, assessment.Freshness);
        Assert.Equal("observation.invalid-provenance", assessment.Reason);
        Assert.False(assessment.HasCurrentPassedMeasurement);
    }

    [Fact]
    public void FutureObservationAndReversedExpiryAreExplicitlyInvalid()
    {
        Assert.Equal("observation.invalid-time", Assess(Observation() with { ObservedUtc = Now.AddTicks(1) }).Reason);
        RecoveryObservation observation = Observation();
        Assert.Equal("observation.invalid-time", Assess(observation with { ExpiresUtc = observation.ObservedUtc.AddTicks(-1) }).Reason);
    }

    [Fact]
    public void AgeAndExpiryBoundariesAreEvaluatedWithoutExtendingAnExplicitExpiry()
    {
        RecoveryObservation boundary = Observation() with { ObservedUtc = Now - Age };
        Assert.True(Assess(boundary).HasCurrentPassedMeasurement);
        Assert.Equal("observation.expired", Assess(boundary with { ObservedUtc = boundary.ObservedUtc.AddTicks(-1) }).Reason);
        Assert.Equal("observation.expired", Assess(Observation() with { ExpiresUtc = Now }).Reason);
        Assert.True(Assess(Observation() with { ExpiresUtc = Now.AddTicks(1) }).HasCurrentPassedMeasurement);
    }

    [Fact]
    public void EquivalentUtcInstantsWithOffsetsRemainApplicable()
    {
        RecoveryObservation observation = Observation() with { ObservedUtc = Now.ToOffset(TimeSpan.FromHours(2)) };
        Assert.True(Assess(observation).HasCurrentPassedMeasurement);
    }

    [Fact]
    public void InvalidEvaluationPolicyIsRejectedInsteadOfProducingASuccess()
    {
        Assert.Throws<ArgumentException>(() => RecoveryObservationService.Assess(Observation(), Guid.Empty, "generation-1", "byte-coverage", Now, Age));
        Assert.Throws<ArgumentException>(() => RecoveryObservationService.Assess(Observation(), Subject, "", "byte-coverage", Now, Age));
        Assert.Throws<ArgumentException>(() => RecoveryObservationService.Assess(Observation(), Subject, "generation-1", " ", Now, Age));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecoveryObservationService.Assess(Observation(), Subject, "generation-1", "byte-coverage", Now, TimeSpan.Zero));
    }

    private static RecoveryObservationAssessment Assess(RecoveryObservation? observation) =>
        RecoveryObservationService.Assess(observation, Subject, "generation-1", "byte-coverage", Now, Age);

    private static RecoveryObservation Observation() => new(
        Guid.NewGuid(), Subject, "generation-1", "byte-coverage", "fixture-build", RecoveryEvidenceBasis.Measured,
        RecoveryObservationOutcome.Passed, Now.AddDays(-1), null, true);
}
