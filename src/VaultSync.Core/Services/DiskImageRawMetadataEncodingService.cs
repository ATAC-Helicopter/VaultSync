using System.Buffers.Binary;
using System.Security.Cryptography;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed record DiskImageRawMetadataRequest(Guid ImageId, Guid CaptureId, Guid PartId,
    ulong Generation, DiskImageCaptureOrigin Origin, string PayloadSha256);

public enum DiskImageRawMetadataEncodingState { Invalid, MetadataEncoded }

/// <summary>Caller-owned serialized bytes only, never completed capture or durable publication.</summary>
public sealed record DiskImageRawMetadataEncodingAssessment(DiskImageRawMetadataEncodingState State,
    byte[]? Metadata = null, string? ManifestRoot = null);

/// <summary>Pure bounded encoder for the frozen raw-block profile; opens and writes no streams or paths.</summary>
public static class DiskImageRawMetadataEncodingService
{
    public static DiskImageRawMetadataEncodingAssessment Encode(DiskImageRawMetadataRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ImageId == Guid.Empty || request.CaptureId == Guid.Empty || request.PartId == Guid.Empty ||
            request.Generation == 0 || request.Origin?.Layout?.Partitions is null ||
            request.PayloadSha256 is not { Length: 64 } ||
            request.PayloadSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return new(DiskImageRawMetadataEncodingState.Invalid);
        // Own a bounded copy before validating and encoding caller-supplied partition claims.
        var partitions = request.Origin.Layout.Partitions.Take(DiskImageRawProfileVerificationService.MaximumGptEntries + 1).ToArray();
        var origin = request.Origin with { Layout = request.Origin.Layout with { Partitions = Array.AsReadOnly(partitions) } };
        if (origin.SourceKind != DiskImageSourceKind.OfflineBlockDevice ||
            origin.Layout.Capacity > DiskImageRawProfileVerificationService.MaximumCapacity ||
            partitions.Length > DiskImageRawProfileVerificationService.MaximumGptEntries ||
            partitions.Any(p => p is null || p.EntryNumber > DiskImageRawProfileVerificationService.MaximumGptEntries) ||
            !DiskImageOriginRecordCodec.Valid(origin))
            return new(DiskImageRawMetadataEncodingState.Invalid);

        byte[] encodedOrigin = DiskImageOriginRecordCodec.Encode(origin);
        int manifestLength = DiskImageManifestVerificationService.ManifestHeaderLength + encodedOrigin.Length +
            DiskImageManifestVerificationService.PartRecordLength + DiskImageManifestVerificationService.ExtentRecordLength;
        byte[] bytes = new byte[DiskImageEnvelopeService.HeaderLength + manifestLength + DiskImageManifestVerificationService.CompletionLength];
        "VSIMAGE\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 1);
        Put32(bytes, 16, (uint)manifestLength);
        Span<byte> manifest = bytes.AsSpan(DiskImageEnvelopeService.HeaderLength, manifestLength);
        Put32(manifest, 0, 2);
        PutGuid(manifest, 4, request.ImageId);
        PutGuid(manifest, 20, request.CaptureId);
        Put64(manifest, 36, request.Generation);
        Put64(manifest, 44, origin.Layout.Capacity);
        Put32(manifest, 52, origin.Layout.LogicalSectorBytes);
        Put32(manifest, 56, 1);
        Put32(manifest, 60, 1);
        encodedOrigin.CopyTo(manifest[DiskImageManifestVerificationService.ManifestHeaderLength..]);
        int part = DiskImageManifestVerificationService.ManifestHeaderLength + encodedOrigin.Length;
        PutGuid(manifest, part, request.PartId);
        Put64(manifest, part + 16, origin.Layout.Capacity);
        Convert.FromHexString(request.PayloadSha256).CopyTo(manifest[(part + 24)..]);
        int extent = part + DiskImageManifestVerificationService.PartRecordLength;
        Put64(manifest, extent + 8, origin.Layout.Capacity);
        PutGuid(manifest, extent + 20, request.PartId);
        byte[] root = SHA256.HashData(manifest);
        Span<byte> completion = bytes.AsSpan(DiskImageEnvelopeService.HeaderLength + manifestLength);
        "VSCOMP\0\0"u8.CopyTo(completion);
        PutGuid(completion, 8, request.ImageId);
        Put64(completion, 24, request.Generation);
        root.CopyTo(completion[32..]);
        return new(DiskImageRawMetadataEncodingState.MetadataEncoded, bytes, Convert.ToHexStringLower(root));
    }

    private static void Put32(Span<byte> bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes[offset..], value);
    private static void Put64(Span<byte> bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes[offset..], value);
    private static void PutGuid(Span<byte> bytes, int offset, Guid value) => value.TryWriteBytes(bytes.Slice(offset, 16), bigEndian: true, out _);
}
