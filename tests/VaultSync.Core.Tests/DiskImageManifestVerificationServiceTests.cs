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

public sealed class DiskImageManifestVerificationServiceTests
{
    private static readonly Guid PartId = Guid.Parse("3a3b4083-876d-4fb2-b6c3-878f54c47560");
    private sealed record Fixture(string Name, string MetadataHex, string PayloadHex, string Root, ulong VerifiedBytes);

    private static async Task<Fixture[]> Fixtures() => JsonSerializer.Deserialize<Fixture[]>(
        await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "disk-image-manifest-v1.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public async Task IndependentlyEncodedRawAndZeroFixturesBindContentToIdentityAndGeneration()
    {
        foreach (Fixture fixture in await Fixtures())
        {
            byte[] bytes = Convert.FromHexString(fixture.MetadataHex);
            using var metadata = new MemoryStream(bytes, writable: false);
            using var payload = new MemoryStream(Convert.FromHexString(fixture.PayloadHex), writable: false);
            var streams = new Dictionary<Guid, Stream>();
            if (fixture.PayloadHex.Length != 0)
                streams.Add(PartId, payload);
            DiskImageManifestVerificationAssessment result = await DiskImageManifestVerificationService.VerifyAsync(metadata, streams);
            Assert.Equal(DiskImageManifestVerificationState.ContentVerified, result.State);
            Assert.Equal("image.draft-content-only", result.Reason);
            Assert.Equal(Guid.Parse("6b7e43cf-d362-4fd2-9305-bbb5d7a387f0"), result.ImageId);
            Assert.Equal(Guid.Parse("1c8be507-13c7-4483-9345-c51047ebf019"), result.CaptureId);
            Assert.Equal(7UL, result.Generation);
            Assert.Equal(fixture.Root, result.ManifestRoot);
            Assert.Equal(fixture.VerifiedBytes, result.VerifiedBytes);
            Assert.Equal(Convert.FromHexString(fixture.MetadataHex), bytes);
        }
    }

    [Theory]
    [InlineData("edited-map", DiskImageManifestVerificationState.Corrupt, "image.manifest-root-mismatch")]
    [InlineData("completion-image", DiskImageManifestVerificationState.Corrupt, "image.completion-identity-mismatch")]
    [InlineData("completion-generation", DiskImageManifestVerificationState.Corrupt, "image.completion-identity-mismatch")]
    [InlineData("completion-root", DiskImageManifestVerificationState.Corrupt, "image.manifest-root-mismatch")]
    [InlineData("missing-completion", DiskImageManifestVerificationState.Incomplete, "image.missing-completion")]
    [InlineData("truncated-manifest", DiskImageManifestVerificationState.Incomplete, "image.truncated-manifest")]
    [InlineData("trailing", DiskImageManifestVerificationState.Corrupt, "image.trailing-metadata-bytes")]
    [InlineData("excess-count", DiskImageManifestVerificationState.Invalid, "image.manifest-record-limit")]
    [InlineData("wrong-count", DiskImageManifestVerificationState.Invalid, "image.manifest-record-limit")]
    [InlineData("empty-id", DiskImageManifestVerificationState.Invalid, "image.invalid-manifest-identity")]
    [InlineData("unknown-schema", DiskImageManifestVerificationState.Unsupported, "image.unsupported-manifest")]
    [InlineData("unknown-rule", DiskImageManifestVerificationState.Unsupported, "image.unsupported-extent")]
    public async Task MetadataFaultsRefuseBeforePayloadAccess(
        string fault, DiskImageManifestVerificationState state, string reason)
    {
        Fixture fixture = (await Fixtures())[0];
        byte[] bytes = Convert.FromHexString(fixture.MetadataHex);
        int completion = bytes.Length - 64;
        switch (fault)
        {
            case "edited-map": bytes[24 + 64 + 56] ^= 1; break;
            case "completion-image": bytes[completion + 8] ^= 1; break;
            case "completion-generation": bytes[completion + 24] ^= 1; break;
            case "completion-root": bytes[^1] ^= 1; break;
            case "missing-completion": bytes = bytes[..^1]; break;
            case "truncated-manifest": bytes = bytes[..(completion - 1)]; break;
            case "trailing": bytes = [.. bytes, 1]; break;
            case "excess-count": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + 56), uint.MaxValue); break;
            case "wrong-count": bytes[24 + 60] = 2; break;
            case "empty-id": bytes.AsSpan(24 + 4, 16).Clear(); break;
            case "unknown-schema": bytes[24] = 3; break;
            case "unknown-rule": bytes[24 + 64 + 56 + 16] = 2; break;
        }
        using var metadata = new MemoryStream(bytes, writable: false);
        using var payload = new MemoryStream(Convert.FromHexString(fixture.PayloadHex), writable: false);
        DiskImageManifestVerificationAssessment result = await DiskImageManifestVerificationService.VerifyAsync(
            metadata, new Dictionary<Guid, Stream> { [PartId] = payload });
        Assert.Equal(state, result.State);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(0, payload.Position);
        Assert.Equal(0UL, result.VerifiedBytes);
    }

    [Fact]
    public async Task BoundManifestCannotConcealMissingOrCorruptPayload()
    {
        Fixture fixture = (await Fixtures())[0];
        using var metadata = new MemoryStream(Convert.FromHexString(fixture.MetadataHex), writable: false);
        var missing = await DiskImageManifestVerificationService.VerifyAsync(metadata, new Dictionary<Guid, Stream>());
        Assert.Equal(DiskImageManifestVerificationState.Incomplete, missing.State);
        metadata.Position = 0;
        byte[] bytes = Convert.FromHexString(fixture.PayloadHex);
        bytes[200] ^= 1;
        using var payload = new MemoryStream(bytes, writable: false);
        var corrupt = await DiskImageManifestVerificationService.VerifyAsync(metadata, new Dictionary<Guid, Stream> { [PartId] = payload });
        Assert.Equal(DiskImageManifestVerificationState.Corrupt, corrupt.State);
        Assert.Equal("image.part-digest-mismatch", corrupt.Reason);
    }

    [Theory]
    [InlineData("gap", DiskImageManifestVerificationState.Invalid, "image.coverage-gap")]
    [InlineData("unsupported-sector", DiskImageManifestVerificationState.Unsupported, "image.unsupported-sector-size")]
    [InlineData("oversized-part", DiskImageManifestVerificationState.Invalid, "image.raw-part-size-limit")]
    [InlineData("zero-part-reference", DiskImageManifestVerificationState.Invalid, "image.invalid-zero-rule")]
    public async Task RecomputedChecksumCannotMakeAnInvalidMapValid(
        string fault, DiskImageManifestVerificationState state, string reason)
    {
        Fixture fixture = (await Fixtures())[2];
        byte[] bytes = Convert.FromHexString(fixture.MetadataHex);
        switch (fault)
        {
            case "gap": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24 + 64 + 56), 512); break;
            case "unsupported-sector": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + 52), 1024); break;
            case "oversized-part": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24 + 64 + 16), 1536); break;
            case "zero-part-reference": bytes[24 + 64 + 56 + 44 + 20] = 1; break;
        }
        // An attacker can replace both the manifest and its untrusted checksum.
        SHA256.HashData(bytes.AsSpan(24, bytes.Length - 24 - 64)).CopyTo(bytes.AsSpan(bytes.Length - 32));
        using var metadata = new MemoryStream(bytes, writable: false);
        using var payload = new MemoryStream(Convert.FromHexString(fixture.PayloadHex), writable: false);
        var result = await DiskImageManifestVerificationService.VerifyAsync(metadata, new Dictionary<Guid, Stream> { [PartId] = payload });
        Assert.Equal(state, result.State);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(0, payload.Position);
    }

    [Fact]
    public async Task NonSeekableShortReadsStillVerifyTheExactStream()
    {
        Fixture fixture = (await Fixtures())[0];
        using var metadata = new ShortReadStream(Convert.FromHexString(fixture.MetadataHex));
        using var payload = new ShortReadStream(Convert.FromHexString(fixture.PayloadHex));
        var result = await DiskImageManifestVerificationService.VerifyAsync(metadata, new Dictionary<Guid, Stream> { [PartId] = payload });
        Assert.Equal(DiskImageManifestVerificationState.ContentVerified, result.State);
        Assert.Equal(fixture.Root, result.ManifestRoot);
    }

    [Fact]
    public async Task CancellationPreventsMetadataAndPayloadReads()
    {
        Fixture fixture = (await Fixtures())[0];
        using var metadata = new MemoryStream(Convert.FromHexString(fixture.MetadataHex), writable: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DiskImageManifestVerificationService.VerifyAsync(metadata, new Dictionary<Guid, Stream>(), cancellation.Token));
        Assert.Equal(0, metadata.Position);
    }

    private sealed class ShortReadStream(byte[] bytes) : Stream
    {
        private int offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(Math.Min(buffer.Length, 3), bytes.Length - offset);
            bytes.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
