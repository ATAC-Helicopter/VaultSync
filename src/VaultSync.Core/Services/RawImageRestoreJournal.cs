using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Owned private journal; no target opener, resume capability or remote reporting.</summary>
internal sealed class RawImageRestoreJournal : RawImageDeviceRestoreService.IJournal, IAsyncDisposable
{
    internal interface IStorage : LinuxRawImageCaptureService.IStorage { string? FilesystemDeviceNumber { get; } }
    private readonly IStorage storage;
    private readonly RawImageRestoreJournalCodec.Cursor cursor;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool poisoned, disposed;

    private RawImageRestoreJournal(IStorage storage,
        RawImageDeviceRestoreRequest request, byte[] header)
    { this.storage = storage; cursor = new(request, header); }

    internal static ValueTask<RawImageRestoreJournal> CreateAsync(string parent, RawImageDeviceRestoreRequest request,
        LinuxBlockInventory expected, CancellationToken token = default) => CreateAsync(request, expected,
            (reserve, allow) => LinuxRawImageStorage.CreateRestoreJournal(parent, request.OperationId, reserve, allow),
            LinuxBlockInventoryService.ObserveAsync, token);

    // Internal file/fault seam; never selected by a wire request or image.
    internal static async ValueTask<RawImageRestoreJournal> CreateAsync(RawImageDeviceRestoreRequest request,
        LinuxBlockInventory expected, Func<ulong, Func<string?, bool>, IStorage> create,
        Func<CancellationToken, ValueTask<LinuxBlockInventory>> observe, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        byte[] header = RawImageRestoreJournalCodec.Header(request);
        if (!Current(expected, request.Target)) throw new InvalidDataException("restore-journal.invalid-inventory");
        var before = await observe(token).ConfigureAwait(false);
        if (before.BootId != expected.BootId || before.MountNamespace != expected.MountNamespace || !Current(before, request.Target))
            throw new InvalidDataException("restore-journal.changed-inventory");
        var storage = create(RawImageRestoreJournalCodec.Reservation(request.Target.Capacity),
            number => OutsideTarget(before, request.Target, number));
        bool transferred = false;
        try
        {
            if (storage.Published || !storage.Payload.CanWrite || !storage.Metadata.CanWrite ||
                !storage.Payload.CanSeek || !storage.Metadata.CanSeek || storage.Payload.Length != 0 ||
                storage.Metadata.Length != 0 || storage.Payload.Position != 0 || storage.Metadata.Position != 0 ||
                ReferenceEquals(storage.Payload, storage.Metadata))
                throw new InvalidDataException("restore-journal.invalid-storage");
            var fresh = await observe(token).ConfigureAwait(false);
            if (fresh.BootId != expected.BootId || fresh.MountNamespace != expected.MountNamespace ||
                !Current(fresh, request.Target) || !OutsideTarget(fresh, request.Target, storage.FilesystemDeviceNumber) ||
                RepositoryDisk(before, request.Target, storage.FilesystemDeviceNumber) !=
                RepositoryDisk(fresh, request.Target, storage.FilesystemDeviceNumber))
                throw new InvalidDataException("restore-journal.changed-topology");
            await storage.Metadata.WriteAsync(header, token).ConfigureAwait(false);
            storage.FlushMetadata(); storage.FlushPayload(); storage.FlushDirectories();
            token.ThrowIfCancellationRequested();
            storage.Publish();
            var journal = new RawImageRestoreJournal(storage, request, header);
            transferred = true;
            return journal;
        }
        finally { if (!transferred) await storage.DisposeAsync().ConfigureAwait(false); }
    }

    internal static bool Current(LinuxBlockInventory inventory, LinuxBlockObservation target)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return inventory.State == LinuxBlockInventoryState.Observed &&
            inventory.Reason == LinuxBlockInventoryReason.NamespaceObservationOnly && !inventory.HasUnresolvedSwaps &&
            inventory.ObservationId != Guid.Empty && inventory.BootId != Guid.Empty &&
            inventory.MountNamespace is { Length: >= 7 and <= 32 } && inventory.ObservedAtUtc <= now &&
            now - inventory.ObservedAtUtc <= TimeSpan.FromSeconds(30) &&
            inventory.Devices is { Count: > 0 and <= LinuxBlockInventoryService.MaximumDevices } &&
            inventory.Devices.All(d => d is not null) &&
            inventory.Devices.Select(d => d.KernelName).Distinct(StringComparer.Ordinal).Count() == inventory.Devices.Count &&
            inventory.Devices.Select(d => d.DeviceNumber).Distinct(StringComparer.Ordinal).Count() == inventory.Devices.Count &&
            inventory.Devices.SingleOrDefault(d => d.KernelName == target.KernelName) == target;
    }

    internal static bool OutsideTarget(LinuxBlockInventory inventory, LinuxBlockObservation target, string? number)
    {
        var disk = RepositoryDisk(inventory, target, number);
        return disk is not null && disk.KernelName != target.KernelName && disk.DeviceNumber != target.DeviceNumber;
    }

    private static LinuxBlockObservation? RepositoryDisk(LinuxBlockInventory inventory, LinuxBlockObservation target, string? number)
    {
        if (number is null || !Current(inventory, target)) return null;
        var repository = inventory.Devices.SingleOrDefault(d => d.DeviceNumber == number);
        if (repository is null) return null;
        var disk = repository.Kind == LinuxBlockKind.Partition && repository.ParentKernelName is not null
            ? inventory.Devices.SingleOrDefault(d => d.KernelName == repository.ParentKernelName) : repository;
        return disk is { Kind: LinuxBlockKind.WholeDisk, ParentKernelName: null,
            Identity: LinuxBlockIdentity.UniqueInSnapshot, DiskSequence: > 0 } ? disk : null;
    }

    public async ValueTask AppendDurableAsync(RawImageRestoreRecord record, CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (disposed || poisoned) throw new IOException("restore-journal.closed-or-unconfirmed");
            ulong expectedLength = cursor.Sequence * RawImageRestoreJournalCodec.RecordBytes;
            if ((ulong)storage.Payload.Length != expectedLength || (ulong)storage.Payload.Position != expectedLength)
            { poisoned = true; throw new IOException("restore-journal.changed-stream"); }
            byte[] frame = cursor.Encode(record);
            token.ThrowIfCancellationRequested();
            try
            {
                await storage.Payload.WriteAsync(frame, token).ConfigureAwait(false);
                storage.FlushPayload();
                cursor.Accept(frame); // Advance ONLY after confirmed durable append.
            }
            catch { poisoned = true; throw; }
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await storage.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
