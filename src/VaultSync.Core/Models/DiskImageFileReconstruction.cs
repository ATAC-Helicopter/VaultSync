namespace VaultSync.Core.Models;

public enum DiskImageFileReconstructionState { Invalid, Unsupported, Incomplete, Corrupt, Interrupted, Failed, FileFixtureReconstructed }

/// <summary>Reconstruction into a new temporary file only; no device restore or boot qualification.</summary>
public sealed record DiskImageFileReconstructionAssessment(
    DiskImageFileReconstructionState State, string Reason, ulong WrittenBytes = 0,
    string? Workspace = null, string? ManifestRoot = null, string? ContentSha256 = null);
