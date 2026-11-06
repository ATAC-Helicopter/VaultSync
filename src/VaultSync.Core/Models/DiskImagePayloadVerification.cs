namespace VaultSync.Core.Models;

public enum DiskImagePayloadVerificationState
{
    Invalid,
    Unsupported,
    Incomplete,
    Corrupt,
    PayloadsVerified
}

public sealed record DiskImagePayloadDigest(Guid PartId, string Sha256);

public sealed record DiskImagePayloadVerificationRequest(
    DiskImageCoveragePlan Coverage, IReadOnlyList<DiskImagePayloadDigest> Digests);

/// <summary>Only the supplied payload bytes were checked; image completion and origin remain unverified.</summary>
public sealed record DiskImagePayloadVerificationAssessment(
    DiskImagePayloadVerificationState State, string Reason, ulong VerifiedBytes);
