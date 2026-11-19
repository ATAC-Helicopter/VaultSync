#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskImageFileReconstructionServiceTests
{
    private static readonly Guid PartId = Guid.Parse("3a3b4083-876d-4fb2-b6c3-878f54c47560");
    private sealed record Fixture(string Name, string MetadataHex, string PayloadHex, string Root, ulong VerifiedBytes);
    private static async Task<Fixture[]> Fixtures() => JsonSerializer.Deserialize<Fixture[]>(
        await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "disk-image-manifest-v1.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public async Task IndependentFixturesReconstructExactRawAndZeroBytes()
    {
        foreach (Fixture fixture in await Fixtures())
        {
            using var metadata = new MemoryStream(Convert.FromHexString(fixture.MetadataHex), writable: false);
            byte[] bytes = Convert.FromHexString(fixture.PayloadHex);
            using var payload = new MemoryStream(bytes, writable: false);
            var sources = new Dictionary<Guid, Stream>();
            if (bytes.Length != 0)
                sources.Add(PartId, payload);
            var result = await DiskImageFileReconstructionService.ReconstructAsync(metadata, sources);
            try
            {
                byte[] expected = fixture.Name switch
                {
                    "zero" => new byte[512],
                    "raw-zero" => [.. bytes, .. new byte[512]],
                    _ => bytes
                };
                Assert.Equal(DiskImageFileReconstructionState.FileFixtureReconstructed, result.State);
                Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(result.Workspace!, DiskImageFileReconstructionService.OutputFileName)));
                Assert.Equal((ulong)expected.Length, result.WrittenBytes);
                Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(expected)), result.ContentSha256);
                Assert.Equal(fixture.Root, result.ManifestRoot);
                Assert.True(metadata.CanRead);
                Assert.True(payload.CanRead);
            }
            finally { Cleanup(result); }
        }
    }

    [Fact]
    public async Task SourceMutationAfterItsLastReadCannotChangeReconstruction()
    {
        Fixture fixture = (await Fixtures())[0];
        byte[] expected = Convert.FromHexString(fixture.PayloadHex);
        byte[] mutable = (byte[])expected.Clone();
        using var metadata = new MemoryStream(Convert.FromHexString(fixture.MetadataHex), writable: false);
        using var payload = new MutateOnEofStream(mutable);
        var result = await DiskImageFileReconstructionService.ReconstructAsync(metadata, new Dictionary<Guid, Stream> { [PartId] = payload });
        try
        {
            Assert.NotEqual(expected[0], mutable[0]);
            Assert.Equal(DiskImageFileReconstructionState.FileFixtureReconstructed, result.State);
            Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(result.Workspace!, DiskImageFileReconstructionService.OutputFileName)));
        }
        finally { Cleanup(result); }
    }

    [Theory]
    [InlineData("metadata", DiskImageFileReconstructionState.Corrupt)]
    [InlineData("completion", DiskImageFileReconstructionState.Incomplete)]
    [InlineData("payload", DiskImageFileReconstructionState.Corrupt)]
    [InlineData("truncated", DiskImageFileReconstructionState.Incomplete)]
    [InlineData("missing", DiskImageFileReconstructionState.Incomplete)]
    public async Task DamagedInputsCannotCreateAReconstructedFile(string fault, DiskImageFileReconstructionState state)
    {
        Fixture fixture = (await Fixtures())[0];
        byte[] metadataBytes = Convert.FromHexString(fixture.MetadataHex);
        byte[] payloadBytes = Convert.FromHexString(fixture.PayloadHex);
        if (fault == "metadata") metadataBytes[^1] ^= 1;
        if (fault == "completion") metadataBytes = metadataBytes[..^1];
        if (fault == "payload") payloadBytes[100] ^= 1;
        if (fault == "truncated") payloadBytes = payloadBytes[..^1];
        using var metadata = new MemoryStream(metadataBytes, writable: false);
        using var payload = new MemoryStream(payloadBytes, writable: false);
        var sources = new Dictionary<Guid, Stream>();
        if (fault != "missing") sources.Add(PartId, payload);
        var result = await DiskImageFileReconstructionService.ReconstructAsync(metadata, sources);
        try
        {
            Assert.Equal(state, result.State);
            Assert.False(File.Exists(Path.Combine(result.Workspace!, DiskImageFileReconstructionService.OutputFileName)));
            Assert.Null(result.ContentSha256);
            Assert.Equal(0UL, result.WrittenBytes);
        }
        finally { Cleanup(result); }
    }

    [Fact]
    public async Task CancelledStagingPreservesNoReconstructedFile()
    {
        Fixture fixture = (await Fixtures())[0];
        using var metadata = new MemoryStream(Convert.FromHexString(fixture.MetadataHex), writable: false);
        using var cancellation = new CancellationTokenSource();
        using var payload = new CancelOnReadStream(Convert.FromHexString(fixture.PayloadHex), cancellation);
        var result = await DiskImageFileReconstructionService.ReconstructAsync(metadata,
            new Dictionary<Guid, Stream> { [PartId] = payload }, cancellation.Token);
        try
        {
            Assert.Equal(DiskImageFileReconstructionState.Interrupted, result.State);
            Assert.False(File.Exists(Path.Combine(result.Workspace!, DiskImageFileReconstructionService.OutputFileName)));
        }
        finally { Cleanup(result); }
    }

    [Fact]
    public async Task ExcessivePartCountRefusesBeforeMetadataReadsOrFileCreation()
    {
        using var metadata = new MemoryStream();
        using var payload = new MemoryStream();
        var sources = new Dictionary<Guid, Stream>();
        for (int index = 0; index <= DiskImageFileReconstructionService.MaximumFixtureParts; index++)
            sources.Add(Guid.NewGuid(), payload);
        var result = await DiskImageFileReconstructionService.ReconstructAsync(metadata, sources);
        Assert.Equal(DiskImageFileReconstructionState.Invalid, result.State);
        Assert.Null(result.Workspace);
        Assert.Equal(0, metadata.Position);
    }

    [Fact]
    public async Task OversizedZeroImageCannotAllocateAReconstructedFile()
    {
        Fixture fixture = (await Fixtures())[1];
        byte[] bytes = Convert.FromHexString(fixture.MetadataHex);
        ulong capacity = DiskImageFileReconstructionService.MaximumFixtureBytes + 512;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24 + 44), capacity);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24 + 64 + 8), capacity);
        SHA256.HashData(bytes.AsSpan(24, bytes.Length - 24 - 64)).CopyTo(bytes.AsSpan(bytes.Length - 32));
        using var metadata = new MemoryStream(bytes, writable: false);
        var result = await DiskImageFileReconstructionService.ReconstructAsync(metadata, new Dictionary<Guid, Stream>());
        try
        {
            Assert.Equal(DiskImageFileReconstructionState.Invalid, result.State);
            Assert.Equal("reconstruct.fixture-size-limit", result.Reason);
            Assert.False(File.Exists(Path.Combine(result.Workspace!, DiskImageFileReconstructionService.OutputFileName)));
        }
        finally { Cleanup(result); }
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(4096, true)]
    [InlineData(512, false)]
    [InlineData(4096, false)]
    public async Task CapacityIsCheckedBeforeStagingRawPayload(uint sector, bool oversized)
    {
        Fixture fixture = (await Fixtures())[0];
        byte[] bytes = Convert.FromHexString(fixture.MetadataHex);
        ulong capacity = DiskImageFileReconstructionService.MaximumFixtureBytes + (oversized ? sector : 0UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24 + 44), capacity);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + 52), sector);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24 + 80), capacity);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24 + 128), capacity);
        SHA256.HashData(bytes.AsSpan(24, bytes.Length - 24 - 64)).CopyTo(bytes.AsSpan(bytes.Length - 32));
        using var metadata = new MemoryStream(bytes, writable: false);
        // A short source makes the historical read-before-refusal observable without allocating 1 GiB.
        using var payload = new MemoryStream(new byte[sector], writable: false);
        var result = await DiskImageFileReconstructionService.ReconstructAsync(metadata,
            new Dictionary<Guid, Stream> { [PartId] = payload });
        try
        {
            Assert.Equal(oversized ? DiskImageFileReconstructionState.Invalid : DiskImageFileReconstructionState.Incomplete, result.State);
            Assert.Equal(oversized ? "reconstruct.fixture-size-limit" : "image.truncated-part", result.Reason);
            Assert.Equal(oversized ? 0 : sector, payload.Position);
            Assert.False(File.Exists(Path.Combine(result.Workspace!, DiskImageFileReconstructionService.OutputFileName)));
            Assert.Equal(oversized ? 0 : sector, new FileInfo(Path.Combine(result.Workspace!, PartId.ToString("N") + ".raw")).Length);
        }
        finally { Cleanup(result); }
    }

    [Fact]
    public async Task CapturedSyntheticImageReconstructsWithoutOriginalSource()
    {
        byte[] expected = new byte[512 * 513];
        for (int index = 0; index < expected.Length; index++) expected[index] = (byte)(index % 251);
        var request = new DiskImageFileCaptureRequest(Guid.NewGuid(), Guid.NewGuid(), PartId, 1, (ulong)expected.Length, 512);
        DiskImageFileCaptureAssessment captured;
        using (var source = new MemoryStream(expected, writable: false))
            captured = await DiskImageFileCaptureService.CaptureAsync(request, source);
        try
        {
            Assert.Equal(DiskImageFileCaptureState.FileFixtureCaptured, captured.State);
            using var metadata = File.OpenRead(Path.Combine(captured.Workspace!, DiskImageFileCaptureService.MetadataFileName));
            using var payload = File.OpenRead(Path.Combine(captured.Workspace!, DiskImageFileCaptureService.PayloadFileName));
            var reconstructed = await DiskImageFileReconstructionService.ReconstructAsync(metadata, new Dictionary<Guid, Stream> { [PartId] = payload });
            try
            {
                Assert.Equal(DiskImageFileReconstructionState.FileFixtureReconstructed, reconstructed.State);
                Assert.Equal(captured.ManifestRoot, reconstructed.ManifestRoot);
                Assert.Equal(expected, await File.ReadAllBytesAsync(Path.Combine(reconstructed.Workspace!, DiskImageFileReconstructionService.OutputFileName)));
            }
            finally { Cleanup(reconstructed); }
        }
        finally { if (captured.Workspace is not null) Directory.Delete(captured.Workspace, recursive: true); }
    }

    private static void Cleanup(DiskImageFileReconstructionAssessment result)
    {
        if (result.Workspace is not null) Directory.Delete(result.Workspace, recursive: true);
    }

    private sealed class MutateOnEofStream : MemoryStream
    {
        private readonly byte[] mutable;
        public MutateOnEofStream(byte[] bytes) : base(bytes, writable: false) { mutable = bytes; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await base.ReadAsync(buffer, cancellationToken);
            if (count == 0) mutable[0] ^= 1;
            return count;
        }
    }

    private sealed class CancelOnReadStream(byte[] bytes, CancellationTokenSource cancellation) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
