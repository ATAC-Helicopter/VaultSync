namespace VaultSync.Core.Services;

/// <summary>
/// Internal console qualification supervisor, not an installed/elevation endpoint.
/// Trusted media bootstrap must supply the actual runner with fixed owned storage
/// and an installed-build/role/pidfd-bound nonroot UI on a different terminal/session.
/// Console approval is independent of the UI acknowledgment; neither an ID nor a
/// wire message supplies this action. Does not qualify graphical consent.
/// </summary>
internal sealed class LinuxRecoverySupervisor
{
    internal interface IConfirmation
    {
        // Return only after independent interaction with the exact native review.
        // Cancellation must finish input ownership; never leave a blocked reader.
        Task ConfirmAsync(LinuxDiskRestoreOverwriteReview review, CancellationToken token);
    }

    private readonly object gate = new();
    private readonly LinuxRecoveryWorkerOperation worker;
    private readonly IConfirmation confirmation;
    private Task<LinuxRecoverySession.Snapshot>? run;

    internal LinuxRecoverySupervisor(LinuxRecoveryWorkerOperation worker, IConfirmation confirmation)
    {
        this.worker = worker ?? throw new ArgumentNullException(nameof(worker));
        this.confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
    }

    // Adopts worker lifetime on first call. A duplicate cannot cancel its owner.
    internal Task<LinuxRecoverySession.Snapshot> RunAsync(CancellationToken token = default)
    {
        TaskCompletionSource<LinuxRecoverySession.Snapshot> completion;
        lock (gate)
        {
            if (run is not null) throw new InvalidOperationException("recovery-supervisor.already-started");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            run = completion.Task;
        }
        _ = CompleteAsync(token, completion);
        return completion.Task;
    }

    private async Task CompleteAsync(CancellationToken token, TaskCompletionSource<LinuxRecoverySession.Snapshot> completion)
    {
        try { completion.TrySetResult(await RunCoreAsync(token).ConfigureAwait(false)); }
        catch (Exception error) { completion.TrySetException(error); }
    }

    private async Task<LinuxRecoverySession.Snapshot> RunCoreAsync(CancellationToken token)
    {
        using var input = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task? confirming = null;
        try
        {
            var running = worker.RunAsync(input.Token);
            await Task.WhenAny(worker.ReviewReady, running).ConfigureAwait(false);
            if (running.IsCompleted) return await running.ConfigureAwait(false);
            if (!worker.ReviewReady.IsCompletedSuccessfully) return await running.ConfigureAwait(false);
            var review = await worker.ReviewReady.ConfigureAwait(false);
            confirming = confirmation.ConfirmAsync(review, input.Token);
            if (await Task.WhenAny(confirming, running).ConfigureAwait(false) == running)
                return await running.ConfigureAwait(false);
            await confirming.ConfigureAwait(false);
            input.Token.ThrowIfCancellationRequested();
            if (running.IsCompleted) return await running.ConfigureAwait(false);
            // This is the concrete independent supervisor action. The worker still
            // waits for its separately bound matching controller acknowledgment.
            _ = worker.ExecuteReviewedAsync(review.OperationId, review.ReviewId);
            return await running.ConfigureAwait(false);
        }
        finally
        {
            try { await input.CancelAsync().ConfigureAwait(false); }
            finally
            {
                try
                {
                    if (confirming is not null)
                    {
                        try { await confirming.ConfigureAwait(false); }
                        catch (OperationCanceledException) when (input.IsCancellationRequested) { }
                    }
                }
                finally { await worker.DisposeAsync().ConfigureAwait(false); }
            }
        }
    }
}
