using System.Security.Cryptography;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Verifies bounded raw part streams without opening paths or writing destinations.</summary>
public static class DiskImagePayloadVerificationService
{
    private const int BufferLength = 128 * 1024;

    public static async ValueTask<DiskImagePayloadVerificationAssessment> VerifyAsync(
        DiskImagePayloadVerificationRequest request,
        IReadOnlyDictionary<Guid, Stream> streams,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(streams);
        if (request.Coverage is null || request.Digests is null)
            return new(DiskImagePayloadVerificationState.Invalid, "image.missing-payload-plan", 0);

        DiskImageCoverageAssessment coverage = DiskImageCoverageService.Assess(request.Coverage);
        if (coverage.State != DiskImageCoverageState.CompleteUnverified)
        {
            DiskImagePayloadVerificationState state = coverage.State switch
            {
                DiskImageCoverageState.Unsupported => DiskImagePayloadVerificationState.Unsupported,
                DiskImageCoverageState.Incomplete => DiskImagePayloadVerificationState.Incomplete,
                _ => DiskImagePayloadVerificationState.Invalid
            };
            return new(state, coverage.Reason, 0);
        }

        ulong declaredBytes = 0;
        foreach (DiskImagePayloadPart part in request.Coverage.Parts)
        {
            if (part.StoredLength % request.Coverage.LogicalSectorBytes != 0 ||
                part.StoredLength > request.Coverage.Capacity - declaredBytes)
                return new(DiskImagePayloadVerificationState.Invalid, "image.raw-part-size-limit", 0);
            declaredBytes += part.StoredLength;
        }

        if (request.Digests.Count != request.Coverage.Parts.Count ||
            streams.Count > request.Coverage.Parts.Count)
            return new(DiskImagePayloadVerificationState.Invalid, "image.part-set-mismatch", 0);
        if (streams.Count < request.Coverage.Parts.Count)
            return new(DiskImagePayloadVerificationState.Incomplete, "image.missing-part", 0);

        var digests = new Dictionary<Guid, byte[]>();
        foreach (DiskImagePayloadDigest digest in request.Digests)
        {
            if (digest is null || digest.PartId == Guid.Empty || !IsLowerSha256(digest.Sha256) ||
                !digests.TryAdd(digest.PartId, Convert.FromHexString(digest.Sha256)))
                return new(DiskImagePayloadVerificationState.Invalid, "image.invalid-part-digest", 0);
        }

        byte[] buffer = new byte[BufferLength];
        ulong verifiedBytes = 0;
        foreach (DiskImagePayloadPart part in request.Coverage.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!digests.TryGetValue(part.PartId, out byte[]? expected) ||
                !streams.TryGetValue(part.PartId, out Stream? source))
                return new(DiskImagePayloadVerificationState.Incomplete, "image.missing-part", verifiedBytes);
            if (source is null || !source.CanRead)
                return new(DiskImagePayloadVerificationState.Invalid, "image.unreadable-part", verifiedBytes);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            ulong remaining = part.StoredLength;
            while (remaining > 0)
            {
                int requested = (int)Math.Min((ulong)buffer.Length, remaining);
                int read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    return new(DiskImagePayloadVerificationState.Incomplete, "image.truncated-part", verifiedBytes);
                if (verifiedBytes > ulong.MaxValue - (ulong)read)
                    return new(DiskImagePayloadVerificationState.Invalid, "image.byte-count-overflow", verifiedBytes);
                hash.AppendData(buffer, 0, read);
                remaining -= (ulong)read;
                verifiedBytes += (ulong)read;
            }

            if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
                return new(DiskImagePayloadVerificationState.Corrupt, "image.trailing-part-bytes", verifiedBytes);
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
                return new(DiskImagePayloadVerificationState.Corrupt, "image.part-digest-mismatch", verifiedBytes);
        }

        return new(DiskImagePayloadVerificationState.PayloadsVerified, "image.payloads-only", verifiedBytes);
    }

    private static bool IsLowerSha256(string? digest) =>
        digest is { Length: 64 } && digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
