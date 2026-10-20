#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRawImageCaptureServiceTests
{
    private static LinuxRawImageCaptureRequest Request() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), 1, "1.9.0", new string('a', 40));

    private sealed class Storage : LinuxRawImageCaptureService.IStorage
    {
        internal readonly MemoryStream Raw = new(), Meta = new();
        internal readonly List<string> Steps = new();
        internal string? Failure;
        internal Action<string>? Hook;
        public Stream Payload => Raw;
        public Stream Metadata => Meta;
        public bool Published { get; private set; }
        private void Step(string step)
        {
            Steps.Add(step); Hook?.Invoke(step);
            if (Failure == step) throw new IOException("private-path-secret-sentinel");
        }
        public void FlushPayload() => Step("payload-flush");
        public void FlushMetadata() => Step("metadata-flush");
        public void FlushDirectories() => Step("directory-flush");
        public void Publish()
        {
            Step("rename");
            Published = true;
            Step("parent-flush");
        }
        public ValueTask DisposeAsync() { Step("dispose"); return ValueTask.CompletedTask; }
    }

    private sealed class Source : MemoryStream
    {
        private readonly byte[] bytes;
        internal Source(byte[] bytes) : base(bytes, writable: true) { this.bytes = bytes; }
        internal string? Fault;
        internal bool CopyStarted;
        private bool changed;
        internal Action? AtCopy;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0 && buffer.Length > 4096)
            {
                CopyStarted = true;
                AtCopy?.Invoke();
                if (Fault == "truncated") return ValueTask.FromResult(0);
                if (Fault == "io") throw new IOException("private-device-sentinel");
                if (Fault == "denied") throw new UnauthorizedAccessException("private-device-sentinel");
            }
            var result = base.ReadAsync(buffer, cancellationToken);
            if (Fault == "changed-layout" && CopyStarted && !changed && Position == Length)
            {
                changed = true;
                bytes[512] ^= 1;
            }
            return result;
        }
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public async Task FullRawCaptureRoundTripsThroughIndependentProfileReader(uint sector)
    {
        byte[] raw = DiskImageRawProfileVerificationServiceTests.Raw(sector);
        using var source = new Source(raw);
        var storage = new Storage();
        var request = Request();
        var result = await LinuxRawImageCaptureService.CaptureAsync(request, source, sector, Guid.NewGuid(),
            () => storage, logicalSectorBytes: sector);
        Assert.Equal(LinuxRawImageCaptureState.PublishedVerified, result.State);
        Assert.Equal((ulong)raw.Length, result.CopiedBytes);
        Assert.Equal(request.ImageId.ToString("N"), result.ImageDirectoryName);
        Assert.Equal(raw, storage.Raw.ToArray());
        Assert.Equal(new[] { "payload-flush", "metadata-flush", "directory-flush", "rename", "parent-flush", "dispose" }, storage.Steps);
        Assert.True(source.CanRead); // Caller still owns source.
        using var meta = new MemoryStream(storage.Meta.ToArray());
        using var payload = new MemoryStream(storage.Raw.ToArray());
        var read = await DiskImageRawProfileVerificationService.VerifyAsync(meta, payload);
        Assert.Equal(DiskImageRawProfileVerificationState.ContentAndLayoutVerified, read.State);
        Assert.Equal(request.ImageId, read.Image!.ImageId);
        Assert.Equal(request.SourceId, read.Image.DeclaredOrigin!.SourceId);
        Assert.Equal(DiskImageSourceKind.OfflineBlockDevice, read.Image.DeclaredOrigin.SourceKind);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(storage.Meta.ToArray().AsSpan(24, storage.Meta.ToArray().Length - 88))), result.ManifestRoot);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("capture")]
    [InlineData("part")]
    [InlineData("source")]
    [InlineData("generation")]
    [InlineData("tool")]
    [InlineData("commit")]
    public async Task BadRequestNeverCreatesOutput(string fault)
    {
        var request = Request();
        request = fault switch
        {
            "image" => request with { ImageId = Guid.Empty },
            "capture" => request with { CaptureId = Guid.Empty },
            "part" => request with { PartId = Guid.Empty },
            "source" => request with { SourceId = Guid.Empty },
            "generation" => request with { Generation = 0 },
            "tool" => request with { ToolVersion = "private/path" },
            _ => request with { ToolSourceCommit = "bad" }
        };
        using var source = new Source(DiskImageRawProfileVerificationServiceTests.Raw(512));
        var result = await LinuxRawImageCaptureService.CaptureAsync(request, source, 512, Guid.NewGuid(),
            () => throw new InvalidOperationException("must not create"));
        Assert.Equal(LinuxRawImageCaptureState.Invalid, result.State);
        Assert.False(source.CopyStarted);
    }

    [Theory]
    [InlineData("payload-flush")]
    [InlineData("metadata-flush")]
    [InlineData("directory-flush")]
    [InlineData("rename")]
    [InlineData("parent-flush")]
    [InlineData("dispose")]
    public async Task FlushAndPublicationFaultsNeverReportSuccess(string failure)
    {
        using var source = new Source(DiskImageRawProfileVerificationServiceTests.Raw(512));
        var storage = new Storage { Failure = failure };
        var result = await LinuxRawImageCaptureService.CaptureAsync(Request(), source, 512, Guid.NewGuid(), () => storage);
        Assert.Equal(failure is "parent-flush" or "dispose" ? LinuxRawImageCaptureState.PublicationUnconfirmed :
            LinuxRawImageCaptureState.Failed, result.State);
        Assert.Null(result.ManifestRoot);
        Assert.Null(result.ImageDirectoryName);
        Assert.DoesNotContain("private", result.Reason);
        Assert.Equal("dispose", storage.Steps[^1]);
    }

    [Theory]
    [InlineData("truncated", LinuxRawImageCaptureState.Incomplete)]
    [InlineData("io", LinuxRawImageCaptureState.Failed)]
    [InlineData("denied", LinuxRawImageCaptureState.Failed)]
    [InlineData("changed-layout", LinuxRawImageCaptureState.Refused)]
    public async Task SourceFailuresLeaveNoPublishedImage(string fault, LinuxRawImageCaptureState expected)
    {
        using var source = new Source(DiskImageRawProfileVerificationServiceTests.Raw(512)) { Fault = fault };
        var storage = new Storage();
        var result = await LinuxRawImageCaptureService.CaptureAsync(Request(), source, 512, Guid.NewGuid(), () => storage);
        Assert.Equal(expected, result.State);
        Assert.False(storage.Published);
        Assert.Equal(0, storage.Meta.Length);
        Assert.DoesNotContain("private", result.Reason);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("payload-flush")]
    [InlineData("metadata-flush")]
    [InlineData("directory-flush")]
    public async Task CancellationBeforeCommitLeavesOnlyPartialStorage(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new Source(DiskImageRawProfileVerificationServiceTests.Raw(512));
        var storage = new Storage();
        if (stage == "copy") source.AtCopy = cancellation.Cancel;
        else storage.Hook = step => { if (step == stage) cancellation.Cancel(); };
        var result = await LinuxRawImageCaptureService.CaptureAsync(Request(), source, 512, Guid.NewGuid(), () => storage, cancellation.Token);
        Assert.Equal(LinuxRawImageCaptureState.Interrupted, result.State);
        Assert.False(storage.Published);
        Assert.DoesNotContain("rename", storage.Steps);
    }

    [Fact]
    public async Task CancellationAfterRenameDoesNotDenyAnAlreadyPublishedImage()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new Source(DiskImageRawProfileVerificationServiceTests.Raw(512));
        var storage = new Storage { Hook = step => { if (step == "parent-flush") cancellation.Cancel(); } };
        var result = await LinuxRawImageCaptureService.CaptureAsync(Request(), source, 512, Guid.NewGuid(), () => storage, cancellation.Token);
        Assert.Equal(LinuxRawImageCaptureState.PublishedVerified, result.State);
        Assert.True(storage.Published);
    }

    [Fact]
    public async Task ReadbackCorruptionRefusesBeforeDirectoryPublication()
    {
        using var source = new Source(DiskImageRawProfileVerificationServiceTests.Raw(512));
        var storage = new Storage();
        storage.Hook = step =>
        {
            if (step == "payload-flush")
            {
                storage.Raw.Position = storage.Raw.Length / 2;
                storage.Raw.WriteByte(0xfe);
            }
        };
        var result = await LinuxRawImageCaptureService.CaptureAsync(Request(), source, 512, Guid.NewGuid(), () => storage);
        Assert.Equal(LinuxRawImageCaptureState.Refused, result.State);
        Assert.Equal("raw-capture.readback-refused", result.Reason);
        Assert.False(storage.Published);
        Assert.DoesNotContain("directory-flush", storage.Steps);
    }

    [Fact]
    public async Task InvalidGptRefusesBeforeStorageAcquisition()
    {
        byte[] raw = DiskImageRawProfileVerificationServiceTests.Raw(512); raw[512] ^= 1;
        using var source = new Source(raw);
        var result = await LinuxRawImageCaptureService.CaptureAsync(Request(), source, 512, Guid.NewGuid(),
            () => throw new InvalidOperationException("must not create"));
        Assert.Equal(LinuxRawImageCaptureState.Refused, result.State);
    }

    [Fact]
    public async Task AlreadyCancelledNeverAcquiresStorage()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var source = new Source(DiskImageRawProfileVerificationServiceTests.Raw(512));
        var result = await LinuxRawImageCaptureService.CaptureAsync(Request(), source, 512, Guid.NewGuid(),
            () => throw new InvalidOperationException("must not create"), cancellation.Token);
        Assert.Equal(LinuxRawImageCaptureState.Interrupted, result.State);
    }

    [Theory]
    [InlineData(512, 1024)]
    [InlineData(1024, 512)]
    [InlineData(512, 4096)]
    public async Task UnsupportedGeometryNeverCreatesStorage(uint physical, uint logical)
    {
        using var source = new Source(DiskImageRawProfileVerificationServiceTests.Raw(512));
        var result = await LinuxRawImageCaptureService.CaptureAsync(Request(), source, physical, Guid.NewGuid(),
            () => throw new InvalidOperationException("must not create"), logicalSectorBytes: logical);
        Assert.Equal(LinuxRawImageCaptureState.Invalid, result.State);
        Assert.False(source.CopyStarted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageAdmissionFailureDoesNotReadSourcePayload(bool denied)
    {
        using var source = new Source(DiskImageRawProfileVerificationServiceTests.Raw(512));
        var result = await LinuxRawImageCaptureService.CaptureAsync(Request(), source, 512, Guid.NewGuid(),
            () => denied ? throw new UnauthorizedAccessException("private-parent") : throw new IOException("private-parent"));
        Assert.Equal(LinuxRawImageCaptureState.Failed, result.State);
        Assert.False(source.CopyStarted);
        Assert.Equal(0UL, result.CopiedBytes);
        Assert.DoesNotContain("private", result.Reason);
    }

    [Fact]
    public async Task PublicCancellationDoesNotAcquireAnyNativeDevice()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var expected = new LinuxBlockInventory(LinuxBlockInventoryState.UnsupportedPlatform, LinuxBlockInventoryReason.LinuxRequired,
            Guid.Empty, DateTimeOffset.UtcNow, Guid.Empty, null, false, Array.Empty<LinuxBlockObservation>());
        var result = await LinuxRawImageCaptureService.CaptureAsync(Request(), expected,
            "must-not-open", "/must-not-create", cancellation.Token);
        Assert.Equal(LinuxRawImageCaptureState.Interrupted, result.State);
    }
}
