using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Startup = VaultSync.Core.Services.LinuxRecoveryStartupDiagnostics;

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
    private Task? diagnosticDrain;
    private readonly CancellationTokenSource diagnosticStop = new();
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
        Startup.Write(Startup.Role.Host, Startup.Phase.HostCreate);
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
            Startup.Write(Startup.Role.Host, Startup.Phase.RuntimeOwned);
            using var service = LinuxRecoveryNative.OpenDirectory("/sys/fs/cgroup" + LinuxRecoveryMediaInstallation.ServiceGroup, false);
            if (!LinuxRecoveryNative.Cgroup(service)) throw LinuxRecoveryMediaInstallation.Invalid();
            Startup.Write(Startup.Role.Host, Startup.Phase.ControllerGroup);
            LinuxRecoveryNative.MakeDirectory(service, "controller");
            group = LinuxRecoveryNative.OpenDirectory(LinuxRecoveryMediaInstallation.ControllerGroup, false);
            if (!LinuxRecoveryNative.Cgroup(group) || LinuxRecoveryNative.ReadBounded(LinuxRecoveryMediaInstallation.ControllerGroup + "/cgroup.procs", 4096).Length != 0)
                throw LinuxRecoveryMediaInstallation.Invalid();
            Startup.Write(Startup.Role.Host, Startup.Phase.ListenerBind);
            listener = BindOwnedListener(LinuxRecoveryMediaInstallation.SocketPath);
            File.SetUnixFileMode(LinuxRecoveryMediaInstallation.SocketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            if (Chown(LinuxRecoveryMediaInstallation.SocketPath, 0, LinuxRecoveryMediaInstallation.ControllerId) != 0) throw LinuxRecoveryMediaInstallation.Invalid();
            Startup.Write(Startup.Role.Host, Startup.Phase.SocketIdentity);
            var socketIdentity = LinuxRecoveryNative.StatPath(LinuxRecoveryMediaInstallation.SocketPath);
            if ((socketIdentity.Mode & 0xf000) != 0xc000 || socketIdentity.Owner != 0 || socketIdentity.Group != LinuxRecoveryMediaInstallation.ControllerId)
                throw LinuxRecoveryMediaInstallation.Invalid();
            listener.Listen(1);
            Startup.Write(Startup.Role.Host, Startup.Phase.HostCreated);
            return new(installation, runtime, group, ownership, listener, socketIdentity);
        }
        catch (Exception error)
        {
            Startup.Write(Startup.Role.Host, Startup.Phase.CreateFailed, Startup.Classify(error));
            listener?.Dispose(); group?.Dispose(); ownership?.Dispose(); runtime.Dispose();
            // Unexpected prior files/subgroups are retained. The owning systemd unit
            // cleans its process group; never sweep runtime paths on failed admission.
            throw;
        }
    }

    internal async Task<LinuxRecoveryOperationChannel> ConnectAsync(Guid operation, CancellationToken token)
    {
        try { return await ConnectCoreAsync(operation, token).ConfigureAwait(false); }
        catch (Exception error)
        { Startup.Write(Startup.Role.Host, Startup.Phase.ConnectFailed, Startup.Classify(error)); throw; }
    }

    private async Task<LinuxRecoveryOperationChannel> ConnectCoreAsync(Guid operation, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        installation.Recheck();
        process.StartInfo = new(LinuxRecoveryMediaInstallation.Root + "/launch/recovery-controller-launch")
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = LinuxRecoveryMediaInstallation.Root + "/launch", CreateNoWindow = true };
        process.StartInfo.Environment.Clear(); process.StartInfo.Environment["LANG"] = "C";
        Startup.Write(Startup.Role.Host, Startup.Phase.ChildStart);
        started = process.Start();
        if (!started) throw LinuxRecoveryMediaInstallation.Invalid();
        earlyExit = process.WaitForExitAsync(CancellationToken.None);
        diagnosticDrain = Startup.ReadControllerAsync(process.StandardError.BaseStream,
            (phase, error) => Startup.Write(Startup.Role.Controller, phase, error), diagnosticStop.Token);
        Startup.Write(Startup.Role.Host, Startup.Phase.ChildStarted);
        var accepting = listener.AcceptAsync(timeout.Token).AsTask();
        Startup.Write(Startup.Role.Host, Startup.Phase.Accept);
        Socket socket;
        try
        {
            if (await Task.WhenAny(accepting, earlyExit, diagnosticDrain).ConfigureAwait(false) == diagnosticDrain)
            {
                await diagnosticDrain.ConfigureAwait(false);
                await Task.WhenAny(accepting, earlyExit).ConfigureAwait(false);
            }
            if (earlyExit.IsCompleted && !accepting.IsCompletedSuccessfully)
            {
                if (process.ExitCode == 77) throw new UnauthorizedAccessException("recovery-controller.access-denied");
                throw LinuxRecoveryMediaInstallation.Invalid();
            }
            socket = await accepting.ConfigureAwait(false);
        }
        catch
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            try { (await accepting.ConfigureAwait(false)).Dispose(); }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
            throw;
        }
        LinuxWorkerPeerBinding? peer = null;
        try
        {
            // An accept racing a rejected startup record cannot hide a drain
            // failure already observed before peer/installation admission.
            if (diagnosticDrain.IsCompleted) await diagnosticDrain.ConfigureAwait(false);
            Startup.Write(Startup.Role.Host, Startup.Phase.Peer);
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
            Startup.Write(Startup.Role.Host, Startup.Phase.Hello);
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

    internal Task WaitForPresentationExitAsync(CancellationToken token)
    {
        lock (gate)
        {
            if (disposal is not null) throw new ObjectDisposedException(nameof(LinuxRecoveryControllerLaunch));
            return WaitForOwnedExitAsync(earlyExit ?? throw new InvalidOperationException("recovery-controller.not-connected"),
                TimeSpan.FromMinutes(10), token);
        }
    }

    // Presentation retention only, after the worker and receipt are terminal.
    // This never grants execution, inspects an exit code, or cancels the owned
    // process-exit task. Existing cgroup disposal follows every return path.
    internal static async Task WaitForOwnedExitAsync(Task exit, TimeSpan limit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(exit);
        if (limit <= TimeSpan.Zero || limit > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(limit));
        try { await exit.WaitAsync(limit, token).ConfigureAwait(false); }
        catch (TimeoutException) when (!exit.IsFaulted) { }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !exit.IsFaulted && !exit.IsCanceled) { }
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
        Startup.Write(Startup.Role.Host, Startup.Phase.Cleanup);
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
            Startup.Write(Startup.Role.Host, Startup.Phase.ControllerStopped);
            Startup.Write(Startup.Role.Host, Startup.Phase.SocketRemoval);
            if (LinuxRecoveryNative.StatPath(LinuxRecoveryMediaInstallation.SocketPath) != socketIdentity) throw LinuxRecoveryMediaInstallation.Invalid();
            LinuxRecoveryNative.Remove(runtime, "controller.sock", false);
            Startup.Write(Startup.Role.Host, Startup.Phase.GroupRemoval);
            using var service = LinuxRecoveryNative.OpenDirectory("/sys/fs/cgroup" + LinuxRecoveryMediaInstallation.ServiceGroup, false);
            LinuxRecoveryNative.Remove(service, "controller", true);
        }
        catch (Exception error) { failure = error; Startup.Write(Startup.Role.Host, Startup.Phase.CleanupFailed, Startup.Classify(error)); }
        finally
        {
            try { await DrainDiagnosticsAsync().ConfigureAwait(false); }
            catch (Exception error)
            { failure ??= error; Startup.Write(Startup.Role.Host, Startup.Phase.ChildStatusRejected, Startup.Classify(error)); }
            finally { process.Dispose(); group.Dispose(); ownership.Dispose(); runtime.Dispose(); }
        }
        if (failure is null) Startup.Write(Startup.Role.Host, Startup.Phase.Cleaned);
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }

    private async Task DrainDiagnosticsAsync()
    {
        Exception? failure = null;
        try { if (diagnosticDrain is not null) await diagnosticDrain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception error) { failure = error; }
        finally
        {
            try { await diagnosticStop.CancelAsync().ConfigureAwait(false); }
            finally
            {
                try
                {
                    if (diagnosticDrain is not null)
                    {
                        try { await diagnosticDrain.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (diagnosticStop.IsCancellationRequested) { }
                    }
                }
                finally { diagnosticStop.Dispose(); }
            }
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    [DllImport("libc", EntryPoint = "chown")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Chown(string path, uint user, uint group);
}
