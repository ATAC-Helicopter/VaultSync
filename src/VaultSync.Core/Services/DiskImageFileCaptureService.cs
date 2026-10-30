using System.Buffers.Binary;
using System.Security.Cryptography;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Copies a supplied synthetic stream to newly created temporary regular files, never to a device.</summary>
public static class DiskImageFileCaptureService
{
    public const string MetadataFileName = "image.metadata";
    public const string PayloadFileName = "payload.raw";
    public const ulong MaximumFixtureBytes = DiskImageFileReconstructionService.MaximumFixtureBytes;
    private const int BufferLength = 128 * 1024;
    private const int ManifestLength = 164;

    public static ValueTask<DiskImageFileCaptureAssessment> CaptureAsync(
        DiskImageFileCaptureRequest request, Stream source, CancellationToken cancellationToken = default) =>
        CaptureCoreAsync(request, source, null, cancellationToken);

    public static async ValueTask<DiskImageFileCaptureAssessment> CaptureGptFixtureAsync(
        DiskImageGptFileCaptureRequest request, Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Image);
        ArgumentNullException.ThrowIfNull(source);
        if (request.Image.Capacity > MaximumFixtureBytes)
            return new(DiskImageFileCaptureState.Invalid, "capture.fixture-size-limit");
        if (request.Image.ImageId == Guid.Empty || request.Image.CaptureId == Guid.Empty || request.Image.PartId == Guid.Empty ||
            request.Image.Generation == 0 || request.Image.Capacity == 0 ||
            request.Image.LogicalSectorBytes is not (512 or 4096) || request.Image.Capacity % request.Image.LogicalSectorBytes != 0 ||
            request.SourceId == Guid.Empty || request.ObservationId == Guid.Empty || request.PhysicalSectorBytes is not (512 or 4096) ||
            request.PhysicalSectorBytes < request.Image.LogicalSectorBytes ||
            !DiskImageOriginRecordCodec.ValidTool(request.ToolVersion, request.ToolSourceCommit))
            return new(DiskImageFileCaptureState.Invalid, "capture.invalid-origin");
        try
        {
            if (!source.CanSeek || source.Position != 0)
                return new(DiskImageFileCaptureState.Invalid, "capture.gpt-source-position");
            var inspection = await DiskImageGptInspectionService.InspectAsync(source,
                request.Image.Capacity, request.Image.LogicalSectorBytes, cancellationToken).ConfigureAwait(false);
            if (inspection.Layout is null)
                return new(inspection.State == DiskImageGptInspectionState.Unsupported
                    ? DiskImageFileCaptureState.Unsupported : DiskImageFileCaptureState.Invalid, "capture.invalid-gpt-source");
            var origin = new DiskImageCaptureOrigin(request.SourceId, request.ObservationId, request.PhysicalSectorBytes,
                DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                request.ToolVersion, request.ToolSourceCommit, inspection.Layout);
            if (!DiskImageOriginRecordCodec.Valid(origin))
                return new(DiskImageFileCaptureState.Invalid, "capture.invalid-origin");
            return await CaptureCoreAsync(request.Image, source, origin, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(DiskImageFileCaptureState.Interrupted, "capture.interrupted"); }
        catch (IOException) { return new(DiskImageFileCaptureState.Failed, "capture.io-failure"); }
        catch (UnauthorizedAccessException) { return new(DiskImageFileCaptureState.Failed, "capture.storage-denied"); }
    }

    private static async ValueTask<DiskImageFileCaptureAssessment> CaptureCoreAsync(
        DiskImageFileCaptureRequest request, Stream source, DiskImageCaptureOrigin? origin, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || request.ImageId == Guid.Empty || request.CaptureId == Guid.Empty ||
            request.PartId == Guid.Empty || request.Generation == 0 || request.Capacity == 0 ||
            request.Capacity > long.MaxValue)
            return new(DiskImageFileCaptureState.Invalid, "capture.invalid-request");
        if (request.LogicalSectorBytes is not (512 or 4096))
            return new(DiskImageFileCaptureState.Unsupported, "capture.unsupported-sector-size");
        if (request.Capacity % request.LogicalSectorBytes != 0)
            return new(DiskImageFileCaptureState.Invalid, "capture.invalid-capacity");
        if (request.Capacity > MaximumFixtureBytes)
            return new(DiskImageFileCaptureState.Invalid, "capture.fixture-size-limit");

        string? workspace = null;
        ulong written = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The caller cannot select output paths or overwrite existing files.
            workspace = Directory.CreateTempSubdirectory("vaultsync-draft-capture-").FullName;
            await using var metadata = NewFile(Path.Combine(workspace, MetadataFileName));
            await using var payload = new FileStream(Path.Combine(workspace, PayloadFileName), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, BufferLength, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] originBytes = origin is null ? [] : DiskImageOriginRecordCodec.Encode(origin);
            byte[] header = EncodeHeader(ManifestLength + originBytes.Length);
            await metadata.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            metadata.Flush(flushToDisk: true);

            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[BufferLength];
            while (written < request.Capacity)
            {
                int count = (int)Math.Min((ulong)buffer.Length, request.Capacity - written);
                int read = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return new(DiskImageFileCaptureState.Incomplete, "capture.truncated-source", written, workspace);
                await payload.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                digest.AppendData(buffer, 0, read);
                written += (ulong)read;
            }
            if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
                return new(DiskImageFileCaptureState.Invalid, "capture.trailing-source-bytes", written, workspace);

            cancellationToken.ThrowIfCancellationRequested();
            payload.Flush(flushToDisk: true);
            if (origin is not null)
            {
                var copied = await DiskImageGptInspectionService.InspectAsync(payload, request.Capacity,
                    request.LogicalSectorBytes, cancellationToken).ConfigureAwait(false);
                if (copied.Layout?.MetadataSha256 != origin.Layout.MetadataSha256)
                    return new(DiskImageFileCaptureState.Invalid, "capture.source-layout-changed", written, workspace);
            }
            byte[] manifest = EncodeManifest(request, digest.GetHashAndReset(), originBytes);
            byte[] root = SHA256.HashData(manifest);
            await metadata.WriteAsync(manifest, cancellationToken).ConfigureAwait(false);
            metadata.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            // After final publication begins, finish it rather than reporting cancellation as rollback.
            byte[] completion = EncodeCompletion(request, root);
            await metadata.WriteAsync(completion, CancellationToken.None).ConfigureAwait(false);
            metadata.Flush(flushToDisk: true);
            return new(DiskImageFileCaptureState.FileFixtureCaptured, "capture.file-fixture-only", written,
                workspace, Convert.ToHexStringLower(root));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(DiskImageFileCaptureState.Interrupted, "capture.interrupted", written, workspace);
        }
        catch (IOException)
        {
            return new(DiskImageFileCaptureState.Failed, "capture.io-failure", written, workspace);
        }
        catch (UnauthorizedAccessException)
        {
            return new(DiskImageFileCaptureState.Failed, "capture.storage-denied", written, workspace);
        }
    }

    private static FileStream NewFile(string path) => new(path, FileMode.CreateNew, FileAccess.Write,
        FileShare.None, BufferLength, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static byte[] EncodeHeader(int manifestLength)
    {
        byte[] header = new byte[DiskImageEnvelopeService.HeaderLength];
        "VSIMAGE\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)manifestLength);
        return header;
    }

    private static byte[] EncodeManifest(DiskImageFileCaptureRequest request, byte[] digest, byte[] origin)
    {
        byte[] manifest = new byte[ManifestLength + origin.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(manifest, origin.Length == 0 ? 1U : 2U);
        request.ImageId.TryWriteBytes(manifest.AsSpan(4, 16), bigEndian: true, out _);
        request.CaptureId.TryWriteBytes(manifest.AsSpan(20, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(manifest.AsSpan(36), request.Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(manifest.AsSpan(44), request.Capacity);
        BinaryPrimitives.WriteUInt32LittleEndian(manifest.AsSpan(52), request.LogicalSectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(manifest.AsSpan(56), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(manifest.AsSpan(60), 1);
        origin.CopyTo(manifest, 64);
        int extra = origin.Length;
        request.PartId.TryWriteBytes(manifest.AsSpan(64 + extra, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(manifest.AsSpan(80 + extra), request.Capacity);
        digest.CopyTo(manifest.AsSpan(88 + extra, 32));
        BinaryPrimitives.WriteUInt64LittleEndian(manifest.AsSpan(128 + extra), request.Capacity);
        request.PartId.TryWriteBytes(manifest.AsSpan(140 + extra, 16), bigEndian: true, out _);
        return manifest;
    }

    private static byte[] EncodeCompletion(DiskImageFileCaptureRequest request, byte[] root)
    {
        byte[] completion = new byte[DiskImageManifestVerificationService.CompletionLength];
        "VSCOMP\0\0"u8.CopyTo(completion);
        request.ImageId.TryWriteBytes(completion.AsSpan(8, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(completion.AsSpan(24), request.Generation);
        root.CopyTo(completion.AsSpan(32));
        return completion;
    }
}
