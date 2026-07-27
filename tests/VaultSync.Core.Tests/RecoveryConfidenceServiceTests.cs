using System;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoveryConfidenceServiceTests
{
    private static readonly DateTime NowUtc = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
    private readonly RecoveryConfidenceService _service = new();

    [Fact]
    public void Evaluate_MissingRecoveryPointIsTheDecisiveBlocker()
    {
        ProjectRecoveryConfidence result = _service.Evaluate(
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
        ProjectRecoveryConfidence result = _service.Evaluate(
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
        ProjectRecoveryConfidence result = _service.Evaluate(
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
    public void Evaluate_BackupCompletionWithoutVerificationRemainsPending()
    {
        ProjectRecoveryConfidence result = _service.Evaluate(
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
        ProjectRecoveryConfidence result = _service.Evaluate(
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
        ProjectRecoveryConfidence result = _service.Evaluate(
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
        ProjectRecoveryConfidence result = _service.Evaluate(
            FullyVerifiedInput() with { IsRestorePlanValid = false },
            NowUtc);

        Assert.Equal(RecoveryConfidenceState.RestorePlanInvalid, result.State);
        Assert.True(result.IsBlocked);
        Assert.Equal("restore-plan.invalid", result.DecisiveEvidenceCode);
    }

    [Fact]
    public void Evaluate_UnsupportedVerificationIsExplicitAndNeverFullyVerified()
    {
        ProjectRecoveryConfidence result = _service.Evaluate(
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

    [Fact]
    public void Evaluate_AllRequiredEvidencePassedIsFullyVerified()
    {
        ProjectRecoveryConfidence result = _service.Evaluate(FullyVerifiedInput(), NowUtc);

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
            _service.Evaluate(
                FullyVerifiedInput(),
                NowUtc,
                verificationFreshness: TimeSpan.Zero));
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
