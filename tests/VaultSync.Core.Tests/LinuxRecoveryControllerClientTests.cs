#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;
using Responses = VaultSync.Core.Services.LinuxRecoveryResponses;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryControllerClientTests
{
    private static readonly Guid Operation = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Observation = Guid.Parse("11112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid ReviewId = Guid.Parse("22112233-4455-6677-8899-aabbccddeeff");
    private static Responses.Device Device => new("sda", null, 8, 0, 131584, 1, 512, 512,
        LinuxBlockKind.WholeDisk, LinuxBlockIdentity.UniqueInSnapshot, LinuxBlockUse.Unknown, false, false, false, false);
    private static Responses.Inventory Inventory => new(Observation, DateTimeOffset.UnixEpoch,
        LinuxBlockInventoryState.Observed, LinuxBlockInventoryReason.NamespaceObservationOnly, false, new[] { Device });
    private static Responses.Review Review => new(Operation, ReviewId, Guid.Parse("33112233-4455-6677-8899-aabbccddeeff"),
        Guid.Parse("44112233-4455-6677-8899-aabbccddeeff"), 1, new string('a', 64), DateTimeOffset.UnixEpoch, 131584, Device, Observation);

    private sealed class Sources : IDisposable
    {
        private readonly string metadata = Path.GetTempFileName(), payload = Path.GetTempFileName();
        internal readonly FileStream Metadata, Payload;
        internal Sources() { Metadata = File.OpenRead(metadata); Payload = File.OpenRead(payload); }
        public void Dispose() { Metadata.Dispose(); Payload.Dispose(); File.Delete(metadata); File.Delete(payload); }
    }
    private sealed class Peer(Action<Peer, LinuxRecoveryMessage, byte[]> sent) : LinuxRecoveryControllerClient.IChannel
    {
        private readonly Channel<LinuxRecoveryOperationChannel.Frame> input = Channel.CreateUnbounded<LinuxRecoveryOperationChannel.Frame>();
        internal readonly ConcurrentQueue<LinuxRecoveryMessage> Sent = new();
        internal readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        internal void Push(LinuxRecoveryMessage kind, byte[]? body = null) =>
            Assert.True(input.Writer.TryWrite(new(kind, body ?? Array.Empty<byte>(), null)));
        public Task OfferAsync(FileStream metadata, FileStream payload, CancellationToken token)
        { Assert.True(metadata.CanRead); Assert.False(metadata.CanWrite); Assert.True(payload.CanRead); return SendAsync(LinuxRecoveryMessage.OfferImages, Array.Empty<byte>(), token); }
        public Task SendAsync(LinuxRecoveryMessage kind, byte[] body, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Sent.Enqueue(kind); sent(this, kind, body); return Task.CompletedTask; }
        public async Task<LinuxRecoveryOperationChannel.Frame> ReceiveAsync(CancellationToken token)
        {
            try { return await input.Reader.ReadAsync(token); }
            catch (ChannelClosedException) { throw new IOException("fixture-peer-closed"); }
        }
        public void Dispose()
        { Disposed = true; input.Writer.TryComplete(); while (input.Reader.TryRead(out var frame)) frame.Dispose(); Closed.TrySetResult(); }
    }

    private static LinuxRecoveryControllerClient.HeadlessInteraction Headless(Sources sources) => new(sources.Metadata, sources.Payload, "sda");
    private sealed class Interaction(Sources sources) : LinuxRecoveryControllerClient.IInteraction
    {
        internal Func<Guid, CancellationToken, Task<LinuxRecoveryControllerClient.ImageSources?>> Images = (operation, _) =>
            Task.FromResult<LinuxRecoveryControllerClient.ImageSources?>(new(operation, sources.Metadata, sources.Payload));
        internal Func<Guid, Responses.Inventory, CancellationToken, Task<LinuxRecoveryControllerClient.TargetSelection?>> Target = (operation, inventory, _) =>
            Task.FromResult<LinuxRecoveryControllerClient.TargetSelection?>(new(operation, inventory.ObservationId, "sda"));
        internal Func<Responses.Review, CancellationToken, Task<LinuxRecoveryControllerClient.ReviewAcknowledgment?>> Acknowledge = (review, _) =>
            Task.FromResult<LinuxRecoveryControllerClient.ReviewAcknowledgment?>(new(review.OperationId, review.ReviewId));
        public CancellationToken CancellationRequested { get; init; }
        public Task<LinuxRecoveryControllerClient.ImageSources?> SelectImagesAsync(Guid operation, CancellationToken token) => Images(operation, token);
        public Task<LinuxRecoveryControllerClient.TargetSelection?> SelectTargetAsync(Guid operation, Responses.Inventory inventory, CancellationToken token) => Target(operation, inventory, token);
        public Task<LinuxRecoveryControllerClient.ReviewAcknowledgment?> AcknowledgeReviewAsync(Responses.Review review, CancellationToken token) => Acknowledge(review, token);
    }
    private static Peer ReviewPeer()
    {
        var peer = new Peer((connection, kind, _) =>
        {
            if (kind == LinuxRecoveryMessage.OfferImages) connection.Push(LinuxRecoveryMessage.Staging);
            if (kind == LinuxRecoveryMessage.InventoryRequest) connection.Push(LinuxRecoveryMessage.Inventory, Responses.EncodeInventory(Inventory));
            if (kind == LinuxRecoveryMessage.Prepare) connection.Push(LinuxRecoveryMessage.Review, Responses.EncodeReview(Review));
            if (kind == LinuxRecoveryMessage.Cancel) connection.Push(LinuxRecoveryMessage.Refused, Responses.EncodeRefused(LinuxRawRestoreOperationReason.ReviewRefused));
        });
        peer.Push(LinuxRecoveryMessage.Ready); return peer;
    }

    [Fact]
    public async Task DeferredImageAndTargetChoicesDoNotSendBeforeSelectionAndNullReviewCancels()
    {
        using var sources = new Sources(); using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var images = new TaskCompletionSource<LinuxRecoveryControllerClient.ImageSources?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var target = new TaskCompletionSource<LinuxRecoveryControllerClient.TargetSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var imageEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interaction = new Interaction(sources)
        {
            Images = (_, token) => { imageEntered.TrySetResult(); return images.Task.WaitAsync(token); },
            Target = (_, _, token) => { targetEntered.TrySetResult(); return target.Task.WaitAsync(token); },
            Acknowledge = (_, _) => Task.FromResult<LinuxRecoveryControllerClient.ReviewAcknowledgment?>(null)
        };
        var peer = ReviewPeer();
        var run = LinuxRecoveryControllerClient.RunAsync(peer, Operation, interaction, lifetime.Token);
        await imageEntered.Task.WaitAsync(lifetime.Token);
        Assert.Empty(peer.Sent);
        images.SetResult(new(Operation, sources.Metadata, sources.Payload));
        await targetEntered.Task.WaitAsync(lifetime.Token);
        Assert.DoesNotContain(LinuxRecoveryMessage.Prepare, peer.Sent);
        target.SetResult(new(Operation, Observation, "sda"));
        Assert.Equal(LinuxRecoveryControllerClient.Result.Refused, await run.WaitAsync(lifetime.Token));
        Assert.Equal(1, peer.Sent.Count(kind => kind == LinuxRecoveryMessage.Cancel));
        Assert.DoesNotContain(LinuxRecoveryMessage.Confirm, peer.Sent);
        Assert.True(sources.Metadata.CanRead); Assert.True(sources.Payload.CanRead);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WorkerRefusalCancelsAndDrainsLateReviewChoiceWithoutAcknowledgment(bool lateFailure)
    {
        using var sources = new Sources(); using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<LinuxRecoveryControllerClient.ReviewAcknowledgment?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken choiceToken = default;
        var interaction = new Interaction(sources)
        { Acknowledge = (_, token) => { choiceToken = token; entered.TrySetResult(); return release.Task; } };
        var peer = ReviewPeer();
        var run = LinuxRecoveryControllerClient.RunAsync(peer, Operation, interaction, lifetime.Token);
        await entered.Task.WaitAsync(lifetime.Token);
        peer.Push(LinuxRecoveryMessage.Refused, Responses.EncodeRefused(LinuxRawRestoreOperationReason.ReviewRefused));
        try
        {
            await peer.Closed.Task.WaitAsync(lifetime.Token);
            Assert.True(choiceToken.IsCancellationRequested); Assert.False(run.IsCompleted);
        }
        finally
        {
            if (lateFailure) release.TrySetException(new IOException("fixture-late-choice-failure"));
            else release.TrySetResult(new(Operation, ReviewId));
        }
        if (lateFailure) await Assert.ThrowsAsync<IOException>(() => run.WaitAsync(lifetime.Token));
        else Assert.Equal(LinuxRecoveryControllerClient.Result.Refused, await run.WaitAsync(lifetime.Token));
        Assert.DoesNotContain(LinuxRecoveryMessage.Confirm, peer.Sent);
        Assert.True(sources.Metadata.CanRead); Assert.True(sources.Payload.CanRead);
    }

    [Theory]
    [InlineData("image-operation")] [InlineData("target-operation")] [InlineData("observation")]
    [InlineData("target")] [InlineData("review-operation")] [InlineData("review")]
    public async Task InexactChoicesFailBeforeSendingTheirCorrelatedCommand(string wrong)
    {
        using var sources = new Sources(); using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Guid other = Guid.NewGuid();
        var interaction = new Interaction(sources)
        {
            Images = (_, _) => Task.FromResult<LinuxRecoveryControllerClient.ImageSources?>(new(
                wrong == "image-operation" ? other : Operation, sources.Metadata, sources.Payload)),
            Target = (_, _, _) => Task.FromResult<LinuxRecoveryControllerClient.TargetSelection?>(new(
                wrong == "target-operation" ? other : Operation, wrong == "observation" ? other : Observation, wrong == "target" ? "sdb" : "sda")),
            Acknowledge = (_, _) => Task.FromResult<LinuxRecoveryControllerClient.ReviewAcknowledgment?>(new(
                wrong == "review-operation" ? other : Operation, wrong == "review" ? other : ReviewId))
        };
        var peer = ReviewPeer();
        await Assert.ThrowsAsync<InvalidDataException>(() => LinuxRecoveryControllerClient.RunAsync(peer, Operation, interaction, lifetime.Token));
        Assert.DoesNotContain(wrong.StartsWith("image", StringComparison.Ordinal) ? LinuxRecoveryMessage.OfferImages :
            wrong.StartsWith("review", StringComparison.Ordinal) ? LinuxRecoveryMessage.Confirm : LinuxRecoveryMessage.Prepare, peer.Sent);
        Assert.True(peer.Disposed); Assert.True(sources.Metadata.CanRead);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GracefulCancellationPreemptsChoicesAndSendsOnlyOneCancel(bool alreadyCancelled)
    {
        using var sources = new Sources(); using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interaction = new Interaction(sources)
        {
            CancellationRequested = cancellation.Token,
            Images = async (_, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; }
                finally { drained.TrySetResult(); }
            }
        };
        if (alreadyCancelled) await cancellation.CancelAsync();
        var peer = ReviewPeer();
        var run = LinuxRecoveryControllerClient.RunAsync(peer, Operation, interaction, lifetime.Token);
        if (!alreadyCancelled) { await entered.Task.WaitAsync(lifetime.Token); await cancellation.CancelAsync(); }
        Assert.Equal(LinuxRecoveryControllerClient.Result.Refused, await run.WaitAsync(lifetime.Token));
        Assert.Equal(new[] { LinuxRecoveryMessage.Cancel }, peer.Sent);
        Assert.Equal(!alreadyCancelled, entered.Task.IsCompleted); Assert.Equal(!alreadyCancelled, drained.Task.IsCompleted);
        Assert.True(sources.Metadata.CanRead);
    }

    [Fact]
    public async Task SyntheticTranscriptUsesActualCodecsCausalSendsAndLiveTypedObservations()
    {
        // Display/protocol fixture only: this peer is not a supervisor or writer.
        using var sources = new Sources();
        var observations = new List<LinuxRecoverySession.Snapshot>();
        var peer = new Peer((connection, kind, body) =>
        {
            switch (kind)
            {
                case LinuxRecoveryMessage.OfferImages: connection.Push(LinuxRecoveryMessage.Staging); break;
                case LinuxRecoveryMessage.InventoryRequest: connection.Push(LinuxRecoveryMessage.Inventory, Responses.EncodeInventory(Inventory)); break;
                case LinuxRecoveryMessage.Prepare:
                    Assert.Equal((Observation, "sda"), LinuxRecoveryProtocol.DecodeSelection(body));
                    connection.Push(LinuxRecoveryMessage.Review, Responses.EncodeReview(Review)); break;
                case LinuxRecoveryMessage.Confirm:
                    Assert.Equal(ReviewId, LinuxRecoveryProtocol.DecodeReviewAcknowledgment(body));
                    connection.Push(LinuxRecoveryMessage.Executing);
                    connection.Push(LinuxRecoveryMessage.Progress, Responses.EncodeProgress(new(ReviewId, 131072), Review));
                    connection.Push(LinuxRecoveryMessage.Outcome, Responses.EncodeOutcome(new(ReviewId, new(
                        RawImageDeviceRestoreState.WrittenAndReadBack, LinuxRawRestoreOperationReason.BytesVerified,
                        new(RawImageDeviceRestoreState.WrittenAndReadBack, 131584, 0, 0, true), true, true)), Review, 131072));
                    break;
            }
        });
        peer.Push(LinuxRecoveryMessage.Ready);
        var result = await LinuxRecoveryControllerClient.RunAsync(peer, Operation, Headless(sources),
            CancellationToken.None, observations.Add);
        Assert.Equal(LinuxRecoveryControllerClient.Result.Restored, result);
        Assert.Equal(new[] { LinuxRecoveryMessage.OfferImages, LinuxRecoveryMessage.InventoryRequest, LinuxRecoveryMessage.Prepare,
            LinuxRecoveryMessage.Confirm }, peer.Sent);
        Assert.Contains(observations, state => state.Inventory is not null);
        Assert.Contains(observations, state => state.Review is not null);
        Assert.Contains(observations, state => state.AcknowledgedBytes == 131072);
        Assert.Equal(LinuxRecoverySessionPhase.Completed, observations.Last().Phase);
        Assert.True(peer.Disposed); Assert.True(sources.Metadata.CanRead); Assert.True(sources.Payload.CanRead);
    }

    [Fact]
    public async Task OutOfOrderWorkerProgressCannotBecomeSuccessfulClientResult()
    {
        using var sources = new Sources();
        var peer = new Peer((connection, kind, _) =>
        { if (kind == LinuxRecoveryMessage.OfferImages) connection.Push(LinuxRecoveryMessage.Progress, Responses.EncodeProgress(new(ReviewId, 0), Review)); });
        peer.Push(LinuxRecoveryMessage.Ready);
        await Assert.ThrowsAsync<InvalidDataException>(() => LinuxRecoveryControllerClient.RunAsync(peer, Operation,
            Headless(sources), CancellationToken.None));
        Assert.True(peer.Disposed);
        Assert.DoesNotContain(LinuxRecoveryMessage.Confirm, peer.Sent);
    }

    [Fact]
    public async Task CancellationDrainsPendingReceiveAndKeepsCallerSourcesOwned()
    {
        using var sources = new Sources(); using var cancellation = new CancellationTokenSource();
        var peer = new Peer((_, _, _) => { });
        var run = LinuxRecoveryControllerClient.RunAsync(peer, Operation, Headless(sources), cancellation.Token);
        await cancellation.CancelAsync();
        Assert.Equal(LinuxRecoveryControllerClient.Result.Cancelled, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(peer.Disposed); Assert.True(sources.Metadata.CanRead);
    }

    [Fact]
    public void IntentIsBoundedSourceSelectionWithoutAuthorityFields()
    {
        string folder = Path.GetFullPath(Path.GetTempPath());
        byte[] valid = JsonSerializer.SerializeToUtf8Bytes(new { imageFolder = folder, kernelName = "sda" });
        Assert.Equal(folder, LinuxRecoveryControllerClient.DecodeIntent(valid).ImageFolder);
        foreach (var value in new[]
        {
            new { imageFolder = "relative", kernelName = "sda" },
            new { imageFolder = folder + "/../other", kernelName = "sda" },
            new { imageFolder = folder, kernelName = "/dev/sda" },
            new { imageFolder = folder, kernelName = "sda\0hidden" }
        }) Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerClient.DecodeIntent(JsonSerializer.SerializeToUtf8Bytes(value)));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerClient.DecodeIntent(JsonSerializer.SerializeToUtf8Bytes(
            new { imageFolder = folder, kernelName = "sda", authorized = true })));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerClient.DecodeIntent(new byte[16385]));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FullAcknowledgmentCannotTurnReadbackOrCleanupFailureIntoRestored(bool cleanupFailure)
    {
        using var sources = new Sources();
        var peer = new Peer((connection, kind, _) =>
        {
            switch (kind)
            {
                case LinuxRecoveryMessage.OfferImages: connection.Push(LinuxRecoveryMessage.Staging); break;
                case LinuxRecoveryMessage.InventoryRequest: connection.Push(LinuxRecoveryMessage.Inventory, Responses.EncodeInventory(Inventory)); break;
                case LinuxRecoveryMessage.Prepare: connection.Push(LinuxRecoveryMessage.Review, Responses.EncodeReview(Review)); break;
                case LinuxRecoveryMessage.Confirm:
                    connection.Push(LinuxRecoveryMessage.Executing);
                    connection.Push(LinuxRecoveryMessage.Progress, Responses.EncodeProgress(new(ReviewId, 131584), Review));
                    connection.Push(LinuxRecoveryMessage.Outcome, Responses.EncodeOutcome(new(ReviewId, new(
                        RawImageDeviceRestoreState.Failed, cleanupFailure ? LinuxRawRestoreOperationReason.CleanupUnconfirmed : LinuxRawRestoreOperationReason.Failed,
                        new(cleanupFailure ? RawImageDeviceRestoreState.WrittenAndReadBack : RawImageDeviceRestoreState.Failed, 131584, 0, 0, true),
                        true, !cleanupFailure)), Review, 131584));
                    break;
            }
        });
        peer.Push(LinuxRecoveryMessage.Ready);
        Assert.Equal(LinuxRecoveryControllerClient.Result.Failed, await LinuxRecoveryControllerClient.RunAsync(peer, Operation,
            Headless(sources), CancellationToken.None));
        Assert.True(peer.Disposed);
    }

    [Fact]
    public async Task RefusalAndObserverFailureCloseConversationWithoutAcknowledgingAReview()
    {
        using var sources = new Sources();
        var refusal = new Peer((connection, kind, _) =>
        {
            if (kind == LinuxRecoveryMessage.OfferImages) connection.Push(LinuxRecoveryMessage.Staging);
            if (kind == LinuxRecoveryMessage.InventoryRequest) connection.Push(LinuxRecoveryMessage.Refused,
                Responses.EncodeRefused(LinuxRawRestoreOperationReason.ReviewRefused));
        });
        refusal.Push(LinuxRecoveryMessage.Ready);
        Assert.Equal(LinuxRecoveryControllerClient.Result.Refused, await LinuxRecoveryControllerClient.RunAsync(refusal, Operation,
            Headless(sources), CancellationToken.None));
        Assert.DoesNotContain(LinuxRecoveryMessage.Confirm, refusal.Sent); Assert.True(refusal.Disposed);
        var observed = new Peer((connection, kind, _) =>
        {
            if (kind == LinuxRecoveryMessage.OfferImages) connection.Push(LinuxRecoveryMessage.Staging);
            if (kind == LinuxRecoveryMessage.InventoryRequest) connection.Push(LinuxRecoveryMessage.Inventory, Responses.EncodeInventory(Inventory));
        });
        observed.Push(LinuxRecoveryMessage.Ready);
        await Assert.ThrowsAsync<IOException>(() => LinuxRecoveryControllerClient.RunAsync(observed, Operation,
            Headless(sources), CancellationToken.None,
            state => { if (state.Inventory is not null) throw new IOException("fixture-observer-failed"); }));
        Assert.DoesNotContain(LinuxRecoveryMessage.Prepare, observed.Sent); Assert.True(observed.Disposed);
    }
}
