using System.Net.Sockets;
using System.Text.Json;

namespace VaultSync.Core.Services;

/// <summary>Internal media controller. Intent and wire acknowledgment never authorize writing.</summary>
internal static class LinuxRecoveryControllerClient
{
    internal enum Result { Restored, Refused, Failed, AccessDenied, Cancelled }
    internal sealed record Intent(string ImageFolder, string KernelName);
    internal interface IChannel : IDisposable
    {
        Task OfferAsync(FileStream metadata, FileStream payload, CancellationToken token);
        Task SendAsync(LinuxRecoveryMessage kind, byte[] body, CancellationToken token);
        Task<LinuxRecoveryOperationChannel.Frame> ReceiveAsync(CancellationToken token);
    }
    private sealed class Channel(LinuxRecoveryOperationChannel channel) : IChannel
    {
        public Task OfferAsync(FileStream metadata, FileStream payload, CancellationToken token) => channel.OfferImagesAsync(metadata, payload, token);
        public Task SendAsync(LinuxRecoveryMessage kind, byte[] body, CancellationToken token) => channel.SendAsync(kind, body, token);
        public Task<LinuxRecoveryOperationChannel.Frame> ReceiveAsync(CancellationToken token) => channel.ReceiveAsync(token);
        public void Dispose() => channel.Dispose();
    }

    internal static Intent DecodeIntent(byte[] bytes)
    {
        if (bytes.Length is 0 or > 16384) throw LinuxRecoveryMediaInstallation.Invalid();
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2, AllowDuplicateProperties = false });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { "imageFolder", "kernelName" })) throw LinuxRecoveryMediaInstallation.Invalid();
        if (root.GetProperty("imageFolder").ValueKind != JsonValueKind.String || root.GetProperty("kernelName").ValueKind != JsonValueKind.String)
            throw LinuxRecoveryMediaInstallation.Invalid();
        string folder = root.GetProperty("imageFolder").GetString()!, kernel = root.GetProperty("kernelName").GetString()!;
        if (folder.Length is 0 or > 4096 || !Path.IsPathFullyQualified(folder) || folder.Any(char.IsControl) ||
            folder.Split('/').Any(part => part is "." or "..") || kernel.Length is 0 or > 63 ||
            !kernel.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) throw LinuxRecoveryMediaInstallation.Invalid();
        return new(folder, kernel);
    }

    internal static async Task<Result> RunInstalledAsync(CancellationToken token)
    {
        using var installation = await LinuxRecoveryMediaInstallation.OpenAsync(LinuxRecoveryMediaInstallation.Role.Controller, token).ConfigureAwait(false);
        // Untrusted intent is read only in this nonroot role. No privileged opener,
        // permissions repair, original chmod or source-access fallback exists.
        byte[] request;
        using (var file = File.OpenRead("/run/vaultsync-recovery/request.json"))
        {
            if (file.Length is <= 0 or > 16384) throw LinuxRecoveryMediaInstallation.Invalid();
            request = new byte[(int)file.Length]; await file.ReadExactlyAsync(request, token).ConfigureAwait(false);
            if (file.ReadByte() != -1) throw LinuxRecoveryMediaInstallation.Invalid();
        }
        var intent = DecodeIntent(request);
        // Traverse-only 0710 is intentional: the controller cannot list/create
        // entries. stat/connect need traversal, not an O_RDONLY directory opener.
        {
            var directory = LinuxRecoveryNative.StatPath("/run/vaultsync-recovery"); var endpoint = LinuxRecoveryNative.StatPath(LinuxRecoveryMediaInstallation.SocketPath);
            if (directory.Mode != 0x41c8 || directory.Owner != 0 || directory.Group != LinuxRecoveryMediaInstallation.ControllerId ||
                endpoint.Mode != 0xc1b0 || endpoint.Owner != 0 || endpoint.Group != LinuxRecoveryMediaInstallation.ControllerId || endpoint.Links != 1)
                throw LinuxRecoveryMediaInstallation.Invalid();
        }
        FileStream? metadata = null, payload = null;
        try
        {
            metadata = File.OpenRead(Path.Combine(intent.ImageFolder, "image.metadata"));
            payload = File.OpenRead(Path.Combine(intent.ImageFolder, "payload.raw"));
        }
        catch (UnauthorizedAccessException) { metadata?.Dispose(); return Result.AccessDenied; }
        catch { metadata?.Dispose(); throw; }
        using (metadata)
        using (payload)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            LinuxWorkerPeerBinding? peer = null; bool transferred = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                int parent = LinuxRecoveryNative.ParentProcess();
                if (parent <= 1) throw LinuxRecoveryMediaInstallation.Invalid();
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(LinuxRecoveryMediaInstallation.SocketPath), timeout.Token).ConfigureAwait(false);
                peer = LinuxWorkerPeerBinding.TryCreate(socket, new(parent, 0, 0)) ?? throw LinuxRecoveryMediaInstallation.Invalid();
                if (!peer.Recheck()) throw LinuxRecoveryMediaInstallation.Invalid();
                installation.Recheck();
                using var stream = new NetworkStream(socket, false); byte[] hello = new byte[24];
                await stream.ReadExactlyAsync(hello, timeout.Token).ConfigureAwait(false);
                Guid operation = LinuxRecoveryControllerLaunch.DecodeHello(hello);
                if (!peer.Recheck()) throw LinuxRecoveryMediaInstallation.Invalid();
                var channel = new LinuxRecoveryOperationChannel(socket, peer, operation, LinuxRecoveryRole.Controller);
                peer = null; transferred = true;
                return await RunAsync(new Channel(channel), operation, metadata, payload, intent.KernelName, token).ConfigureAwait(false);
            }
            finally { if (!transferred) { peer?.Dispose(); socket.Dispose(); } }
        }
    }

    // Fixture channel only. Production must perform the installed/peer admission above.
    internal static async Task<Result> RunAsync(IChannel channel, Guid operation, FileStream metadata, FileStream payload,
        string kernelName, CancellationToken token, Action<LinuxRecoverySession.Snapshot>? observe = null)
    {
        var session = new LinuxRecoverySession(operation);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        Task<LinuxRecoveryOperationChannel.Frame>? incoming = null; Task<bool>? tick = null;
        try
        {
            incoming = channel.ReceiveAsync(stop.Token); tick = timer.WaitForNextTickAsync(stop.Token).AsTask();
            while (true)
            {
                await Task.WhenAny(incoming, tick).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (incoming.IsCompleted)
                {
                    using var frame = await incoming.ConfigureAwait(false); incoming = null;
                    var state = session.Apply(LinuxRecoveryRole.Worker, frame); observe?.Invoke(state);
                    switch (frame.Kind)
                    {
                        case LinuxRecoveryMessage.Ready:
                            await channel.OfferAsync(metadata, payload, stop.Token).ConfigureAwait(false);
                            Apply(LinuxRecoveryMessage.OfferImages, []); break;
                        case LinuxRecoveryMessage.Staging when state.Phase == LinuxRecoverySessionPhase.Staging:
                            await Send(LinuxRecoveryMessage.InventoryRequest, []).ConfigureAwait(false); break;
                        case LinuxRecoveryMessage.Inventory:
                            if (state.Inventory!.State != Models.LinuxBlockInventoryState.Observed || state.Inventory.HasUnresolvedSwaps ||
                                !state.Inventory.Devices.Any(d => d.KernelName == kernelName)) return Result.Refused;
                            await Send(LinuxRecoveryMessage.Prepare, LinuxRecoveryProtocol.Selection(state.Inventory.ObservationId, kernelName)).ConfigureAwait(false); break;
                        case LinuxRecoveryMessage.Review:
                            // Automatic headless ACK, explicitly not independent human approval.
                            await Send(LinuxRecoveryMessage.Confirm, LinuxRecoveryProtocol.ReviewAcknowledgment(state.Review!.ReviewId)).ConfigureAwait(false); break;
                        case LinuxRecoveryMessage.Refused: return Result.Refused;
                        case LinuxRecoveryMessage.Outcome:
                            return state.Outcome!.Execution.State == RawImageDeviceRestoreState.WrittenAndReadBack
                                ? Result.Restored : Result.Failed;
                    }
                    incoming = channel.ReceiveAsync(stop.Token);
                }
                if (tick.IsCompleted)
                {
                    _ = await tick.ConfigureAwait(false);
                    await Send(LinuxRecoveryMessage.Heartbeat, []).ConfigureAwait(false);
                    tick = timer.WaitForNextTickAsync(stop.Token).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { session.Fail(); return Result.Cancelled; }
        catch { session.Fail(); throw; }
        finally
        {
            try { await stop.CancelAsync().ConfigureAwait(false); }
            finally
            {
                try { channel.Dispose(); }
                finally
                {
                    if (incoming is not null)
                    {
                        try { (await incoming.ConfigureAwait(false)).Dispose(); }
                        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException or SocketException or ObjectDisposedException) { }
                    }
                    if (tick is not null)
                    { try { _ = await tick.ConfigureAwait(false); } catch (OperationCanceledException) { } }
                }
            }
        }
        void Apply(LinuxRecoveryMessage kind, byte[] bytes)
        { using var frame = new LinuxRecoveryOperationChannel.Frame(kind, bytes, null); session.Apply(LinuxRecoveryRole.Controller, frame); }
        async Task Send(LinuxRecoveryMessage kind, byte[] bytes)
        { await channel.SendAsync(kind, bytes, stop.Token).ConfigureAwait(false); Apply(kind, bytes); }
    }
}
