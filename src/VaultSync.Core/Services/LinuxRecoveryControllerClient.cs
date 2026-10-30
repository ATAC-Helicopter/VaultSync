using System.Net.Sockets;
using System.Text.Json;

namespace VaultSync.Core.Services;

/// <summary>Internal media controller. Intent and wire acknowledgment never authorize writing.</summary>
internal static class LinuxRecoveryControllerClient
{
    internal enum Result { Restored, Refused, Failed, AccessDenied, Cancelled }
    internal sealed record Intent(string ImageFolder, string KernelName);
    // All choices are correlation only, never overwrite authority. Image streams
    // remain provider-owned (including late results) and must stay open until
    // RunAsync has drained every choice and returned. The client never closes them.
    internal sealed record ImageSources(Guid OperationId, FileStream Metadata, FileStream Payload);
    internal sealed record TargetSelection(Guid OperationId, Guid ObservationId, string KernelName);
    internal sealed record ReviewAcknowledgment(Guid OperationId, Guid ReviewId);
    internal interface IInteraction
    {
        // Separate from the runner's lifetime token: request one wire Cancel and
        // keep receiving the worker's actual refusal/outcome. Choices must return
        // promptly, honor cancellation and release their own UI/input ownership.
        CancellationToken CancellationRequested { get; }
        Task<ImageSources?> SelectImagesAsync(Guid operation, CancellationToken token);
        Task<TargetSelection?> SelectTargetAsync(Guid operation, LinuxRecoveryResponses.Inventory inventory, CancellationToken token);
        Task<ReviewAcknowledgment?> AcknowledgeReviewAsync(LinuxRecoveryResponses.Review review, CancellationToken token);
    }
    // Explicit compatibility policy for the installed headless request. A future
    // graphical caller must supply its own interaction; there is no default ACK.
    internal sealed class HeadlessInteraction(FileStream metadata, FileStream payload, string kernelName) : IInteraction
    {
        public CancellationToken CancellationRequested => CancellationToken.None;
        public Task<ImageSources?> SelectImagesAsync(Guid operation, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<ImageSources?>(new(operation, metadata, payload)); }
        public Task<TargetSelection?> SelectTargetAsync(Guid operation, LinuxRecoveryResponses.Inventory inventory, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<TargetSelection?>(new(operation, inventory.ObservationId, kernelName)); }
        public Task<ReviewAcknowledgment?> AcknowledgeReviewAsync(LinuxRecoveryResponses.Review review, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult<ReviewAcknowledgment?>(new(review.OperationId, review.ReviewId)); }
    }
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
                return await RunAsync(new Channel(channel), operation,
                    new HeadlessInteraction(metadata, payload, intent.KernelName), token).ConfigureAwait(false);
            }
            finally { if (!transferred) { peer?.Dispose(); socket.Dispose(); } }
        }
    }

    // Fixture channel only. Production must perform the installed/peer admission above.
    internal static async Task<Result> RunAsync(IChannel channel, Guid operation, IInteraction interaction,
        CancellationToken token, Action<LinuxRecoverySession.Snapshot>? observe = null)
    {
        var session = new LinuxRecoverySession(operation);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var choices = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = interaction.CancellationRequested.Register(() => cancellation.TrySetResult());
        Task<LinuxRecoveryOperationChannel.Frame>? incoming = null; Task<bool>? tick = null;
        Task? pendingChoice = null;
        try
        {
            incoming = channel.ReceiveAsync(stop.Token); tick = timer.WaitForNextTickAsync(stop.Token).AsTask();
            while (true)
            {
                var pending = new List<Task> { incoming, tick };
                if (!session.State.CancellationRequested)
                {
                    pending.Add(cancellation.Task);
                    if (pendingChoice is not null) pending.Add(pendingChoice);
                }
                await Task.WhenAny(pending).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (incoming.IsCompleted)
                {
                    using var frame = await incoming.ConfigureAwait(false); incoming = null;
                    var state = session.Apply(LinuxRecoveryRole.Worker, frame); observe?.Invoke(state);
                    switch (frame.Kind)
                    {
                        case LinuxRecoveryMessage.Ready:
                            if (!CancelRequested()) pendingChoice = interaction.SelectImagesAsync(operation, choices.Token);
                            break;
                        case LinuxRecoveryMessage.Staging when state.Phase == LinuxRecoverySessionPhase.Staging:
                            if (!CancelRequested()) await Send(LinuxRecoveryMessage.InventoryRequest, []).ConfigureAwait(false);
                            break;
                        case LinuxRecoveryMessage.Inventory:
                            if (state.Inventory!.State != Models.LinuxBlockInventoryState.Observed || state.Inventory.HasUnresolvedSwaps)
                            { await Cancel().ConfigureAwait(false); break; }
                            if (!CancelRequested()) pendingChoice = interaction.SelectTargetAsync(operation, state.Inventory, choices.Token);
                            break;
                        case LinuxRecoveryMessage.Review:
                            if (!CancelRequested()) pendingChoice = interaction.AcknowledgeReviewAsync(state.Review!, choices.Token);
                            break;
                        case LinuxRecoveryMessage.Refused: return Result.Refused;
                        case LinuxRecoveryMessage.Outcome:
                            return state.Outcome!.Execution.State == RawImageDeviceRestoreState.WrittenAndReadBack
                                ? Result.Restored : Result.Failed;
                    }
                    incoming = channel.ReceiveAsync(stop.Token);
                }
                if (cancellation.Task.IsCompleted && !session.State.CancellationRequested)
                    await Cancel().ConfigureAwait(false);
                if (!session.State.CancellationRequested && !incoming.IsCompleted && pendingChoice?.IsCompleted == true)
                {
                    // Consume one choice on this transcript owner. Incoming terminal
                    // frames and cancellation win over a simultaneously completed UI.
                    var completed = pendingChoice; pendingChoice = null;
                    if (!await ApplyChoiceAsync(completed, operation, session, channel, stop.Token, CancelRequested).ConfigureAwait(false))
                        await Cancel().ConfigureAwait(false);
                    if (cancellation.Task.IsCompleted && !session.State.CancellationRequested)
                        await Cancel().ConfigureAwait(false);
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
        finally { await CloseAsync(channel, stop, choices.Token, incoming, tick, pendingChoice).ConfigureAwait(false); }
        bool CancelRequested() => session.State.CancellationRequested || cancellation.Task.IsCompleted;
        async Task Cancel()
        {
            if (session.State.CancellationRequested) return;
            await choices.CancelAsync().ConfigureAwait(false);
            await Send(LinuxRecoveryMessage.Cancel, []).ConfigureAwait(false);
        }
        Task Send(LinuxRecoveryMessage kind, byte[] bytes) => SendAsync(channel, session, kind, bytes, stop.Token);
    }

    private static async Task<bool> ApplyChoiceAsync(Task choice, Guid operation, LinuxRecoverySession session,
        IChannel channel, CancellationToken token, Func<bool> cancelRequested)
    {
        switch (choice)
        {
            case Task<ImageSources?> images:
            {
                var selected = await images.ConfigureAwait(false);
                if (selected is null) return false;
                Validate(selected, operation);
                if (!cancelRequested())
                {
                    await channel.OfferAsync(selected.Metadata, selected.Payload, token).ConfigureAwait(false);
                    Apply(session, LinuxRecoveryMessage.OfferImages, []);
                }
                break;
            }
            case Task<TargetSelection?> target:
            {
                var selected = await target.ConfigureAwait(false);
                if (selected is null) return false;
                Validate(selected, operation, session.State.Inventory!);
                if (!cancelRequested()) await SendAsync(channel, session, LinuxRecoveryMessage.Prepare,
                    LinuxRecoveryProtocol.Selection(selected.ObservationId, selected.KernelName), token).ConfigureAwait(false);
                break;
            }
            case Task<ReviewAcknowledgment?> acknowledgment:
            {
                var selected = await acknowledgment.ConfigureAwait(false);
                if (selected is null) return false;
                Validate(selected, operation, session.State.Review!);
                if (!cancelRequested()) await SendAsync(channel, session, LinuxRecoveryMessage.Confirm,
                    LinuxRecoveryProtocol.ReviewAcknowledgment(selected.ReviewId), token).ConfigureAwait(false);
                break;
            }
            default: throw new InvalidOperationException("recovery-controller.invalid-choice");
        }
        return true;
    }

    private static void Validate(ImageSources selected, Guid operation)
    {
        if (selected.OperationId != operation || !selected.Metadata.CanRead || selected.Metadata.CanWrite ||
            !selected.Payload.CanRead || selected.Payload.CanWrite) throw LinuxRecoveryMediaInstallation.Invalid();
    }
    private static void Validate(TargetSelection selected, Guid operation, LinuxRecoveryResponses.Inventory inventory)
    {
        if (selected.OperationId != operation || selected.ObservationId != inventory.ObservationId ||
            !inventory.Devices.Any(d => d.KernelName == selected.KernelName)) throw LinuxRecoveryMediaInstallation.Invalid();
    }
    private static void Validate(ReviewAcknowledgment selected, Guid operation, LinuxRecoveryResponses.Review review)
    {
        if (selected.OperationId != operation || selected.ReviewId != review.ReviewId) throw LinuxRecoveryMediaInstallation.Invalid();
    }

    private static async Task SendAsync(IChannel channel, LinuxRecoverySession session,
        LinuxRecoveryMessage kind, byte[] bytes, CancellationToken token)
    {
        await channel.SendAsync(kind, bytes, token).ConfigureAwait(false);
        Apply(session, kind, bytes);
    }
    private static void Apply(LinuxRecoverySession session, LinuxRecoveryMessage kind, byte[] bytes)
    { using var frame = new LinuxRecoveryOperationChannel.Frame(kind, bytes, null); session.Apply(LinuxRecoveryRole.Controller, frame); }

    private static async Task CloseAsync(IChannel channel, CancellationTokenSource stop, CancellationToken choiceToken,
        Task<LinuxRecoveryOperationChannel.Frame>? incoming, Task<bool>? tick, Task? choice)
    {
        try { await stop.CancelAsync().ConfigureAwait(false); }
        finally
        {
            try { channel.Dispose(); }
            finally { await DrainAsync(incoming, tick, choice, choiceToken).ConfigureAwait(false); }
        }
    }
    private static async Task DrainAsync(Task<LinuxRecoveryOperationChannel.Frame>? incoming, Task<bool>? tick,
        Task? choice, CancellationToken choiceToken)
    {
        try
        {
            if (incoming is not null)
            {
                try { (await incoming.ConfigureAwait(false)).Dispose(); }
                catch (Exception error) when (error is OperationCanceledException or IOException or InvalidDataException or SocketException or ObjectDisposedException) { }
            }
        }
        finally
        {
            try
            {
                if (tick is not null)
                { try { _ = await tick.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            }
            finally
            {
                if (choice is not null)
                { try { await choice.ConfigureAwait(false); } catch (OperationCanceledException) when (choiceToken.IsCancellationRequested) { } }
            }
        }
    }
}
