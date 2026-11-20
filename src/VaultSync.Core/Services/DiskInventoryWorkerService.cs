using System.Buffers.Binary;
using System.Text.Json;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>
/// One-shot, read-only development worker. Inherited input/output pipes are transport,
/// not elevated authorization, peer authentication or an operating-system sandbox.
/// </summary>
public static class DiskInventoryWorkerService
{
    public const int RequestBytes = 32;
    public const int ResponseHeaderBytes = 36;
    public const int MaximumResponseBytes = 1024 * 1024;
    public const uint ProtocolVersion = 1;
    public const uint InventoryOperation = 1;
    private static ReadOnlySpan<byte> RequestMagic => "VSINVREQ"u8;
    private static ReadOnlySpan<byte> ResponseMagic => "VSINVRSP"u8;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8 };

    public static Task<bool> RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default) =>
        RunAsync(input, output, LinuxBlockInventoryService.ObserveAsync, cancellationToken);

    // The provider seam is internal: a request can never select a path, tool, provider or device.
    internal static async Task<bool> RunAsync(Stream input, Stream output,
        Func<CancellationToken, ValueTask<LinuxBlockInventory>> observe, CancellationToken cancellationToken = default)
    {
        byte[] request = new byte[RequestBytes];
        try
        {
            await input.ReadExactlyAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException) { return false; }

        if (!request.AsSpan(0, 8).SequenceEqual(RequestMagic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(8)) != ProtocolVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(12)) != InventoryOperation)
            return false;
        Guid operationId = new(request.AsSpan(16), bigEndian: true);
        if (operationId == Guid.Empty)
            return false;

        // EOF is part of the handshake. A second request or trailing byte refuses the
        // entire operation before any observation. A silent open pipe needs host timeout.
        if (await input.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            return false;

        LinuxBlockInventory inventory = await observe(cancellationToken).ConfigureAwait(false);
        // Serialize into a bounded output buffer before emitting a header. The provider
        // independently bounds device counts and strings; the wire body has its own cap.
        using var body = new BoundedBuffer(MaximumResponseBytes);
        await JsonSerializer.SerializeAsync(body, inventory, JsonOptions, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] header = new byte[ResponseHeaderBytes];
        ResponseMagic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), InventoryOperation);
        operationId.TryWriteBytes(header.AsSpan(16, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), checked((uint)body.Length));
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        body.Position = 0;
        await body.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private sealed class BoundedBuffer(int maximum) : Stream
    {
        private readonly MemoryStream bufferStream = new();
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => bufferStream.Length;
        public override long Position { get => bufferStream.Position; set => bufferStream.Position = value; }
        public override void Flush() => bufferStream.Flush();
        public override int Read(byte[] buffer, int offset, int count) => bufferStream.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => bufferStream.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maximum - Length)
                throw new InvalidDataException("inventory.response-limit");
            bufferStream.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                bufferStream.Dispose();
            base.Dispose(disposing);
        }
    }
}
