using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VaultSync.Core.Services;

/// <summary>Fixed installed child and owned cgroup lifecycle; never signals a numeric PID.</summary>
internal sealed class LinuxRecoveryControllerLaunch : IAsyncDisposable
{
    private const string Runtime = "/run/vaultsync-recovery";
    private readonly LinuxRecoveryMediaInstallation installation;
    private readonly SafeFileHandle runtime, group, ownership;
    private readonly Socket listener;
    private readonly Process process = new();
    private readonly LinuxRecoveryNative.Identity socketIdentity;
    private bool started;
    private Task? earlyExit;
    private Task? disposal;
    private readonly object gate = new();

    private LinuxRecoveryControllerLaunch(LinuxRecoveryMediaInstallation installation, SafeFileHandle runtime,
        SafeFileHandle group, SafeFileHandle ownership, Socket listener, LinuxRecoveryNative.Identity socketIdentity)
    {
        this.installation = installation; this.runtime = runtime; this.group = group;
        this.ownership = ownership; this.listener = listener; this.socketIdentity = socketIdentity;
    }

    internal static LinuxRecoveryControllerLaunch Create(LinuxRecoveryMediaInstallation installation)
    {
        if (!OperatingSystem.IsLinux()) throw LinuxRecoveryMediaInstallation.Invalid();
        installation.RecheckBoot(LinuxRecoveryMediaInstallation.Role.Host); installation.Recheck();
        // The boot-created request file may already exist. Never delete or adopt an
        // old socket. The root-only lock prevents duplicate owners from interfering.
        if (!Directory.Exists(Runtime)) Directory.CreateDirectory(Runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runtime = LinuxRecoveryNative.OpenDirectory(Runtime, false);
        SafeFileHandle? ownership = null, group = null; Socket? listener = null;
        try
        {
            ownership = LinuxRecoveryNative.Open(Runtime + "/host.lock", 2 | 0x40 | 0x20000 | 0x80000, 0x180);
            if (!LinuxRecoveryNative.TrustedFile(LinuxRecoveryNative.Stat(ownership), false)) throw LinuxRecoveryMediaInstallation.Invalid();
            LinuxRecoveryNative.Lock(ownership);
            LinuxRecoveryNative.SetGroup(runtime, LinuxRecoveryMediaInstallation.ControllerId, 0x1c8); // root:60999 0710
            using var service = LinuxRecoveryNative.OpenDirectory("/sys/fs/cgroup" + LinuxRecoveryMediaInstallation.ServiceGroup, false);
            if (!LinuxRecoveryNative.Cgroup(service)) throw LinuxRecoveryMediaInstallation.Invalid();
            LinuxRecoveryNative.MakeDirectory(service, "controller");
            group = LinuxRecoveryNative.OpenDirectory(LinuxRecoveryMediaInstallation.ControllerGroup, false);
            if (!LinuxRecoveryNative.Cgroup(group) || LinuxRecoveryNative.ReadBounded(LinuxRecoveryMediaInstallation.ControllerGroup + "/cgroup.procs", 4096).Length != 0)
                throw LinuxRecoveryMediaInstallation.Invalid();
            listener = BindOwnedListener(LinuxRecoveryMediaInstallation.SocketPath);
            File.SetUnixFileMode(LinuxRecoveryMediaInstallation.SocketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            if (Chown(LinuxRecoveryMediaInstallation.SocketPath, 0, LinuxRecoveryMediaInstallation.ControllerId) != 0) throw LinuxRecoveryMediaInstallation.Invalid();
            var socketIdentity = LinuxRecoveryNative.StatPath(LinuxRecoveryMediaInstallation.SocketPath);
            if ((socketIdentity.Mode & 0xf000) != 0xc000 || socketIdentity.Owner != 0 || socketIdentity.Group != LinuxRecoveryMediaInstallation.ControllerId)
                throw LinuxRecoveryMediaInstallation.Invalid();
            listener.Listen(1);
            return new(installation, runtime, group, ownership, listener, socketIdentity);
        }
        catch
        {
            listener?.Dispose(); group?.Dispose(); ownership?.Dispose(); runtime.Dispose();
            // Unexpected prior files/subgroups are retained. The owning systemd unit
            // cleans its process group; never sweep runtime paths on failed admission.
            throw;
        }
    }

    internal async Task<LinuxRecoveryOperationChannel> ConnectAsync(Guid operation, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        installation.Recheck();
        process.StartInfo = new(LinuxRecoveryMediaInstallation.Root + "/launch/recovery-controller-launch")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = LinuxRecoveryMediaInstallation.Root + "/launch", CreateNoWindow = true };
        process.StartInfo.Environment.Clear(); process.StartInfo.Environment["LANG"] = "C";
        started = process.Start();
        if (!started) throw LinuxRecoveryMediaInstallation.Invalid();
        var accepting = listener.AcceptAsync(timeout.Token).AsTask();
        earlyExit = process.WaitForExitAsync(CancellationToken.None);
        if (await Task.WhenAny(accepting, earlyExit).ConfigureAwait(false) == earlyExit && !accepting.IsCompletedSuccessfully)
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            try { (await accepting.ConfigureAwait(false)).Dispose(); } catch (OperationCanceledException) { }
            if (process.ExitCode == 77) throw new UnauthorizedAccessException("recovery-controller.access-denied");
            throw LinuxRecoveryMediaInstallation.Invalid();
        }
        Socket socket = await accepting.ConfigureAwait(false);
        LinuxWorkerPeerBinding? peer = null;
        try
        {
            if (earlyExit.IsCompleted || process.HasExited) throw LinuxRecoveryMediaInstallation.Invalid();
            peer = LinuxWorkerPeerBinding.TryCreate(socket, new(process.Id,
                LinuxRecoveryMediaInstallation.ControllerId, LinuxRecoveryMediaInstallation.ControllerId)) ?? throw LinuxRecoveryMediaInstallation.Invalid();
            if (!peer.Recheck() || earlyExit.IsCompleted || process.HasExited) throw LinuxRecoveryMediaInstallation.Invalid();
            ValidateNonrootProcess(process.Id);
            installation.RequireExecutable(process.Id, LinuxRecoveryMediaInstallation.Role.Controller);
            installation.Recheck();
            if (!peer.Recheck()) throw LinuxRecoveryMediaInstallation.Invalid();
            using var stream = new NetworkStream(socket, false);
            byte[] hello = Hello(operation);
            await stream.WriteAsync(hello, timeout.Token).ConfigureAwait(false);
            if (!peer.Recheck()) throw LinuxRecoveryMediaInstallation.Invalid();
            var channel = new LinuxRecoveryOperationChannel(socket, peer, operation, LinuxRecoveryRole.Worker);
            peer = null; return channel;
        }
        catch { peer?.Dispose(); socket.Dispose(); throw; }
    }

    internal static byte[] Hello(Guid operation)
    {
        if (operation == Guid.Empty) throw LinuxRecoveryMediaInstallation.Invalid();
        byte[] bytes = new byte[24]; "VSRHEL01"u8.CopyTo(bytes);
        operation.TryWriteBytes(bytes.AsSpan(8), true, out _); return bytes;
    }

    internal static Socket BindOwnedListener(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(new OwnedPathEndPoint(new UnixDomainSocketEndPoint(path)));
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    // Socket.Dispose automatically unlinks a directly bound UnixDomainSocketEndPoint.
    // This owner must instead verify the exact filesystem identity and unlink via
    // its held directory after closing the listener. Preserve ordinary Unix wire
    // serialization while keeping filesystem cleanup out of Socket.Dispose,
    // including when an unexpected entry has replaced the owned socket name.
    private sealed class OwnedPathEndPoint(UnixDomainSocketEndPoint address) : EndPoint
    {
        public override AddressFamily AddressFamily => address.AddressFamily;
        public override SocketAddress Serialize() => address.Serialize();
        public override EndPoint Create(SocketAddress socketAddress) =>
            new OwnedPathEndPoint((UnixDomainSocketEndPoint)address.Create(socketAddress));
    }
    internal static Guid DecodeHello(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 24 || !bytes[..8].SequenceEqual("VSRHEL01"u8)) throw LinuxRecoveryMediaInstallation.Invalid();
        var operation = new Guid(bytes[8..], true); return operation != Guid.Empty ? operation : throw LinuxRecoveryMediaInstallation.Invalid();
    }

    internal static void ValidateNonrootProcess(int process)
    {
        if (process <= 1 || LinuxRecoveryNative.Session(process) != process) throw LinuxRecoveryMediaInstallation.Invalid();
        string group = LinuxRecoveryNative.ReadBounded($"/proc/{process}/cgroup", 4096);
        if (group != "0::" + LinuxRecoveryMediaInstallation.ServiceGroup + "/controller\n") throw LinuxRecoveryMediaInstallation.Invalid();
        string stat = LinuxRecoveryNative.ReadBounded($"/proc/{process}/stat", 8192);
        int end = stat.LastIndexOf(')');
        if (end < 0 || stat[(end + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is not { Length: >= 5 } fields ||
            fields[4] != "0") throw LinuxRecoveryMediaInstallation.Invalid();
        LinuxImageInspectionSandbox.ValidateThreads(LinuxRecoveryMediaInstallation.ControllerId, LinuxRecoveryMediaInstallation.ControllerId, false,
            () => Directory.GetDirectories($"/proc/{process}/task"), task =>
            {
                string status = LinuxRecoveryNative.ReadBounded(task + "/status", 65536);
                if (!status.Split('\n').Any(line => line.Split(':', 2) is ["NoNewPrivs", var value] && value.Trim() == "1"))
                    throw LinuxRecoveryMediaInstallation.Invalid();
                return status;
            });
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously); disposal = completion.Task;
        }
        _ = CloseAsync(completion); return new(completion.Task);
    }
    private async Task CloseAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            listener.Dispose();
            if (started)
            {
                bool exited = false;
                try { await earlyExit!.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); exited = true; }
                catch (TimeoutException) { }
                if (!exited || LinuxRecoveryNative.ReadBounded(LinuxRecoveryMediaInstallation.ControllerGroup + "/cgroup.procs", 4096).Length != 0)
                {
                    // Kernel-owned subgroup, including descendants. Before joining
                    // it the fixed launcher is still owned by KillMode=control-group.
                    using var kill = new FileStream(LinuxRecoveryNative.Open(LinuxRecoveryMediaInstallation.ControllerGroup + "/cgroup.kill", 1 | 0x20000 | 0x80000), FileAccess.Write);
                    await kill.WriteAsync("1"u8.ToArray(), CancellationToken.None).ConfigureAwait(false);
                    await earlyExit!.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    using var empty = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    while (LinuxRecoveryNative.ReadBounded(LinuxRecoveryMediaInstallation.ControllerGroup + "/cgroup.procs", 4096).Length != 0)
                        await Task.Delay(25, empty.Token).ConfigureAwait(false);
                }
            }
            if (LinuxRecoveryNative.ReadBounded(LinuxRecoveryMediaInstallation.ControllerGroup + "/cgroup.procs", 4096).Length != 0)
                throw new IOException("recovery-controller.cleanup-unconfirmed");
            if (LinuxRecoveryNative.StatPath(LinuxRecoveryMediaInstallation.SocketPath) != socketIdentity) throw LinuxRecoveryMediaInstallation.Invalid();
            LinuxRecoveryNative.Remove(runtime, "controller.sock", false);
            using var service = LinuxRecoveryNative.OpenDirectory("/sys/fs/cgroup" + LinuxRecoveryMediaInstallation.ServiceGroup, false);
            LinuxRecoveryNative.Remove(service, "controller", true);
        }
        catch (Exception error) { failure = error; }
        finally { process.Dispose(); group.Dispose(); ownership.Dispose(); runtime.Dispose(); }
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }
    [DllImport("libc", EntryPoint = "chown")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Chown(string path, uint user, uint group);
}
