using System.Security.Cryptography;
using System.Runtime.InteropServices;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed record LinuxRawImageCaptureRequest(Guid ImageId, Guid CaptureId, Guid PartId,
    Guid SourceId, ulong Generation, string ToolVersion, string ToolSourceCommit);

public enum LinuxRawImageCaptureState
{
    Invalid, Unsupported, Refused, Incomplete, Interrupted, Failed, PublicationUnconfirmed, PublishedVerified
}

/// <summary>Capture observation only; no filesystem, boot or recovery qualification.</summary>
public sealed record LinuxRawImageCaptureAssessment(LinuxRawImageCaptureState State, string Reason,
    ulong CopiedBytes = 0, string? ImageDirectoryName = null, string? ManifestRoot = null);

/// <summary>
/// Same-privilege Linux x64 capture. Requires a kernel-read-only exclusive source;
/// opens no writable device. An authenticated worker and recovery UI remain separate.
/// </summary>
public static class LinuxRawImageCaptureService
{
    private const int BufferBytes = 128 * 1024;

    public static async ValueTask<LinuxRawImageCaptureAssessment> CaptureAsync(
        LinuxRawImageCaptureRequest request, LinuxBlockInventory expected, string kernelName,
        string outputParent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(expected);
        if (!Valid(request)) return Result(LinuxRawImageCaptureState.Invalid, "raw-capture.invalid-request");
        if (cancellationToken.IsCancellationRequested)
            return Result(LinuxRawImageCaptureState.Interrupted, "raw-capture.interrupted");
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return Result(LinuxRawImageCaptureState.Unsupported, "raw-capture.linux-x64-required");
        try
        {
            var opened = await LinuxBlockReadService.OpenAsync(expected, kernelName, cancellationToken).ConfigureAwait(false);
            if (opened.Source is null)
                return Result(LinuxRawImageCaptureState.Refused, "raw-capture.source-refused");
            await using var source = opened.Source;
            if (!source.Identity.IsReadOnly)
                return Result(LinuxRawImageCaptureState.Refused, "raw-capture.read-only-source-required");
            return await CaptureAsync(request, source, source.Identity.PhysicalSectorBytes, expected.ObservationId,
                () => LinuxRawImageStorage.Create(outputParent, request.ImageId, (ulong)source.Length),
                DiskGptInspectionWorkerClient.InspectAsync, DiskImageInspectionWorkerClient.InspectAsync,
                cancellationToken, source.Identity.LogicalSectorBytes).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Result(LinuxRawImageCaptureState.Interrupted, "raw-capture.interrupted"); }
    }

    internal interface IStorage : IAsyncDisposable
    {
        Stream Payload { get; }
        Stream Metadata { get; }
        bool Published { get; }
        void FlushPayload();
        void FlushMetadata();
        void FlushDirectories();
        void Publish();
    }

    internal interface ICaptureStorage : IStorage { void Seal(); }

    // Only the public entry can acquire a device. This seam uses caller-held fixture
    // streams and storage for deterministic I/O faults; it is not a worker protocol.
    internal static async ValueTask<LinuxRawImageCaptureAssessment> CaptureAsync(
        LinuxRawImageCaptureRequest request, Stream source, uint physicalSectorBytes, Guid observationId,
        Func<ICaptureStorage> createStorage,
        Func<Stream, uint, CancellationToken, Task<DiskGptInspectionWorkerResult>> inspectLayout,
        Func<Stream, Stream, CancellationToken, Task<DiskImageInspectionWorkerResult>> inspectImage,
        CancellationToken cancellationToken = default,
        uint logicalSectorBytes = 512)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(createStorage);
        ArgumentNullException.ThrowIfNull(inspectLayout);
        ArgumentNullException.ThrowIfNull(inspectImage);
        if (!Valid(request) || observationId == Guid.Empty ||
            physicalSectorBytes is not (512 or 4096) || logicalSectorBytes is not (512 or 4096) ||
            physicalSectorBytes < logicalSectorBytes)
            return Result(LinuxRawImageCaptureState.Invalid, "raw-capture.invalid-request");
        ICaptureStorage? storage = null;
        ulong copied = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.CanRead || !source.CanSeek || source.Position != 0 || source.Length <= 0 ||
                (ulong)source.Length > DiskImageRawProfileVerificationService.MaximumCapacity ||
                (ulong)source.Length % logicalSectorBytes != 0)
                return Result(LinuxRawImageCaptureState.Invalid, "raw-capture.invalid-source");
            ulong capacity = (ulong)source.Length;
            using var sourceView = new LinuxRawImageStagingService.BorrowedReadStream(source);
            var layout = await InspectLayoutAsync(sourceView, logicalSectorBytes, inspectLayout, cancellationToken).ConfigureAwait(false);
            if (layout is null)
                return Result(LinuxRawImageCaptureState.Refused, "raw-capture.source-layout-refused");
            var origin = new DiskImageCaptureOrigin(request.SourceId, observationId, physicalSectorBytes,
                DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                request.ToolVersion, request.ToolSourceCommit, layout)
                { SourceKind = DiskImageSourceKind.OfflineBlockDevice };
            cancellationToken.ThrowIfCancellationRequested();
            storage = createStorage();
            await using var storageLifetime = storage;
            if (!IndependentEmptyStorage(source, storage))
                return Result(LinuxRawImageCaptureState.Refused, "raw-capture.storage-refused");
            source.Position = 0; // The separate reader's byte oracle changes the held stream position.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[BufferBytes];
            while (copied < capacity)
            {
                int count = (int)Math.Min((ulong)buffer.Length, capacity - copied);
                int read = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return new(LinuxRawImageCaptureState.Incomplete, "raw-capture.truncated-source", copied);
                await storage.Payload.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                copied += (ulong)read;
            }
            // Includes a held-device binding check at EOF, not just length arithmetic.
            if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
                return new(LinuxRawImageCaptureState.Refused, "raw-capture.source-length-changed", copied);
            var after = await InspectLayoutAsync(sourceView, logicalSectorBytes, inspectLayout, cancellationToken).ConfigureAwait(false);
            if (after?.MetadataSha256 != origin.Layout.MetadataSha256)
                return new(LinuxRawImageCaptureState.Refused, "raw-capture.source-layout-changed", copied);
            storage.FlushPayload();
            string payloadHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            var encoded = DiskImageRawMetadataEncodingService.Encode(new(request.ImageId, request.CaptureId,
                request.PartId, request.Generation, origin, payloadHash));
            if (encoded.Metadata is null)
                return new(LinuxRawImageCaptureState.Refused, "raw-capture.metadata-refused", copied);
            await storage.Metadata.WriteAsync(encoded.Metadata, cancellationToken).ConfigureAwait(false);
            storage.FlushMetadata();
            storage.Seal();
            if (storage.Payload.CanWrite || storage.Metadata.CanWrite ||
                !await VerifySealedAsync(storage, request, encoded, capacity, logicalSectorBytes,
                    physicalSectorBytes, payloadHash, inspectImage, cancellationToken).ConfigureAwait(false))
                return new(LinuxRawImageCaptureState.Refused, "raw-capture.readback-refused", copied);
            storage.FlushDirectories();
            cancellationToken.ThrowIfCancellationRequested();
            // Commit point. Publish is synchronous rename-no-replace plus parent fsync;
            // cancellation after rename cannot retroactively turn publication into cancellation.
            storage.Publish();
            return new(LinuxRawImageCaptureState.PublishedVerified, "raw-capture.published", copied,
                request.ImageId.ToString("N"), encoded.ManifestRoot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && storage?.Published != true)
        { return new(LinuxRawImageCaptureState.Interrupted, "raw-capture.interrupted", copied); }
        catch (Exception error) when (error is IOException or InvalidDataException or ObjectDisposedException)
        { return Failure(storage, copied); }
        catch (UnauthorizedAccessException)
        { return Failure(storage, copied); }
    }

    private static async Task<DiskImageGptLayout?> InspectLayoutAsync(Stream source, uint sector,
        Func<Stream, uint, CancellationToken, Task<DiskGptInspectionWorkerResult>> inspect, CancellationToken token)
    {
        long capacity = source.Length;
        var result = await inspect(source, sector, token).ConfigureAwait(false);
        if (result.Outcome != DiskImageInspectionWorkerOutcome.Completed ||
            result.Assessment is not { State: DiskImageGptInspectionState.LayoutInspected } assessment)
            return null;
        Guid operation = Guid.NewGuid();
        return DiskGptInspectionProtocol.DecodeResult(DiskGptInspectionProtocol.Result(operation, assessment),
            operation, capacity, sector).Layout;
    }

    private static async Task<bool> VerifySealedAsync(IStorage storage, LinuxRawImageCaptureRequest request,
        DiskImageRawMetadataEncodingAssessment encoded, ulong capacity, uint logical, uint physical, string payloadHash,
        Func<Stream, Stream, CancellationToken, Task<DiskImageInspectionWorkerResult>> inspect, CancellationToken token)
    {
        storage.Metadata.Position = 0; storage.Payload.Position = 0;
        var result = await inspect(storage.Metadata, storage.Payload, token).ConfigureAwait(false);
        if (result.Outcome != DiskImageInspectionWorkerOutcome.Completed ||
            result.Observation is not { State: DiskImageRawProfileVerificationState.ContentAndLayoutVerified } observation)
            return false;
        Guid operation = Guid.NewGuid();
        var verified = DiskImageInspectionProtocol.DecodeResult(DiskImageInspectionProtocol.Result(operation, observation),
            operation, checked((long)capacity));
        return verified.ImageId == request.ImageId && verified.CaptureId == request.CaptureId &&
            verified.Generation == request.Generation && verified.ManifestRoot == encoded.ManifestRoot &&
            verified.LogicalSectorBytes == logical && verified.PhysicalSectorBytes == physical && verified.PayloadSha256 == payloadHash &&
            await DiskImageByteIntegrity.HashExactAsync(storage.Metadata, (ulong)encoded.Metadata!.Length, token).ConfigureAwait(false) ==
                Convert.ToHexStringLower(SHA256.HashData(encoded.Metadata)) &&
            await DiskImageByteIntegrity.HashExactAsync(storage.Payload, capacity, token).ConfigureAwait(false) == payloadHash;
    }

    private static LinuxRawImageCaptureAssessment Failure(IStorage? storage, ulong copied) => storage?.Published == true
        ? new(LinuxRawImageCaptureState.PublicationUnconfirmed, "raw-capture.publication-unconfirmed", copied)
        : new(LinuxRawImageCaptureState.Failed, "raw-capture.io-failure", copied);

    private static LinuxRawImageCaptureAssessment Result(LinuxRawImageCaptureState state, string reason) => new(state, reason);

    private static bool Valid(LinuxRawImageCaptureRequest request) => request.ImageId != Guid.Empty &&
        request.CaptureId != Guid.Empty && request.PartId != Guid.Empty && request.SourceId != Guid.Empty &&
        request.Generation > 0 && DiskImageOriginRecordCodec.ValidTool(request.ToolVersion, request.ToolSourceCommit);

    private static bool IndependentEmptyStorage(Stream source, IStorage storage) =>
        !ReferenceEquals(source, storage.Payload) && !ReferenceEquals(source, storage.Metadata) &&
        !ReferenceEquals(storage.Payload, storage.Metadata) && !storage.Published &&
        storage.Payload.CanRead && storage.Payload.CanWrite && storage.Payload.CanSeek &&
        storage.Metadata.CanRead && storage.Metadata.CanWrite && storage.Metadata.CanSeek &&
        storage.Payload.Position == 0 && storage.Metadata.Position == 0 &&
        storage.Payload.Length == 0 && storage.Metadata.Length == 0;
}
