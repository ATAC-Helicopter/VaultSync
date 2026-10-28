using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>
/// Internal trusted-worker owner, not an elevation endpoint. The supervisor must
/// establish installed build/role trust and controlled offline execution before
/// entry, and actual human/device authorization before calling ExecuteReviewedAsync.
/// A wire Confirm never initiates writing. Pair the channel with the same operation
/// ID; Frame does not expose the channel's authenticated header identity.
/// </summary>
internal sealed class LinuxRecoveryWorkerOperation : IAsyncDisposable
{
    // These fixture seams are never selected by a wire message or image.
    internal interface ITransport : IDisposable
    {
        Task<LinuxRecoveryOperationChannel.Frame> ReceiveAsync(CancellationToken token);
        Task SendAsync(LinuxRecoveryMessage kind, byte[] body, CancellationToken token);
    }
    internal interface IPlatform
    {
        ValueTask<LinuxRawImageStagingAssessment> StageAsync(Stream metadata, Stream payload, CancellationToken token);
        ValueTask<LinuxBlockInventory> ObserveAsync(CancellationToken token);
        ValueTask<LinuxRawRestorePreparation> PrepareAsync(Guid operation, LinuxRawImageStagingSession stage,
            LinuxBlockInventory inventory, string selection, CancellationToken token);
    }
    private sealed class Transport(LinuxRecoveryOperationChannel channel) : ITransport
    {
        public Task<LinuxRecoveryOperationChannel.Frame> ReceiveAsync(CancellationToken token) => channel.ReceiveAsync(token);
        public Task SendAsync(LinuxRecoveryMessage kind, byte[] body, CancellationToken token) => channel.SendAsync(kind, body, token);
        public void Dispose() => channel.Dispose();
    }
    private sealed class Platform(string stagingParent, string journalParent) : IPlatform
    {
        public ValueTask<LinuxRawImageStagingAssessment> StageAsync(Stream metadata, Stream payload, CancellationToken token) =>
            LinuxRawImageStagingService.StageAsync(metadata, payload, stagingParent, token);
        public ValueTask<LinuxBlockInventory> ObserveAsync(CancellationToken token) => LinuxBlockInventoryService.ObserveAsync(token);
        public ValueTask<LinuxRawRestorePreparation> PrepareAsync(Guid operation, LinuxRawImageStagingSession stage,
            LinuxBlockInventory inventory, string selection, CancellationToken token) =>
            LinuxRawRestoreOperation.PrepareAsync(operation, stage, inventory, selection, journalParent, token);
    }

    private readonly object startup = new(), progressGate = new();
    private readonly Guid operation;
    private readonly ITransport transport;
    private readonly IPlatform platform;
    private readonly LinuxRecoverySession session;
    private readonly TimeSpan runLimit, reviewLimit;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource<LinuxDiskRestoreOverwriteReview> reviewReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource executionRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<LinuxRawRestoreExecution> executionCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ulong latestProgress;
    private bool progressPending;
    private Task<LinuxRecoverySession.Snapshot>? run;
    private int executionClaimed;
    private bool disposed;

    // Adopts the channel on successful construction. Paths come only from the
    // trusted supervisor, never from controller input. Native storage validates them.
    internal LinuxRecoveryWorkerOperation(LinuxRecoveryOperationChannel channel, Guid operation,
        string stagingParent, string journalParent) : this(operation,
            new Transport(channel ?? throw new ArgumentNullException(nameof(channel))), new Platform(stagingParent, journalParent)) { }

    internal LinuxRecoveryWorkerOperation(Guid operation, ITransport transport, IPlatform platform,
        TimeSpan? runLimit = null, TimeSpan? reviewLimit = null)
    {
        if (operation == Guid.Empty) throw new ArgumentException("recovery-worker.invalid-operation", nameof(operation));
        this.runLimit = runLimit ?? LinuxRawRestoreOperation.MaximumExecutionTime;
        this.reviewLimit = reviewLimit ?? TimeSpan.FromSeconds(30);
        if (this.runLimit <= TimeSpan.Zero || this.runLimit > LinuxRawRestoreOperation.MaximumExecutionTime ||
            this.reviewLimit <= TimeSpan.Zero || this.reviewLimit > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(runLimit), "recovery-worker.invalid-lifetime");
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        this.operation = operation;
        session = new(operation);
    }

    internal Task<LinuxDiskRestoreOverwriteReview> ReviewReady => reviewReady.Task;
    internal LinuxRecoverySession.Snapshot State => session.State;

    internal Task<LinuxRecoverySession.Snapshot> RunAsync(CancellationToken token = default)
    {
        lock (startup)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (run is not null) throw new InvalidOperationException("recovery-worker.already-started");
            return run = RunCoreAsync(token);
        }
    }

    /// <summary>
    /// Supervisor-only action AFTER actual human authorization of ReviewReady's
    /// exact native review. Also waits for the wire acknowledgment. IDs correlate;
    /// they grant no permission. Duplicate calls never cancel the active owner.
    /// </summary>
    internal Task<LinuxRawRestoreExecution> ExecuteReviewedAsync(Guid operationId, Guid reviewId)
    {
        if (!reviewReady.Task.IsCompletedSuccessfully || operationId != operation ||
            reviewReady.Task.Result.ReviewId != reviewId || session.State.CancellationRequested ||
            session.State.Phase is not (LinuxRecoverySessionPhase.Reviewed or LinuxRecoverySessionPhase.Confirmed))
            throw new InvalidOperationException("recovery-worker.review-unavailable");
        if (Interlocked.CompareExchange(ref executionClaimed, 1, 0) != 0)
            throw new InvalidOperationException("recovery-worker.already-consumed");
        executionRequested.TrySetResult();
        return executionCompleted.Task;
    }

    private async Task<LinuxRecoverySession.Snapshot> RunCoreAsync(CancellationToken token)
    {
        using var deadline = new CancellationTokenSource(runLimit);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, deadline.Token);
        using var work = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        // One latest-count slot is drained at most four times/second, independent
        // of image size/chunk count. Status has its own twenty-second cadence.
        using var statusTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        long lastStatus = System.Diagnostics.Stopwatch.GetTimestamp();
        Task<bool>? tick = null;
        Task<LinuxRecoveryOperationChannel.Frame>? receiving = null;
        LinuxRecoveryOperationChannel.Frame? offered = null;
        LinuxRawImageStagingSession? stage = null;
        LinuxRawRestoreOperation? owner = null;
        LinuxBlockInventory? inventory = null;
        Task<LinuxRawImageStagingAssessment>? staging = null;
        Task<LinuxBlockInventory>? observing = null;
        Task<LinuxRawRestorePreparation>? preparing = null;
        Task<LinuxRawRestoreExecution>? executing = null;
        bool executionQueued = false, finished = false;
        long reviewPublished = 0;
        TimeSpan remainingReview = TimeSpan.Zero;
        try
        {
            await SendAsync(LinuxRecoveryMessage.Ready, [], stop.Token).ConfigureAwait(false);
            receiving = transport.ReceiveAsync(stop.Token);
            tick = statusTimer.WaitForNextTickAsync(stop.Token).AsTask();
            while (!finished)
            {
                var pending = new List<Task> { receiving, tick };
                if (staging is not null) pending.Add(staging);
                if (observing is not null) pending.Add(observing);
                if (preparing is not null) pending.Add(preparing);
                if (executing is not null) pending.Add(executing);
                if (!executionQueued) pending.Add(executionRequested.Task);
                await Task.WhenAny(pending).ConfigureAwait(false);
                stop.Token.ThrowIfCancellationRequested();

                // One transcript owner commits every successful send before it can
                // apply a dependent reply, including replies already read by channel.
                if (receiving.IsCompleted)
                {
                    var frame = await receiving.ConfigureAwait(false);
                    receiving = null;
                    try
                    {
                        if (frame.Kind == LinuxRecoveryMessage.OfferImages && frame.Sources is null)
                            throw new InvalidDataException("recovery-worker.missing-sources");
                        session.Apply(LinuxRecoveryRole.Controller, frame);
                        switch (frame.Kind)
                        {
                            case LinuxRecoveryMessage.OfferImages:
                                offered = frame;
                                staging = Task.Run(async () => await platform.StageAsync(frame.Sources!.Metadata,
                                    frame.Sources.Payload, work.Token).ConfigureAwait(false), CancellationToken.None);
                                await SendAsync(LinuxRecoveryMessage.Staging, [], stop.Token).ConfigureAwait(false);
                                break;
                            case LinuxRecoveryMessage.Prepare:
                                var selection = LinuxRecoveryProtocol.DecodeSelection(frame.Body.Span);
                                var transferred = stage ?? throw new InvalidDataException("recovery-worker.stage-unavailable");
                                stage = null; // Prepare adopts on entry, including refusal and cancellation.
                                preparing = Task.Run(async () => await platform.PrepareAsync(operation, transferred,
                                    inventory!, selection.KernelName, work.Token).ConfigureAwait(false), CancellationToken.None);
                                break;
                            case LinuxRecoveryMessage.Cancel:
                                await work.CancelAsync().ConfigureAwait(false);
                                break;
                        }
                    }
                    finally { if (!ReferenceEquals(offered, frame)) frame.Dispose(); }
                    receiving = transport.ReceiveAsync(stop.Token);
                }

                if (staging?.IsCompleted == true)
                {
                    var completed = staging; staging = null;
                    var result = await completed.ConfigureAwait(false);
                    stage = result.Session;
                    offered?.Dispose(); offered = null;
                    if (result.State is LinuxRawImageStagingState.Failed or LinuxRawImageStagingState.PublicationUnconfirmed)
                        throw new IOException("recovery-worker.staging-unconfirmed");
                    if (result.State != LinuxRawImageStagingState.StagedVerified || stage is null)
                        finished = await RefuseAsync().ConfigureAwait(false);
                }
                if (!finished && observing?.IsCompleted == true)
                {
                    var completed = observing; observing = null;
                    try { inventory = await completed.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (session.State.CancellationRequested) { inventory = null; }
                    if (!session.State.CancellationRequested)
                        await SendAsync(LinuxRecoveryMessage.Inventory,
                            LinuxRecoveryResponses.EncodeInventory(LinuxRecoveryResponses.ProjectInventory(inventory!)), stop.Token).ConfigureAwait(false);
                }
                if (!finished && preparing?.IsCompleted == true)
                {
                    var completed = preparing; preparing = null;
                    var result = await completed.ConfigureAwait(false);
                    owner = result.Operation;
                    if (result.Reason != LinuxRawRestoreOperationReason.ReviewReady || owner is null)
                        finished = await RefuseAsync().ConfigureAwait(false);
                    else if (!session.State.CancellationRequested)
                    {
                        var wireInventory = LinuxRecoveryResponses.ProjectInventory(inventory!);
                        var review = LinuxRecoveryResponses.ProjectReview(owner.Review, wireInventory);
                        await SendAsync(LinuxRecoveryMessage.Review, LinuxRecoveryResponses.EncodeReview(review), stop.Token).ConfigureAwait(false);
                        var age = DateTimeOffset.UtcNow - owner.Review.ObservedAtUtc;
                        if (age < TimeSpan.Zero) throw new InvalidDataException("recovery-worker.future-review");
                        remainingReview = TimeSpan.FromSeconds(30) - age;
                        if (remainingReview > reviewLimit) remainingReview = reviewLimit;
                        reviewPublished = System.Diagnostics.Stopwatch.GetTimestamp();
                        reviewReady.TrySetResult(owner.Review);
                    }
                }
                if (!finished && executing?.IsCompleted == true)
                {
                    var completed = executing; executing = null;
                    var result = await completed.ConfigureAwait(false);
                    await owner!.DisposeAsync().ConfigureAwait(false); owner = null;
                    await FlushProgressAsync(stop.Token).ConfigureAwait(false);
                    await SendAsync(LinuxRecoveryMessage.Outcome,
                        LinuxRecoveryResponses.EncodeOutcome(new(session.State.Review!.ReviewId, result),
                            session.State.Review, session.State.AcknowledgedBytes), stop.Token).ConfigureAwait(false);
                    executionCompleted.TrySetResult(result);
                    finished = true;
                }
                if (finished) continue;
                if (executionRequested.Task.IsCompleted) executionQueued = true;
                if (owner is not null && executing is null &&
                    System.Diagnostics.Stopwatch.GetElapsedTime(reviewPublished) >= remainingReview)
                { finished = await RefuseAsync().ConfigureAwait(false); continue; }
                if (session.State.CancellationRequested && executing is null && staging is null && observing is null && preparing is null)
                { finished = await RefuseAsync().ConfigureAwait(false); continue; }
                if (!session.State.CancellationRequested && stage is not null &&
                    session.State.Phase == LinuxRecoverySessionPhase.InventoryRequested && observing is null)
                    observing = Task.Run(async () => await platform.ObserveAsync(work.Token).ConfigureAwait(false), CancellationToken.None);
                if (executionQueued && !session.State.CancellationRequested && session.State.Phase == LinuxRecoverySessionPhase.Confirmed)
                {
                    await SendAsync(LinuxRecoveryMessage.Executing, [], stop.Token).ConfigureAwait(false);
                    var executingOwner = owner!;
                    executing = Task.Run(async () => await executingOwner.ExecuteReviewedAsync(operation,
                        executingOwner.Review.ReviewId, work.Token, ReportProgressAsync).ConfigureAwait(false), CancellationToken.None);
                }
                if (tick.IsCompleted)
                {
                    _ = await tick.ConfigureAwait(false);
                    if (executing is not null) await FlushProgressAsync(stop.Token).ConfigureAwait(false);
                    if (System.Diagnostics.Stopwatch.GetElapsedTime(lastStatus) >= TimeSpan.FromSeconds(20))
                    {
                        // Live owner status only; no claim of native I/O advancement.
                        if (executing is not null)
                            await SendAsync(LinuxRecoveryMessage.Executing, [], stop.Token).ConfigureAwait(false);
                        else if (staging is not null || preparing is not null)
                            await SendAsync(LinuxRecoveryMessage.Staging, [], stop.Token).ConfigureAwait(false);
                        lastStatus = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                    tick = statusTimer.WaitForNextTickAsync(stop.Token).AsTask();
                }
            }
        }
        catch (Exception error) when (ExpectedFailure(error))
        { session.Fail(); }
        catch { session.Fail(); throw; }
        finally
        {
            Exception? unexpected = null;
            try
            {
                try { await stop.CancelAsync().ConfigureAwait(false); }
                catch (Exception error) { session.Fail(); if (!ExpectedFailure(error)) unexpected = error; }
                try { transport.Dispose(); }
                catch (Exception error) { session.Fail(); if (!ExpectedFailure(error)) unexpected ??= error; }
                if (receiving is not null)
                {
                    try { (await receiving.ConfigureAwait(false)).Dispose(); }
                    catch (Exception error) { if (!ExpectedFailure(error)) { session.Fail(); unexpected ??= error; } }
                }
                if (tick is not null)
                {
                    try { _ = await tick.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
            }
            finally
            {
                try { await CleanupAsync().ConfigureAwait(false); }
                finally { reviewReady.TrySetCanceled(); executionCompleted.TrySetCanceled(); }
            }
            if (unexpected is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unexpected).Throw();
        }
        return session.State;

        async Task<bool> RefuseAsync()
        {
            await CleanupAsync().ConfigureAwait(false);
            if (session.State.Phase == LinuxRecoverySessionPhase.Faulted) throw new IOException("recovery-worker.cleanup-unconfirmed");
            await SendAsync(LinuxRecoveryMessage.Refused,
                LinuxRecoveryResponses.EncodeRefused(LinuxRawRestoreOperationReason.ReviewRefused), stop.Token).ConfigureAwait(false);
            return true;
        }

        async Task CleanupAsync()
        {
            Exception? unexpected = null;
            void Failed(Exception error) { session.Fail(); if (!ExpectedFailure(error)) unexpected ??= error; }
            try { await work.CancelAsync().ConfigureAwait(false); }
            catch (Exception error) { Failed(error); }
            // These waits intentionally ignore cancellation: ownership cannot be
            // abandoned while a native task may still hold staging or a target.
            try { if (executing is not null) { await executing.ConfigureAwait(false); executing = null; } }
            catch (Exception error) { Failed(error); }
            try { if (preparing is not null) { owner = (await preparing.ConfigureAwait(false)).Operation; preparing = null; } }
            catch (Exception error) { Failed(error); }
            try { if (staging is not null) { stage = (await staging.ConfigureAwait(false)).Session; staging = null; } }
            catch (Exception error) { Failed(error); }
            try { if (observing is not null) { await observing.ConfigureAwait(false); observing = null; } }
            catch (Exception error) { Failed(error); }
            try { if (owner is not null) { await owner.DisposeAsync().ConfigureAwait(false); owner = null; } }
            catch (Exception error) { Failed(error); }
            try { if (stage is not null) { await stage.DisposeAsync().ConfigureAwait(false); stage = null; } }
            catch (Exception error) { Failed(error); }
            try { offered?.Dispose(); offered = null; }
            catch (Exception error) { Failed(error); }
            if (unexpected is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(unexpected).Throw();
        }
    }

    private ValueTask ReportProgressAsync(ulong count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (progressGate)
        {
            latestProgress = count; progressPending = true;
        }
        return ValueTask.CompletedTask;
    }

    private async Task FlushProgressAsync(CancellationToken token)
    {
        ulong count;
        lock (progressGate)
        {
            if (!progressPending) return;
            count = latestProgress; progressPending = false;
        }
        var state = session.State;
        await SendAsync(LinuxRecoveryMessage.Progress,
            LinuxRecoveryResponses.EncodeProgress(new(state.Review!.ReviewId, count), state.Review, state.AcknowledgedBytes), token).ConfigureAwait(false);
    }

    private async Task SendAsync(LinuxRecoveryMessage kind, byte[] body, CancellationToken token)
    {
        await transport.SendAsync(kind, body, token).ConfigureAwait(false);
        using var frame = new LinuxRecoveryOperationChannel.Frame(kind, body, null);
        session.Apply(LinuxRecoveryRole.Worker, frame);
    }

    private static bool ExpectedFailure(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException;

    public async ValueTask DisposeAsync()
    {
        Task<LinuxRecoverySession.Snapshot>? running;
        lock (startup)
        {
            if (disposed) { running = run; }
            else { disposed = true; running = run; }
        }
        try { await lifetime.CancelAsync().ConfigureAwait(false); }
        finally
        {
            try { transport.Dispose(); }
            finally
            {
                if (running is not null) await running.ConfigureAwait(false);
                else { session.Fail(); reviewReady.TrySetCanceled(); executionCompleted.TrySetCanceled(); }
            }
        }
        // Retain the tiny CTS for concurrent DisposeAsync callers, like owner gates.
    }
}
