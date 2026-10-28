#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryWorkerOperationTests
{
    internal sealed class Transport : LinuxRecoveryWorkerOperation.ITransport
    {
        private readonly Channel<LinuxRecoveryOperationChannel.Frame> incoming = Channel.CreateUnbounded<LinuxRecoveryOperationChannel.Frame>();
        private readonly Channel<LinuxRecoveryOperationChannel.Frame> outgoing = Channel.CreateUnbounded<LinuxRecoveryOperationChannel.Frame>();
        internal readonly ConcurrentQueue<LinuxRecoveryMessage> Sent = new();
        internal bool ThrowDispose;
        internal int Receives;
        private int closed;
        public async Task<LinuxRecoveryOperationChannel.Frame> ReceiveAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Receives);
            try { return await incoming.Reader.ReadAsync(token); }
            catch (ChannelClosedException error) { throw error.InnerException ?? new IOException("fixture-peer-closed"); }
        }
        public Task SendAsync(LinuxRecoveryMessage kind, byte[] body, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Sent.Enqueue(kind);
            if (!outgoing.Writer.TryWrite(new(kind, (byte[])body.Clone(), null))) throw new IOException("fixture-peer-closed");
            return Task.CompletedTask;
        }
        internal void Controller(LinuxRecoveryMessage kind, byte[]? body = null, LinuxRecoveryImageTransfer.Sources? sources = null) =>
            Assert.True(incoming.Writer.TryWrite(new(kind, body ?? Array.Empty<byte>(), sources)));
        internal void LosePeer(Exception? error = null) => incoming.Writer.TryComplete(error);
        internal async Task<LinuxRecoveryOperationChannel.Frame> Next(LinuxRecoveryMessage expected)
        {
            var frame = await NextAny();
            Assert.Equal(expected, frame.Kind); return frame;
        }
        internal Task<LinuxRecoveryOperationChannel.Frame> NextAny() =>
            outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        internal async Task Barrier()
        {
            for (int i = 0; i < 2; i++)
            {
                int before = Volatile.Read(ref Receives);
                Controller(LinuxRecoveryMessage.Heartbeat);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (Volatile.Read(ref Receives) == before) await Task.Delay(1, deadline.Token);
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            incoming.Writer.TryComplete(); outgoing.Writer.TryComplete();
            while (incoming.Reader.TryRead(out var frame)) frame.Dispose();
            if (ThrowDispose) throw new IOException("fixture-transport-dispose");
        }
        internal void CleanupFrames() { while (outgoing.Reader.TryRead(out var frame)) frame.Dispose(); }
    }

    internal sealed class Platform : LinuxRecoveryWorkerOperation.IPlatform
    {
        internal Func<Stream, Stream, CancellationToken, ValueTask<LinuxRawImageStagingAssessment>> Stage =
            (_, _, _) => ValueTask.FromResult(new LinuxRawImageStagingAssessment(LinuxRawImageStagingState.Invalid, "fixture-invalid"));
        internal Func<CancellationToken, ValueTask<LinuxBlockInventory>> Observe = _ => throw new InvalidOperationException("fixture-unexpected-observe");
        internal Func<Guid, LinuxRawImageStagingSession, LinuxBlockInventory, string, CancellationToken, ValueTask<LinuxRawRestorePreparation>> Prepare =
            (_, _, _, _, _) => throw new InvalidOperationException("fixture-unexpected-prepare");
        public ValueTask<LinuxRawImageStagingAssessment> StageAsync(Stream metadata, Stream payload, CancellationToken token) => Stage(metadata, payload, token);
        public ValueTask<LinuxBlockInventory> ObserveAsync(CancellationToken token) => Observe(token);
        public ValueTask<LinuxRawRestorePreparation> PrepareAsync(Guid operation, LinuxRawImageStagingSession stage,
            LinuxBlockInventory inventory, string selection, CancellationToken token) => Prepare(operation, stage, inventory, selection, token);
    }

    internal sealed class Sources : IDisposable
    {
        private readonly string metadataPath = Path.GetTempFileName(), payloadPath = Path.GetTempFileName();
        internal LinuxRecoveryImageTransfer.Sources Lease { get; }
        internal Sources() => Lease = new(File.OpenRead(metadataPath), File.OpenRead(payloadPath));
        public void Dispose() { Lease.Dispose(); File.Delete(metadataPath); File.Delete(payloadPath); }
    }

    private sealed class Storage : LinuxRawImageStagingService.IStorage
    {
        private MemoryStream metadata = new(), payload = new();
        internal int Disposals;
        internal bool ThrowDispose;
        public Stream Metadata => metadata;
        public Stream Payload => payload;
        public bool Published { get; private set; }
        public void Publish() => Published = true;
        public void FlushPayload() { }
        public void FlushMetadata() { }
        public void FlushDirectories() { }
        public void Seal()
        {
            byte[] raw = payload.ToArray(), meta = metadata.ToArray();
            payload.Dispose(); metadata.Dispose(); payload = new(raw, false); metadata = new(meta, false);
        }
        public ValueTask DisposeAsync()
        {
            Disposals++; payload.Dispose(); metadata.Dispose();
            if (ThrowDispose) throw new IOException("fixture-stage-dispose");
            return ValueTask.CompletedTask;
        }
    }
    private static async Task<LinuxRawImageStagingAssessment> Stage(Storage storage)
    {
        using var metadata = new MemoryStream(Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid(512).MetadataHex));
        using var payload = new MemoryStream(DiskImageRawProfileVerificationServiceTests.Raw(512));
        return await LinuxRawImageStagingService.StageAsync(metadata, payload, _ => storage, DiskImageInspectionFixture.InspectAsync);
    }

    [Fact]
    public async Task CancelAndHeartbeatsRemainReachableDuringActualStagingTask()
    {
        var transport = new Transport(); using var sources = new Sources();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new Platform
        {
            Stage = async (_, _, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { }
                return new(LinuxRawImageStagingState.Interrupted, "fixture-interrupted");
            }
        };
        await using var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, platform);
        var run = worker.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.Controller(LinuxRecoveryMessage.OfferImages, sources: sources.Lease);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (await transport.Next(LinuxRecoveryMessage.Staging)) { }
        transport.Controller(LinuxRecoveryMessage.Heartbeat);
        transport.Controller(LinuxRecoveryMessage.Cancel);
        using (await transport.Next(LinuxRecoveryMessage.Refused)) { }
        var state = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(state.CancellationRequested);
        Assert.Equal(LinuxRecoverySessionPhase.Refused, state.Phase);
        Assert.False(sources.Lease.Metadata.CanRead);
        Assert.False(sources.Lease.Payload.CanRead);
        Assert.True(worker.ReviewReady.IsCompleted);
        Assert.DoesNotContain(LinuxRecoveryMessage.Executing, transport.Sent);
        transport.CleanupFrames();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PeerLossAndThrowingTransportDisposalStillReclaimCompletedStage(bool throwDispose)
    {
        var storage = new Storage(); var staged = await Stage(storage);
        var transport = new Transport { ThrowDispose = throwDispose }; using var sources = new Sources();
        var platform = new Platform { Stage = (_, _, _) => ValueTask.FromResult(staged) };
        await using var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, platform);
        var run = worker.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.Controller(LinuxRecoveryMessage.OfferImages, sources: sources.Lease);
        using (await transport.Next(LinuxRecoveryMessage.Staging)) { }
        transport.LosePeer(new SocketException((int)SocketError.ConnectionReset));
        var state = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LinuxRecoverySessionPhase.Faulted, state.Phase);
        Assert.False(staged.Session!.IsAlive);
        Assert.Equal(1, storage.Disposals);
        Assert.True(worker.ReviewReady.IsCompleted);
        Assert.DoesNotContain(LinuxRecoveryMessage.Outcome, transport.Sent);
        Assert.DoesNotContain(LinuxRecoveryMessage.Refused, transport.Sent);
        transport.CleanupFrames();
    }

    [Fact]
    public async Task PreExecutionCleanupFailureCannotSendCleanRefusal()
    {
        var storage = new Storage { ThrowDispose = true }; var staged = await Stage(storage);
        var transport = new Transport(); using var sources = new Sources();
        var platform = new Platform { Stage = (_, _, _) => ValueTask.FromResult(staged) };
        await using var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, platform);
        var run = worker.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.Controller(LinuxRecoveryMessage.OfferImages, sources: sources.Lease);
        using (await transport.Next(LinuxRecoveryMessage.Staging)) { }
        transport.Controller(LinuxRecoveryMessage.Cancel);
        Assert.Equal(LinuxRecoverySessionPhase.Faulted, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Phase);
        Assert.DoesNotContain(LinuxRecoveryMessage.Refused, transport.Sent);
        Assert.DoesNotContain(LinuxRecoveryMessage.Outcome, transport.Sent);
        Assert.True(worker.ReviewReady.IsCompleted);
        transport.CleanupFrames();
    }

    [Theory]
    [InlineData(LinuxRawImageStagingState.Failed)] [InlineData(LinuxRawImageStagingState.PublicationUnconfirmed)]
    public async Task UnconfirmedStagingFailureCannotBecomeCleanRefusal(LinuxRawImageStagingState stagingState)
    {
        var transport = new Transport(); using var sources = new Sources();
        var platform = new Platform { Stage = (_, _, _) => ValueTask.FromResult(new LinuxRawImageStagingAssessment(stagingState, "fixture-unconfirmed")) };
        await using var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, platform);
        var run = worker.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.Controller(LinuxRecoveryMessage.OfferImages, sources: sources.Lease);
        using (await transport.Next(LinuxRecoveryMessage.Staging)) { }
        Assert.Equal(LinuxRecoverySessionPhase.Faulted, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Phase);
        Assert.DoesNotContain(LinuxRecoveryMessage.Refused, transport.Sent);
        Assert.False(sources.Lease.Metadata.CanRead);
        transport.CleanupFrames();
    }

    [Fact]
    public async Task MissingDescriptorsAndPrematureSupervisorExecutionFailClosed()
    {
        var transport = new Transport();
        await using var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, new Platform());
        Assert.Throws<InvalidOperationException>(() => { _ = worker.ExecuteReviewedAsync(Guid.NewGuid(), Guid.NewGuid()); });
        var run = worker.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.Controller(LinuxRecoveryMessage.OfferImages);
        Assert.Equal(LinuxRecoverySessionPhase.Faulted, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Phase);
        Assert.DoesNotContain(LinuxRecoveryMessage.Staging, transport.Sent);
        Assert.Throws<InvalidOperationException>(() => { _ = worker.RunAsync(); });
        transport.CleanupFrames();
    }

    [Fact]
    public async Task TotalRunDeadlineCancelsStagingEvenWhileControllerRemainsConnected()
    {
        var transport = new Transport(); using var sources = new Sources();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new Platform
        {
            Stage = async (_, _, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { stopped.TrySetResult(); }
                return new(LinuxRawImageStagingState.Interrupted, "fixture-deadline");
            }
        };
        await using var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, platform,
            runLimit: TimeSpan.FromMilliseconds(200));
        var run = worker.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.Controller(LinuxRecoveryMessage.OfferImages, sources: sources.Lease);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (await transport.Next(LinuxRecoveryMessage.Staging)) { }
        transport.Controller(LinuxRecoveryMessage.Heartbeat);
        Assert.Equal(LinuxRecoverySessionPhase.Faulted, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Phase);
        Assert.True(stopped.Task.IsCompleted);
        Assert.False(sources.Lease.Metadata.CanRead);
        Assert.DoesNotContain(LinuxRecoveryMessage.Outcome, transport.Sent);
        transport.CleanupFrames();
    }

    [Fact]
    public async Task SynchronousNativeStageEntryCannotBlockHeartbeatOrCancellationDispatch()
    {
        var transport = new Transport(); using var sources = new Sources();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var canceled = new ManualResetEventSlim();
        var platform = new Platform
        {
            Stage = (_, _, token) =>
            {
                // Intentionally synchronous before returning any ValueTask.
                using var registration = token.Register(() => canceled.Set());
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new IOException("fixture-entry-timeout");
                return ValueTask.FromResult(new LinuxRawImageStagingAssessment(LinuxRawImageStagingState.Interrupted, "fixture-synchronous"));
            }
        };
        await using var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, platform);
        var run = worker.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.Controller(LinuxRecoveryMessage.OfferImages, sources: sources.Lease);
        try
        {
            using (await transport.Next(LinuxRecoveryMessage.Staging)) { }
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await transport.Barrier();
            transport.Controller(LinuxRecoveryMessage.Cancel);
            Assert.True(canceled.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(run.IsCompleted);
        }
        finally { release.Set(); }
        using (await transport.Next(LinuxRecoveryMessage.Refused)) { }
        Assert.Equal(LinuxRecoverySessionPhase.Refused, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Phase);
        transport.CleanupFrames();
    }
}
