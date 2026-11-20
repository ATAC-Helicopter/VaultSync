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
        var inspected = await DiskImageInspectionFixture.InspectAsync(meta, payload, CancellationToken.None);
        meta.Position = 0;
        return new(new Storage(), inspected.Observation!,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(meta)), (ulong)meta.Length, metadataDevice, payloadDevice);
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
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        internal long Ticks;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private sealed class ReviewFixture : IAsyncDisposable
    {
        private readonly string path = Path.GetTempFileName();
        internal LinuxRawImageStagingSession Stage = null!;
        internal LinuxBlockInventory Fresh = Inventory();
        internal LinuxDiskRestoreTargetPreview Preview = null!;
        internal SafeFileHandle Handle = null!;
        internal Clock Clock = new();
        internal Access Access = new();
        internal int Observations;
        internal bool ObservationFails;
        internal Action? OnObservation;
        internal static async Task<ReviewFixture> Create()
        {
            var fixture = new ReviewFixture();
            fixture.Stage = await LinuxDiskRestoreTargetPreviewServiceTests.Stage();
            fixture.Handle = File.OpenHandle(fixture.path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var source = new LinuxBlockReadSession(fixture.Handle, Identity, fixture.Access);
            var result = await LinuxDiskRestoreTargetPreviewService.CreateAsync(fixture.Stage, fixture.Fresh, "sdb",
                _ =>
                {
                    fixture.Observations++;
                    fixture.OnObservation?.Invoke();
                    if (fixture.ObservationFails) throw new IOException();
                    return ValueTask.FromResult(fixture.Fresh);
                }, (_, _, _) => Task.FromResult(new LinuxBlockReadResult(LinuxBlockReadState.OpenedReadOnly, source)),
                clock: fixture.Clock);
            fixture.Preview = Assert.IsType<LinuxDiskRestoreTargetPreview>(result.Preview);
            return fixture;
        }
        public async ValueTask DisposeAsync()
        { await Preview.DisposeAsync(); await Stage.DisposeAsync(); File.Delete(path); }
    }

    [Fact]
    public async Task ReviewBindsExactImageTargetAndOperationAndCanBeAcknowledgedOnce()
    {
        await using var fixture = await ReviewFixture.Create();
        Guid operation = Guid.NewGuid();
        var review = Assert.IsType<LinuxDiskRestoreOverwriteReview>(await fixture.Preview.CreateOverwriteReviewAsync(operation));
        Assert.Equal(operation, review.OperationId);
        Assert.NotEqual(Guid.Empty, review.ReviewId);
        Assert.Same(fixture.Stage.Image, review.Image);
        Assert.Equal(Target, review.Target);
        Assert.Equal(0ul, review.OverwriteOffset);
        Assert.Equal(65536ul, review.OverwriteLength);
        Assert.True(await review.AcknowledgeAsync(operation, review.ReviewId));
        Assert.False(await review.AcknowledgeAsync(operation, review.ReviewId));
        Assert.Equal(3, fixture.Observations); // Create preview, create review, acknowledge.
        Assert.False(fixture.Handle.IsClosed);
        Assert.Equal(LinuxBlockUse.Unknown, review.Target.Use);
    }

    [Fact]
    public async Task NewReviewReplacesOldReviewWithoutLettingOldResponseConsumeNewOne()
    {
        await using var fixture = await ReviewFixture.Create();
        Guid operation = Guid.NewGuid();
        var old = (await fixture.Preview.CreateOverwriteReviewAsync(operation))!;
        var current = (await fixture.Preview.CreateOverwriteReviewAsync(operation))!;
        Assert.NotEqual(old.ReviewId, current.ReviewId);
        Assert.False(await old.AcknowledgeAsync(operation, old.ReviewId));
        Assert.True(await current.AcknowledgeAsync(operation, current.ReviewId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MismatchedResponseConsumesReviewAndCannotBeRetried(bool wrongOperation)
    {
        await using var fixture = await ReviewFixture.Create();
        Guid operation = Guid.NewGuid();
        var review = (await fixture.Preview.CreateOverwriteReviewAsync(operation))!;
        Assert.False(await review.AcknowledgeAsync(wrongOperation ? Guid.NewGuid() : operation,
            wrongOperation ? review.ReviewId : Guid.NewGuid()));
        Assert.False(await review.AcknowledgeAsync(operation, review.ReviewId));
        Assert.Equal(2, fixture.Observations);
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("namespace")]
    [InlineData("mounted")]
    [InlineData("swap")]
    [InlineData("holders")]
    [InlineData("identity")]
    [InlineData("ancestry")]
    [InlineData("missing-repository")]
    [InlineData("incomplete")]
    [InlineData("stale")]
    [InlineData("io")]
    [InlineData("stage")]
    [InlineData("descriptor")]
    public async Task ReviewRefusesChangedOrUnavailableConditionsAndClosesClaim(string fault)
    {
        await using var fixture = await ReviewFixture.Create();
        Guid operation = Guid.NewGuid();
        var review = (await fixture.Preview.CreateOverwriteReviewAsync(operation))!;
        var devices = fixture.Fresh.Devices.ToArray();
        switch (fault)
        {
            case "boot": fixture.Fresh = fixture.Fresh with { BootId = Guid.NewGuid() }; break;
            case "namespace": fixture.Fresh = fixture.Fresh with { MountNamespace = "mnt:[125]" }; break;
            case "mounted": devices[2] = devices[2] with { MountObserved = true }; break;
            case "swap": fixture.Fresh = fixture.Fresh with { HasUnresolvedSwaps = true }; break;
            case "holders": devices[2] = devices[2] with { HasHolders = true }; break;
            case "identity": devices[2] = devices[2] with { DiskSequence = 112 }; break;
            case "ancestry": devices[1] = devices[1] with { ParentKernelName = "sdb" }; break;
            case "missing-repository": devices[1] = devices[1] with { DeviceNumber = "8:2" }; break;
            case "incomplete": fixture.Fresh = fixture.Fresh with { State = LinuxBlockInventoryState.Incomplete }; break;
            case "stale": fixture.Fresh = fixture.Fresh with { ObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }; break;
            case "io": fixture.ObservationFails = true; break;
            case "stage": await fixture.Stage.DisposeAsync(); break;
            case "descriptor": fixture.Access.Current = Identity with { DiskSequence = 112 }; break;
        }
        fixture.Fresh = fixture.Fresh with { Devices = devices };
        Assert.False(await review.AcknowledgeAsync(operation, review.ReviewId));
        Assert.True(fixture.Handle.IsClosed);
        Assert.False(await review.AcknowledgeAsync(operation, review.ReviewId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewExpiresWithoutDependingOnWallClockAdvance(bool rollBackWallClock)
    {
        await using var fixture = await ReviewFixture.Create();
        Guid operation = Guid.NewGuid();
        var review = (await fixture.Preview.CreateOverwriteReviewAsync(operation))!;
        fixture.Clock.Ticks += TimeSpan.FromSeconds(31).Ticks;
        if (rollBackWallClock) fixture.Clock.Now = fixture.Clock.Now.AddSeconds(-1);
        Assert.False(await review.AcknowledgeAsync(operation, review.ReviewId));
        Assert.True(fixture.Handle.IsClosed);
    }

    [Fact]
    public async Task StageClosingDuringObservationCannotBecomeAcknowledged()
    {
        await using var fixture = await ReviewFixture.Create();
        Guid operation = Guid.NewGuid();
        var review = (await fixture.Preview.CreateOverwriteReviewAsync(operation))!;
        fixture.OnObservation = () => fixture.Stage.DisposeAsync().GetAwaiter().GetResult();
        Assert.False(await review.AcknowledgeAsync(operation, review.ReviewId));
        Assert.True(fixture.Handle.IsClosed);
    }

    [Fact]
    public async Task ConcurrentRepliesOnlyAcknowledgeOneExchange()
    {
        await using var fixture = await ReviewFixture.Create();
        Guid operation = Guid.NewGuid();
        var review = (await fixture.Preview.CreateOverwriteReviewAsync(operation))!;
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => review.AcknowledgeAsync(operation, review.ReviewId).AsTask()));
        Assert.Single(results, value => value);
    }

    [Fact]
    public async Task MissingOperationInvalidatesPreviousReview()
    {
        await using var fixture = await ReviewFixture.Create();
        Guid operation = Guid.NewGuid();
        var review = (await fixture.Preview.CreateOverwriteReviewAsync(operation))!;
        Assert.Null(await fixture.Preview.CreateOverwriteReviewAsync(Guid.Empty));
        Assert.False(await review.AcknowledgeAsync(operation, review.ReviewId));
    }

    [Fact]
    public async Task CancellationDuringAcknowledgementConsumesReview()
    {
        await using var fixture = await ReviewFixture.Create();
        Guid operation = Guid.NewGuid();
        var review = (await fixture.Preview.CreateOverwriteReviewAsync(operation))!;
        using var cancellation = new CancellationTokenSource();
        fixture.OnObservation = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => review.AcknowledgeAsync(operation, review.ReviewId, cancellation.Token).AsTask());
        Assert.False(await review.AcknowledgeAsync(operation, review.ReviewId));
    }

}
