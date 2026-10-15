#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskImageOriginTests
{
    private sealed record OriginFixture(uint Sector, string MetadataHex, Guid PartId, Guid SourceId,
        Guid ObservationId, long TimestampMilliseconds, string ToolVersion, string ToolSourceCommit, string ManifestRoot);
    private static async Task<OriginFixture> Fixture(uint sector) => (await Load<OriginFixture[]>("disk-image-origin-v2.json")).Single(x => x.Sector == sector);
    private static async Task<T> Load<T>(string name) => JsonSerializer.Deserialize<T>(
        await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    private static async Task<byte[]> Raw(uint sector)
    {
        JsonElement element = (await Load<JsonElement>("disk-gpt-v1.json")).EnumerateArray().Single(x => x.GetProperty("sector").GetUInt32() == sector);
        byte[] raw = new byte[element.GetProperty("capacity").GetInt32()];
        byte[] entries = Convert.FromHexString(element.GetProperty("entriesHex").GetString()!);
        Convert.FromHexString(element.GetProperty("mbrHex").GetString()!).CopyTo(raw, 0);
        Convert.FromHexString(element.GetProperty("primaryHex").GetString()!).CopyTo(raw, sector);
        Convert.FromHexString(element.GetProperty("backupHex").GetString()!).CopyTo(raw, raw.Length - sector);
        entries.CopyTo(raw, 2 * sector);
        entries.CopyTo(raw, raw.Length - sector - entries.Length);
        return raw;
    }
    private static void Rehash(byte[] bytes) => SHA256.HashData(bytes.AsSpan(24, bytes.Length - 88)).CopyTo(bytes.AsSpan(bytes.Length - 32));
    private static Dictionary<Guid, Stream> Parts(Guid id, Stream source) => new() { [id] = source };
    private static void Cleanup(string? path) { if (path is not null) Directory.Delete(path, true); }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public async Task IndependentOriginFixtureVerifiesAndReconstructsWithoutOriginalSource(uint sector)
    {
        OriginFixture fixture = await Fixture(sector);
        byte[] raw = await Raw(sector);
        using var metadata = new MemoryStream(Convert.FromHexString(fixture.MetadataHex), false);
        using var part = new MemoryStream(raw, false);
        var verified = await DiskImageManifestVerificationService.VerifyAsync(metadata, Parts(fixture.PartId, part));
        Assert.Equal(DiskImageManifestVerificationState.ContentVerified, verified.State);
        Assert.Equal(fixture.ManifestRoot, verified.ManifestRoot);
        var origin = verified.DeclaredOrigin!;
        Assert.Equal(fixture.SourceId, origin.SourceId);
        Assert.Equal(fixture.ObservationId, origin.ObservationId);
        Assert.Equal(fixture.TimestampMilliseconds, origin.CapturedAtUtc.ToUnixTimeMilliseconds());
        Assert.Equal(TimeSpan.Zero, origin.CapturedAtUtc.Offset);
        Assert.Equal(fixture.ToolVersion, origin.ToolVersion);
        Assert.Equal(fixture.ToolSourceCommit, origin.ToolSourceCommit);
        Assert.Equal(sector, origin.PhysicalSectorBytes);
        Assert.Equal(2, origin.Layout.Partitions.Count);
        metadata.Position = 0; part.Position = 0;
        var result = await DiskImageFileReconstructionService.ReconstructAsync(metadata, Parts(fixture.PartId, part));
        try
        {
            Assert.Equal(DiskImageFileReconstructionState.FileFixtureReconstructed, result.State);
            Assert.Equal(raw, await File.ReadAllBytesAsync(Path.Combine(result.Workspace!, DiskImageFileReconstructionService.OutputFileName)));
        }
        finally { Cleanup(result.Workspace); }
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public async Task GptCaptureWritesBoundOriginAndChecksCopiedLayout(uint sector)
    {
        OriginFixture fixture = await Fixture(sector);
        byte[] raw = await Raw(sector);
        var request = Request(fixture, (ulong)raw.Length);
        using var source = new MemoryStream(raw, false);
        DateTimeOffset started = DateTimeOffset.UtcNow.AddMilliseconds(-1);
        var capture = await DiskImageFileCaptureService.CaptureGptFixtureAsync(request, source);
        DiskImageFileReconstructionAssessment? restored = null;
        try
        {
            Assert.Equal(DiskImageFileCaptureState.FileFixtureCaptured, capture.State);
            Assert.Equal((ulong)raw.Length, capture.WrittenBytes);
            await using var metadata = File.OpenRead(Path.Combine(capture.Workspace!, DiskImageFileCaptureService.MetadataFileName));
            await using var payload = File.OpenRead(Path.Combine(capture.Workspace!, DiskImageFileCaptureService.PayloadFileName));
            var read = await DiskImageManifestVerificationService.VerifyAsync(metadata, Parts(fixture.PartId, payload));
            Assert.Equal(DiskImageManifestVerificationState.ContentVerified, read.State);
            Assert.Equal(capture.ManifestRoot, read.ManifestRoot);
            Assert.Equal(request.SourceId, read.DeclaredOrigin!.SourceId);
            Assert.Equal(request.ToolVersion, read.DeclaredOrigin.ToolVersion);
            Assert.InRange(read.DeclaredOrigin.CapturedAtUtc, started, DateTimeOffset.UtcNow);
            metadata.Position = 0; payload.Position = 0;
            restored = await DiskImageFileReconstructionService.ReconstructAsync(metadata, Parts(fixture.PartId, payload));
            Assert.Equal(DiskImageFileReconstructionState.FileFixtureReconstructed, restored.State);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(raw)), restored.ContentSha256);
        }
        finally { Cleanup(capture.Workspace); Cleanup(restored?.Workspace); }
    }

    [Theory]
    [InlineData("origin-schema", true)]
    [InlineData("source-kind", true)]
    [InlineData("source-id", false)]
    [InlineData("observation-id", false)]
    [InlineData("physical-sector", false)]
    [InlineData("timestamp", false)]
    [InlineData("timestamp-overflow", false)]
    [InlineData("partition-count", false)]
    [InlineData("partition-reserved", false)]
    [InlineData("partition-overflow", false)]
    [InlineData("partition-duplicate", false)]
    [InlineData("partition-overlap", false)]
    [InlineData("version-padding", false)]
    [InlineData("version-control", false)]
    [InlineData("commit", false)]
    [InlineData("map", true)]
    public async Task MalformedOriginRefusesBeforePayloadReadsEvenWithValidCompletion(string fault, bool unsupported)
    {
        OriginFixture fixture = await Fixture(512);
        byte[] bytes = Convert.FromHexString(fixture.MetadataHex);
        const int origin = 24 + 64, partition = origin + 216;
        switch (fault)
        {
            case "origin-schema": bytes[origin] = 2; break;
            case "source-kind": bytes[origin + 4] = 2; break;
            case "source-id": bytes.AsSpan(origin + 8, 16).Clear(); break;
            case "observation-id": bytes.AsSpan(origin + 24, 16).Clear(); break;
            case "physical-sector": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(origin + 40), 1024); break;
            case "timestamp": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(origin + 44), -1); break;
            case "timestamp-overflow": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(origin + 44), long.MaxValue); break;
            case "partition-count": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(origin + 116), uint.MaxValue); break;
            case "partition-reserved": bytes[partition + 60] = 1; break;
            case "partition-overflow": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(partition + 44), ulong.MaxValue); break;
            case "partition-duplicate": bytes.AsSpan(partition + 20, 16).CopyTo(bytes.AsSpan(partition + 64 + 20)); break;
            case "partition-overlap": bytes.AsSpan(partition + 36, 8).CopyTo(bytes.AsSpan(partition + 64 + 36)); break;
            case "version-padding": bytes[origin + 151] = (byte)'x'; break;
            case "version-control": bytes[origin + 120] = 10; break;
            case "commit": bytes[origin + 152] = (byte)'G'; break;
            case "map": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + 60), 2); break;
        }
        Rehash(bytes);
        using var metadata = new MemoryStream(bytes, false);
        using var payload = new MemoryStream(await Raw(512), false);
        var result = await DiskImageManifestVerificationService.VerifyAsync(metadata, Parts(fixture.PartId, payload));
        Assert.Equal(unsupported ? DiskImageManifestVerificationState.Unsupported : DiskImageManifestVerificationState.Invalid, result.State);
        Assert.Equal(0, payload.Position);
        Assert.Null(result.DeclaredOrigin);
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("disk-id")]
    [InlineData("first-lba")]
    [InlineData("partition-type")]
    public async Task SelfConsistentFalseLayoutClaimsCannotCompleteReconstruction(string fault)
    {
        OriginFixture fixture = await Fixture(512);
        byte[] bytes = Convert.FromHexString(fixture.MetadataHex);
        const int origin = 88;
        if (fault == "digest") bytes[origin + 68] ^= 1;
        if (fault == "disk-id") bytes[origin + 52] ^= 1;
        if (fault == "first-lba") bytes[origin + 100] += 1;
        if (fault == "partition-type") bytes[origin + 216 + 4] ^= 1;
        Rehash(bytes); // An attacker can recompute an unkeyed checksum.
        using var metadata = new MemoryStream(bytes, false);
        using var payload = new MemoryStream(await Raw(512), false);
        var bound = await DiskImageManifestVerificationService.VerifyAsync(metadata, Parts(fixture.PartId, payload));
        Assert.Equal(DiskImageManifestVerificationState.ContentVerified, bound.State); // Declared claims, not observed layout.
        metadata.Position = 0; payload.Position = 0;
        var result = await DiskImageFileReconstructionService.ReconstructAsync(metadata, Parts(fixture.PartId, payload));
        try
        {
            Assert.Equal(DiskImageFileReconstructionState.Corrupt, result.State);
            Assert.Equal("reconstruct.layout-mismatch", result.Reason);
            Assert.Null(result.ContentSha256);
        }
        finally { Cleanup(result.Workspace); }
    }

    [Fact]
    public async Task AlteredOriginWithoutNewCompletionRootRefusesBeforePayloadAccess()
    {
        OriginFixture fixture = await Fixture(512);
        byte[] bytes = Convert.FromHexString(fixture.MetadataHex);
        bytes[88 + 24] ^= 1;
        using var metadata = new MemoryStream(bytes, false);
        using var payload = new MemoryStream(await Raw(512), false);
        var result = await DiskImageManifestVerificationService.VerifyAsync(metadata, Parts(fixture.PartId, payload));
        Assert.Equal(DiskImageManifestVerificationState.Corrupt, result.State);
        Assert.Equal("image.manifest-root-mismatch", result.Reason);
        Assert.Equal(0, payload.Position);
    }

    [Fact]
    public async Task ToolClaimsRemainUnauthenticatedEvenWhenContentAndLayoutMatch()
    {
        OriginFixture fixture = await Fixture(512);
        byte[] bytes = Convert.FromHexString(fixture.MetadataHex);
        bytes[88 + 152] = (byte)'f';
        Rehash(bytes);
        using var metadata = new MemoryStream(bytes, false);
        using var payload = new MemoryStream(await Raw(512), false);
        var result = await DiskImageManifestVerificationService.VerifyAsync(metadata, Parts(fixture.PartId, payload));
        Assert.Equal(DiskImageManifestVerificationState.ContentVerified, result.State);
        Assert.StartsWith("f", result.DeclaredOrigin!.ToolSourceCommit);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("tool")]
    [InlineData("capacity")]
    [InlineData("physical")]
    public async Task InvalidCaptureRequestsAllocateNothingAndReadNothing(string fault)
    {
        OriginFixture fixture = await Fixture(512);
        byte[] raw = await Raw(512);
        var request = Request(fixture, (ulong)raw.Length);
        request = fault switch
        {
            "source" => request with { SourceId = Guid.Empty },
            "tool" => request with { ToolVersion = "unsafe\nversion" },
            "capacity" => request with { Image = request.Image with { Capacity = DiskImageFileCaptureService.MaximumFixtureBytes + 512 } },
            _ => request with { PhysicalSectorBytes = 1024 }
        };
        using var source = new CountingStream(raw);
        var result = await DiskImageFileCaptureService.CaptureGptFixtureAsync(request, source);
        Assert.Equal(DiskImageFileCaptureState.Invalid, result.State);
        Assert.Null(result.Workspace);
        Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task ChangedLayoutDuringCopyCannotPublishCompletion()
    {
        OriginFixture fixture = await Fixture(512);
        byte[] raw = await Raw(512);
        using var source = new ChangeOnCopyStream(raw);
        var result = await DiskImageFileCaptureService.CaptureGptFixtureAsync(Request(fixture, (ulong)raw.Length), source);
        try
        {
            Assert.Equal(DiskImageFileCaptureState.Invalid, result.State);
            Assert.Equal("capture.source-layout-changed", result.Reason);
            Assert.Null(result.ManifestRoot);
            Assert.Equal(24, new FileInfo(Path.Combine(result.Workspace!, DiskImageFileCaptureService.MetadataFileName)).Length);
        }
        finally { Cleanup(result.Workspace); }
    }

    [Fact]
    public async Task CancelledPreinspectionCreatesNoWorkspace()
    {
        OriginFixture fixture = await Fixture(512);
        byte[] raw = await Raw(512);
        using var source = new MemoryStream(raw, false);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var result = await DiskImageFileCaptureService.CaptureGptFixtureAsync(Request(fixture, (ulong)raw.Length), source, cancelled.Token);
        Assert.Equal(DiskImageFileCaptureState.Interrupted, result.State);
        Assert.Null(result.Workspace);
        Assert.Equal(0, source.Position);
    }

    private static DiskImageGptFileCaptureRequest Request(OriginFixture fixture, ulong capacity) =>
        new(new(Guid.NewGuid(), Guid.NewGuid(), fixture.PartId, 1, capacity, fixture.Sector),
            fixture.SourceId, fixture.ObservationId, fixture.Sector, fixture.ToolVersion, fixture.ToolSourceCommit);

    private class CountingStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        public int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Reads++; return base.ReadAsync(buffer, cancellationToken); }
    }
    private sealed class ChangeOnCopyStream : CountingStream
    {
        private readonly byte[] bytes;
        private int starts;
        public ChangeOnCopyStream(byte[] bytes) : base(bytes) { this.bytes = bytes; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0 && ++starts == 2) bytes[0] ^= 1; // Boot-code byte: still valid GPT, different observed metadata.
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
