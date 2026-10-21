using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VaultSync.Core.Services;

internal readonly record struct LinuxWorkerPeerCredentials(int ProcessId, uint UserId, uint GroupId);

/// <summary>
/// Binds an existing Unix stream connection to connect-time kernel credentials and
/// its exact peer process lifetime. Not publisher trust, per-message authentication,
/// privilege, offline proof or permission to perform an operation. Caller owns socket.
/// </summary>
internal sealed class LinuxWorkerPeerBinding : IDisposable
{
    internal interface IProcessLifetime : IDisposable { bool IsAlive { get; } }
    internal interface IAccess
    {
        bool ConnectionOpen { get; }
        bool TryCredentials(out LinuxWorkerPeerCredentials credentials);
        IProcessLifetime? OpenPeerProcess();
    }

    private readonly object gate = new();
    private readonly IAccess access;
    private readonly IProcessLifetime process;
    private readonly LinuxWorkerPeerCredentials expected;
    private bool disposed;

    private LinuxWorkerPeerBinding(IAccess access, IProcessLifetime process, LinuxWorkerPeerCredentials expected)
    { this.access = access; this.process = process; this.expected = expected; }

    internal static LinuxWorkerPeerBinding? TryCreate(Socket connection, LinuxWorkerPeerCredentials expected)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            connection.AddressFamily != AddressFamily.Unix || connection.SocketType != SocketType.Stream)
            return null;
        return TryCreate(new SocketAccess(connection), expected);
    }

    // Provider seam is internal, never selectable by a wire request or image.
    internal static LinuxWorkerPeerBinding? TryCreate(IAccess access, LinuxWorkerPeerCredentials expected)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (expected.ProcessId <= 0 || expected.UserId == uint.MaxValue || expected.GroupId == uint.MaxValue) return null;
        IProcessLifetime? lifetime = null;
        try
        {
            if (!access.ConnectionOpen || !access.TryCredentials(out var first) || first != expected) return null;
            // SO_PEERPIDFD returns a descriptor of THIS socket's peer, rather than
            // reopening a numeric PID that could have been recycled after exit.
            lifetime = access.OpenPeerProcess();
            if (lifetime is null || !lifetime.IsAlive || !access.TryCredentials(out var second) ||
                second != expected || !access.ConnectionOpen || !lifetime.IsAlive) return null;
            var binding = new LinuxWorkerPeerBinding(access, lifetime, expected);
            lifetime = null;
            return binding;
        }
        catch (Exception error) when (Unavailable(error)) { return null; }
        finally { lifetime?.Dispose(); }
    }

    internal bool Recheck()
    {
        lock (gate)
        {
            if (disposed) return false;
            try
            {
                if (process.IsAlive && access.ConnectionOpen && access.TryCredentials(out var current) && current == expected)
                    return true;
            }
            catch (Exception error) when (Unavailable(error)) { /* Invalidate, never retain peer diagnostics. */ }
            Dispose();
            return false;
        }
    }

    private static bool Unavailable(Exception error) => error is SocketException or IOException or
        UnauthorizedAccessException or ObjectDisposedException or PlatformNotSupportedException;

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            process.Dispose();
        }
    }

    private sealed class SocketAccess(Socket socket) : IAccess
    {
        public bool ConnectionOpen => socket.Connected && !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        public bool TryCredentials(out LinuxWorkerPeerCredentials credentials)
        {
            credentials = default;
            Span<byte> buffer = stackalloc byte[12];
            if (socket.GetRawSocketOption(1, 17, buffer) != buffer.Length) return false; // SOL_SOCKET/SO_PEERCRED
            credentials = new(BinaryPrimitives.ReadInt32LittleEndian(buffer),
                BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]), BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]));
            return credentials.ProcessId > 0;
        }
        public IProcessLifetime? OpenPeerProcess()
        {
            Span<byte> buffer = stackalloc byte[4];
            if (socket.GetRawSocketOption(1, 77, buffer) != buffer.Length) return null; // SO_PEERPIDFD, no PID fallback.
            int descriptor = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            if (descriptor < 0) return null;
            var handle = new PeerProcessHandle(descriptor);
            int flags = DescriptorFlags(handle, 1); // F_GETFD / FD_CLOEXEC
            if (flags >= 0 && (flags & 1) != 0)
                return new ProcessLifetime(handle);
            handle.Dispose();
            return null;
        }
    }

    private sealed class ProcessLifetime(PeerProcessHandle handle) : IProcessLifetime
    {
        public bool IsAlive
        {
            get
            {
                bool held = false;
                try
                {
                    handle.DangerousAddRef(ref held);
                    var poll = new PollDescriptor { Descriptor = handle.DangerousGetHandle().ToInt32(), Events = 1 };
                    return Poll(ref poll, 1, 0) == 0 && poll.ReturnedEvents == 0;
                }
                catch (ObjectDisposedException) { return false; }
                finally { if (held) handle.DangerousRelease(); }
            }
        }
        public void Dispose() => handle.Dispose();
    }

    // FD zero is valid on Linux. Do not use a zero-is-invalid handle wrapper.
    private sealed class PeerProcessHandle : SafeHandle
    {
        internal PeerProcessHandle(int descriptor) : base((IntPtr)(-1), ownsHandle: true) => SetHandle((IntPtr)descriptor);
        public override bool IsInvalid => handle.ToInt64() < 0;
        protected override bool ReleaseHandle() => NativeClose(handle.ToInt32()) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor { public int Descriptor; public short Events; public short ReturnedEvents; }

    [DllImport("libc", EntryPoint = "fcntl", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int DescriptorFlags(PeerProcessHandle descriptor, int command);
    [DllImport("libc", EntryPoint = "poll", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeout);
    [DllImport("libc", EntryPoint = "close", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeClose(int descriptor);
}
