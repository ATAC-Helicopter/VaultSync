#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskImageInspectionDigestTests
{
    // In-memory duplex byte oracle, not an authenticated/native process fixture.
    private sealed class Oracle(byte[] metadata, byte[] payload, Guid operation) : Stream
    {
        private MemoryStream reply = new();
        internal int LargePayloadReads, MaximumRequest, PayloadReads;
        internal long PayloadServed;
        internal string? Fault;
        internal Action<uint>? OnRequest;
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => reply.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => reply.Read(buffer);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var read = DiskImageInspectionProtocol.DecodeRead(buffer.ToArray(), operation);
            byte[] bytes = read.Source == 0 ? metadata : payload;
            MaximumRequest = Math.Max(MaximumRequest, read.Count);
            if (read.Source == 1)
            {
                PayloadReads++; PayloadServed += read.Count;
                if (read.Count >= 65536) LargePayloadReads++;
            }
            string? fault = read.Source == 1 ? Fault : null;
            reply.Dispose(); reply = new(); Span<byte> header = stackalloc byte[8]; header.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(header, read.Count + (fault == "length" ? 1 : 0));
            if (fault == "reserved") header[4] = 1;
            reply.Write(header); reply.Write(bytes.AsSpan((int)read.Offset, read.Count - (fault == "truncated" ? 1 : 0)));
            reply.Position = 0; OnRequest?.Invoke(read.Source);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { reply.Dispose(); base.Dispose(disposing); }
    }

    private static async Task<DiskImageInspectionObservation> Verify(Oracle oracle, Guid id, int metadataLength,
        int payloadLength, CancellationToken token = default)
    {
        using var metadata = new DiskImageInspectionWorkerService.RemoteStream(oracle, oracle, id, 0, metadataLength, token);
        using var payload = new DiskImageInspectionWorkerService.RemoteStream(oracle, oracle, id, 1, payloadLength, token);
        return await DiskImageInspectionWorkerService.VerifyImageAsync(metadata, payload, token);
    }

    [Theory] [InlineData(512u)] [InlineData(4096u)]
    public async Task VerifiedDigestUsesOneWholePayloadScanAndBoundedGptReads(uint sector)
    {
        byte[] raw = DiskImageRawProfileVerificationServiceTests.Raw(sector);
        byte[] metadata = Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid(sector).MetadataHex);
        Guid id = Guid.NewGuid(); using var oracle = new Oracle(metadata, raw, id);
        var result = await Verify(oracle, id, metadata.Length, raw.Length);
        Assert.Equal(DiskImageRawProfileVerificationState.ContentAndLayoutVerified, result.State);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(raw)), result.PayloadSha256);
        Assert.Equal((ulong)raw.Length, result.VerifiedBytes);
        Assert.Equal((raw.Length + DiskImageInspectionProtocol.ChunkBytes - 1) / DiskImageInspectionProtocol.ChunkBytes,
            oracle.LargePayloadReads);
        Assert.InRange(oracle.MaximumRequest, 1, DiskImageInspectionProtocol.ChunkBytes);
        Assert.InRange(oracle.PayloadServed, raw.Length, raw.Length + 128 * 1024);
    }

    [Theory] [InlineData("length")] [InlineData("reserved")] [InlineData("truncated")]
    public async Task MalformedOrTruncatedOracleRepliesNeverReturnAVerifiedHash(string fault)
    {
        byte[] raw = DiskImageRawProfileVerificationServiceTests.Raw(512);
        byte[] metadata = Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid().MetadataHex);
        Guid id = Guid.NewGuid(); using var oracle = new Oracle(metadata, raw, id) { Fault = fault };
        DiskImageInspectionObservation? result = null;
        Exception? error = await Record.ExceptionAsync(async () => result = await Verify(oracle, id, metadata.Length, raw.Length));
        if (error is not null) Assert.True(error is IOException or InvalidDataException);
        else Assert.NotNull(result);
        Assert.NotEqual(DiskImageRawProfileVerificationState.ContentAndLayoutVerified, result?.State);
        Assert.Null(result?.PayloadSha256);
        Assert.Equal(1, oracle.PayloadReads);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationBeforeOrDuringPayloadReadCannotProduceAHash(bool during)
    {
        byte[] raw = DiskImageRawProfileVerificationServiceTests.Raw(512);
        byte[] metadata = Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid().MetadataHex);
        Guid id = Guid.NewGuid(); using var cancel = new CancellationTokenSource();
        using var oracle = new Oracle(metadata, raw, id);
        if (during) oracle.OnRequest = source => { if (source == 1) cancel.Cancel(); }; else cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Verify(oracle, id, metadata.Length, raw.Length, cancel.Token));
        Assert.Equal(during ? 1 : 0, oracle.PayloadReads);
    }

    [Fact]
    public async Task CancellationDuringGptAfterThePayloadHashCannotExportThatHash()
    {
        byte[] raw = DiskImageRawProfileVerificationServiceTests.Raw(512);
        byte[] metadata = Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid().MetadataHex);
        Guid id = Guid.NewGuid(); using var cancel = new CancellationTokenSource();
        using var oracle = new Oracle(metadata, raw, id);
        oracle.OnRequest = source => { if (source == 1 && oracle.PayloadServed > raw.Length) cancel.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Verify(oracle, id, metadata.Length, raw.Length, cancel.Token));
        Assert.Equal(1, oracle.LargePayloadReads);
        Assert.True(oracle.PayloadServed > raw.Length);
    }

    [Fact]
    public async Task RepeatedVerificationReadsFreshBytesAndRefusesMutationWithoutRetainingAHash()
    {
        byte[] raw = DiskImageRawProfileVerificationServiceTests.Raw(512);
        byte[] metadata = Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid().MetadataHex);
        Guid id = Guid.NewGuid(); using var oracle = new Oracle(metadata, raw, id);
        var first = await Verify(oracle, id, metadata.Length, raw.Length);
        Assert.NotNull(first.PayloadSha256);
        raw[raw.Length / 2] ^= 1;
        var second = await Verify(oracle, id, metadata.Length, raw.Length);
        Assert.Equal(DiskImageRawProfileVerificationState.Corrupt, second.State);
        Assert.Null(second.PayloadSha256);
        Assert.Equal(2, oracle.LargePayloadReads);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task ManifestOrGptFailureCannotExportTheComputedPayloadHash(bool gpt)
    {
        byte[] raw = DiskImageRawProfileVerificationServiceTests.Raw(512);
        byte[] metadata = Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid().MetadataHex);
        if (gpt)
        {
            raw[512] ^= 1; // Invalid GPT, with matching untrusted payload/manifest digests.
            const int partRecord = 24 + 64 + 216 + 2 * 64;
            SHA256.HashData(raw).CopyTo(metadata, partRecord + 24);
            SHA256.HashData(metadata.AsSpan(24, metadata.Length - 88)).CopyTo(metadata, metadata.Length - 32);
        }
        else metadata[^1] ^= 1;
        Guid id = Guid.NewGuid(); using var oracle = new Oracle(metadata, raw, id);
        var result = await Verify(oracle, id, metadata.Length, raw.Length);
        Assert.NotEqual(DiskImageRawProfileVerificationState.ContentAndLayoutVerified, result.State);
        Assert.Null(result.PayloadSha256);
        Assert.Equal(gpt ? 1 : 0, oracle.LargePayloadReads);
    }
}
