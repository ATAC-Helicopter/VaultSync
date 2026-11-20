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

    // A valid binding for a different connection must never authenticate this one.
    internal bool OwnsConnection(Socket connection) => access is SocketAccess native && native.Owns(connection);

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
        internal bool Owns(Socket connection) => ReferenceEquals(socket, connection);
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
            var handle = new NativeHandle(descriptor);
            NativeHandle? monitor = null;
            bool transferred = false;
            try
            {
                if (!CloseOnExec(handle)) return null;
                int polling = CreateEpoll(0x80000); // EPOLL_CLOEXEC
                if (polling < 0) return null;
                monitor = new NativeHandle(polling);
                var registration = new EpollEvent { Events = 1, Data = 1 }; // EPOLLIN, opaque local cookie.
                if (!CloseOnExec(monitor) || Register(monitor, 1, handle, ref registration) != 0) return null;
                var lifetime = new ProcessLifetime(handle, monitor);
                transferred = true;
                return lifetime;
            }
            finally
            {
                if (!transferred) { monitor?.Dispose(); handle.Dispose(); }
            }
        }
    }

    private static bool CloseOnExec(NativeHandle handle)
    {
        int flags = DescriptorFlags(handle, 1); // F_GETFD / FD_CLOEXEC
        return flags >= 0 && (flags & 1) != 0;
    }

    private sealed class ProcessLifetime(NativeHandle handle, NativeHandle monitor) : IProcessLifetime
    {
        public bool IsAlive
        {
            get
            {
                // SafeHandle arguments receive automatic native-call lifetime
                // protection. No raw descriptor extraction or manual ref counting.
                int ready = Wait(monitor, out _, 1, 0);
                GC.KeepAlive(handle); // Keep the registered pidfd alive through the wait.
                return ready == 0;
            }
        }
        public void Dispose() { monitor.Dispose(); handle.Dispose(); }
    }

    // FD zero is valid on Linux. Do not use a zero-is-invalid handle wrapper.
    private sealed class NativeHandle : SafeHandle
    {
        internal NativeHandle(int descriptor) : base((IntPtr)(-1), ownsHandle: true) => SetHandle((IntPtr)descriptor);
        public override bool IsInvalid => handle.ToInt64() < 0;
        protected override bool ReleaseHandle() => NativeClose(handle.ToInt32()) == 0;
    }

    // Linux x64 epoll_event is packed, 12 bytes (u32 events + u64 data).
    [StructLayout(LayoutKind.Explicit, Pack = 1, Size = 12)]
    private struct EpollEvent
    {
        [FieldOffset(0)] public uint Events;
        [FieldOffset(4)] public ulong Data;
    }

    [DllImport("libc", EntryPoint = "fcntl", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int DescriptorFlags(NativeHandle descriptor, int command);
    [DllImport("libc", EntryPoint = "epoll_create1", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int CreateEpoll(int flags);
    [DllImport("libc", EntryPoint = "epoll_ctl", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Register(NativeHandle monitor, int operation, NativeHandle process, ref EpollEvent registration);
    [DllImport("libc", EntryPoint = "epoll_wait", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Wait(NativeHandle monitor, out EpollEvent ready, int maximumEvents, int timeout);
    [DllImport("libc", EntryPoint = "close", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int NativeClose(int descriptor);
}
