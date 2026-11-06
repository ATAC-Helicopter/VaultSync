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

public sealed class DiskImageGptInspectionServiceTests
{
    private sealed record Partition(int EntryNumber, Guid TypeId, Guid PartitionId, ulong Offset, ulong Length);
    private sealed record Fixture(uint Sector, ulong Capacity, Guid DiskId, ulong FirstUsableLba, ulong LastUsableLba,
        string MbrHex, string PrimaryHex, string BackupHex, string EntriesHex,
        string CollisionTailHex, string MetadataSha256, string SourceSha256, Partition[] Partitions);

    private static async Task<Fixture> FixtureAsync(uint sector) => (JsonSerializer.Deserialize<Fixture[]>(
        await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "disk-gpt-v1.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!).Single(fixture => fixture.Sector == sector);

    private static byte[] Raw(Fixture fixture)
    {
        byte[] raw = new byte[fixture.Capacity];
        Convert.FromHexString(fixture.MbrHex).CopyTo(raw, 0);
        Convert.FromHexString(fixture.PrimaryHex).CopyTo(raw, fixture.Sector);
        byte[] entries = Convert.FromHexString(fixture.EntriesHex);
        entries.CopyTo(raw, fixture.Sector * 2);
        entries.CopyTo(raw, raw.Length - fixture.Sector - entries.Length);
        Convert.FromHexString(fixture.BackupHex).CopyTo(raw, raw.Length - fixture.Sector);
        return raw;
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public async Task IndependentGoldenLayoutPreservesExactIdsRangesAndPosition(uint sector)
    {
        Fixture fixture = await FixtureAsync(sector);
        byte[] raw = Raw(fixture);
        Assert.Equal(fixture.SourceSha256, Convert.ToHexStringLower(SHA256.HashData(raw)));
        using var source = new ObservedStream(raw) { Position = 37 };
        var result = await DiskImageGptInspectionService.InspectAsync(source, fixture.Capacity, sector);
        Assert.Equal(DiskImageGptInspectionState.LayoutInspected, result.State);
        Assert.Equal(DiskImageGptInspectionReason.LayoutOnly, result.Reason);
        Assert.Equal(fixture.DiskId, result.Layout!.DiskId);
        Assert.Equal(fixture.MetadataSha256, result.Layout.MetadataSha256);
        Assert.Equal(fixture.FirstUsableLba, result.Layout.FirstUsableLba);
        Assert.Equal(fixture.LastUsableLba, result.Layout.LastUsableLba);
        Assert.Equal(fixture.Partitions.Select(p => new DiskImageGptPartition(p.EntryNumber, p.TypeId, p.PartitionId, p.Offset, p.Length, 0)), result.Layout.Partitions);
        Assert.Equal(37, source.Position);
        Assert.Equal(3 * sector + 32768, source.BytesRead);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData("primary-crc", DiskImageGptInspectionReason.HeaderChecksumMismatch)]
    [InlineData("backup-crc", DiskImageGptInspectionReason.HeaderChecksumMismatch)]
    [InlineData("entry-crc", DiskImageGptInspectionReason.EntryChecksumMismatch)]
    [InlineData("backup-entry-crc", DiskImageGptInspectionReason.EntryChecksumMismatch)]
    [InlineData("entry-crc-collision", DiskImageGptInspectionReason.EntryCopiesDisagree)]
    [InlineData("identity", DiskImageGptInspectionReason.HeaderCopiesDisagree)]
    [InlineData("alternate", DiskImageGptInspectionReason.HeaderCopiesDisagree)]
    [InlineData("table-overlap", DiskImageGptInspectionReason.MetadataOverlap)]
    [InlineData("table-overflow", DiskImageGptInspectionReason.MetadataOverlap)]
    [InlineData("usable-overflow", DiskImageGptInspectionReason.MetadataOverlap)]
    [InlineData("partition-overlap", DiskImageGptInspectionReason.OverlappingPartitions)]
    [InlineData("partition-range", DiskImageGptInspectionReason.InvalidPartition)]
    [InlineData("partition-overflow", DiskImageGptInspectionReason.InvalidPartition)]
    [InlineData("partition-id", DiskImageGptInspectionReason.InvalidPartition)]
    [InlineData("empty-id", DiskImageGptInspectionReason.InvalidPartition)]
    [InlineData("attributes", DiskImageGptInspectionReason.InvalidPartition)]
    [InlineData("reserved", DiskImageGptInspectionReason.InvalidHeader)]
    [InlineData("protective-size", DiskImageGptInspectionReason.InvalidProtectiveMbr)]
    public async Task CorruptMetadataRefusesEvenWithRecomputedChecksums(string fault, DiskImageGptInspectionReason reason)
    {
        Fixture fixture = await FixtureAsync(512);
        byte[] raw = Raw(fixture);
        const int primary = 512, table = 1024;
        int backup = raw.Length - 512;
        switch (fault)
        {
            case "primary-crc": raw[primary + 24] ^= 1; break;
            case "backup-crc": raw[backup + 24] ^= 1; break;
            case "entry-crc": raw[table + 80] ^= 1; break;
            case "backup-entry-crc": raw[backup - 16384 + 80] ^= 1; break;
            case "entry-crc-collision":
                raw[backup - 16384 + 80] = 1;
                Convert.FromHexString(fixture.CollisionTailHex).CopyTo(raw, backup - 4);
                Assert.Equal(Crc(raw.AsSpan(table, 16384)), Crc(raw.AsSpan(backup - 16384, 16384)));
                break;
            case "identity": raw[backup + 56] ^= 1; FixHeader(raw, backup); break;
            case "alternate": Put64(raw, primary + 32, 100); FixHeader(raw, primary); break;
            case "table-overlap": Put64(raw, primary + 72, 30); FixHeader(raw, primary); break;
            case "table-overflow": Put64(raw, primary + 72, ulong.MaxValue); FixHeader(raw, primary); break;
            case "usable-overflow":
                Put64(raw, primary + 48, ulong.MaxValue); Put64(raw, backup + 48, ulong.MaxValue);
                FixHeader(raw, primary); FixHeader(raw, backup); break;
            case "partition-overlap": Put64(raw, table + 128 + 32, fixture.FirstUsableLba + 3); FixEntries(raw); break;
            case "partition-range": Put64(raw, table + 32, fixture.FirstUsableLba - 1); FixEntries(raw); break;
            case "partition-overflow": Put64(raw, table + 40, ulong.MaxValue); FixEntries(raw); break;
            case "partition-id": raw.AsSpan(table + 16, 16).CopyTo(raw.AsSpan(table + 128 + 16)); FixEntries(raw); break;
            case "empty-id": raw.AsSpan(table + 16, 16).Clear(); FixEntries(raw); break;
            case "attributes": Put64(raw, table + 48, 8); FixEntries(raw); break;
            case "reserved": raw[primary + 100] = 1; break;
            case "protective-size": raw[458] ^= 1; break;
        }
        using var source = new ObservedStream(raw) { Position = 19 };
        var result = await DiskImageGptInspectionService.InspectAsync(source, fixture.Capacity, fixture.Sector);
        Assert.Equal(DiskImageGptInspectionState.Invalid, result.State);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Layout);
        Assert.Equal(19, source.Position);
        Assert.True(source.BytesRead <= 3 * fixture.Sector + 32768);
    }

    [Theory]
    [InlineData("future-header", DiskImageGptInspectionReason.UnsupportedHeader)]
    [InlineData("extended-entry", DiskImageGptInspectionReason.UnsupportedEntryFormat)]
    [InlineData("entry-budget", DiskImageGptInspectionReason.UnsupportedEntryFormat)]
    [InlineData("hybrid-mbr", DiskImageGptInspectionReason.HybridMbrUnsupported)]
    [InlineData("legacy-mbr", DiskImageGptInspectionReason.ProtectiveMbrRequired)]
    public async Task UnsupportedLayoutsRefuseBeforeArrayReads(string fault, DiskImageGptInspectionReason reason)
    {
        Fixture fixture = await FixtureAsync(512);
        byte[] raw = Raw(fixture);
        if (fault == "future-header") BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(512 + 8), 0x20000);
        if (fault == "extended-entry") BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(512 + 84), 256);
        if (fault == "entry-budget") BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(512 + 80), uint.MaxValue);
        if (fault == "hybrid-mbr") raw[462 + 4] = 0x83;
        if (fault == "legacy-mbr") raw[450] = 0x83;
        FixHeader(raw, 512);
        using var source = new ObservedStream(raw);
        var result = await DiskImageGptInspectionService.InspectAsync(source, fixture.Capacity, fixture.Sector);
        Assert.Equal(DiskImageGptInspectionState.Unsupported, result.State);
        Assert.Equal(reason, result.Reason);
        Assert.Null(result.Layout);
        Assert.True(source.BytesRead <= 1024);
    }

    [Fact]
    public async Task InvalidGeometryAndLengthRefuseWithoutReads()
    {
        Fixture fixture = await FixtureAsync(512);
        using var source = new ObservedStream(Raw(fixture));
        foreach (var pair in new[] { (fixture.Capacity, 0U), (fixture.Capacity, 1024U), (ulong.MaxValue, 512U), (1UL, 512U), (0UL, 512U) })
        {
            var result = await DiskImageGptInspectionService.InspectAsync(source, pair.Item1, pair.Item2);
            Assert.Equal(DiskImageGptInspectionReason.InvalidGeometry, result.Reason);
        }
        Assert.Equal(DiskImageGptInspectionState.Incomplete, (await DiskImageGptInspectionService.InspectAsync(source, fixture.Capacity + 512, 512)).State);
        Assert.Equal(DiskImageGptInspectionState.Invalid, (await DiskImageGptInspectionService.InspectAsync(source, fixture.Capacity - 512, 512)).State);
        Assert.Equal(0, source.BytesRead);
    }

    [Fact]
    public async Task NonSeekableOrInitiallyCancelledSourcesAreNotRead()
    {
        Fixture fixture = await FixtureAsync(512);
        using var nonSeekable = new NonSeekableStream(Raw(fixture));
        var result = await DiskImageGptInspectionService.InspectAsync(nonSeekable, fixture.Capacity, 512);
        Assert.Equal(DiskImageGptInspectionReason.SeekableSourceRequired, result.Reason);
        Assert.Equal(0, nonSeekable.BytesRead);
        using var source = new ObservedStream(Raw(fixture));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DiskImageGptInspectionService.InspectAsync(source, fixture.Capacity, 512, cancelled.Token));
        Assert.Equal(0, source.BytesRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndTruncationRestorePosition(bool cancelled)
    {
        Fixture fixture = await FixtureAsync(512);
        using var cancellation = new CancellationTokenSource();
        using var source = new InterruptedStream(Raw(fixture), cancelled ? cancellation : null) { Position = 19 };
        if (cancelled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await DiskImageGptInspectionService.InspectAsync(source, fixture.Capacity, 512, cancellation.Token));
        else
            Assert.Equal(DiskImageGptInspectionState.Incomplete, (await DiskImageGptInspectionService.InspectAsync(source, fixture.Capacity, 512)).State);
        Assert.Equal(19, source.Position);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public async Task CaptureReconstructionPreservesIndependentlyInspectedLayout(uint sector)
    {
        Fixture fixture = await FixtureAsync(sector);
        using var source = new MemoryStream(Raw(fixture), writable: false);
        var partId = Guid.NewGuid();
        var capture = await DiskImageFileCaptureService.CaptureAsync(new(Guid.NewGuid(), Guid.NewGuid(), partId, 1, fixture.Capacity, sector), source);
        DiskImageFileReconstructionAssessment? restore = null;
        try
        {
            Assert.Equal(DiskImageFileCaptureState.FileFixtureCaptured, capture.State);
            await using var metadata = File.OpenRead(Path.Combine(capture.Workspace!, DiskImageFileCaptureService.MetadataFileName));
            await using var payload = File.OpenRead(Path.Combine(capture.Workspace!, DiskImageFileCaptureService.PayloadFileName));
            restore = await DiskImageFileReconstructionService.ReconstructAsync(metadata, new Dictionary<Guid, Stream> { [partId] = payload });
            Assert.Equal(DiskImageFileReconstructionState.FileFixtureReconstructed, restore.State);
            Assert.Equal(fixture.SourceSha256, restore.ContentSha256);
            await using var reconstructed = File.OpenRead(Path.Combine(restore.Workspace!, DiskImageFileReconstructionService.OutputFileName));
            var inspected = await DiskImageGptInspectionService.InspectAsync(reconstructed, fixture.Capacity, sector);
            Assert.Equal(DiskImageGptInspectionState.LayoutInspected, inspected.State);
            Assert.Equal(fixture.MetadataSha256, inspected.Layout!.MetadataSha256);
        }
        finally
        {
            if (capture.Workspace is not null) Directory.Delete(capture.Workspace, true);
            if (restore?.Workspace is not null) Directory.Delete(restore.Workspace, true);
        }
    }

    private static void Put64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
    private static void FixHeader(byte[] raw, int start)
    {
        raw.AsSpan(start + 16, 4).Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(start + 16), Crc(raw.AsSpan(start, 92)));
    }
    private static void FixEntries(byte[] raw)
    {
        raw.AsSpan(1024, 16384).CopyTo(raw.AsSpan(raw.Length - 512 - 16384));
        uint crc = Crc(raw.AsSpan(1024, 16384));
        foreach (int start in new[] { 512, raw.Length - 512 })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(start + 88), crc);
            FixHeader(raw, start);
        }
    }
    // Independent test CRC: non-reflected bit order and normal polynomial.
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
            for (int bit = 0; bit < 8; bit++)
            {
                bool top = ((crc >> 31) ^ ((uint)value >> bit)) % 2 != 0;
                crc <<= 1;
                if (top) crc ^= 0x04c11db7;
            }
        uint result = 0;
        for (int bit = 0; bit < 32; bit++) result = (result << 1) | ((crc >> bit) & 1);
        return ~result;
    }

    private class ObservedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public long BytesRead { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }
    private sealed class NonSeekableStream(byte[] bytes) : ObservedStream(bytes)
    {
        public override bool CanSeek => false;
    }
    private sealed class InterruptedStream(byte[] bytes, CancellationTokenSource? cancellation) : ObservedStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position != 0)
            {
                cancellation?.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(0);
            }
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
