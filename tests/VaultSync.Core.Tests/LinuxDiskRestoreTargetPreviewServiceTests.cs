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

public sealed class LinuxDiskRestoreTargetPreviewServiceTests
{
    private sealed class Storage : LinuxRawImageStagingService.IStorage
    {
        public Stream Payload { get; } = new MemoryStream(new byte[1], writable: false);
        public Stream Metadata { get; } = new MemoryStream(new byte[1], writable: false);
        public string? FilesystemDeviceNumber => "8:1";
        public bool Published => true;
        public void Seal() => throw new NotSupportedException();
        public void FlushPayload() => throw new NotSupportedException();
        public void FlushMetadata() => throw new NotSupportedException();
        public void FlushDirectories() => throw new NotSupportedException();
        public void Publish() => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Payload.Dispose(); Metadata.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Access : LinuxBlockReadService.IDeviceAccess
    {
        internal LinuxBlockReadService.HandleIdentity Current = Identity;
        internal bool Inspectable = true;
        public bool Inspect(SafeFileHandle handle, out LinuxBlockReadService.HandleIdentity identity)
        { identity = Current; return Inspectable && !handle.IsClosed; }
        public SafeFileHandle? Open(string name, out LinuxBlockReadState failure) => throw new NotSupportedException();
    }

    private static LinuxBlockReadService.HandleIdentity Identity => new(true, 8, 16, 65536, 512, 512, 111, false);
    private static LinuxBlockObservation Target => new("sdb", "8:16", null, LinuxBlockKind.WholeDisk, 65536, 512, 512,
        111, false, "serial:fixture-target", LinuxBlockIdentity.UniqueInSnapshot, false, false, false, LinuxBlockUse.Unknown);
    private static LinuxBlockInventory Inventory()
    {
        var disk = Target with { KernelName = "sda", DeviceNumber = "8:0", Capacity = 1048576,
            HardwareIdentityCandidate = "serial:fixture-repository", DiskSequence = 22, Use = LinuxBlockUse.ObservedInUse };
        var partition = disk with { KernelName = "sda1", DeviceNumber = "8:1", Kind = LinuxBlockKind.Partition,
            ParentKernelName = "sda", Capacity = 524288, MountObserved = true, Identity = LinuxBlockIdentity.Unknown };
        return new(LinuxBlockInventoryState.Observed, LinuxBlockInventoryReason.NamespaceObservationOnly, Guid.NewGuid(),
            DateTimeOffset.UtcNow, Guid.NewGuid(), "mnt:[123]", false, new[] { disk, partition, Target });
    }

    private static async Task<LinuxRawImageStagingSession> Stage(string? metadataDevice = "8:1", string? payloadDevice = "8:1")
    {
        using var meta = new MemoryStream(Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid().MetadataHex));
        using var payload = new MemoryStream(DiskImageRawProfileVerificationServiceTests.Raw(512));
        var verified = await DiskImageRawProfileVerificationService.VerifyAsync(meta, payload);
        Assert.Equal(DiskImageRawProfileVerificationState.ContentAndLayoutVerified, verified.State);
        return new(new Storage(), verified.Image!, metadataDevice, payloadDevice);
    }

    [Fact]
    public async Task FreshMatchingTargetHasOnlyReadOnlyObservationAndRechecksNativeBinding()
    {
        await using var stage = await Stage();
        var inventory = Inventory();
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[65536]);
            var access = new Access();
            var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var source = new LinuxBlockReadSession(handle, Identity, access);
            var result = await LinuxDiskRestoreTargetPreviewService.CreateAsync(stage, inventory, "sdb",
                _ => ValueTask.FromResult(inventory), (fresh, name, token) =>
                {
                    Assert.Same(inventory, fresh); Assert.Equal("sdb", name);
                    return Task.FromResult(new LinuxBlockReadResult(LinuxBlockReadState.OpenedReadOnly, source));
                });
            Assert.Equal(LinuxDiskRestoreTargetPreviewReason.ObservationOnly, result.Reason);
            Assert.NotNull(result.Preview);
            await using var preview = result.Preview;
            Assert.Equal(LinuxBlockUse.Unknown, preview.ObservedTarget.Use); // Never upgrade absence to Offline.
            Assert.Equal(stage.Image.ManifestRoot, preview.Image.ManifestRoot);
            Assert.False(source.CanWrite); Assert.True(await preview.RecheckAsync());
            access.Current = access.Current with { DiskSequence = 112 };
            Assert.False(await preview.RecheckAsync()); Assert.True(handle.IsClosed);
            Assert.True(stage.IsAlive); // Preview does not dispose the caller's stage.
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("repository", LinuxDiskRestoreTargetPreviewReason.RepositoryOnTarget)]
    [InlineData("repository-partition", LinuxDiskRestoreTargetPreviewReason.RepositoryOnTarget)]
    [InlineData("unknown-repository", LinuxDiskRestoreTargetPreviewReason.UnknownRepositoryDevice)]
    [InlineData("duplicate-repository", LinuxDiskRestoreTargetPreviewReason.UnknownRepositoryDevice)]
    [InlineData("stacked-repository", LinuxDiskRestoreTargetPreviewReason.UnknownRepositoryDevice)]
    [InlineData("missing-parent", LinuxDiskRestoreTargetPreviewReason.UnknownRepositoryDevice)]
    [InlineData("read-only", LinuxDiskRestoreTargetPreviewReason.TargetReadOnly)]
    [InlineData("larger", LinuxDiskRestoreTargetPreviewReason.UnsupportedGeometry)]
    [InlineData("smaller", LinuxDiskRestoreTargetPreviewReason.UnsupportedGeometry)]
    [InlineData("logical", LinuxDiskRestoreTargetPreviewReason.UnsupportedGeometry)]
    [InlineData("physical", LinuxDiskRestoreTargetPreviewReason.UnsupportedGeometry)]
    [InlineData("stale", LinuxDiskRestoreTargetPreviewReason.InvalidInventory)]
    [InlineData("future", LinuxDiskRestoreTargetPreviewReason.InvalidInventory)]
    [InlineData("swaps", LinuxDiskRestoreTargetPreviewReason.InvalidInventory)]
    [InlineData("unknown-target", LinuxDiskRestoreTargetPreviewReason.UnsupportedTarget)]
    public async Task UnsafeOrIncompleteSelectionNeverOpensDevice(string fault, LinuxDiskRestoreTargetPreviewReason reason)
    {
        await using var stage = await Stage();
        var inventory = Inventory();
        string target = "sdb";
        var devices = inventory.Devices.ToArray();
        switch (fault)
        {
            case "repository": target = "sda"; break;
            case "repository-partition": target = "sda1"; break;
            case "unknown-repository": devices[1] = devices[1] with { DeviceNumber = "8:2" }; break;
            case "duplicate-repository": devices = devices.Append(devices[1] with { KernelName = "sdc1" }).ToArray(); break;
            case "stacked-repository": devices[1] = devices[1] with { Kind = LinuxBlockKind.VirtualOrStacked }; break;
            case "missing-parent": devices[1] = devices[1] with { ParentKernelName = "missing" }; break;
            case "read-only": devices[2] = devices[2] with { IsReadOnly = true }; break;
            case "larger": devices[2] = devices[2] with { Capacity = 131072 }; break;
            case "smaller": devices[2] = devices[2] with { Capacity = 32768 }; break;
            case "logical": devices[2] = devices[2] with { LogicalSectorBytes = 4096, PhysicalSectorBytes = 4096 }; break;
            case "physical": devices[2] = devices[2] with { PhysicalSectorBytes = 4096 }; break;
            case "stale": inventory = inventory with { ObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }; break;
            case "future": inventory = inventory with { ObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) }; break;
            case "swaps": inventory = inventory with { HasUnresolvedSwaps = true }; break;
            case "unknown-target": target = "missing"; break;
        }
        inventory = inventory with { Devices = devices };
        var result = await LinuxDiskRestoreTargetPreviewService.CreateAsync(stage, inventory, target,
            _ => ValueTask.FromResult(inventory), (_, _, _) => throw new InvalidOperationException("must not open"));
        Assert.Equal(reason, result.Reason); Assert.Null(result.Preview);
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("namespace")]
    [InlineData("identity")]
    public async Task FreshKernelSelectionMustMatchExpectedObservation(string fault)
    {
        await using var stage = await Stage();
        var expected = Inventory();
        var fresh = fault switch
        {
            "boot" => expected with { BootId = Guid.NewGuid() },
            "namespace" => expected with { MountNamespace = "mnt:[124]" },
            _ => expected with { Devices = expected.Devices.Select(d => d.KernelName == "sdb" ? d with { DiskSequence = 112 } : d).ToArray() }
        };
        var result = await LinuxDiskRestoreTargetPreviewService.CreateAsync(stage, expected, "sdb",
            _ => ValueTask.FromResult(fresh), (_, _, _) => throw new InvalidOperationException("must not open"));
        Assert.Equal(LinuxDiskRestoreTargetPreviewReason.SelectionChanged, result.Reason); Assert.Null(result.Preview);
    }

    [Fact]
    public async Task NativeInventoryNotCallerRepositoryMappingControlsExclusion()
    {
        await using var stage = await Stage();
        var fresh = Inventory();
        var forged = fresh with { Devices = fresh.Devices.Select(d => d.KernelName == "sda1" ?
            d with { ParentKernelName = "sdb" } : d).ToArray() };
        var result = await LinuxDiskRestoreTargetPreviewService.CreateAsync(stage, forged, "sda",
            _ => ValueTask.FromResult(fresh), (_, _, _) => throw new InvalidOperationException("must not open"));
        Assert.Equal(LinuxDiskRestoreTargetPreviewReason.RepositoryOnTarget, result.Reason);
    }

    [Fact]
    public async Task UnknownImageRepositoryNeverAcquiresADevice()
    {
        await using var stage = await Stage(metadataDevice: null);
        var inventory = Inventory();
        var result = await LinuxDiskRestoreTargetPreviewService.CreateAsync(stage, inventory, "sdb",
            _ => throw new InvalidOperationException("must not observe"), (_, _, _) => throw new InvalidOperationException("must not open"));
        Assert.Equal(LinuxDiskRestoreTargetPreviewReason.UnknownRepositoryDevice, result.Reason);
    }

    [Fact]
    public async Task DisposedStageNeverAcquiresADevice()
    {
        var stage = await Stage(); await stage.DisposeAsync();
        var result = await LinuxDiskRestoreTargetPreviewService.CreateAsync(stage, Inventory(), "sdb",
            _ => throw new InvalidOperationException("must not observe"), (_, _, _) => throw new InvalidOperationException("must not open"));
        Assert.Equal(LinuxDiskRestoreTargetPreviewReason.InvalidImage, result.Reason);
    }

    [Fact]
    public async Task CancellationAcquiresNoDevice()
    {
        await using var stage = await Stage();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var result = await LinuxDiskRestoreTargetPreviewService.CreateAsync(stage, Inventory(), "sdb",
            _ => throw new InvalidOperationException("must not observe"), (_, _, _) => throw new InvalidOperationException("must not open"), cancellation.Token);
        Assert.Equal(LinuxDiskRestoreTargetPreviewReason.Interrupted, result.Reason);
    }

    [Theory]
    [InlineData("wrong-identity", LinuxDiskRestoreTargetPreviewReason.SelectionChanged)]
    [InlineData("unavailable-binding", LinuxDiskRestoreTargetPreviewReason.TargetUnavailable)]
    [InlineData("refused-with-source", LinuxDiskRestoreTargetPreviewReason.TargetUnavailable)]
    [InlineData("stage-closed", LinuxDiskRestoreTargetPreviewReason.InvalidImage)]
    public async Task LateRefusalClosesEveryAcquiredHandle(string fault, LinuxDiskRestoreTargetPreviewReason reason)
    {
        await using var stage = await Stage();
        var inventory = Inventory();
        string path = Path.GetTempFileName();
        try
        {
            var access = new Access { Inspectable = fault != "unavailable-binding" };
            var identity = fault == "wrong-identity" ? Identity with { DiskSequence = 112 } : Identity;
            var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var source = new LinuxBlockReadSession(handle, identity, access);
            var result = await LinuxDiskRestoreTargetPreviewService.CreateAsync(stage, inventory, "sdb",
                _ => ValueTask.FromResult(inventory), async (_, _, _) =>
                {
                    if (fault == "stage-closed") await stage.DisposeAsync();
                    return new LinuxBlockReadResult(fault == "refused-with-source" ? LinuxBlockReadState.Refused :
                        LinuxBlockReadState.OpenedReadOnly, source);
                });
            Assert.Equal(reason, result.Reason); Assert.Null(result.Preview); Assert.True(handle.IsClosed);
        }
        finally { File.Delete(path); }
    }
}
