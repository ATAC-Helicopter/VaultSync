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
    private sealed class Oracle(byte[] bytes, Guid operation) : Stream
    {
        private MemoryStream reply = new();
        internal int Calls, MaximumRequest;
        internal long Served;
        internal string? Fault;
        internal Action? OnRequest;
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
            Assert.Equal(1u, read.Source); Assert.Equal((ulong)Served, read.Offset);
            Calls++; MaximumRequest = Math.Max(MaximumRequest, read.Count); Served += read.Count;
            reply.Dispose(); reply = new(); Span<byte> header = stackalloc byte[8]; header.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(header, read.Count + (Fault == "length" ? 1 : 0));
            if (Fault == "reserved") header[4] = 1;
            reply.Write(header); reply.Write(bytes.AsSpan((int)read.Offset, read.Count - (Fault == "truncated" ? 1 : 0)));
            reply.Position = 0; OnRequest?.Invoke();
        }
        internal void BeginPass() => Served = 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { reply.Dispose(); base.Dispose(disposing); }
    }

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(4097)] [InlineData(2097189)]
    public async Task IndependentDigestUsesBoundedLargeRequestsWithoutTinyReadAmplification(int length)
    {
        byte[] bytes = new byte[length]; new Random(47).NextBytes(bytes); Guid id = Guid.NewGuid();
        using var oracle = new Oracle(bytes, id);
        using var payload = new DiskImageInspectionWorkerService.RemoteStream(oracle, oracle, id, 1, length, default);
        string expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(expected, await DiskImageInspectionWorkerService.PayloadDigestAsync(payload, default));
        Assert.Equal(length, oracle.Served);
        int maximumCalls = (length + DiskImageInspectionProtocol.ChunkBytes - 1) / DiskImageInspectionProtocol.ChunkBytes;
        Assert.InRange(oracle.Calls, 0, maximumCalls);
        Assert.InRange(oracle.MaximumRequest, 0, DiskImageInspectionProtocol.ChunkBytes);
        Assert.True(payload.CanRead);
    }

    [Theory] [InlineData("length")] [InlineData("reserved")] [InlineData("truncated")]
    public async Task MalformedOrTruncatedOracleRepliesNeverReturnAHash(string fault)
    {
        Guid id = Guid.NewGuid(); using var oracle = new Oracle(new byte[4097], id) { Fault = fault };
        using var payload = new DiskImageInspectionWorkerService.RemoteStream(oracle, oracle, id, 1, 4097, default);
        if (fault == "truncated")
            await Assert.ThrowsAsync<EndOfStreamException>(() => DiskImageInspectionWorkerService.PayloadDigestAsync(payload, default).AsTask());
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => DiskImageInspectionWorkerService.PayloadDigestAsync(payload, default).AsTask());
        Assert.Equal(1, oracle.Calls);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationBeforeOrDuringReadCannotProduceAPartialDigest(bool during)
    {
        Guid id = Guid.NewGuid(); using var cancel = new CancellationTokenSource();
        using var oracle = new Oracle(new byte[262144], id);
        using var payload = new DiskImageInspectionWorkerService.RemoteStream(oracle, oracle, id, 1, 262144, cancel.Token);
        if (during) oracle.OnRequest = cancel.Cancel; else cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskImageInspectionWorkerService.PayloadDigestAsync(payload, cancel.Token).AsTask());
        Assert.Equal(during ? 1 : 0, oracle.Calls);
    }

    [Fact]
    public async Task RepeatedDigestReadsFreshBytesWithoutRetainingAStalePayloadCache()
    {
        byte[] bytes = new byte[262144]; Guid id = Guid.NewGuid(); using var oracle = new Oracle(bytes, id);
        using var payload = new DiskImageInspectionWorkerService.RemoteStream(oracle, oracle, id, 1, bytes.Length, default);
        string? first = await DiskImageInspectionWorkerService.PayloadDigestAsync(payload, default);
        bytes[0] = 17; oracle.BeginPass();
        string? second = await DiskImageInspectionWorkerService.PayloadDigestAsync(payload, default);
        Assert.NotEqual(first, second); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), second);
        Assert.Equal(4, oracle.Calls);
    }
}
