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
        internal int Disposals;
        internal Action? OnDispose;
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
        internal Task<LinuxRecoveryOperationChannel.Frame> NextAny(TimeSpan? timeout = null) =>
            outgoing.Reader.ReadAsync().AsTask().WaitAsync(timeout ?? TimeSpan.FromSeconds(10));
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
            Interlocked.Increment(ref Disposals);
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            incoming.Writer.TryComplete(); outgoing.Writer.TryComplete();
            while (incoming.Reader.TryRead(out var frame)) frame.Dispose();
            OnDispose?.Invoke();
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

    [Theory]
    [InlineData(0, false)] [InlineData(-1, false)] [InlineData(86400001, false)]
    [InlineData(0, true)] [InlineData(-1, true)] [InlineData(30001, true)]
    public void InvalidLifetimeBoundsCannotCreateAnOwner(double milliseconds, bool review)
    {
        using var transport = new Transport();
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, new Platform(),
            runLimit: review ? TimeSpan.FromHours(24) : TimeSpan.FromMilliseconds(milliseconds),
            reviewLimit: review ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ControllerCancellationDuringInventoryObservationDrainsStagingBeforeRefusal()
    {
        var storage = new Storage(); var staged = await Stage(storage);
        var transport = new Transport(); using var sources = new Sources();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new Platform
        {
            Stage = (_, _, _) => ValueTask.FromResult(staged),
            Observe = async token =>
            {
                entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("fixture-unreachable");
            }
        };
        await using var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, platform);
        var run = worker.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.Controller(LinuxRecoveryMessage.OfferImages, sources: sources.Lease);
        using (await transport.Next(LinuxRecoveryMessage.Staging)) { }
        transport.Controller(LinuxRecoveryMessage.InventoryRequest);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        transport.Controller(LinuxRecoveryMessage.Cancel);
        using (await transport.Next(LinuxRecoveryMessage.Refused)) { }
        Assert.Equal(1, storage.Disposals); Assert.False(staged.Session!.IsAlive);
        Assert.Equal(LinuxRecoverySessionPhase.Refused, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Phase);
        Assert.DoesNotContain(LinuxRecoveryMessage.Inventory, transport.Sent);
        Assert.DoesNotContain(LinuxRecoveryMessage.Executing, transport.Sent);
        transport.CleanupFrames();
    }

    [Fact]
    public async Task PreparationRefusalDisposesTransferredStageWithoutReviewOrExecution()
    {
        var storage = new Storage(); var staged = await Stage(storage);
        var target = new LinuxBlockObservation("sda", "8:0", null, LinuxBlockKind.WholeDisk, 65536, 512, 512, 1,
            false, "serial:fixture", LinuxBlockIdentity.UniqueInSnapshot, false, false, false, LinuxBlockUse.Unknown);
        var snapshot = new LinuxBlockInventory(LinuxBlockInventoryState.Observed, LinuxBlockInventoryReason.NamespaceObservationOnly,
            Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "mnt:[1]", false, new[] { target });
        var transport = new Transport(); using var sources = new Sources();
        var platform = new Platform
        {
            Stage = (_, _, _) => ValueTask.FromResult(staged), Observe = _ => ValueTask.FromResult(snapshot),
            Prepare = async (_, owned, _, _, _) =>
            { await owned.DisposeAsync(); return new LinuxRawRestorePreparation(LinuxRawRestoreOperationReason.ReviewRefused); }
        };
        await using var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, platform);
        var run = worker.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.Controller(LinuxRecoveryMessage.OfferImages, sources: sources.Lease);
        using (await transport.Next(LinuxRecoveryMessage.Staging)) { }
        transport.Controller(LinuxRecoveryMessage.InventoryRequest);
        using (await transport.Next(LinuxRecoveryMessage.Inventory)) { }
        transport.Controller(LinuxRecoveryMessage.Prepare, LinuxRecoveryProtocol.Selection(snapshot.ObservationId, "sda"));
        using (await transport.Next(LinuxRecoveryMessage.Refused)) { }
        Assert.Equal(LinuxRecoverySessionPhase.Refused, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Phase);
        Assert.Equal(1, storage.Disposals); Assert.False(staged.Session!.IsAlive);
        Assert.True(worker.ReviewReady.IsCanceled);
        Assert.DoesNotContain(LinuxRecoveryMessage.Review, transport.Sent);
        Assert.DoesNotContain(LinuxRecoveryMessage.Executing, transport.Sent);
        transport.CleanupFrames();
    }

    [Fact]
    public async Task StalledStageReportsRealLivenessAtTwentySecondsWithoutInventingProgressAndStillCancels()
    {
        var transport = new Transport(); using var sources = new Sources();
        var platform = new Platform
        {
            Stage = async (_, _, token) =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } catch (OperationCanceledException) { }
                return new(LinuxRawImageStagingState.Interrupted, "fixture-staging-cancelled");
            }
        };
        await using var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, platform);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var run = worker.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.Controller(LinuxRecoveryMessage.OfferImages, sources: sources.Lease);
        using (await transport.Next(LinuxRecoveryMessage.Staging)) { }
        transport.Controller(LinuxRecoveryMessage.Heartbeat);
        using (var live = await transport.NextAny(TimeSpan.FromSeconds(25))) Assert.Equal(LinuxRecoveryMessage.Staging, live.Kind);
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(20));
        Assert.Equal(LinuxRecoverySessionPhase.Staging, worker.State.Phase);
        Assert.Equal(0UL, worker.State.AcknowledgedBytes);
        Assert.DoesNotContain(LinuxRecoveryMessage.Progress, transport.Sent);
        Assert.DoesNotContain(LinuxRecoveryMessage.Executing, transport.Sent);
        Assert.False(run.IsCompleted);
        transport.Controller(LinuxRecoveryMessage.Cancel);
        using (await transport.Next(LinuxRecoveryMessage.Refused)) { }
        Assert.Equal(LinuxRecoverySessionPhase.Refused, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Phase);
        Assert.False(sources.Lease.Metadata.CanRead); Assert.True(worker.ReviewReady.IsCanceled);
        transport.CleanupFrames();
    }

    [Fact]
    public async Task DisposalBeforeRunIsSharedAndRejectsLaterStartup()
    {
        var transport = new Transport();
        var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, new Platform());
        Task first = worker.DisposeAsync().AsTask(), second = worker.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await first;
        Assert.Same(first, worker.DisposeAsync().AsTask());
        Assert.Equal(1, transport.Disposals);
        Assert.Equal(LinuxRecoverySessionPhase.Faulted, worker.State.Phase);
        Assert.True(worker.ReviewReady.IsCanceled);
        Assert.Throws<ObjectDisposedException>(() => { _ = worker.RunAsync(); });
        transport.CleanupFrames();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConcurrentDisposalAwaitsLateStageCleanupAndSharesTransportFailure(bool throwDispose)
    {
        var storage = new Storage(); var staged = await Stage(storage);
        var transport = new Transport { ThrowDispose = throwDispose }; using var sources = new Sources();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int cancellations = 0;
        var platform = new Platform
        {
            Stage = async (_, _, token) =>
            {
                using var registration = token.Register(() => { Interlocked.Increment(ref cancellations); canceled.TrySetResult(); });
                entered.TrySetResult();
                await release.Task;
                return staged;
            }
        };
        var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, platform);
        var run = worker.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.Controller(LinuxRecoveryMessage.OfferImages, sources: sources.Lease);
        using (await transport.Next(LinuxRecoveryMessage.Staging)) { }
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task first = worker.DisposeAsync().AsTask(), second = worker.DisposeAsync().AsTask();
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
            Assert.False(run.IsCompleted);
            Assert.True(staged.Session!.IsAlive);
        }
        finally { release.TrySetResult(); }
        if (throwDispose)
        {
            await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(TimeSpan.FromSeconds(10)));
            await Assert.ThrowsAsync<IOException>(() => second);
        }
        else await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LinuxRecoverySessionPhase.Faulted, (await run).Phase);
        Assert.Same(first, worker.DisposeAsync().AsTask());
        Assert.Equal(1, cancellations);
        Assert.Equal(1, storage.Disposals);
        Assert.False(staged.Session!.IsAlive);
        Assert.False(sources.Lease.Metadata.CanRead);
        Assert.True(worker.ReviewReady.IsCanceled);
        Assert.Equal(1, transport.Disposals);
        transport.CleanupFrames();
    }

    [Fact]
    public async Task FaultedRunHasOneRepeatableDisposalFailureAfterClosingSources()
    {
        var transport = new Transport(); using var sources = new Sources();
        var platform = new Platform
        {
            Stage = (_, _, _) => throw new InvalidOperationException("fixture-programming-fault")
        };
        var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, platform);
        var run = worker.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.Controller(LinuxRecoveryMessage.OfferImages, sources: sources.Lease);
        using (await transport.Next(LinuxRecoveryMessage.Staging)) { }
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Task first = worker.DisposeAsync().AsTask(), second = worker.DisposeAsync().AsTask();
        Assert.Same(first, second);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.False(sources.Lease.Metadata.CanRead);
        Assert.True(worker.ReviewReady.IsCanceled);
        Assert.Equal(1, transport.Disposals);
        Assert.Equal(LinuxRecoverySessionPhase.Faulted, worker.State.Phase);
        transport.CleanupFrames();
    }

    [Fact]
    public async Task RunInitiatedCloseRetainsFailureAcrossReentrantDisposalWithoutClosingTwice()
    {
        var transport = new Transport { ThrowDispose = true };
        var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, new Platform());
        Task? reentered = null;
        transport.OnDispose = () => { reentered = worker.DisposeAsync().AsTask(); };
        var run = worker.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.LosePeer(new SocketException((int)SocketError.ConnectionReset));
        Assert.Equal(LinuxRecoverySessionPhase.Faulted, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Phase);
        Assert.NotNull(reentered);
        await Assert.ThrowsAsync<IOException>(() => reentered!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(reentered, worker.DisposeAsync().AsTask());
        Assert.Equal(1, transport.Disposals);
        Assert.True(worker.ReviewReady.IsCanceled);
        transport.CleanupFrames();
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
        var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, platform);
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
        if (throwDispose) await Assert.ThrowsAsync<IOException>(() => worker.DisposeAsync().AsTask());
        else await worker.DisposeAsync();
        Assert.Equal(1, transport.Disposals);
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
