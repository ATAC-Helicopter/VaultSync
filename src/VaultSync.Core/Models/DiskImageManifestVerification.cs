namespace VaultSync.Core.Models;

public enum DiskImageManifestVerificationState { Invalid, Unsupported, Incomplete, Corrupt, ContentVerified }

/// <summary>Draft content binding only; neither trusted origin nor device restore authorization.</summary>
public sealed record DiskImageManifestVerificationAssessment(
    DiskImageManifestVerificationState State, string Reason, ulong VerifiedBytes = 0,
    Guid ImageId = default, Guid CaptureId = default, ulong Generation = 0, string? ManifestRoot = null);
