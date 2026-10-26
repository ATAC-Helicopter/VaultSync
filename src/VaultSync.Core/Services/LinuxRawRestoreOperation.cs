using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

internal enum LinuxRawRestoreOperationReason
{
    ReviewReady, ReviewRefused, ImageChanged, SelectionChanged, JournalUnavailable, ExecutionRefused,
    TargetUnavailable, Interrupted, TimedOut, Failed, CleanupUnconfirmed, BytesVerified, AlreadyConsumed
}

internal sealed record LinuxRawRestorePreparation(LinuxRawRestoreOperationReason Reason,
    LinuxRawRestoreOperation? Operation = null);
internal sealed record LinuxRawRestoreExecution(RawImageDeviceRestoreState State,
    LinuxRawRestoreOperationReason Reason, RawImageDeviceRestoreResult? WriteResult,
    bool JournalAcquired, bool CleanupConfirmed);

/// <summary>
/// Single-owner restore lifecycle inside a trusted worker. The caller MUST already
/// establish controlled offline execution, authenticated operation ownership and
/// actual human consent. Review identifiers are correlation, never that authority.
/// No public device-writing API or wire/elevation endpoint is exposed here.
/// </summary>
internal sealed class LinuxRawRestoreOperation : IAsyncDisposable
{
    internal static readonly TimeSpan MaximumExecutionTime = TimeSpan.FromHours(24);

    // Local fault fixtures only; no image, UI or wire input chooses a provider.
    internal interface IPlatform
    {
        ValueTask<LinuxDiskRestoreTargetPreviewAssessment> PreviewAsync(LinuxRawImageStagingSession stage,
            LinuxBlockInventory expected, string kernelName, CancellationToken token);
        ValueTask<LinuxBlockInventory> ObserveAsync(CancellationToken token);
        ValueTask<RawImageRestoreJournal> CreateJournalAsync(string parent, RawImageDeviceRestoreRequest request,
            LinuxBlockInventory expected, CancellationToken token);
        ValueTask<LinuxRawRestoreOpenResult> OpenAsync(LinuxRawImageStagingSession stage,
            RawImageDeviceRestoreRequest request, RawImageRestoreJournal journal,
            LinuxBlockInventory expected, CancellationToken token);
    }

    private sealed class NativePlatform : IPlatform
    {
        public ValueTask<LinuxDiskRestoreTargetPreviewAssessment> PreviewAsync(LinuxRawImageStagingSession stage,
            LinuxBlockInventory expected, string kernelName, CancellationToken token) =>
            LinuxDiskRestoreTargetPreviewService.CreateAsync(stage, expected, kernelName, token);
        public ValueTask<LinuxBlockInventory> ObserveAsync(CancellationToken token) => LinuxBlockInventoryService.ObserveAsync(token);
        public ValueTask<RawImageRestoreJournal> CreateJournalAsync(string parent, RawImageDeviceRestoreRequest request,
            LinuxBlockInventory expected, CancellationToken token) => RawImageRestoreJournal.CreateAsync(parent, request, expected, token);
        public ValueTask<LinuxRawRestoreOpenResult> OpenAsync(LinuxRawImageStagingSession stage,
            RawImageDeviceRestoreRequest request, RawImageRestoreJournal journal,
            LinuxBlockInventory expected, CancellationToken token) =>
            LinuxRawRestoreTargetOpenService.OpenAsync(stage, request, journal, expected, token);
    }

    private readonly LinuxRawImageStagingSession stage;
    private LinuxDiskRestoreTargetPreview? preview;
    private readonly LinuxBlockInventory initial;
    private readonly string journalParent;
    private readonly IPlatform platform;
    private readonly TimeSpan executionTime;
    private readonly CancellationTokenSource lifetime = new();
    // Retained for concurrent waiters, like the held device/stage gates.
    private readonly SemaphoreSlim gate = new(1, 1);
    private int consumed, closed;
    private bool cleanupConfirmed;
    internal LinuxDiskRestoreOverwriteReview Review { get; }

    private LinuxRawRestoreOperation(LinuxRawImageStagingSession stage, LinuxDiskRestoreTargetPreview preview,
        LinuxDiskRestoreOverwriteReview review, LinuxBlockInventory initial, string journalParent,
        IPlatform platform, TimeSpan executionTime)
    {
        this.stage = stage; this.preview = preview; Review = review; this.initial = initial;
        this.journalParent = journalParent; this.platform = platform; this.executionTime = executionTime;
    }

    // Ownership of stage transfers on entry, including refusal/cancellation.
    internal static ValueTask<LinuxRawRestorePreparation> PrepareAsync(LinuxRawImageStagingSession stage,
        LinuxBlockInventory expected, string kernelName, string journalParent, CancellationToken token = default) =>
        PrepareAsync(stage, expected, kernelName, journalParent, new NativePlatform(), MaximumExecutionTime, token);

    internal static async ValueTask<LinuxRawRestorePreparation> PrepareAsync(LinuxRawImageStagingSession stage,
        LinuxBlockInventory expected, string kernelName, string journalParent, IPlatform platform,
        TimeSpan executionTime, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        LinuxDiskRestoreTargetPreview? preview = null;
        bool transferred = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (executionTime <= TimeSpan.Zero || executionTime > MaximumExecutionTime ||
                !Path.IsPathFullyQualified(journalParent) || platform is null || expected is null)
                return new(LinuxRawRestoreOperationReason.ReviewRefused);
            var assessment = await platform.PreviewAsync(stage, expected, kernelName, token).ConfigureAwait(false);
            preview = assessment.Preview;
            if (assessment.Reason != LinuxDiskRestoreTargetPreviewReason.ObservationOnly || preview is null)
                return new(LinuxRawRestoreOperationReason.ReviewRefused);
            var review = await preview.CreateOverwriteReviewAsync(Guid.NewGuid(), token).ConfigureAwait(false);
            if (review is null) return new(LinuxRawRestoreOperationReason.ReviewRefused);
            var operation = new LinuxRawRestoreOperation(stage, preview, review, expected, journalParent, platform, executionTime);
            transferred = true;
            return new(LinuxRawRestoreOperationReason.ReviewReady, operation);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(LinuxRawRestoreOperationReason.Interrupted); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        { return new(LinuxRawRestoreOperationReason.Failed); }
        finally
        {
            if (!transferred)
            {
                try { if (preview is not null) await preview.DisposeAsync().ConfigureAwait(false); }
                finally { await stage.DisposeAsync().ConfigureAwait(false); }
            }
        }
    }

    internal async ValueTask<LinuxRawRestoreExecution> ExecuteReviewedAsync(Guid operationId, Guid reviewId,
        CancellationToken token = default)
    {
        // A duplicate attempt neither cancels nor disposes an executing operation.
        if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0)
            return new(RawImageDeviceRestoreState.Refused, LinuxRawRestoreOperationReason.AlreadyConsumed,
                null, false, Volatile.Read(ref closed) != 0 && cleanupConfirmed);
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        if (Volatile.Read(ref closed) != 0)
        {
            gate.Release();
            return new(RawImageDeviceRestoreState.Refused, LinuxRawRestoreOperationReason.AlreadyConsumed,
                null, false, cleanupConfirmed);
        }
        RawImageRestoreJournal? journal = null;
        RawImageDeviceRestoreService.ITarget? target = null;
        bool journalAcquired = false;
        LinuxRawRestoreExecution result;
        try
        {
            using var deadline = new CancellationTokenSource(executionTime);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token, deadline.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                if (operationId != Review.OperationId || reviewId != Review.ReviewId || preview is null ||
                    !await Review.AcknowledgeAsync(operationId, reviewId, linked.Token).ConfigureAwait(false))
                    result = Refused(LinuxRawRestoreOperationReason.ReviewRefused);
                else
                {
                    // Release the read-only O_EXCL claim before acquiring a write
                    // claim; the native opener independently rechecks this gap.
                    await preview.DisposeAsync().ConfigureAwait(false); preview = null;
                    if (!await stage.VerifyBytesAsync(linked.Token).ConfigureAwait(false))
                        result = Refused(LinuxRawRestoreOperationReason.ImageChanged);
                    else
                    {
                        var fresh = await platform.ObserveAsync(linked.Token).ConfigureAwait(false);
                        if (!RawImageRestoreJournal.Current(fresh, Review.Target) ||
                            !LinuxDiskRestoreTargetPreviewService.MatchesReview(stage, initial, fresh, Review.Target))
                            result = Refused(LinuxRawRestoreOperationReason.SelectionChanged);
                        else
                        {
                            var request = new RawImageDeviceRestoreRequest(operationId, reviewId,
                                Review.Image.ImageId, Review.Image.CaptureId, Review.Image.Generation,
                                Review.Image.ManifestRoot!, Review.Target);
                            journal = await platform.CreateJournalAsync(journalParent, request, fresh, linked.Token).ConfigureAwait(false);
                            journalAcquired = true;
                            linked.Token.ThrowIfCancellationRequested();
                            var opened = await platform.OpenAsync(stage, request, journal, fresh, linked.Token).ConfigureAwait(false);
                            target = opened.Target;
                            if (opened.State != LinuxRawRestoreOpenState.OpenedExclusive || target is null)
                                result = Refused(LinuxRawRestoreOperationReason.TargetUnavailable);
                            else
                            {
                                // Keep an idempotent cleanup lease: the engine normally
                                // closes it, while an unconfirmed close gets one final attempt.
                                target = new OwnedTarget(target);
                                var written = await RawImageDeviceRestoreService.RunAsync(stage, request, target, journal, linked.Token)
                                    .ConfigureAwait(false);
                                result = new(written.State, written.State switch
                                {
                                    RawImageDeviceRestoreState.WrittenAndReadBack => LinuxRawRestoreOperationReason.BytesVerified,
                                    RawImageDeviceRestoreState.Interrupted => deadline.IsCancellationRequested
                                        ? LinuxRawRestoreOperationReason.TimedOut : LinuxRawRestoreOperationReason.Interrupted,
                                    RawImageDeviceRestoreState.Refused => LinuxRawRestoreOperationReason.ExecutionRefused,
                                    _ => LinuxRawRestoreOperationReason.Failed
                                }, written, journalAcquired, false);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                result = new(RawImageDeviceRestoreState.Interrupted, deadline.IsCancellationRequested
                    ? LinuxRawRestoreOperationReason.TimedOut : LinuxRawRestoreOperationReason.Interrupted,
                    null, journalAcquired, false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
            { result = new(RawImageDeviceRestoreState.Failed, LinuxRawRestoreOperationReason.Failed, null, journalAcquired, false); }
        }
        finally
        {
            bool released = await ReleaseAsync(target).ConfigureAwait(false);
            released &= await ReleaseAsync(journal).ConfigureAwait(false);
            released &= await ReleaseOwnedAsync().ConfigureAwait(false);
            cleanupConfirmed = released;
            Volatile.Write(ref closed, 1);
            lifetime.Dispose();
            gate.Release();
        }
        return cleanupConfirmed ? result with { JournalAcquired = journalAcquired, CleanupConfirmed = true }
            : result with { State = RawImageDeviceRestoreState.Failed, Reason = LinuxRawRestoreOperationReason.CleanupUnconfirmed,
                JournalAcquired = journalAcquired, CleanupConfirmed = false };
    }

    private sealed class OwnedTarget(RawImageDeviceRestoreService.ITarget target) : RawImageDeviceRestoreService.ITarget
    {
        private bool released;
        public LinuxBlockObservation Observation => target.Observation;
        public Stream ReadBack => target.ReadBack;
        public ValueTask RecheckAsync(CancellationToken token) => target.RecheckAsync(token);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, ulong offset, CancellationToken token) => target.WriteAsync(bytes, offset, token);
        public void Flush() => target.Flush();
        public async ValueTask DisposeAsync()
        {
            if (released) return;
            await target.DisposeAsync().ConfigureAwait(false);
            released = true;
        }
    }

    private static LinuxRawRestoreExecution Refused(LinuxRawRestoreOperationReason reason) =>
        new(RawImageDeviceRestoreState.Refused, reason, null, false, false);

    private async ValueTask<bool> ReleaseOwnedAsync()
    {
        var held = preview; preview = null;
        bool released = await ReleaseAsync(held).ConfigureAwait(false);
        released &= await ReleaseAsync(stage).ConfigureAwait(false);
        return released;
    }

    private static async ValueTask<bool> ReleaseAsync(IAsyncDisposable? resource)
    {
        if (resource is null) return true;
        try { await resource.DisposeAsync().ConfigureAwait(false); return true; }
        catch (Exception) { return false; } // Attempt ALL remaining cleanup; never replace evidence with success.
    }

    public async ValueTask DisposeAsync()
    {
        try { await lifetime.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) when (Volatile.Read(ref closed) != 0) { /* Execution already released ownership. */ }
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref closed) != 0) return;
            Interlocked.Exchange(ref consumed, 1);
            cleanupConfirmed = await ReleaseOwnedAsync().ConfigureAwait(false);
            Volatile.Write(ref closed, 1);
            lifetime.Dispose();
            if (!cleanupConfirmed) throw new IOException("restore-operation.cleanup-unconfirmed");
        }
        finally { gate.Release(); }
    }
}
