#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRawRestoreTargetOpenServiceTests
{
    private sealed class Storage : LinuxRawImageStagingService.IStorage, RawImageRestoreJournal.IStorage
    {
        private MemoryStream payload = new(), metadata = new();
        public Stream Payload => payload;
        public Stream Metadata => metadata;
        public bool Published { get; private set; }
        public string? FilesystemDeviceNumber { get; set; } = "254:1";
        public void FlushPayload() { }
        public void FlushMetadata() { }
        public void FlushDirectories() { }
        public void Publish() => Published = true;
        public void Seal()
        {
            byte[] p = payload.ToArray(), m = metadata.ToArray();
            payload.Dispose(); metadata.Dispose(); payload = new(p, false); metadata = new(m, false);
        }
        public ValueTask DisposeAsync() { payload.Dispose(); metadata.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Target(SafeFileHandle handle, LinuxBlockObservation observation) : RawImageDeviceRestoreService.ITarget
    {
        internal Action? Recheck;
        public LinuxBlockObservation Observation => observation;
        public Stream ReadBack => Stream.Null;
        public ValueTask RecheckAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Recheck?.Invoke(); return ValueTask.CompletedTask; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, ulong offset, CancellationToken token) => throw new InvalidOperationException("fixture-must-not-write");
        public void Flush() => throw new InvalidOperationException("fixture-must-not-flush");
        public ValueTask DisposeAsync() { handle.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Access : LinuxRawRestoreTargetOpenService.IDeviceAccess, IDisposable
    {
        private readonly string path = Path.GetTempFileName();
        internal int Opens, Inspects;
        internal SafeFileHandle? Handle;
        internal LinuxBlockReadService.HandleIdentity Identity;
        internal Action<int>? InspectHook;
        internal Action? Recheck;
        internal bool RejectAdoption, RejectInspection;
        internal LinuxRawRestoreOpenState? Failure;
        public SafeFileHandle? Open(string name, out LinuxRawRestoreOpenState failure)
        {
            Assert.Equal("vdb", name); Opens++; failure = Failure ?? LinuxRawRestoreOpenState.OpenedExclusive;
            return Failure.HasValue ? null : Handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
        }
        public bool Inspect(SafeFileHandle handle, out LinuxBlockReadService.HandleIdentity identity)
        { Assert.Same(Handle, handle); Inspects++; InspectHook?.Invoke(Inspects); identity = Identity; return !RejectInspection; }
        public RawImageDeviceRestoreService.ITarget? Adopt(SafeFileHandle handle, LinuxBlockObservation observation) =>
            RejectAdoption ? null : new Target(handle, observation) { Recheck = Recheck };
        public void Dispose() { Handle?.Dispose(); File.Delete(path); }
    }

    private sealed class Fixture(LinuxRawImageStagingSession stage, RawImageDeviceRestoreRequest request,
        LinuxBlockInventory inventory, RawImageRestoreJournal journal, Storage journalStorage) : IAsyncDisposable
    {
        internal readonly LinuxRawImageStagingSession Stage = stage;
        internal readonly RawImageDeviceRestoreRequest Request = request;
        internal readonly LinuxBlockInventory Inventory = inventory;
        internal readonly RawImageRestoreJournal Journal = journal;
        internal readonly Storage JournalStorage = journalStorage;
        internal Access Access()
        {
            var d = Request.Target;
            return new() { Identity = new(true, 254, 16, d.Capacity, d.LogicalSectorBytes, d.PhysicalSectorBytes, d.DiskSequence!.Value, false) };
        }
        internal ValueTask<LinuxRawRestoreOpenResult> Open(Access access,
            Func<CancellationToken, ValueTask<LinuxBlockInventory>>? observe = null, RawImageDeviceRestoreRequest? request = null,
            LinuxBlockInventory? expected = null, CancellationToken token = default) =>
            LinuxRawRestoreTargetOpenService.OpenAsync(Stage, request ?? Request, Journal, expected ?? Inventory,
                observe ?? (_ => ValueTask.FromResult(Inventory)), access, token);
        public async ValueTask DisposeAsync() { await Journal.DisposeAsync(); await Stage.DisposeAsync(); }
    }

    private static async Task<Fixture> Create(uint sector = 512)
    {
        using var metadata = new MemoryStream(Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid(sector).MetadataHex));
        using var payload = new MemoryStream(DiskImageRawProfileVerificationServiceTests.Raw(sector));
        var storage = new Storage();
        var image = (await LinuxRawImageStagingService.StageAsync(metadata, payload, _ => storage)).Session!;
        // Test-only known filesystem provenance; ordinary files never establish native ancestry.
        var stage = new LinuxRawImageStagingSession(storage, image.Image, "254:1", "254:1");
        var device = new LinuxBlockObservation("vdb", "254:16", null, LinuxBlockKind.WholeDisk, image.Image.VerifiedBytes,
            sector, sector, 2, false, "serial:RESTORE-FIXTURE", LinuxBlockIdentity.UniqueInSnapshot, false, false, false, LinuxBlockUse.Unknown);
        var request = new RawImageDeviceRestoreRequest(Guid.NewGuid(), Guid.NewGuid(), image.Image.ImageId,
            image.Image.CaptureId, image.Image.Generation, image.Image.ManifestRoot!, device);
        var root = device with { KernelName = "vda", DeviceNumber = "254:0", HardwareIdentityCandidate = "serial:OS", MountObserved = true, Use = LinuxBlockUse.ObservedInUse };
        var partition = root with { KernelName = "vda1", DeviceNumber = "254:1", ParentKernelName = "vda", Kind = LinuxBlockKind.Partition };
        var inventory = new LinuxBlockInventory(LinuxBlockInventoryState.Observed, LinuxBlockInventoryReason.NamespaceObservationOnly,
            Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "mnt:[123]", false, new[] { device, root, partition });
        var journalStorage = new Storage();
        var journal = await RawImageRestoreJournal.CreateAsync(request, inventory, (_, allow) =>
        { Assert.True(allow(journalStorage.FilesystemDeviceNumber)); return journalStorage; }, _ => ValueTask.FromResult(inventory));
        return new(stage, request, inventory, journal, journalStorage);
    }

    [Theory]
    [InlineData(512u)][InlineData(4096u)]
    public async Task TransfersOnlyAfterTwoObservationsAndHeldInspectionsWithoutWriting(uint sector)
    {
        await using var f = await Create(sector); using var a = f.Access(); int observations = 0;
        var result = await f.Open(a, _ => { observations++; return ValueTask.FromResult(f.Inventory); });
        Assert.Equal(LinuxRawRestoreOpenState.OpenedExclusive, result.State);
        Assert.Equal(2, observations); Assert.Equal(2, a.Inspects); Assert.Equal(1, a.Opens);
        Assert.False(a.Handle!.IsClosed); Assert.Equal(0, f.JournalStorage.Payload.Length);
        await result.Target!.DisposeAsync(); Assert.True(a.Handle.IsClosed);
    }

    private static LinuxBlockInventory Change(LinuxBlockInventory inventory, string kind) => kind switch
    {
        "boot" => inventory with { BootId = Guid.NewGuid() },
        "namespace" => inventory with { MountNamespace = "mnt:[456]" },
        "stale" => inventory with { ObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) },
        "future" => inventory with { ObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) },
        "swaps" => inventory with { HasUnresolvedSwaps = true },
        "duplicate" => inventory with { Devices = inventory.Devices.Concat(new[] { inventory.Devices[0] }).ToArray() },
        "target" => inventory with { Devices = inventory.Devices.Select((d, i) => i == 0 ? d with { DiskSequence = 22 } : d).ToArray() },
        "mounted" => inventory with { Devices = inventory.Devices.Select((d, i) => i == 0 ? d with { MountObserved = true } : d).ToArray() },
        "repository" => inventory with { Devices = inventory.Devices.Select((d, i) => i == 1 ? d with { DiskSequence = 99 } : d).ToArray() },
        "ancestry" => inventory with { Devices = inventory.Devices.Select((d, i) => i == 2 ? d with { ParentKernelName = "vdb" } : d).ToArray() },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    [Theory]
    [InlineData("boot")][InlineData("namespace")][InlineData("stale")][InlineData("future")][InlineData("swaps")]
    [InlineData("duplicate")][InlineData("target")][InlineData("mounted")][InlineData("repository")][InlineData("ancestry")]
    public async Task RefusesChangedInventoryBeforeAnyNativeOpen(string kind)
    {
        await using var f = await Create(); using var a = f.Access();
        var result = await f.Open(a, _ => ValueTask.FromResult(Change(f.Inventory, kind)));
        Assert.Equal(LinuxRawRestoreOpenState.Changed, result.State); Assert.Null(result.Target); Assert.Equal(0, a.Opens);
    }

    [Theory]
    [InlineData("boot")][InlineData("namespace")][InlineData("stale")][InlineData("future")][InlineData("swaps")]
    [InlineData("duplicate")][InlineData("target")][InlineData("mounted")][InlineData("repository")][InlineData("ancestry")]
    public async Task ClosesHeldDescriptorWhenSecondInventoryChanges(string kind)
    {
        await using var f = await Create(); using var a = f.Access(); int observations = 0;
        var result = await f.Open(a, _ => ValueTask.FromResult(++observations == 1 ? f.Inventory : Change(f.Inventory, kind)));
        Assert.Equal(LinuxRawRestoreOpenState.Changed, result.State); Assert.Null(result.Target); Assert.True(a.Handle!.IsClosed);
    }

    [Theory]
    [InlineData("operation")][InlineData("review")][InlineData("image")][InlineData("root")][InlineData("path")]
    public async Task RejectsRequestSubstitutionBeforeOpen(string kind)
    {
        await using var f = await Create(); using var a = f.Access();
        var request = kind switch
        {
            "operation" => f.Request with { OperationId = Guid.NewGuid() },
            "review" => f.Request with { ReviewId = Guid.NewGuid() },
            "image" => f.Request with { ImageId = Guid.NewGuid() },
            "root" => f.Request with { ManifestRoot = new string('a', 64) },
            _ => f.Request with { Target = f.Request.Target with { KernelName = "../vdb" } }
        };
        var result = await f.Open(a, request: request);
        Assert.Equal(LinuxRawRestoreOpenState.Refused, result.State); Assert.Equal(0, a.Opens);
    }

    [Theory]
    [InlineData(1)][InlineData(2)]
    public async Task RefusesHeldIdentityChangesAndClosesWithoutAdoption(int inspection)
    {
        await using var f = await Create(); using var a = f.Access();
        a.InspectHook = n => { if (n == inspection) a.Identity = a.Identity with { DiskSequence = 99 }; };
        var result = await f.Open(a);
        Assert.Equal(LinuxRawRestoreOpenState.Changed, result.State); Assert.True(a.Handle!.IsClosed);
    }

    [Theory]
    [InlineData("started")][InlineData("disposed")][InlineData("storage")][InlineData("stage")]
    public async Task RequiresLiveUnstartedBoundJournalAndStage(string state)
    {
        await using var f = await Create(); using var a = f.Access();
        if (state == "started") await f.Journal.AppendDurableAsync(new(RawImageRestoreRecordKind.Started, f.Request, 0, 0), default);
        if (state == "disposed") await f.Journal.DisposeAsync();
        if (state == "storage") f.JournalStorage.FilesystemDeviceNumber = "254:16";
        if (state == "stage") await f.Stage.DisposeAsync();
        var result = await f.Open(a);
        Assert.Equal(LinuxRawRestoreOpenState.Refused, result.State); Assert.Equal(0, a.Opens);
    }

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public async Task CancellationAtEveryAcquisitionPhaseClosesAnyDescriptor(int phase)
    {
        await using var f = await Create(); using var a = f.Access(); using var cancel = new CancellationTokenSource();
        int observations = 0;
        if (phase == 0) cancel.Cancel();
        a.Recheck = () => { if (phase == 3) cancel.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await f.Open(a, _ =>
        { if (++observations == phase) cancel.Cancel(); return ValueTask.FromResult(f.Inventory); }, token: cancel.Token));
        Assert.True(a.Handle is null || a.Handle.IsClosed);
    }

    [Theory]
    [InlineData("inspection")][InlineData("adoption")][InlineData("recheck")][InlineData("journal")]
    public async Task AcquisitionFailuresAlwaysReleaseTheDescriptor(string phase)
    {
        await using var f = await Create(); using var a = f.Access();
        a.RejectInspection = phase == "inspection"; a.RejectAdoption = phase == "adoption";
        a.Recheck = () => { if (phase == "recheck") throw new IOException("fixture-recheck"); };
        int observations = 0;
        var result = await f.Open(a, async _ =>
        { if (++observations == 2 && phase == "journal") await f.Journal.DisposeAsync(); return f.Inventory; });
        Assert.NotEqual(LinuxRawRestoreOpenState.OpenedExclusive, result.State); Assert.True(a.Handle!.IsClosed);
    }

    [Theory]
    [InlineData((int)LinuxRawRestoreOpenState.InUse)][InlineData((int)LinuxRawRestoreOpenState.AccessDenied)]
    [InlineData((int)LinuxRawRestoreOpenState.Unavailable)][InlineData((int)LinuxRawRestoreOpenState.Unsupported)]
    public async Task PreservesNativeRefusalWithoutRetry(int value)
    {
        await using var f = await Create(); using var a = f.Access(); var failure = (LinuxRawRestoreOpenState)value; a.Failure = failure;
        var result = await f.Open(a); Assert.Equal(failure, result.State); Assert.Equal(1, a.Opens); Assert.Null(a.Handle);
    }
}
