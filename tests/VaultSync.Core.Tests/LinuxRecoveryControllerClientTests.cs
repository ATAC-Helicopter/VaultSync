#nullable enable
using System;
using System.Collections.Generic;
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
        internal readonly List<LinuxRecoveryMessage> Sent = new();
        internal bool Disposed;
        internal void Push(LinuxRecoveryMessage kind, byte[]? body = null) =>
            Assert.True(input.Writer.TryWrite(new(kind, body ?? Array.Empty<byte>(), null)));
        public Task OfferAsync(FileStream metadata, FileStream payload, CancellationToken token)
        { Assert.True(metadata.CanRead); Assert.False(metadata.CanWrite); Assert.True(payload.CanRead); return SendAsync(LinuxRecoveryMessage.OfferImages, Array.Empty<byte>(), token); }
        public Task SendAsync(LinuxRecoveryMessage kind, byte[] body, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Sent.Add(kind); sent(this, kind, body); return Task.CompletedTask; }
        public async Task<LinuxRecoveryOperationChannel.Frame> ReceiveAsync(CancellationToken token)
        {
            try { return await input.Reader.ReadAsync(token); }
            catch (ChannelClosedException) { throw new IOException("fixture-peer-closed"); }
        }
        public void Dispose()
        { Disposed = true; input.Writer.TryComplete(); while (input.Reader.TryRead(out var frame)) frame.Dispose(); }
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
        var result = await LinuxRecoveryControllerClient.RunAsync(peer, Operation, sources.Metadata, sources.Payload,
            "sda", CancellationToken.None, observations.Add);
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
            sources.Metadata, sources.Payload, "sda", CancellationToken.None));
        Assert.True(peer.Disposed);
        Assert.DoesNotContain(LinuxRecoveryMessage.Confirm, peer.Sent);
    }

    [Fact]
    public async Task CancellationDrainsPendingReceiveAndKeepsCallerSourcesOwned()
    {
        using var sources = new Sources(); using var cancellation = new CancellationTokenSource();
        var peer = new Peer((_, _, _) => { });
        var run = LinuxRecoveryControllerClient.RunAsync(peer, Operation, sources.Metadata, sources.Payload, "sda", cancellation.Token);
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
}
