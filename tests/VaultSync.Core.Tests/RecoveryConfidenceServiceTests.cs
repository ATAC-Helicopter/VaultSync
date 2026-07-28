using System;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoveryConfidenceServiceTests
{
    private static readonly DateTime NowUtc = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Evaluate_MissingRecoveryPointIsTheDecisiveBlocker()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            new RecoveryConfidenceInput { ProjectId = 7 },
            NowUtc);

        Assert.Equal(RecoveryConfidenceState.NoRecoveryPoint, result.State);
        Assert.True(result.IsBlocked);
        Assert.Equal("recovery-point.missing", result.DecisiveEvidenceCode);
        Assert.Equal("action.create-backup", result.RecommendedActionCode);
        Assert.Single(result.Evidence);
    }

    [Fact]
    public void Evaluate_DestinationFailureCannotBeHiddenByPassedEvidence()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with { IsDestinationReachable = false },
            NowUtc);

        Assert.Equal(RecoveryConfidenceState.DestinationUnavailable, result.State);
        Assert.Equal("destination.unavailable", result.DecisiveEvidenceCode);
        Assert.Contains(result.Evidence, item =>
            item.Kind == RecoveryEvidenceKind.IntegrityVerification &&
            item.Status == RecoveryEvidenceStatus.Passed);
    }

    [Fact]
    public void Evaluate_EncryptedPointWithoutCredentialIsBlocked()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with
            {
                IsEncrypted = true,
                IsCredentialAvailable = false
            },
            NowUtc);

        Assert.Equal(RecoveryConfidenceState.CredentialUnavailable, result.State);
        Assert.Equal("credential.unavailable", result.DecisiveEvidenceCode);
    }

    [Fact]
    public void Evaluate_EncryptedPointWithUncheckedCredentialRequestsCheck()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with
            {
                IsEncrypted = true,
                IsCredentialAvailable = null
            },
            NowUtc);

        Assert.Equal(RecoveryConfidenceState.CredentialUnavailable, result.State);
        Assert.Equal("credential.not-checked", result.DecisiveEvidenceCode);
        Assert.Equal("action.check-credential", result.RecommendedActionCode);
    }

    [Fact]
    public void Evaluate_EncryptedPointWithCredentialRecordsPassedEvidence()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with
            {
                IsEncrypted = true,
                IsCredentialAvailable = true
            },
            NowUtc);

        RecoveryConfidenceEvidence credential = Assert.Single(
            result.Evidence,
            item => item.Kind == RecoveryEvidenceKind.Credential);
        Assert.Equal(RecoveryEvidenceStatus.Passed, credential.Status);
        Assert.Equal("credential.available", credential.Code);
    }

    [Fact]
    public void Evaluate_BackupCompletionWithoutVerificationRemainsPending()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with
            {
                VerificationStatus = RecoveryVerificationStatus.NotRun,
                VerificationUtc = null
            },
            NowUtc);

        Assert.Equal(RecoveryConfidenceState.VerificationPending, result.State);
        Assert.False(result.IsBlocked);
        Assert.Equal("verification.not-run", result.DecisiveEvidenceCode);
    }

    [Fact]
    public void Evaluate_StalePassedDrillBecomesOverdue()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with { DrillUtc = NowUtc.AddDays(-31) },
            NowUtc);

        Assert.Equal(RecoveryConfidenceState.DrillOverdue, result.State);
        Assert.Equal("restore-drill.overdue", result.DecisiveEvidenceCode);
        Assert.Contains(result.Evidence, item =>
            item.Kind == RecoveryEvidenceKind.RestoreDrill &&
            item.Status == RecoveryEvidenceStatus.Stale);
    }

    [Fact]
    public void Evaluate_MissingDrillIsDistinctFromAnOverdueDrill()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with
            {
                DrillStatus = null,
                DrillUtc = null
            },
            NowUtc);

        Assert.Equal(RecoveryConfidenceState.DrillNotRun, result.State);
        Assert.Equal("restore-drill.not-run", result.DecisiveEvidenceCode);
    }

    [Fact]
    public void Evaluate_InvalidRestorePlanHasItsOwnBlockerState()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with { IsRestorePlanValid = false },
            NowUtc);

        Assert.Equal(RecoveryConfidenceState.RestorePlanInvalid, result.State);
        Assert.True(result.IsBlocked);
        Assert.Equal("restore-plan.invalid", result.DecisiveEvidenceCode);
    }

    [Fact]
    public void Evaluate_MissingRestorePlanIsAWarning()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with { IsRestorePlanValid = null },
            NowUtc);

        Assert.Equal(RecoveryConfidenceState.RecoverableWithWarnings, result.State);
        Assert.Equal("restore-plan.not-run", result.DecisiveEvidenceCode);
        Assert.Equal("action.review-evidence", result.RecommendedActionCode);
    }

    [Theory]
    [InlineData(RecoveryVerificationStatus.Limited, RecoveryConfidenceState.RecoverableWithWarnings, "verification.limited")]
    [InlineData(RecoveryVerificationStatus.Failed, RecoveryConfidenceState.VerificationFailed, "verification.failed")]
    public void Evaluate_MapsVerificationOutcomes(
        RecoveryVerificationStatus verificationStatus,
        RecoveryConfidenceState expectedState,
        string expectedCode)
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with { VerificationStatus = verificationStatus },
            NowUtc);

        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedCode, result.DecisiveEvidenceCode);
    }

    [Fact]
    public void Evaluate_StaleVerificationRequestsNewVerification()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with { VerificationUtc = NowUtc.AddDays(-8) },
            NowUtc);

        Assert.Equal(RecoveryConfidenceState.RecoverableWithWarnings, result.State);
        Assert.Equal("verification.stale", result.DecisiveEvidenceCode);
        Assert.Equal("action.run-verification", result.RecommendedActionCode);
    }

    [Fact]
    public void Evaluate_UnsupportedVerificationIsExplicitAndNeverFullyVerified()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with
            {
                VerificationStatus = RecoveryVerificationStatus.Unsupported,
                VerificationUtc = null
            },
            NowUtc);

        Assert.Equal(RecoveryConfidenceState.RecoverableWithWarnings, result.State);
        RecoveryConfidenceEvidence verification = Assert.Single(
            result.Evidence,
            item => item.Kind == RecoveryEvidenceKind.IntegrityVerification);
        Assert.Equal(RecoveryEvidenceBasis.Unsupported, verification.Basis);
        Assert.Equal(RecoveryEvidenceStatus.Unsupported, verification.Status);
    }

    [Theory]
    [InlineData(RecoveryDrillStatus.Attention, RecoveryConfidenceState.RecoverableWithWarnings, "restore-drill.limited")]
    [InlineData(RecoveryDrillStatus.Failed, RecoveryConfidenceState.DrillFailed, "restore-drill.failed")]
    public void Evaluate_MapsDrillOutcomes(
        RecoveryDrillStatus drillStatus,
        RecoveryConfidenceState expectedState,
        string expectedCode)
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with { DrillStatus = drillStatus },
            NowUtc);

        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedCode, result.DecisiveEvidenceCode);
    }

    [Fact]
    public void Evaluate_MissingOffsiteConfirmationIsAWarning()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with { HasOffsiteCopy = false },
            NowUtc);

        Assert.Equal(RecoveryConfidenceState.RecoverableWithWarnings, result.State);
        Assert.Equal("offsite.not-confirmed", result.DecisiveEvidenceCode);
    }

    [Fact]
    public void Evaluate_AllRequiredEvidencePassedIsFullyVerified()
    {
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(FullyVerifiedInput(), NowUtc);

        Assert.Equal(RecoveryConfidenceState.FullyVerified, result.State);
        Assert.False(result.IsBlocked);
        Assert.Equal("confidence.fully-verified", result.DecisiveEvidenceCode);
        Assert.All(result.Evidence, item => Assert.Equal(RecoveryEvidenceStatus.Passed, item.Status));
        Assert.Contains(result.Evidence, item =>
            item.Kind == RecoveryEvidenceKind.RestorePlan &&
            item.Basis == RecoveryEvidenceBasis.Simulated);
        Assert.Contains(result.Evidence, item =>
            item.Kind == RecoveryEvidenceKind.OffsiteCopy &&
            item.Basis == RecoveryEvidenceBasis.UserConfirmed);
    }

    [Fact]
    public void Evaluate_RejectsNonPositiveFreshness()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecoveryConfidenceService.Evaluate(
                FullyVerifiedInput(),
                NowUtc,
                verificationFreshness: TimeSpan.Zero));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RecoveryConfidenceService.Evaluate(
                FullyVerifiedInput(),
                NowUtc,
                drillFreshness: TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void Evaluate_CustomFreshnessAndLocalTimeAreNormalized()
    {
        DateTime localNow = NowUtc.ToLocalTime();
        ProjectRecoveryConfidence result = RecoveryConfidenceService.Evaluate(
            FullyVerifiedInput() with
            {
                VerificationUtc = localNow.AddHours(-2),
                DrillUtc = localNow.AddHours(-3)
            },
            localNow,
            verificationFreshness: TimeSpan.FromHours(4),
            drillFreshness: TimeSpan.FromHours(4));

        Assert.Equal(RecoveryConfidenceState.FullyVerified, result.State);
    }

    private static RecoveryConfidenceInput FullyVerifiedInput() =>
        new()
        {
            ProjectId = 7,
            HasRecoveryPoint = true,
            IsDestinationReachable = true,
            VerificationStatus = RecoveryVerificationStatus.Passed,
            VerificationUtc = NowUtc.AddDays(-1),
            IsRestorePlanValid = true,
            DrillStatus = RecoveryDrillStatus.Passed,
            DrillUtc = NowUtc.AddDays(-2),
            HasOffsiteCopy = true
        };
}
