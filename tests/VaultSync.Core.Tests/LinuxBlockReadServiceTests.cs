#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxBlockReadServiceTests
{
    private sealed class Device : LinuxBlockReadService.IDeviceAccess, IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("vaultsync-read-fixture-").FullName;
        public readonly byte[] Bytes;
        public readonly string Path;
        public LinuxBlockReadService.HandleIdentity Identity;
        public Func<int, LinuxBlockReadService.HandleIdentity>? InspectOverride;
        public LinuxBlockReadState? OpenFailure;
        public bool InspectFails;
        public SafeFileHandle? LastHandle;
        public int OpenCalls, InspectCalls, Observations;
        public LinuxBlockObservation Selected;
        public LinuxBlockInventory Snapshot;

        public Device(int length = 4096)
        {
            Path = System.IO.Path.Combine(directory, "source.bin");
            Bytes = Enumerable.Range(0, length).Select(n => (byte)(n % 251)).ToArray();
            File.WriteAllBytes(Path, Bytes);
            Identity = new(true, 254, 16, (ulong)length, 512, 512, 55, false);
            Selected = new("vdb", "254:16", null, LinuxBlockKind.WholeDisk, (ulong)length, 512, 512,
                55, false, "serial:PUBLIC-READ-FIXTURE", LinuxBlockIdentity.UniqueInSnapshot,
                false, false, false, LinuxBlockUse.Unknown);
            Snapshot = new(LinuxBlockInventoryState.Observed, LinuxBlockInventoryReason.NamespaceObservationOnly,
                Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "mnt:[1234]", false, new[] { Selected });
        }

        public SafeFileHandle? Open(string kernelName, out LinuxBlockReadState failure)
        {
            Assert.Equal("vdb", kernelName);
            OpenCalls++;
            failure = OpenFailure ?? LinuxBlockReadState.OpenedReadOnly;
            return OpenFailure.HasValue ? null : LastHandle = File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public bool Inspect(SafeFileHandle handle, out LinuxBlockReadService.HandleIdentity identity)
        {
            Assert.Same(LastHandle, handle);
            InspectCalls++;
            identity = InspectOverride?.Invoke(InspectCalls) ?? Identity;
            return !InspectFails;
        }

        public ValueTask<LinuxBlockInventory> Observe(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Observations++; return ValueTask.FromResult(Snapshot); }

        public Task<LinuxBlockReadResult> OpenAsync(LinuxBlockInventory? expected = null,
            Func<CancellationToken, ValueTask<LinuxBlockInventory>>? observe = null, CancellationToken token = default) =>
            LinuxBlockReadService.OpenAsync(expected ?? Snapshot, "vdb", observe ?? Observe, this, token);

        public void Dispose() { LastHandle?.Dispose(); Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task HeldReadOnlySourcePreservesBytesAndSupportsBoundedSeek()
    {
        using var device = new Device();
        LinuxBlockReadResult result = await device.OpenAsync();
        Assert.Equal(LinuxBlockReadState.OpenedReadOnly, result.State);
        using LinuxBlockReadSession source = Assert.IsType<LinuxBlockReadSession>(result.Source);
        Assert.True(source.CanRead); Assert.True(source.CanSeek); Assert.False(source.CanWrite);
        Assert.Equal(device.Bytes.Length, source.Length);
        byte[] first = new byte[512];
        Assert.Equal(first.Length, await source.ReadAsync(first));
        Assert.Equal(device.Bytes[..512], first);
        Assert.Equal(512, source.Position);
        source.Seek(-512, SeekOrigin.End);
        byte[] last = new byte[512];
        Assert.Equal(last.Length, source.Read(last, 0, last.Length));
        Assert.Equal(device.Bytes[^512..], last);
        Assert.Equal(0, source.ReadByte() + 1); // Exact-capacity EOF.
        Assert.Throws<NotSupportedException>(() => source.Write(first, 0, first.Length));
        Assert.Throws<NotSupportedException>(() => source.SetLength(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Seek(1, SeekOrigin.End));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Seek(long.MaxValue, SeekOrigin.Current));
        Assert.Equal(device.Bytes, File.ReadAllBytes(device.Path));
        Assert.Equal(2, device.Observations);
    }

    [Fact]
    public async Task ExplicitBindingCheckDoesNotReadOrAdvanceTheHeldSource()
    {
        using var device = new Device();
        using var source = (await device.OpenAsync()).Source!;
        source.Position = 512;
        int before = device.InspectCalls;
        await source.ValidateBindingAsync();
        Assert.Equal(before + 1, device.InspectCalls);
        Assert.Equal(512, source.Position);
        Assert.Equal(device.Bytes, File.ReadAllBytes(device.Path));
    }

    [Fact]
    public async Task ExplicitBindingCheckClosesAChangedSourceWithoutReadingIt()
    {
        using var device = new Device();
        using var source = (await device.OpenAsync()).Source!;
        device.Identity = device.Identity with { DiskSequence = 56 };
        await Assert.ThrowsAsync<IOException>(async () => await source.ValidateBindingAsync());
        Assert.False(source.CanRead); Assert.True(device.LastHandle!.IsClosed);
        Assert.Equal(device.Bytes, File.ReadAllBytes(device.Path));
    }

    [Fact]
    public async Task CancelledBindingCheckDoesNotInspectOrInvalidateTheSource()
    {
        using var device = new Device();
        using var source = (await device.OpenAsync()).Source!;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        int before = device.InspectCalls;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await source.ValidateBindingAsync(cancelled.Token));
        Assert.Equal(before, device.InspectCalls); Assert.True(source.CanRead);
    }

    [Fact]
    public async Task ConcurrentReadsSerializePositionAndReadSizeIsBounded()
    {
        using var device = new Device(512 * 1024);
        using LinuxBlockReadSession source = (await device.OpenAsync()).Source!;
        byte[] first = new byte[256 * 1024], second = new byte[256 * 1024];
        Task<int> a = source.ReadAsync(first).AsTask(), b = source.ReadAsync(second).AsTask();
        await Task.WhenAll(a, b);
        Assert.InRange(a.Result, 1, 128 * 1024);
        Assert.InRange(b.Result, 1, 128 * 1024);
        Assert.Equal(a.Result + b.Result, source.Position);
        Assert.Equal(device.Bytes[..a.Result], first[..a.Result]);
        Assert.Equal(device.Bytes[a.Result..(a.Result + b.Result)], second[..b.Result]);
    }

    [Theory]
    [InlineData("../vdb")]
    [InlineData("/dev/vdb")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("vdb\n")]
    [InlineData("vdb\0")]
    public async Task UntrustedNamesNeverReachNativeOpen(string name)
    {
        using var device = new Device();
        var result = await LinuxBlockReadService.OpenAsync(device.Snapshot, name, device.Observe, device);
        Assert.Equal(LinuxBlockReadState.Refused, result.State);
        Assert.Equal(0, device.OpenCalls); Assert.Equal(0, device.Observations);
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(60)]
    public async Task StaleOrFutureSelectionNeverOpens(int seconds)
    {
        using var device = new Device();
        var result = await device.OpenAsync(device.Snapshot with { ObservedAtUtc = DateTimeOffset.UtcNow.AddSeconds(seconds) });
        Assert.Equal(LinuxBlockReadState.StaleObservation, result.State);
        Assert.Equal(0, device.OpenCalls); Assert.Equal(0, device.Observations);
    }

    public static IEnumerable<object[]> UnsafeSelections => new LinuxBlockObservation[]
    {
        Baseline with { Kind = LinuxBlockKind.Partition, ParentKernelName = "vda" },
        Baseline with { Identity = LinuxBlockIdentity.Ambiguous },
        Baseline with { HardwareIdentityCandidate = null },
        Baseline with { DiskSequence = null },
        Baseline with { Capacity = LinuxBlockReadService.MaximumCapacity + 512 },
        Baseline with { LogicalSectorBytes = 0 },
        Baseline with { PhysicalSectorBytes = 1024 },
        Baseline with { Use = (LinuxBlockUse)999 },
    }.Select(d => new object[] { d });

    private static LinuxBlockObservation Baseline => new("vdb", "254:16", null, LinuxBlockKind.WholeDisk,
        4096, 512, 512, 55, false, "serial:PUBLIC-READ-FIXTURE", LinuxBlockIdentity.UniqueInSnapshot,
        false, false, false, LinuxBlockUse.Unknown);

    [Theory]
    [MemberData(nameof(UnsafeSelections))]
    public async Task UnsupportedOrAmbiguousSelectionsNeverOpen(LinuxBlockObservation selected)
    {
        using var device = new Device();
        var result = await device.OpenAsync(device.Snapshot with { Devices = new[] { selected } });
        Assert.Equal(LinuxBlockReadState.Refused, result.State);
        Assert.Equal(0, device.OpenCalls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task AnyObservedUseNeverOpens(int flag)
    {
        using var device = new Device();
        LinuxBlockObservation d = device.Selected;
        d = flag switch { 0 => d with { MountObserved = true }, 1 => d with { SwapObserved = true },
            2 => d with { HasHolders = true }, _ => d with { Use = LinuxBlockUse.ObservedInUse } };
        var result = await device.OpenAsync(device.Snapshot with { Devices = new[] { d } });
        Assert.Equal(LinuxBlockReadState.InUse, result.State);
        Assert.Equal(0, device.OpenCalls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ChangedObservationBeforeOrAfterOpeningNeverReturnsSource(bool afterOpen)
    {
        using var device = new Device(); int calls = 0;
        ValueTask<LinuxBlockInventory> Observe(CancellationToken _)
        {
            calls++;
            return ValueTask.FromResult(calls == (afterOpen ? 2 : 1)
                ? device.Snapshot with { BootId = Guid.NewGuid() } : device.Snapshot);
        }
        var result = await device.OpenAsync(observe: Observe);
        Assert.Equal(LinuxBlockReadState.Changed, result.State); Assert.Null(result.Source);
        Assert.Equal(afterOpen ? 1 : 0, device.OpenCalls);
        if (afterOpen) Assert.True(device.LastHandle!.IsClosed);
    }

    public static IEnumerable<object[]> ChangedHandles => new LinuxBlockReadService.HandleIdentity[]
    {
        Handle with { IsBlock = false }, Handle with { Major = 1 }, Handle with { Minor = 1 },
        Handle with { Capacity = 8192 }, Handle with { LogicalSectorBytes = 4096 },
        Handle with { PhysicalSectorBytes = 4096 }, Handle with { DiskSequence = 56 }, Handle with { IsReadOnly = true }
    }.Select(identity => new object[] { identity });
    private static LinuxBlockReadService.HandleIdentity Handle => new(true, 254, 16, 4096, 512, 512, 55, false);

    [Theory]
    [MemberData(nameof(ChangedHandles))]
    public async Task EveryHeldIdentityMismatchClosesWithoutReturningSource(object value)
    {
        using var device = new Device { Identity = (LinuxBlockReadService.HandleIdentity)value };
        var result = await device.OpenAsync();
        Assert.Equal(LinuxBlockReadState.Changed, result.State); Assert.Null(result.Source);
        Assert.True(device.LastHandle!.IsClosed);
    }

    [Fact]
    public async Task ReadDetectsChangedHeldDiskAndDisposesTheSource()
    {
        using var device = new Device();
        using LinuxBlockReadSession source = (await device.OpenAsync()).Source!;
        device.Identity = device.Identity with { DiskSequence = 56 };
        await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(new byte[512]).AsTask());
        Assert.False(source.CanRead); Assert.True(device.LastHandle!.IsClosed);
    }

    [Theory]
    [InlineData(2)] [InlineData(4)]
    public async Task IdentityChangeDuringObservationOrReadNeverCommitsSuccess(int changedInspection)
    {
        using var device = new Device();
        device.InspectOverride = n => n == changedInspection ? device.Identity with { DiskSequence = 56 } : device.Identity;
        var result = await device.OpenAsync();
        if (changedInspection == 2)
        {
            Assert.Equal(LinuxBlockReadState.Changed, result.State); Assert.Null(result.Source);
        }
        else
        {
            using LinuxBlockReadSession source = result.Source!;
            await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(new byte[512]).AsTask());
        }
        Assert.True(device.LastHandle!.IsClosed);
    }

    [Fact]
    public async Task MissingHeldMetadataIsUnverifiableRatherThanProvenChanged()
    {
        using var device = new Device { InspectFails = true };
        var result = await device.OpenAsync();
        Assert.Equal(LinuxBlockReadState.Unverifiable, result.State); Assert.Null(result.Source);
        Assert.True(device.LastHandle!.IsClosed);
    }

    [Fact]
    public async Task PrematureEndDoesNotBecomeNormalCapacityEof()
    {
        using var device = new Device();
        device.Identity = device.Identity with { Capacity = 8192 };
        device.Selected = device.Selected with { Capacity = 8192 };
        device.Snapshot = device.Snapshot with { Devices = new[] { device.Selected } };
        using LinuxBlockReadSession source = (await device.OpenAsync()).Source!;
        Assert.Equal(4096, await source.ReadAsync(new byte[8192]));
        await Assert.ThrowsAsync<EndOfStreamException>(() => source.ReadAsync(new byte[512]).AsTask());
        Assert.False(source.CanRead);
        Assert.True(device.LastHandle!.IsClosed);
    }

    [Fact]
    public async Task CancellationAfterOpenClosesHandleAndIsNotAUsableResult()
    {
        using var device = new Device(); using var cancel = new CancellationTokenSource(); int calls = 0;
        ValueTask<LinuxBlockInventory> Observe(CancellationToken token)
        {
            if (++calls == 2) cancel.Cancel();
            token.ThrowIfCancellationRequested(); return ValueTask.FromResult(device.Snapshot);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.OpenAsync(observe: Observe, token: cancel.Token));
        Assert.True(device.LastHandle!.IsClosed);
    }

    [Fact]
    public async Task NativeAccessFailureAndObserverErrorReturnNoPrivateDiagnostics()
    {
        using var device = new Device { OpenFailure = LinuxBlockReadState.AccessDenied };
        var result = await device.OpenAsync();
        Assert.Equal(LinuxBlockReadState.AccessDenied, result.State); Assert.Null(result.Source);
        result = await device.OpenAsync(observe: _ => throw new IOException("PRIVATE-PATH"));
        Assert.Equal(LinuxBlockReadState.Failed, result.State); Assert.Null(result.Source);
    }
}
