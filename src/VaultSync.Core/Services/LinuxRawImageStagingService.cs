using VaultSync.Core.Models;

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
    internal Stream Payload => storage.Payload;
    internal Stream Metadata => storage.Metadata;
    internal bool IsAlive => storage.Payload.CanRead && storage.Metadata.CanRead;
    internal IReadOnlyList<string> RepositoryDeviceNumbers { get; }
    internal bool RepositoryAncestryKnown { get; }
    internal LinuxRawImageStagingSession(LinuxRawImageStagingService.IStorage storage,
        DiskImageManifestVerificationAssessment image, string? metadataDevice, string? payloadDevice)
    {
        this.storage = storage;
        Image = image;
        var devices = new[] { metadataDevice, payloadDevice, storage.FilesystemDeviceNumber };
        RepositoryAncestryKnown = devices.All(value => value is not null);
        RepositoryDeviceNumbers = Array.AsReadOnly(devices.Where(value => value is not null)
            .Select(value => value!).Distinct(StringComparer.Ordinal).ToArray());
    }
    public ValueTask DisposeAsync() => storage.DisposeAsync();
}

/// <summary>
/// Copies and verifies hostile caller-held image streams before sealing Linux
/// x64/ext4 storage. Never opens or authorizes a restore destination.
/// </summary>
public static class LinuxRawImageStagingService
{
    internal interface IStorage : LinuxRawImageCaptureService.IStorage
    {
        void Seal();
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
        return StageAsync(metadata, payload, capacity => LinuxRawImageStorage.Create(stagingParent, Guid.NewGuid(), capacity), cancellationToken);
    }

    internal static async ValueTask<LinuxRawImageStagingAssessment> StageAsync(Stream metadata, Stream payload,
        Func<ulong, IStorage> createStorage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(createStorage);
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
            var initial = await DiskImageRawProfileVerificationService.VerifyAsync(ownedMetadata, payload, cancellationToken).ConfigureAwait(false);
            if (initial.State != DiskImageRawProfileVerificationState.ContentAndLayoutVerified)
                return Result(LinuxRawImageStagingState.Refused, "raw-stage.input-refused");
            cancellationToken.ThrowIfCancellationRequested();
            storage = createStorage(initial.Image!.VerifiedBytes);
            await using var lease = new StorageLease(storage);
            if (!EmptyIndependentStorage(metadata, payload, storage))
                return Result(LinuxRawImageStagingState.Refused, "raw-stage.storage-refused");
            payload.Position = 0;
            if (!await CopyPayloadAsync(payload, storage.Payload, initial.Image.VerifiedBytes, cancellationToken).ConfigureAwait(false))
                return Result(LinuxRawImageStagingState.Refused, "raw-stage.source-changed");
            await storage.Metadata.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
            storage.FlushPayload(); storage.FlushMetadata();
            storage.Metadata.Position = 0; storage.Payload.Position = 0;
            var staged = await DiskImageRawProfileVerificationService.VerifyAsync(storage.Metadata, storage.Payload, cancellationToken).ConfigureAwait(false);
            if (staged.State != DiskImageRawProfileVerificationState.ContentAndLayoutVerified ||
                staged.Image!.ManifestRoot != initial.Image.ManifestRoot)
                return Result(LinuxRawImageStagingState.Refused, "raw-stage.copy-refused");
            storage.Seal();
            // Native Seal closes all writable descriptors and reopens the SAME
            // private inodes read-only. A provider merely chmodding is insufficient.
            if (storage.Payload.CanWrite || storage.Metadata.CanWrite || !storage.Payload.CanRead || !storage.Metadata.CanRead)
                return Result(LinuxRawImageStagingState.Refused, "raw-stage.seal-refused");
            storage.Payload.Position = 0; storage.Metadata.Position = 0;
            var sealedImage = await DiskImageRawProfileVerificationService.VerifyAsync(storage.Metadata, storage.Payload, cancellationToken).ConfigureAwait(false);
            if (sealedImage.State != DiskImageRawProfileVerificationState.ContentAndLayoutVerified ||
                sealedImage.Image!.ManifestRoot != staged.Image.ManifestRoot)
                return Result(LinuxRawImageStagingState.Refused, "raw-stage.sealed-copy-refused");
            storage.Payload.Position = 0; storage.Metadata.Position = 0;
            storage.FlushDirectories();
            cancellationToken.ThrowIfCancellationRequested();
            storage.Publish();
            var session = new LinuxRawImageStagingSession(storage, sealedImage.Image!, metadataDevice, payloadDevice);
            lease.Detach();
            return new(LinuxRawImageStagingState.StagedVerified, "raw-stage.staged", session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && storage?.Published != true)
        { return Result(LinuxRawImageStagingState.Interrupted, "raw-stage.interrupted"); }
        catch (IOException) { return Failure(storage); }
        catch (UnauthorizedAccessException) { return Failure(storage); }
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
