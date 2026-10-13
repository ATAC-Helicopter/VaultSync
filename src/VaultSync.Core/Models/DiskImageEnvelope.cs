namespace VaultSync.Core.Models;

public enum DiskImageEnvelopeState { NotImage, Incomplete, Invalid, Unsupported, SupportedEnvelope }

/// <summary>Recognition only: SupportedEnvelope never means a complete or verified image.</summary>
public sealed record DiskImageEnvelopeAssessment(
    DiskImageEnvelopeState State, string Reason, ushort Major = 0, ushort Minor = 0,
    uint ManifestLength = 0);
