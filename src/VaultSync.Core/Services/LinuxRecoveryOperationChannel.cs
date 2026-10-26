using System.Net.Sockets;

namespace VaultSync.Core.Services;

/// <summary>
/// Internal single-operation transport. Its caller must already establish trusted
/// installation, role, controlled offline execution and actual human authority.
/// Kernel credentials/IDs do not supply these prerequisites. No production host
/// or public writable endpoint is introduced by this channel.
/// </summary>
internal sealed class LinuxRecoveryOperationChannel : IDisposable
{
    internal sealed class Frame(LinuxRecoveryMessage kind, byte[] body, LinuxRecoveryImageTransfer.Sources? sources) : IDisposable
    {
        internal LinuxRecoveryMessage Kind { get; } = kind;
        internal ReadOnlyMemory<byte> Body { get; } = body;
        internal LinuxRecoveryImageTransfer.Sources? Sources { get; } = sources;
        public void Dispose() => Sources?.Dispose();
    }

    private readonly Socket socket;
    private readonly LinuxWorkerPeerBinding peer;
    private readonly NetworkStream stream;
    private readonly Guid operation;
    private readonly LinuxRecoveryRole role;
    // Retain the managed gates for in-flight waiters during disposal.
    private readonly SemaphoreSlim reading = new(1, 1), writing = new(1, 1);
    private uint received, sent;
    private int closed;

    // Adopts socket and binding on success only; never accepts a binding from a
    // different connection or a test-provider binding in the native transport.
    internal LinuxRecoveryOperationChannel(Socket socket, LinuxWorkerPeerBinding peer, Guid operation, LinuxRecoveryRole role)
    {
        if (operation == Guid.Empty || role is not (LinuxRecoveryRole.Controller or LinuxRecoveryRole.Worker) ||
            !peer.OwnsConnection(socket) || !peer.Recheck()) throw Refused();
        this.socket = socket; this.peer = peer; this.operation = operation; this.role = role;
        stream = new(socket, ownsSocket: false);
    }

    internal Task SendAsync(LinuxRecoveryMessage kind, ReadOnlyMemory<byte> body, CancellationToken token = default)
    {
        if (kind == LinuxRecoveryMessage.OfferImages) throw Refused(); // Handoff must hold the writer gate through SCM_RIGHTS.
        return SendCoreAsync(kind, body, null, token);
    }

    internal Task OfferImagesAsync(FileStream metadata, FileStream payload, CancellationToken token = default) =>
        SendCoreAsync(LinuxRecoveryMessage.OfferImages, ReadOnlyMemory<byte>.Empty, (metadata, payload), token);

    private async Task SendCoreAsync(LinuxRecoveryMessage kind, ReadOnlyMemory<byte> body,
        (FileStream Metadata, FileStream Payload)? sources, CancellationToken token)
    {
        // Validate before queuing or consuming a sequence. Local malformed calls
        // cannot inject bytes or cancel the owner currently holding the writer.
        _ = LinuxRecoveryProtocol.EncodeHeader(role, kind, operation, 1, body.Length);
        byte[] ownedBody = body.ToArray();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await writing.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            Check(deadline.Token);
            if (sent == uint.MaxValue) throw Refused();
            byte[] header = LinuxRecoveryProtocol.EncodeHeader(role, kind, operation, sent + 1, ownedBody.Length);
            await stream.WriteAsync(header, deadline.Token).ConfigureAwait(false);
            if (ownedBody.Length != 0) await stream.WriteAsync(ownedBody, deadline.Token).ConfigureAwait(false);
            if (sources is { } pair)
                await LinuxRecoveryImageTransfer.SendAsync(socket, peer, pair.Metadata, pair.Payload, deadline.Token).ConfigureAwait(false);
            Check(deadline.Token); sent++;
        }
        catch { Dispose(); throw; } // Framing may be partial; never retry a used channel.
        finally { writing.Release(); }
    }

    internal async Task<Frame> ReceiveAsync(CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Long operations require controller heartbeats; no silent infinite wait.
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        await reading.WaitAsync(deadline.Token).ConfigureAwait(false);
        LinuxRecoveryImageTransfer.Sources? sources = null;
        bool transferred = false;
        try
        {
            Check(deadline.Token);
            if (received == uint.MaxValue) throw Refused();
            byte[] bytes = new byte[LinuxRecoveryProtocol.HeaderBytes];
            await stream.ReadExactlyAsync(bytes, deadline.Token).ConfigureAwait(false);
            var header = LinuxRecoveryProtocol.DecodeHeader(bytes,
                role == LinuxRecoveryRole.Worker ? LinuxRecoveryRole.Controller : LinuxRecoveryRole.Worker, operation, received + 1);
            // Length and direction are checked before any body allocation/read.
            byte[] body = new byte[header.BodyBytes];
            if (body.Length != 0) await stream.ReadExactlyAsync(body, deadline.Token).ConfigureAwait(false);
            if (header.Kind == LinuxRecoveryMessage.OfferImages)
                sources = await LinuxRecoveryImageTransfer.ReceiveAsync(socket, peer, deadline.Token).ConfigureAwait(false);
            Check(deadline.Token);
            var result = new Frame(header.Kind, body, sources);
            received++; transferred = true; return result;
        }
        catch { Dispose(); throw; }
        finally
        {
            try { if (!transferred) sources?.Dispose(); }
            finally { reading.Release(); }
        }
    }

    private void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref closed) != 0 || !peer.Recheck()) throw Refused();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        try { peer.Dispose(); }
        finally { try { stream.Dispose(); } finally { socket.Dispose(); } }
    }
    private static IOException Refused() => new("recovery-channel.refused");
}
