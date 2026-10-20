#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRawImageStagingServiceTests
{
    private sealed class Storage : LinuxRawImageStagingService.IStorage
    {
        internal readonly MemoryStream WritablePayload = new(), WritableMetadata = new();
        private MemoryStream? sealedPayload, sealedMetadata;
        internal readonly List<string> Steps = new();
        internal string? Fault;
        internal Action<string>? Hook;
        internal bool Disposed;
        public Stream Payload => sealedPayload ?? WritablePayload;
        public Stream Metadata => sealedMetadata ?? WritableMetadata;
        public bool Published { get; private set; }
        private void Step(string step)
        {
            Steps.Add(step); Hook?.Invoke(step);
            if (Fault == step) throw new IOException("private-path-secret");
        }
        public void FlushPayload() => Step("flush-payload");
        public void FlushMetadata() => Step("flush-metadata");
        public void FlushDirectories() => Step("flush-directories");
        public void Seal()
        {
            Step("seal");
            if (Fault == "still-writable") return;
            byte[] raw = WritablePayload.ToArray();
            if (Fault == "sealed-corrupt") raw[raw.Length / 2] ^= 1;
            sealedPayload = new(raw, writable: false);
            sealedMetadata = new(WritableMetadata.ToArray(), writable: false);
            WritablePayload.Dispose(); WritableMetadata.Dispose();
        }
        public void Publish() { Step("rename"); Published = true; Step("parent-flush"); }
        public ValueTask DisposeAsync()
        {
            Disposed = true; WritablePayload.Dispose(); WritableMetadata.Dispose();
            sealedPayload?.Dispose(); sealedMetadata?.Dispose(); Step("dispose");
            return ValueTask.CompletedTask;
        }
    }

    private static byte[] Metadata(uint sector = 512) => Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid(sector).MetadataHex);

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public async Task StagedSessionOwnsOnlyVerifiedReadOnlyBytesAndOutlivesCallerStreams(uint sector)
    {
        byte[] sourceBytes = DiskImageRawProfileVerificationServiceTests.Raw(sector), metadataBytes = Metadata(sector);
        var source = new MemoryStream(sourceBytes, writable: true);
        var metadata = new MemoryStream(metadataBytes, writable: true);
        var storage = new Storage();
        var result = await LinuxRawImageStagingService.StageAsync(metadata, source, capacity =>
        {
            Assert.Equal((ulong)source.Length, capacity); return storage;
        });
        Assert.Equal(LinuxRawImageStagingState.StagedVerified, result.State);
        Assert.NotNull(result.Session);
        Assert.False(storage.Disposed);
        Assert.False(result.Session.Payload.CanWrite); Assert.False(result.Session.Metadata.CanWrite);
        sourceBytes.AsSpan().Fill(0xff); metadataBytes.AsSpan().Fill(0xff);
        source.Dispose(); metadata.Dispose();
        await using (var session = result.Session)
        {
            var verified = await DiskImageRawProfileVerificationService.VerifyAsync(session.Metadata, session.Payload);
            Assert.Equal(DiskImageRawProfileVerificationState.ContentAndLayoutVerified, verified.State);
            Assert.Equal(session.Image.ManifestRoot, verified.Image!.ManifestRoot);
        }
        Assert.True(storage.Disposed);
        Assert.Equal(new[] { "flush-payload", "flush-metadata", "seal", "flush-directories", "rename", "parent-flush", "dispose" }, storage.Steps);
    }

    [Fact]
    public async Task ChangingInputAfterInitialVerificationCannotProduceAUsableStage()
    {
        byte[] sourceBytes = DiskImageRawProfileVerificationServiceTests.Raw(512);
        using var source = new MemoryStream(sourceBytes, writable: true);
        using var metadata = new MemoryStream(Metadata());
        var storage = new Storage();
        var result = await LinuxRawImageStagingService.StageAsync(metadata, source, _ =>
        {
            sourceBytes[sourceBytes.Length / 2] ^= 1; return storage;
        });
        Assert.Equal(LinuxRawImageStagingState.Refused, result.State);
        Assert.Null(result.Session); Assert.False(storage.Published); Assert.True(storage.Disposed);
        Assert.DoesNotContain("seal", storage.Steps);
    }

    [Theory]
    [InlineData("still-writable")]
    [InlineData("sealed-corrupt")]
    public async Task ReadOnlyReopenMustPreserveVerifiedBytes(string fault)
    {
        using var source = new MemoryStream(DiskImageRawProfileVerificationServiceTests.Raw(512));
        using var metadata = new MemoryStream(Metadata());
        var storage = new Storage { Fault = fault };
        var result = await LinuxRawImageStagingService.StageAsync(metadata, source, _ => storage);
        Assert.Equal(LinuxRawImageStagingState.Refused, result.State);
        Assert.Null(result.Session); Assert.False(storage.Published); Assert.True(storage.Disposed);
    }

    [Theory]
    [InlineData("flush-payload")]
    [InlineData("flush-metadata")]
    [InlineData("seal")]
    [InlineData("flush-directories")]
    [InlineData("rename")]
    [InlineData("parent-flush")]
    public async Task IoFailuresReturnNoSession(string fault)
    {
        using var source = new MemoryStream(DiskImageRawProfileVerificationServiceTests.Raw(512));
        using var metadata = new MemoryStream(Metadata());
        var storage = new Storage { Fault = fault };
        var result = await LinuxRawImageStagingService.StageAsync(metadata, source, _ => storage);
        Assert.Equal(fault == "parent-flush" ? LinuxRawImageStagingState.PublicationUnconfirmed : LinuxRawImageStagingState.Failed, result.State);
        Assert.Null(result.Session); Assert.True(storage.Disposed);
        Assert.DoesNotContain("private", result.Reason);
    }

    [Theory]
    [InlineData("flush-payload")]
    [InlineData("seal")]
    [InlineData("flush-directories")]
    public async Task CancellationBeforePublicationReturnsNoStagingCapability(string phase)
    {
        using var source = new MemoryStream(DiskImageRawProfileVerificationServiceTests.Raw(512));
        using var metadata = new MemoryStream(Metadata());
        using var cancellation = new CancellationTokenSource();
        var storage = new Storage { Hook = step => { if (step == phase) cancellation.Cancel(); } };
        var result = await LinuxRawImageStagingService.StageAsync(metadata, source, _ => storage, cancellation.Token);
        Assert.Equal(LinuxRawImageStagingState.Interrupted, result.State);
        Assert.Null(result.Session); Assert.False(storage.Published); Assert.True(storage.Disposed);
    }

    [Fact]
    public async Task InvalidInputNeverAllocatesStorage()
    {
        using var source = new MemoryStream(DiskImageRawProfileVerificationServiceTests.Raw(512));
        byte[] bytes = Metadata(); bytes[^1] ^= 1;
        using var metadata = new MemoryStream(bytes);
        var result = await LinuxRawImageStagingService.StageAsync(metadata, source,
            _ => throw new InvalidOperationException("must not allocate"));
        Assert.Equal(LinuxRawImageStagingState.Refused, result.State); Assert.Null(result.Session);
    }

    [Fact]
    public async Task MetadataLimitRefusesBeforePayloadOrStorageAccess()
    {
        using var source = new MemoryStream();
        using var metadata = new MemoryStream(new byte[DiskImageRawProfileVerificationService.MaximumMetadataBytes + 1]);
        var result = await LinuxRawImageStagingService.StageAsync(metadata, source,
            _ => throw new InvalidOperationException("must not allocate"));
        Assert.Equal(LinuxRawImageStagingState.Invalid, result.State); Assert.Null(result.Session);
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public async Task CancellationAfterCommitStillReturnsThePublishedSession()
    {
        using var source = new MemoryStream(DiskImageRawProfileVerificationServiceTests.Raw(512));
        using var metadata = new MemoryStream(Metadata());
        using var cancellation = new CancellationTokenSource();
        var storage = new Storage { Hook = step => { if (step == "parent-flush") cancellation.Cancel(); } };
        var result = await LinuxRawImageStagingService.StageAsync(metadata, source, _ => storage, cancellation.Token);
        Assert.Equal(LinuxRawImageStagingState.StagedVerified, result.State);
        Assert.NotNull(result.Session); await result.Session.DisposeAsync();
    }
}
