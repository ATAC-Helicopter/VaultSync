using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed class RecoveryConfidenceService
{
    public static readonly TimeSpan DefaultVerificationFreshness = TimeSpan.FromDays(7);
    public static readonly TimeSpan DefaultDrillFreshness = TimeSpan.FromDays(30);

    private static readonly (string Code, RecoveryConfidenceState State, string Action)[] DecisiveStates =
    [
        ("recovery-point.missing", RecoveryConfidenceState.NoRecoveryPoint, "action.create-backup"),
        ("destination.unavailable", RecoveryConfidenceState.DestinationUnavailable, "action.reconnect-destination"),
        ("credential.unavailable", RecoveryConfidenceState.CredentialUnavailable, "action.restore-credential"),
        ("credential.not-checked", RecoveryConfidenceState.CredentialUnavailable, "action.check-credential"),
        ("verification.failed", RecoveryConfidenceState.VerificationFailed, "action.run-verification"),
        ("restore-plan.invalid", RecoveryConfidenceState.RestorePlanInvalid, "action.review-restore-plan"),
        ("restore-drill.failed", RecoveryConfidenceState.DrillFailed, "action.review-drill"),
        ("verification.not-run", RecoveryConfidenceState.VerificationPending, "action.run-verification"),
        ("verification.unsupported", RecoveryConfidenceState.RecoverableWithWarnings, "action.review-limitations"),
        ("verification.stale", RecoveryConfidenceState.RecoverableWithWarnings, "action.run-verification"),
        ("restore-drill.not-run", RecoveryConfidenceState.DrillNotRun, "action.run-restore-drill"),
        ("restore-drill.overdue", RecoveryConfidenceState.DrillOverdue, "action.run-restore-drill")
    ];

    public static ProjectRecoveryConfidence Evaluate(
        RecoveryConfidenceInput input,
        DateTime? nowUtc = null,
        TimeSpan? verificationFreshness = null,
        TimeSpan? drillFreshness = null)
    {
        ArgumentNullException.ThrowIfNull(input);

        DateTime now = NormalizeUtc(nowUtc ?? DateTime.UtcNow);
        TimeSpan verificationWindow = ValidateFreshness(
            verificationFreshness ?? DefaultVerificationFreshness,
            nameof(verificationFreshness));
        TimeSpan drillWindow = ValidateFreshness(
            drillFreshness ?? DefaultDrillFreshness,
            nameof(drillFreshness));
        var evidence = new List<RecoveryConfidenceEvidence>();

        AddRecoveryPointEvidence(input, evidence);
        AddDestinationEvidence(input, evidence);
        AddCredentialEvidence(input, evidence);
        AddVerificationEvidence(input, evidence, now, verificationWindow);
        AddRestorePlanEvidence(input, evidence);
        AddDrillEvidence(input, evidence, now, drillWindow);
        AddOffsiteEvidence(input, evidence);

        (RecoveryConfidenceState state, string decisiveCode, string actionCode) =
            SelectState(evidence);

        return new ProjectRecoveryConfidence
        {
            ProjectId = input.ProjectId,
            State = state,
            DecisiveEvidenceCode = decisiveCode,
            RecommendedActionCode = actionCode,
            Evidence = evidence
        };
    }

    private static void AddRecoveryPointEvidence(
        RecoveryConfidenceInput input,
        List<RecoveryConfidenceEvidence> evidence) =>
        evidence.Add(new(
            RecoveryEvidenceKind.RecoveryPoint,
            RecoveryEvidenceBasis.Measured,
            input.HasRecoveryPoint ? RecoveryEvidenceStatus.Passed : RecoveryEvidenceStatus.Missing,
            input.HasRecoveryPoint ? "recovery-point.available" : "recovery-point.missing"));

    private static void AddDestinationEvidence(
        RecoveryConfidenceInput input,
        List<RecoveryConfidenceEvidence> evidence)
    {
        if (!input.HasRecoveryPoint)
            return;

        evidence.Add(new(
            RecoveryEvidenceKind.Destination,
            RecoveryEvidenceBasis.Measured,
            input.IsDestinationReachable ? RecoveryEvidenceStatus.Passed : RecoveryEvidenceStatus.Failed,
            input.IsDestinationReachable ? "destination.reachable" : "destination.unavailable"));
    }

    private static void AddCredentialEvidence(
        RecoveryConfidenceInput input,
        List<RecoveryConfidenceEvidence> evidence)
    {
        if (!input.HasRecoveryPoint || !input.IsEncrypted)
            return;

        RecoveryEvidenceStatus status = input.IsCredentialAvailable switch
        {
            true => RecoveryEvidenceStatus.Passed,
            false => RecoveryEvidenceStatus.Failed,
            null => RecoveryEvidenceStatus.Missing
        };
        string code = input.IsCredentialAvailable switch
        {
            true => "credential.available",
            false => "credential.unavailable",
            null => "credential.not-checked"
        };
        evidence.Add(new(
            RecoveryEvidenceKind.Credential,
            RecoveryEvidenceBasis.Measured,
            status,
            code));
    }

    private static void AddVerificationEvidence(
        RecoveryConfidenceInput input,
        List<RecoveryConfidenceEvidence> evidence,
        DateTime nowUtc,
        TimeSpan freshness)
    {
        if (!input.HasRecoveryPoint)
            return;

        RecoveryEvidenceStatus status;
        RecoveryEvidenceBasis basis;
        string code;
        switch (input.VerificationStatus)
        {
            case RecoveryVerificationStatus.Passed when IsStale(input.VerificationUtc, nowUtc, freshness):
                status = RecoveryEvidenceStatus.Stale;
                basis = RecoveryEvidenceBasis.Measured;
                code = "verification.stale";
                break;
            case RecoveryVerificationStatus.Passed:
                status = RecoveryEvidenceStatus.Passed;
                basis = RecoveryEvidenceBasis.Measured;
                code = "verification.passed";
                break;
            case RecoveryVerificationStatus.Limited:
                status = RecoveryEvidenceStatus.Warning;
                basis = RecoveryEvidenceBasis.Measured;
                code = "verification.limited";
                break;
            case RecoveryVerificationStatus.Failed:
                status = RecoveryEvidenceStatus.Failed;
                basis = RecoveryEvidenceBasis.Measured;
                code = "verification.failed";
                break;
            case RecoveryVerificationStatus.Unsupported:
                status = RecoveryEvidenceStatus.Unsupported;
                basis = RecoveryEvidenceBasis.Unsupported;
                code = "verification.unsupported";
                break;
            default:
                status = RecoveryEvidenceStatus.Missing;
                basis = RecoveryEvidenceBasis.Measured;
                code = "verification.not-run";
                break;
        }

        evidence.Add(new(
            RecoveryEvidenceKind.IntegrityVerification,
            basis,
            status,
            code,
            input.VerificationUtc));
    }

    private static void AddRestorePlanEvidence(
        RecoveryConfidenceInput input,
        List<RecoveryConfidenceEvidence> evidence)
    {
        if (!input.HasRecoveryPoint)
            return;

        RecoveryEvidenceStatus status = input.IsRestorePlanValid switch
        {
            true => RecoveryEvidenceStatus.Passed,
            false => RecoveryEvidenceStatus.Failed,
            null => RecoveryEvidenceStatus.Missing
        };
        string code = input.IsRestorePlanValid switch
        {
            true => "restore-plan.valid",
            false => "restore-plan.invalid",
            null => "restore-plan.not-run"
        };
        evidence.Add(new(
            RecoveryEvidenceKind.RestorePlan,
            RecoveryEvidenceBasis.Simulated,
            status,
            code));
    }

    private static void AddDrillEvidence(
        RecoveryConfidenceInput input,
        List<RecoveryConfidenceEvidence> evidence,
        DateTime nowUtc,
        TimeSpan freshness)
    {
        if (!input.HasRecoveryPoint)
            return;

        RecoveryEvidenceStatus status;
        string code;
        switch (input.DrillStatus)
        {
            case RecoveryDrillStatus.Passed when IsStale(input.DrillUtc, nowUtc, freshness):
                status = RecoveryEvidenceStatus.Stale;
                code = "restore-drill.overdue";
                break;
            case RecoveryDrillStatus.Passed:
                status = RecoveryEvidenceStatus.Passed;
                code = "restore-drill.passed";
                break;
            case RecoveryDrillStatus.Attention:
                status = RecoveryEvidenceStatus.Warning;
                code = "restore-drill.limited";
                break;
            case RecoveryDrillStatus.Failed:
                status = RecoveryEvidenceStatus.Failed;
                code = "restore-drill.failed";
                break;
            default:
                status = RecoveryEvidenceStatus.Missing;
                code = "restore-drill.not-run";
                break;
        }

        evidence.Add(new(
            RecoveryEvidenceKind.RestoreDrill,
            RecoveryEvidenceBasis.Measured,
            status,
            code,
            input.DrillUtc));
    }

    private static void AddOffsiteEvidence(
        RecoveryConfidenceInput input,
        List<RecoveryConfidenceEvidence> evidence)
    {
        if (!input.HasRecoveryPoint)
            return;

        evidence.Add(new(
            RecoveryEvidenceKind.OffsiteCopy,
            RecoveryEvidenceBasis.UserConfirmed,
            input.HasOffsiteCopy ? RecoveryEvidenceStatus.Passed : RecoveryEvidenceStatus.Missing,
            input.HasOffsiteCopy ? "offsite.confirmed" : "offsite.not-confirmed"));
    }

    private static (RecoveryConfidenceState State, string EvidenceCode, string ActionCode) SelectState(
        List<RecoveryConfidenceEvidence> evidence)
    {
        if (evidence.Count == 0)
            return (RecoveryConfidenceState.NotMeasured, "confidence.not-measured", "action.measure-recovery");

        foreach ((string code, RecoveryConfidenceState state, string action) in DecisiveStates)
        {
            if (HasCode(evidence, code))
                return (state, code, action);
        }

        if (evidence.Any(item => item.Status is RecoveryEvidenceStatus.Warning or RecoveryEvidenceStatus.Missing))
        {
            RecoveryConfidenceEvidence decisive = evidence.First(item =>
                item.Status is RecoveryEvidenceStatus.Warning or RecoveryEvidenceStatus.Missing);
            return (RecoveryConfidenceState.RecoverableWithWarnings, decisive.Code, "action.review-evidence");
        }

        return (RecoveryConfidenceState.FullyVerified, "confidence.fully-verified", "action.none");
    }

    private static bool HasCode(IEnumerable<RecoveryConfidenceEvidence> evidence, string code) =>
        evidence.Any(item => string.Equals(item.Code, code, StringComparison.Ordinal));

    private static bool IsStale(DateTime? observedUtc, DateTime nowUtc, TimeSpan freshness) =>
        observedUtc is null || nowUtc - NormalizeUtc(observedUtc.Value) > freshness;

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static TimeSpan ValidateFreshness(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "Freshness must be greater than zero.");
}
