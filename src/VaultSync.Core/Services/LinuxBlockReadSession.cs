using Microsoft.Win32.SafeHandles;

namespace VaultSync.Core.Services;

/// <summary>
/// Owns a read-only held block descriptor. No raw handle, writable stream or path is exposed.
/// It does not prove global source consistency, trusted origin or restore authorization.
/// </summary>
public sealed class LinuxBlockReadSession : Stream
{
    private const int MaximumReadBytes = 128 * 1024;
    private readonly SafeFileHandle handle;
    private readonly LinuxBlockReadService.HandleIdentity identity;
    private readonly LinuxBlockReadService.IDeviceAccess deviceAccess;
    // Never expose AvailableWaitHandle; this semaphore owns no native wait handle.
    // Keep it alive for in-flight reads when Dispose closes the device concurrently.
    private readonly SemaphoreSlim gate = new(1, 1);
    private long position;
    private int disposed;

    internal LinuxBlockReadSession(SafeFileHandle handle, LinuxBlockReadService.HandleIdentity identity,
        LinuxBlockReadService.IDeviceAccess deviceAccess)
    { this.handle = handle; this.identity = identity; this.deviceAccess = deviceAccess; }

    public override bool CanRead => Volatile.Read(ref disposed) == 0;
    public override bool CanSeek => CanRead;
    public override bool CanWrite => false;
    public override long Length { get { ThrowIfDisposed(); return checked((long)identity.Capacity); } }
    public override long Position { get { ThrowIfDisposed(); return Interlocked.Read(ref position); } set => Seek(value, SeekOrigin.Begin); }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    private void CheckBinding()
    {
        ThrowIfDisposed();
        if (!deviceAccess.Inspect(handle, out var current))
        {
            Dispose();
            throw new IOException("block.read-binding-unavailable");
        }
        if (current != identity)
        {
            Dispose();
            throw new IOException("block.read-binding-changed");
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        gate.Wait();
        try
        {
            CheckBinding();
            int count = (int)Math.Min(Math.Min((long)buffer.Length, MaximumReadBytes), Length - position);
            if (count == 0) return 0;
            int read;
            try { read = RandomAccess.Read(handle, buffer[..count], position); }
            catch (IOException) { Dispose(); throw; }
            CheckBinding();
            if (read == 0) { Dispose(); throw new EndOfStreamException("block.read-truncated"); }
            position += read;
            return read;
        }
        finally { gate.Release(); }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CheckBinding();
            int count = (int)Math.Min(Math.Min((long)buffer.Length, MaximumReadBytes), Length - position);
            if (count == 0) return 0;
            int read;
            try { read = await RandomAccess.ReadAsync(handle, buffer[..count], position, cancellationToken).ConfigureAwait(false); }
            catch (IOException) { Dispose(); throw; }
            CheckBinding();
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) { Dispose(); throw new EndOfStreamException("block.read-truncated"); }
            position += read;
            return read;
        }
        finally { gate.Release(); }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        gate.Wait();
        try
        {
            ThrowIfDisposed();
            long basis = origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => position,
                SeekOrigin.End => Length,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            long next;
            try { next = checked(basis + offset); }
            catch (OverflowException) { throw new ArgumentOutOfRangeException(nameof(offset)); }
            if (next < 0 || next > Length) throw new ArgumentOutOfRangeException(nameof(offset));
            position = next;
            return next;
        }
        finally { gate.Release(); }
    }

    public override void Flush() => ThrowIfDisposed();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref disposed, 1) == 0) handle.Dispose();
        base.Dispose(disposing);
    }
}
