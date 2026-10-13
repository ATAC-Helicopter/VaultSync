using System.Buffers.Binary;
using System.Security.Cryptography;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Copies a supplied synthetic stream to newly created temporary regular files, never to a device.</summary>
public static class DiskImageFileCaptureService
{
    public const string MetadataFileName = "image.metadata";
    public const string PayloadFileName = "payload.raw";
    private const int BufferLength = 128 * 1024;
    private const int ManifestLength = 164;

    public static async ValueTask<DiskImageFileCaptureAssessment> CaptureAsync(
        DiskImageFileCaptureRequest request, Stream source, CancellationToken cancellationToken = default)
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

        string? workspace = null;
        ulong written = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The caller cannot select output paths or overwrite existing files.
            workspace = Directory.CreateTempSubdirectory("vaultsync-draft-capture-").FullName;
            await using var metadata = NewFile(Path.Combine(workspace, MetadataFileName));
            await using var payload = NewFile(Path.Combine(workspace, PayloadFileName));
            byte[] header = EncodeHeader();
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
            byte[] manifest = EncodeManifest(request, digest.GetHashAndReset());
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

    private static byte[] EncodeHeader()
    {
        byte[] header = new byte[DiskImageEnvelopeService.HeaderLength];
        "VSIMAGE\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), ManifestLength);
        return header;
    }

    private static byte[] EncodeManifest(DiskImageFileCaptureRequest request, byte[] digest)
    {
        byte[] manifest = new byte[ManifestLength];
        BinaryPrimitives.WriteUInt32LittleEndian(manifest, 1);
        request.ImageId.TryWriteBytes(manifest.AsSpan(4, 16), bigEndian: true, out _);
        request.CaptureId.TryWriteBytes(manifest.AsSpan(20, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(manifest.AsSpan(36), request.Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(manifest.AsSpan(44), request.Capacity);
        BinaryPrimitives.WriteUInt32LittleEndian(manifest.AsSpan(52), request.LogicalSectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(manifest.AsSpan(56), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(manifest.AsSpan(60), 1);
        request.PartId.TryWriteBytes(manifest.AsSpan(64, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(manifest.AsSpan(80), request.Capacity);
        digest.CopyTo(manifest.AsSpan(88, 32));
        BinaryPrimitives.WriteUInt64LittleEndian(manifest.AsSpan(128), request.Capacity);
        request.PartId.TryWriteBytes(manifest.AsSpan(140, 16), bigEndian: true, out _);
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
