using VaultSync.Core.Models;
using System.Security.Cryptography;

namespace VaultSync.Core.Services;

public enum LinuxRawImageStagingState { Invalid, Unsupported, Refused, Interrupted, Failed, PublicationUnconfirmed, StagedVerified }

public sealed record LinuxRawImageStagingAssessment(LinuxRawImageStagingState State, string Reason,
    LinuxRawImageStagingSession? Session = null);

/// <summary>
/// Owns a private sealed staging copy. No device authorization or public writable
/// stream/handle/path; the caller must dispose it. Staging files are retained.
/// </summary>
public sealed class LinuxRawImageStagingSession : IAsyncDisposable
{
    private readonly LinuxRawImageStagingService.IStorage storage;
    public DiskImageManifestVerificationAssessment Image { get; }
    public uint LogicalSectorBytes => Inspection.LogicalSectorBytes;
    public uint PhysicalSectorBytes => Inspection.PhysicalSectorBytes;
    internal DiskImageInspectionObservation Inspection { get; }
    internal string MetadataSha256 { get; }
    internal ulong MetadataLength { get; }
    internal Stream Payload => storage.Payload;
    internal Stream Metadata => storage.Metadata;
    internal bool IsAlive => storage.Payload.CanRead && storage.Metadata.CanRead;
    internal IReadOnlyList<string> RepositoryDeviceNumbers { get; }
    internal bool RepositoryAncestryKnown { get; }
    internal LinuxRawImageStagingSession(LinuxRawImageStagingService.IStorage storage,
        DiskImageInspectionObservation inspection, string metadataSha256, ulong metadataLength,
        string? metadataDevice, string? payloadDevice)
    {
        // Accept only the canonical bounded result, never parser-owned graphs or
        // declared origin/tool/layout claims in the privileged consumer.
        _ = DiskImageInspectionProtocol.Result(Guid.NewGuid(), inspection);
        if (inspection.State != DiskImageRawProfileVerificationState.ContentAndLayoutVerified ||
            !RawImageRestoreJournalCodec.Hash(metadataSha256) || metadataLength > DiskImageRawProfileVerificationService.MaximumMetadataBytes)
            throw new InvalidDataException("raw-stage.invalid-binding");
        this.storage = storage;
        Inspection = inspection;
        MetadataSha256 = metadataSha256;
        MetadataLength = metadataLength;
        Image = new(DiskImageManifestVerificationState.ContentVerified, "raw-stage.isolated-byte-verification",
            inspection.VerifiedBytes, inspection.ImageId, inspection.CaptureId, inspection.Generation, inspection.ManifestRoot);
        var devices = new[] { metadataDevice, payloadDevice, storage.FilesystemDeviceNumber };
        RepositoryAncestryKnown = devices.All(value => value is not null);
        RepositoryDeviceNumbers = Array.AsReadOnly(devices.Where(value => value is not null)
            .Select(value => value!).Distinct(StringComparer.Ordinal).ToArray());
    }
    internal async ValueTask<bool> VerifyBytesAsync(CancellationToken token) =>
        IsAlive && !Payload.CanWrite && !Metadata.CanWrite &&
        await DiskImageByteIntegrity.HashExactAsync(Metadata, MetadataLength, token).ConfigureAwait(false) == MetadataSha256 &&
        await DiskImageByteIntegrity.HashExactAsync(Payload, Image.VerifiedBytes, token).ConfigureAwait(false) == Inspection.PayloadSha256;
    public ValueTask DisposeAsync() => storage.DisposeAsync();
}

/// <summary>
/// Copies and seals image bytes on Linux x64/ext4. Only the isolated reader parses
/// hostile structures. Never opens or authorizes a restore destination.
/// </summary>
public static class LinuxRawImageStagingService
{
    internal interface IStorage : LinuxRawImageCaptureService.ICaptureStorage
    {
        string? FilesystemDeviceNumber => null;
    }

    public static ValueTask<LinuxRawImageStagingAssessment> StageAsync(Stream metadata, Stream payload,
        string stagingParent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(payload);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromResult(new LinuxRawImageStagingAssessment(LinuxRawImageStagingState.Interrupted, "raw-stage.interrupted"));
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture !=
            System.Runtime.InteropServices.Architecture.X64)
            return ValueTask.FromResult(new LinuxRawImageStagingAssessment(LinuxRawImageStagingState.Unsupported, "raw-stage.linux-x64-required"));
        return StageAsync(metadata, payload, capacity => LinuxRawImageStorage.Create(stagingParent, Guid.NewGuid(), capacity),
            DiskImageInspectionWorkerClient.InspectAsync, cancellationToken);
    }

    internal static async ValueTask<LinuxRawImageStagingAssessment> StageAsync(Stream metadata, Stream payload,
        Func<ulong, IStorage> createStorage,
        Func<Stream, Stream, CancellationToken, Task<DiskImageInspectionWorkerResult>> inspect,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(createStorage);
        ArgumentNullException.ThrowIfNull(inspect);
        IStorage? storage = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!metadata.CanRead || !payload.CanRead || !payload.CanSeek || ReferenceEquals(metadata, payload))
                return Result(LinuxRawImageStagingState.Invalid, "raw-stage.invalid-streams");
            string? metadataDevice = LinuxRawImageStorage.TryFilesystemDeviceNumber(metadata);
            string? payloadDevice = LinuxRawImageStorage.TryFilesystemDeviceNumber(payload);
            byte[]? snapshot = await SnapshotAsync(metadata, cancellationToken).ConfigureAwait(false);
            if (snapshot is null) return Result(LinuxRawImageStagingState.Invalid, "raw-stage.metadata-limit");
            using var ownedMetadata = new MemoryStream(snapshot, writable: false);
            using var borrowedPayload = new BorrowedReadStream(payload);
            var initial = await InspectAsync(ownedMetadata, borrowedPayload, inspect, cancellationToken).ConfigureAwait(false);
            if (initial is null)
                return Result(LinuxRawImageStagingState.Refused, "raw-stage.input-refused");
            cancellationToken.ThrowIfCancellationRequested();
            storage = createStorage(initial.VerifiedBytes);
            await using var lease = new StorageLease(storage);
            if (!EmptyIndependentStorage(metadata, payload, storage))
                return Result(LinuxRawImageStagingState.Refused, "raw-stage.storage-refused");
            payload.Position = 0;
            if (!await CopyPayloadAsync(payload, storage.Payload, initial.VerifiedBytes, cancellationToken).ConfigureAwait(false))
                return Result(LinuxRawImageStagingState.Refused, "raw-stage.source-changed");
            await storage.Metadata.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
            storage.FlushPayload(); storage.FlushMetadata();
            storage.Metadata.Position = 0; storage.Payload.Position = 0;
            storage.Seal();
            // Native Seal closes all writable descriptors and reopens the SAME
            // private inodes read-only. A provider merely chmodding is insufficient.
            if (storage.Payload.CanWrite || storage.Metadata.CanWrite || !storage.Payload.CanRead || !storage.Metadata.CanRead)
                return Result(LinuxRawImageStagingState.Refused, "raw-stage.seal-refused");
            storage.Payload.Position = 0; storage.Metadata.Position = 0;
            var sealedImage = await InspectAsync(storage.Metadata, storage.Payload, inspect, cancellationToken).ConfigureAwait(false);
            if (sealedImage is null || sealedImage != initial)
                return Result(LinuxRawImageStagingState.Refused, "raw-stage.sealed-copy-refused");
            var session = new LinuxRawImageStagingSession(storage, sealedImage,
                Convert.ToHexStringLower(SHA256.HashData(snapshot)), (ulong)snapshot.Length, metadataDevice, payloadDevice);
            if (!await session.VerifyBytesAsync(cancellationToken).ConfigureAwait(false))
                return Result(LinuxRawImageStagingState.Refused, "raw-stage.sealed-bytes-changed");
            storage.Payload.Position = 0; storage.Metadata.Position = 0;
            storage.FlushDirectories();
            cancellationToken.ThrowIfCancellationRequested();
            storage.Publish();
            lease.Detach();
            return new(LinuxRawImageStagingState.StagedVerified, "raw-stage.staged", session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && storage?.Published != true)
        { return Result(LinuxRawImageStagingState.Interrupted, "raw-stage.interrupted"); }
        catch (InvalidDataException) { return Result(LinuxRawImageStagingState.Refused, "raw-stage.inspection-refused"); }
        catch (IOException) { return Failure(storage); }
        catch (UnauthorizedAccessException) { return Failure(storage); }
    }

    private static async Task<DiskImageInspectionObservation?> InspectAsync(Stream metadata, Stream payload,
        Func<Stream, Stream, CancellationToken, Task<DiskImageInspectionWorkerResult>> inspect, CancellationToken token)
    {
        metadata.Position = 0; payload.Position = 0;
        var result = await inspect(metadata, payload, token).ConfigureAwait(false);
        if (result.Outcome != DiskImageInspectionWorkerOutcome.Completed || result.Observation is not { } observation ||
            observation.State != DiskImageRawProfileVerificationState.ContentAndLayoutVerified)
            return null;
        // Local test providers have the same canonical summary/length obligation.
        Guid correlation = Guid.NewGuid();
        return DiskImageInspectionProtocol.DecodeResult(DiskImageInspectionProtocol.Result(correlation, observation),
            correlation, payload.Length);
    }

    // Non-owning view: mutable caller input is never represented as sealed.
    // Only the independently re-inspected private copy can become a session.
    internal sealed class BorrowedReadStream(Stream source) : Stream
    {
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => source.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            source.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static LinuxRawImageStagingAssessment Result(LinuxRawImageStagingState state, string reason) => new(state, reason);
    private static LinuxRawImageStagingAssessment Failure(IStorage? storage) => storage?.Published == true
        ? Result(LinuxRawImageStagingState.PublicationUnconfirmed, "raw-stage.publication-unconfirmed")
        : Result(LinuxRawImageStagingState.Failed, "raw-stage.io-failure");

    private static bool EmptyIndependentStorage(Stream metadata, Stream payload, IStorage storage) =>
        !ReferenceEquals(storage.Payload, storage.Metadata) && !ReferenceEquals(storage.Payload, metadata) &&
        !ReferenceEquals(storage.Payload, payload) && !ReferenceEquals(storage.Metadata, metadata) &&
        !ReferenceEquals(storage.Metadata, payload) && !storage.Published &&
        storage.Payload.CanRead && storage.Payload.CanSeek && storage.Payload.CanWrite &&
        storage.Metadata.CanRead && storage.Metadata.CanSeek && storage.Metadata.CanWrite &&
        storage.Payload.Length == 0 && storage.Metadata.Length == 0 &&
        storage.Payload.Position == 0 && storage.Metadata.Position == 0;

    private static async ValueTask<bool> CopyPayloadAsync(Stream source, Stream destination, ulong capacity, CancellationToken token)
    {
        byte[] buffer = new byte[128 * 1024];
        ulong copied = 0;
        while (copied < capacity)
        {
            int count = (int)Math.Min((ulong)buffer.Length, capacity - copied);
            int read = await source.ReadAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (read == 0) return false;
            await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            copied += (ulong)read;
        }
        return await source.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) == 0;
    }

    private static async ValueTask<byte[]?> SnapshotAsync(Stream source, CancellationToken token)
    {
        using var owned = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (owned.Length < DiskImageRawProfileVerificationService.MaximumMetadataBytes)
        {
            int count = (int)Math.Min(buffer.Length, DiskImageRawProfileVerificationService.MaximumMetadataBytes - owned.Length);
            int read = await source.ReadAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (read == 0) return owned.ToArray();
            await owned.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        return await source.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) == 0 ? owned.ToArray() : null;
    }

    private sealed class StorageLease(IStorage storage) : IAsyncDisposable
    {
        private bool detached;
        internal void Detach() => detached = true;
        public ValueTask DisposeAsync() => detached ? ValueTask.CompletedTask : storage.DisposeAsync();
    }
}
