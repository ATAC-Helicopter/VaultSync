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

public sealed class DiskImageRawProfileVerificationServiceTests
{
    public sealed record Fixture(string Name, uint Sector, string MetadataHex, string State, bool ReadsPayload, string? PayloadFault);
    private static T Load<T>(string name) => JsonSerializer.Deserialize<T>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    public static IEnumerable<object[]> Corpus() => Load<Fixture[]>("disk-image-raw-profile-v1.json").Select(f => new object[] { f });
    private static Fixture Valid(uint sector = 512) => Load<Fixture[]>("disk-image-raw-profile-v1.json").Single(f => f.Sector == sector && f.Name == "valid");
    private static byte[] Raw(uint sector)
    {
        var gpt = Load<JsonElement>("disk-gpt-v1.json").EnumerateArray().Single(g => g.GetProperty("sector").GetUInt32() == sector);
        byte[] raw = new byte[gpt.GetProperty("capacity").GetInt32()];
        byte[] entries = Convert.FromHexString(gpt.GetProperty("entriesHex").GetString()!);
        Convert.FromHexString(gpt.GetProperty("mbrHex").GetString()!).CopyTo(raw, 0);
        Convert.FromHexString(gpt.GetProperty("primaryHex").GetString()!).CopyTo(raw, sector);
        Convert.FromHexString(gpt.GetProperty("backupHex").GetString()!).CopyTo(raw, raw.Length - sector);
        entries.CopyTo(raw, 2 * sector);
        entries.CopyTo(raw, raw.Length - sector - entries.Length);
        return raw;
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task PermanentIndependentCorpusRefusesBadImages(Fixture fixture)
    {
        byte[] raw = Raw(fixture.Sector);
        if (fixture.PayloadFault == "flip-middle") raw[raw.Length / 2] ^= 1;
        using var payload = new ObservedStream(raw);
        using var metadata = new MemoryStream(Convert.FromHexString(fixture.MetadataHex), false);
        var result = await DiskImageRawProfileVerificationService.VerifyAsync(metadata, payload);
        Assert.Equal(Enum.Parse<DiskImageRawProfileVerificationState>(fixture.State), result.State);
        Assert.Equal(fixture.ReadsPayload, payload.Reads > 0);
        if (result.State == DiskImageRawProfileVerificationState.ContentAndLayoutVerified)
        {
            Assert.NotNull(result.Image);
            Assert.Equal((ulong)raw.Length, result.Image.VerifiedBytes);
            Assert.Equal(DiskImageSourceKind.OfflineBlockDevice, result.Image.DeclaredOrigin!.SourceKind);
            Assert.Equal(2, result.Image.DeclaredOrigin.Layout.Partitions.Count);
            Assert.False(payload.WasWritten);
        }
        else Assert.Null(result.Image);
    }

    [Theory]
    [InlineData(-1, DiskImageRawProfileVerificationState.Incomplete)]
    [InlineData(1, DiskImageRawProfileVerificationState.Corrupt)]
    public async Task LengthMismatchRefusesBeforeReadingPayload(int change, DiskImageRawProfileVerificationState state)
    {
        using var metadata = new MemoryStream(Convert.FromHexString(Valid().MetadataHex));
        using var payload = new ObservedStream(new byte[Raw(512).Length + change]);
        Assert.Equal(state, (await DiskImageRawProfileVerificationService.VerifyAsync(metadata, payload)).State);
        Assert.Equal(0, payload.Reads);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutPayloadAccess()
    {
        using var metadata = new MemoryStream(Convert.FromHexString(Valid().MetadataHex));
        using var payload = new ObservedStream(Raw(512));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DiskImageRawProfileVerificationService.VerifyAsync(metadata, payload, cancellation.Token));
        Assert.Equal(0, payload.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFailuresReturnNoVerifiedImage(bool denied)
    {
        using var metadata = new MemoryStream(Convert.FromHexString(Valid().MetadataHex));
        using var payload = new ObservedStream(Raw(512)) { FailRead = true, Denied = denied };
        var result = await DiskImageRawProfileVerificationService.VerifyAsync(metadata, payload);
        Assert.Equal(DiskImageRawProfileVerificationState.Failed, result.State);
        Assert.Null(result.Image);
    }

    [Fact]
    public async Task GptTableSlotLimitAppliesEvenWithRecomputedPayloadAndManifestHashes()
    {
        Fixture fixture = Valid(4096);
        byte[] raw = Raw(4096), metadataBytes = Convert.FromHexString(fixture.MetadataHex);
        // A checksummed header declaring a larger table must refuse before table allocation.
        foreach (int offset in new[] { 4096, raw.Length - 4096 })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset + 80), 256);
            raw.AsSpan(offset + 16, 4).Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(offset + 16), Crc32(raw.AsSpan(offset, 92)));
        }
        const int partRecord = 24 + 64 + 216 + 2 * 64;
        SHA256.HashData(raw).CopyTo(metadataBytes, partRecord + 24);
        SHA256.HashData(metadataBytes.AsSpan(24, metadataBytes.Length - 88)).CopyTo(metadataBytes, metadataBytes.Length - 32);
        using var metadata = new MemoryStream(metadataBytes);
        using var payload = new ObservedStream(raw);
        var result = await DiskImageRawProfileVerificationService.VerifyAsync(metadata, payload);
        Assert.Equal(DiskImageRawProfileVerificationState.Unsupported, result.State);
        Assert.Equal("raw.invalid-payload-gpt", result.Reason);
        Assert.Null(result.Image);
    }

    [Fact]
    public async Task CallerMustProvideDistinctRewoundSeekablePayload()
    {
        using var metadata = new MemoryStream(Convert.FromHexString(Valid().MetadataHex));
        using var payload = new ObservedStream(Raw(512)); payload.Position = 1;
        Assert.Equal(DiskImageRawProfileVerificationState.Invalid,
            (await DiskImageRawProfileVerificationService.VerifyAsync(metadata, payload)).State);
        payload.Position = 0;
        Assert.Equal(DiskImageRawProfileVerificationState.Invalid,
            (await DiskImageRawProfileVerificationService.VerifyAsync(payload, payload)).State);
        payload.Seekable = false;
        Assert.Equal(DiskImageRawProfileVerificationState.Unsupported,
            (await DiskImageRawProfileVerificationService.VerifyAsync(metadata, payload)).State);
        Assert.Equal(0, payload.Reads);
    }

    [Fact]
    public async Task FragmentedNonseekableMetadataRemainsReadableWithoutTakingOwnership()
    {
        using var metadata = new ObservedStream(Convert.FromHexString(Valid().MetadataHex))
            { Seekable = false, MaximumRead = 3 };
        using var payload = new ObservedStream(Raw(512)) { MaximumRead = 257 };
        var result = await DiskImageRawProfileVerificationService.VerifyAsync(metadata, payload);
        Assert.Equal(DiskImageRawProfileVerificationState.ContentAndLayoutVerified, result.State);
        Assert.True(metadata.Reads > 100);
        Assert.True(metadata.CanRead);
        Assert.True(payload.CanRead);
        Assert.False(metadata.WasWritten);
        Assert.False(payload.WasWritten);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte b in bytes)
        {
            value ^= b;
            for (int bit = 0; bit < 8; bit++) value = (value >> 1) ^ (0xedb88320U & (uint)-(int)(value & 1));
        }
        return ~value;
    }

    private sealed class ObservedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public int Reads { get; private set; }
        public bool WasWritten { get; private set; }
        public bool Seekable { get; set; } = true;
        public int MaximumRead { get; init; } = int.MaxValue;
        public bool FailRead { get; init; }
        public bool Denied { get; init; }
        public override bool CanSeek => Seekable;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reads++;
            if (FailRead) throw Denied ? new UnauthorizedAccessException("synthetic private detail") : new IOException("synthetic private detail");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, MaximumRead)], cancellationToken);
        }
        public override void Write(byte[] buffer, int offset, int count) { WasWritten = true; throw new InvalidOperationException(); }
    }
}
