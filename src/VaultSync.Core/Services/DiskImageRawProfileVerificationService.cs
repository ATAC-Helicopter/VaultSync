using System.Buffers.Binary;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public enum DiskImageRawProfileVerificationState
{
    Invalid, Unsupported, Incomplete, Corrupt, Failed, ContentAndLayoutVerified
}

/// <summary>An observation of supplied streams, never trusted origin, durable completion or restore authority.</summary>
public sealed record DiskImageRawProfileVerificationAssessment(
    DiskImageRawProfileVerificationState State, string Reason,
    DiskImageManifestVerificationAssessment? Image = null);

/// <summary>
/// Read-only VSIMAGE 1.0/schema-2 offline-block profile. Opens no paths and writes nothing.
/// The caller must hold immutable/staged bytes for any later operation; streams can change after this observation.
/// </summary>
public static class DiskImageRawProfileVerificationService
{
    public const ulong MaximumCapacity = 8UL * 1024 * 1024 * 1024 * 1024;
    public const int MaximumMetadataBytes = 16 * 1024 * 1024;
    public const int MaximumGptEntries = 128;

    public static async ValueTask<DiskImageRawProfileVerificationAssessment> VerifyAsync(
        Stream metadata, Stream payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!metadata.CanRead || !payload.CanRead || !payload.CanSeek)
                return Refuse(DiskImageRawProfileVerificationState.Unsupported, "raw.seekable-payload-required");
            if (ReferenceEquals(metadata, payload) || payload.Position != 0)
                return Refuse(DiskImageRawProfileVerificationState.Invalid, "raw.invalid-streams");
            return await VerifyCoreAsync(metadata, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) { return Refuse(DiskImageRawProfileVerificationState.Failed, "raw.read-failure"); }
        catch (UnauthorizedAccessException) { return Refuse(DiskImageRawProfileVerificationState.Failed, "raw.read-denied"); }
    }

    private static async ValueTask<DiskImageRawProfileVerificationAssessment> VerifyCoreAsync(
        Stream metadata, Stream payload, CancellationToken cancellationToken)
    {
        byte[] header = new byte[DiskImageEnvelopeService.HeaderLength];
        if (!await ReadExactlyAsync(metadata, header, cancellationToken).ConfigureAwait(false))
            return Refuse(DiskImageRawProfileVerificationState.Incomplete, "raw.truncated-envelope");
        using var headerStream = new MemoryStream(header, writable: false);
        var envelope = await DiskImageEnvelopeService.InspectAsync(headerStream, cancellationToken).ConfigureAwait(false);
        if (envelope.State != DiskImageEnvelopeState.SupportedEnvelope)
            return Refuse(envelope.State == DiskImageEnvelopeState.Unsupported
                ? DiskImageRawProfileVerificationState.Unsupported : DiskImageRawProfileVerificationState.Invalid, envelope.Reason);
        const int framing = DiskImageEnvelopeService.HeaderLength + DiskImageManifestVerificationService.CompletionLength;
        if (envelope.ManifestLength > MaximumMetadataBytes - framing)
            return Refuse(DiskImageRawProfileVerificationState.Invalid, "raw.metadata-policy-limit");
        byte[] bytes = new byte[framing + (int)envelope.ManifestLength];
        header.CopyTo(bytes, 0);
        if (!await ReadExactlyAsync(metadata, bytes.AsMemory(header.Length), cancellationToken).ConfigureAwait(false))
            return Refuse(DiskImageRawProfileVerificationState.Incomplete, "raw.truncated-metadata");
        if (await metadata.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            return Refuse(DiskImageRawProfileVerificationState.Corrupt, "raw.trailing-metadata");
        var admission = AdmitMetadata(bytes, envelope.ManifestLength);
        if (admission.Failure is not null) return admission.Failure;
        return await VerifyPayloadAsync(bytes, admission.Origin!, admission.PartId, payload, cancellationToken).ConfigureAwait(false);
    }

    private static (DiskImageCaptureOrigin? Origin, Guid PartId, DiskImageRawProfileVerificationAssessment? Failure)
        AdmitMetadata(byte[] bytes, uint manifestLength)
    {
        const int headerLength = DiskImageEnvelopeService.HeaderLength;
        if (manifestLength < DiskImageManifestVerificationService.ManifestHeaderLength)
            return (null, default, Refuse(DiskImageRawProfileVerificationState.Invalid, "raw.short-manifest"));
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(headerLength)) != 2)
            return (null, default, Refuse(DiskImageRawProfileVerificationState.Unsupported, "raw.schema-two-required"));
        ulong capacity = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(headerLength + 44));
        uint sector = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(headerLength + 52));
        if (capacity is 0 or > MaximumCapacity || sector is not (512 or 4096) || capacity % sector != 0)
            return (null, default, Refuse(DiskImageRawProfileVerificationState.Invalid, "raw.geometry-policy-limit"));
        if (!DiskImageOriginRecordCodec.TryDecode(bytes.AsSpan(headerLength + 64, (int)manifestLength - 64),
                capacity, sector, out var origin, out _, out bool unsupported))
            return (null, default, Refuse(unsupported ? DiskImageRawProfileVerificationState.Unsupported : DiskImageRawProfileVerificationState.Invalid,
                "raw.invalid-origin"));
        if (origin!.SourceKind != DiskImageSourceKind.OfflineBlockDevice)
            return (null, default, Refuse(DiskImageRawProfileVerificationState.Unsupported, "raw.block-origin-required"));
        if (origin.Layout.Partitions.Any(p => p.EntryNumber > MaximumGptEntries))
            return (null, default, Refuse(DiskImageRawProfileVerificationState.Unsupported, "raw.gpt-entry-policy-limit"));
        // The generic verifier obtains the sole part ID from bound metadata, never a path.
        int partOffset = headerLength + 64 + DiskImageOriginRecordCodec.HeaderLength + origin.Layout.Partitions.Count * 64;
        if (partOffset + DiskImageManifestVerificationService.PartRecordLength > headerLength + manifestLength)
            return (null, default, Refuse(DiskImageRawProfileVerificationState.Invalid, "raw.short-part-record"));
        Guid partId = new(bytes.AsSpan(partOffset, 16), bigEndian: true);
        return (origin, partId, null);
    }

    private static async ValueTask<DiskImageRawProfileVerificationAssessment> VerifyPayloadAsync(
        byte[] bytes, DiskImageCaptureOrigin origin, Guid partId, Stream payload, CancellationToken cancellationToken)
    {
        ulong capacity = origin.Layout.Capacity;
        uint sector = origin.Layout.LogicalSectorBytes;
        long payloadLength = payload.Length;
        if (payloadLength != (long)capacity)
            return Refuse(payloadLength < (long)capacity ? DiskImageRawProfileVerificationState.Incomplete
                : DiskImageRawProfileVerificationState.Corrupt, "raw.payload-length-mismatch");
        using var snapshot = new MemoryStream(bytes, writable: false);
        var verified = await DiskImageManifestVerificationService.VerifyWithinCapacityAsync(snapshot,
            new Dictionary<Guid, Stream> { [partId] = payload }, MaximumCapacity, cancellationToken).ConfigureAwait(false);
        if (verified.State != DiskImageManifestVerificationState.ContentVerified)
            return Refuse(verified.State switch
            {
                DiskImageManifestVerificationState.Unsupported => DiskImageRawProfileVerificationState.Unsupported,
                DiskImageManifestVerificationState.Incomplete => DiskImageRawProfileVerificationState.Incomplete,
                DiskImageManifestVerificationState.Corrupt => DiskImageRawProfileVerificationState.Corrupt,
                _ => DiskImageRawProfileVerificationState.Invalid
            }, verified.Reason);
        var inspected = await DiskImageGptInspectionService.InspectWithinEntryLimitAsync(payload, capacity, sector,
            MaximumGptEntries, cancellationToken).ConfigureAwait(false);
        if (inspected.State != DiskImageGptInspectionState.LayoutInspected)
            return Refuse(inspected.State switch
            {
                DiskImageGptInspectionState.Unsupported => DiskImageRawProfileVerificationState.Unsupported,
                DiskImageGptInspectionState.Incomplete => DiskImageRawProfileVerificationState.Incomplete,
                _ => DiskImageRawProfileVerificationState.Corrupt
            }, "raw.invalid-payload-gpt");
        DiskImageGptLayout actual = inspected.Layout!, expected = origin.Layout;
        if (actual.DiskId != expected.DiskId || actual.MetadataSha256 != expected.MetadataSha256 ||
            actual.FirstUsableLba != expected.FirstUsableLba || actual.LastUsableLba != expected.LastUsableLba ||
            !actual.Partitions.SequenceEqual(expected.Partitions))
            return Refuse(DiskImageRawProfileVerificationState.Corrupt, "raw.layout-claim-mismatch");
        return new(DiskImageRawProfileVerificationState.ContentAndLayoutVerified, "raw.observed-content-and-layout", verified);
    }


    private static DiskImageRawProfileVerificationAssessment Refuse(DiskImageRawProfileVerificationState state, string reason) => new(state, reason);

    private static async ValueTask<bool> ReadExactlyAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int count = await source.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0) return false;
            offset += count;
        }
        return true;
    }
}
