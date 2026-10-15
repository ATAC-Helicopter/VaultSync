using System.Buffers.Binary;
using System.Security.Cryptography;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Reads a bounded provisional raw manifest/completion stream; never resolves paths or writes disks.</summary>
public static class DiskImageManifestVerificationService
{
    public const int ManifestHeaderLength = 64;
    public const int PartRecordLength = 56;
    public const int ExtentRecordLength = 44;
    public const int CompletionLength = 64;

    public static async ValueTask<DiskImageManifestVerificationAssessment> VerifyAsync(
        Stream metadata, IReadOnlyDictionary<Guid, Stream> payloads,
        CancellationToken cancellationToken = default)
        => await VerifyWithinCapacityAsync(metadata, payloads, ulong.MaxValue, cancellationToken).ConfigureAwait(false);

    // Fixture admission happens during metadata parsing, before any payload is read or staged.
    // This bound is caller policy, not a change to the provisional format's capacity range.
    internal static async ValueTask<DiskImageManifestVerificationAssessment> VerifyWithinCapacityAsync(
        Stream metadata, IReadOnlyDictionary<Guid, Stream> payloads, ulong maximumCapacity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(payloads);
        DiskImageEnvelopeAssessment envelope = await DiskImageEnvelopeService.InspectAsync(
            metadata, cancellationToken).ConfigureAwait(false);
        if (envelope.State != DiskImageEnvelopeState.SupportedEnvelope)
            return new(envelope.State switch
            {
                DiskImageEnvelopeState.Unsupported => DiskImageManifestVerificationState.Unsupported,
                DiskImageEnvelopeState.Incomplete => DiskImageManifestVerificationState.Incomplete,
                _ => DiskImageManifestVerificationState.Invalid
            }, envelope.Reason);

        if (envelope.ManifestLength < ManifestHeaderLength)
            return new(DiskImageManifestVerificationState.Invalid, "image.short-manifest");
        byte[] manifest = new byte[envelope.ManifestLength];
        if (!await ReadExactlyAsync(metadata, manifest, cancellationToken).ConfigureAwait(false))
            return new(DiskImageManifestVerificationState.Incomplete, "image.truncated-manifest");
        if (BinaryPrimitives.ReadUInt32LittleEndian(manifest) != 1)
            return new(DiskImageManifestVerificationState.Unsupported, "image.unsupported-manifest");

        Guid imageId = new(manifest.AsSpan(4, 16), bigEndian: true);
        Guid captureId = new(manifest.AsSpan(20, 16), bigEndian: true);
        ulong generation = BinaryPrimitives.ReadUInt64LittleEndian(manifest.AsSpan(36));
        ulong capacity = BinaryPrimitives.ReadUInt64LittleEndian(manifest.AsSpan(44));
        uint sector = BinaryPrimitives.ReadUInt32LittleEndian(manifest.AsSpan(52));
        uint partCount = BinaryPrimitives.ReadUInt32LittleEndian(manifest.AsSpan(56));
        uint extentCount = BinaryPrimitives.ReadUInt32LittleEndian(manifest.AsSpan(60));
        if (imageId == Guid.Empty || captureId == Guid.Empty || generation == 0)
            return new(DiskImageManifestVerificationState.Invalid, "image.invalid-manifest-identity");
        if (capacity > maximumCapacity)
            return new(DiskImageManifestVerificationState.Invalid, "image.capacity-policy-limit");
        if (partCount > DiskImageCoverageService.MaximumParts ||
            extentCount > DiskImageCoverageService.MaximumExtents ||
            (ulong)ManifestHeaderLength + (ulong)partCount * PartRecordLength +
            (ulong)extentCount * ExtentRecordLength != (ulong)manifest.Length)
            return new(DiskImageManifestVerificationState.Invalid, "image.manifest-record-limit");

        var parts = new List<DiskImagePayloadPart>((int)partCount);
        var digests = new List<DiskImagePayloadDigest>((int)partCount);
        int offset = ManifestHeaderLength;
        for (int index = 0; index < partCount; index++, offset += PartRecordLength)
        {
            Guid partId = new(manifest.AsSpan(offset, 16), bigEndian: true);
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(manifest.AsSpan(offset + 16));
            parts.Add(new(partId, length));
            digests.Add(new(partId, Convert.ToHexStringLower(manifest.AsSpan(offset + 24, 32))));
        }
        var extents = new List<DiskImageExtent>((int)extentCount);
        for (int index = 0; index < extentCount; index++, offset += ExtentRecordLength)
        {
            ulong start = BinaryPrimitives.ReadUInt64LittleEndian(manifest.AsSpan(offset));
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(manifest.AsSpan(offset + 8));
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(manifest.AsSpan(offset + 16));
            if (kind > 1)
                return new(DiskImageManifestVerificationState.Unsupported, "image.unsupported-extent");
            Guid partId = new(manifest.AsSpan(offset + 20, 16), bigEndian: true);
            ulong partOffset = BinaryPrimitives.ReadUInt64LittleEndian(manifest.AsSpan(offset + 36));
            extents.Add(new(start, length, (DiskImageExtentKind)kind,
                partId == Guid.Empty ? null : partId, partOffset));
        }

        byte[] completion = new byte[CompletionLength];
        if (!await ReadExactlyAsync(metadata, completion, cancellationToken).ConfigureAwait(false))
            return new(DiskImageManifestVerificationState.Incomplete, "image.missing-completion");
        if (!completion.AsSpan(0, 8).SequenceEqual("VSCOMP\0\0"u8))
            return new(DiskImageManifestVerificationState.Invalid, "image.invalid-completion");
        if (new Guid(completion.AsSpan(8, 16), bigEndian: true) != imageId ||
            BinaryPrimitives.ReadUInt64LittleEndian(completion.AsSpan(24)) != generation)
            return new(DiskImageManifestVerificationState.Corrupt, "image.completion-identity-mismatch");
        byte[] root = SHA256.HashData(manifest);
        if (!CryptographicOperations.FixedTimeEquals(root, completion.AsSpan(32, 32)))
            return new(DiskImageManifestVerificationState.Corrupt, "image.manifest-root-mismatch");
        if (await metadata.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            return new(DiskImageManifestVerificationState.Corrupt, "image.trailing-metadata-bytes");

        DiskImagePayloadVerificationAssessment result = await DiskImagePayloadVerificationService.VerifyAsync(
            new(new(capacity, sector, parts, extents, HasCompletion: true), digests),
            payloads, cancellationToken).ConfigureAwait(false);
        DiskImageManifestVerificationState state = result.State switch
        {
            DiskImagePayloadVerificationState.PayloadsVerified => DiskImageManifestVerificationState.ContentVerified,
            DiskImagePayloadVerificationState.Unsupported => DiskImageManifestVerificationState.Unsupported,
            DiskImagePayloadVerificationState.Incomplete => DiskImageManifestVerificationState.Incomplete,
            DiskImagePayloadVerificationState.Corrupt => DiskImageManifestVerificationState.Corrupt,
            _ => DiskImageManifestVerificationState.Invalid
        };
        return new(state, state == DiskImageManifestVerificationState.ContentVerified
            ? "image.draft-content-only" : result.Reason, result.VerifiedBytes,
            imageId, captureId, generation, Convert.ToHexStringLower(root))
        {
            VerifiedCoverage = state == DiskImageManifestVerificationState.ContentVerified
                ? new(capacity, sector, parts, extents, HasCompletion: true) : null
        };
    }

    private static async ValueTask<bool> ReadExactlyAsync(
        Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return false;
            offset += read;
        }
        return true;
    }
}
