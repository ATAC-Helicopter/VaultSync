using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace VaultSync.Core.Services;

/// <summary>
/// Same-privilege inventory startup/completion rendezvous. It binds kernel process
/// lifetimes, not executable trust, individual senders, privilege or device authority.
/// The bounded inventory frames still travel over the inherited pipes.
/// </summary>
internal sealed class LinuxInventoryWorkerPeerChannel : IDisposable
{
    private const byte Ready = 0x31;
    private const byte ResponseReady = 0x32;
    private const byte Accepted = 0x33;
    internal const string Argument = "--inventory-peer";
    internal static bool Supported => OperatingSystem.IsLinux() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private readonly Socket connection;
    private readonly NetworkStream stream;
    private readonly LinuxWorkerPeerBinding binding;

    private LinuxInventoryWorkerPeerChannel(Socket connection, LinuxWorkerPeerBinding binding)
    {
        this.connection = connection;
        this.binding = binding;
        stream = new NetworkStream(connection, ownsSocket: false);
    }

    private static LinuxInventoryWorkerPeerChannel Bind(Socket connection, int expectedProcess)
    {
        var expected = new LinuxWorkerPeerCredentials(expectedProcess, EffectiveUser(), EffectiveGroup());
        LinuxWorkerPeerBinding binding = LinuxWorkerPeerBinding.TryCreate(connection, expected)
            ?? throw new InvalidDataException("inventory.peer-binding");
        try { return new(connection, binding); }
        catch { binding.Dispose(); throw; }
    }

    internal static async Task<LinuxInventoryWorkerPeerChannel> ConnectParentAsync(string endpoint,
        CancellationToken token)
    {
        if (!Supported || !Path.IsPathFullyQualified(endpoint) || endpoint.Length > 100 ||
            Path.GetFileName(endpoint) != "peer")
            throw new InvalidDataException("inventory.peer-endpoint");
        // This is the kernel parent relationship, never a PID claimed in an argument.
        int parent = ParentProcess();
        if (parent <= 1) throw new InvalidDataException("inventory.peer-parent");
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), token).ConfigureAwait(false);
            var channel = Bind(socket, parent);
            try
            {
                if (ParentProcess() != parent) throw new InvalidDataException("inventory.peer-parent");
                await channel.SendAsync(Ready, token).ConfigureAwait(false);
                return channel;
            }
            catch { channel.Dispose(); throw; }
        }
        catch { socket.Dispose(); throw; }
    }

    internal Task ConfirmStartupAsync(CancellationToken token) => ReceiveAsync(Ready, token);

    internal async Task AcceptResponseAsync(CancellationToken token)
    {
        // The worker keeps itself and its socket alive until the bounded reply
        // has been validated. Stdout EOF/trailing bytes are checked after this ACK.
        await ReceiveAsync(ResponseReady, token).ConfigureAwait(false);
        RequirePeer();
        // This terminal acknowledgement releases the worker to exit. A post-send
        // liveness check would race that legitimate exit; stdout was already
        // validated while the exact worker was alive. Exit zero is checked next.
        await stream.WriteAsync(new[] { Accepted }, token).ConfigureAwait(false);
    }

    internal async Task CompleteWorkerAsync(CancellationToken token)
    {
        await SendAsync(ResponseReady, token).ConfigureAwait(false);
        await ReceiveAsync(Accepted, token).ConfigureAwait(false);
    }

    private async Task SendAsync(byte value, CancellationToken token)
    {
        RequirePeer();
        await stream.WriteAsync(new[] { value }, token).ConfigureAwait(false);
        RequirePeer();
    }

    private async Task ReceiveAsync(byte expected, CancellationToken token)
    {
        RequirePeer();
        byte[] value = new byte[1];
        await stream.ReadExactlyAsync(value, token).ConfigureAwait(false);
        if (value[0] != expected) throw new InvalidDataException("inventory.peer-phase");
        RequirePeer();
    }

    private void RequirePeer()
    {
        if (!binding.Recheck()) throw new InvalidDataException("inventory.peer-expired");
    }

    public void Dispose()
    {
        binding.Dispose();
        stream.Dispose();
        connection.Dispose();
    }

    internal sealed class Listener : IDisposable
    {
        private readonly DirectoryInfo directory;
        private readonly Socket socket;
        internal string Endpoint { get; }

        internal Listener()
        {
            if (!OperatingSystem.IsLinux() || !Supported) throw new PlatformNotSupportedException();
            directory = Directory.CreateTempSubdirectory("vsi-");
            Endpoint = Path.Combine(directory.FullName, "peer");
            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                // CreateTempSubdirectory creates an exclusive private 0700 directory.
                if (Endpoint.Length > 100 || directory.LinkTarget is not null ||
                    File.GetUnixFileMode(directory.FullName) !=
                    (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
                    throw new InvalidDataException("inventory.peer-directory");
                socket.Bind(new UnixDomainSocketEndPoint(Endpoint));
                socket.Listen(1);
            }
            catch { Dispose(); throw; }
        }

        internal async Task<LinuxInventoryWorkerPeerChannel> AcceptAsync(int childProcess, CancellationToken token)
        {
            Socket peer = await socket.AcceptAsync(token).ConfigureAwait(false);
            try { return Bind(peer, childProcess); }
            catch { peer.Dispose(); throw; }
        }

        public void Dispose()
        {
            socket.Dispose();
            // Never recurse through a directory containing unexpected entries.
            try { File.Delete(Endpoint); directory.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [DllImport("libc", EntryPoint = "getppid", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int ParentProcess();
    [DllImport("libc", EntryPoint = "geteuid", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint EffectiveUser();
    [DllImport("libc", EntryPoint = "getegid", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint EffectiveGroup();
}
