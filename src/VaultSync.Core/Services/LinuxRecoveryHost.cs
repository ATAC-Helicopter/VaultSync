namespace VaultSync.Core.Services;

/// <summary>Actual installed media entry. No ordinary-desktop privilege endpoint.</summary>
internal static class LinuxRecoveryHost
{
    internal static async Task<int> RunAsync(CancellationToken token)
    {
        using var installation = await LinuxRecoveryMediaInstallation.OpenAsync(LinuxRecoveryMediaInstallation.Role.Host, token).ConfigureAwait(false);
        await using var controller = LinuxRecoveryControllerLaunch.Create(installation);
        Guid operation = Guid.NewGuid();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var channel = await controller.ConnectAsync(operation, linked.Token).ConfigureAwait(false);
        using var storage = LinuxRecoveryStorageLease.Create(operation);
        var text = installation.ConsoleText();
        var worker = new LinuxRecoveryWorkerOperation(channel, operation, storage.StagingParent, storage.JournalParent);
        var console = new GuardedConsole(installation, new LinuxRecoveryLocalConsole(text.Text, text.Culture));
        var supervisor = new LinuxRecoverySupervisor(worker, console);
        Task<LinuxRecoverySession.Snapshot>? running = null;
        Task? monitor = null;
        Task? presentation = null;
        try
        {
            running = supervisor.RunAsync(linked.Token);
            monitor = MonitorAsync(installation, linked.Token);
            if (await Task.WhenAny(running, monitor).ConfigureAwait(false) == monitor) await monitor.ConfigureAwait(false);
            var state = await running.ConfigureAwait(false);
            // Device outcome is already truthful. Artifact cleanup is separately
            // recorded before the host can report a fully clean successful exit.
            storage.Complete(state);
            if (installation.Description.ControllerMode == "graphical")
            {
                presentation = controller.WaitForPresentationExitAsync(linked.Token);
                if (await Task.WhenAny(presentation, monitor).ConfigureAwait(false) == monitor) await monitor.ConfigureAwait(false);
                await presentation.ConfigureAwait(false);
            }
            return LinuxRecoveryStorageLease.Successful(state) ? 0 : 74;
        }
        finally
        {
            try { await linked.CancelAsync().ConfigureAwait(false); }
            finally
            {
                try { await worker.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    try { if (running is not null) await running.ConfigureAwait(false); }
                    finally
                    {
                        try
                        {
                            if (!storage.CompletionAttempted && worker.State.Phase != LinuxRecoverySessionPhase.Completed) storage.Complete(worker.State);
                        }
                        finally
                        {
                            try { if (presentation is not null) await presentation.ConfigureAwait(false); }
                            finally
                            {
                                if (monitor is not null)
                                { try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) when (linked.IsCancellationRequested) { } }
                            }
                        }
                    }
                }
            }
        }
    }
    private static async Task MonitorAsync(LinuxRecoveryMediaInstallation installation, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) installation.RecheckBoot(LinuxRecoveryMediaInstallation.Role.Host);
    }
    private sealed class GuardedConsole(LinuxRecoveryMediaInstallation installation,
        LinuxRecoveryLocalConsole console) : LinuxRecoverySupervisor.IConfirmation
    {
        public async Task ConfirmAsync(LinuxDiskRestoreOverwriteReview review, CancellationToken token)
        {
            installation.RecheckBoot(LinuxRecoveryMediaInstallation.Role.Host); installation.Recheck();
            await console.ConfirmAsync(review, token).ConfigureAwait(false);
            installation.RecheckBoot(LinuxRecoveryMediaInstallation.Role.Host); installation.Recheck();
            token.ThrowIfCancellationRequested();
        }
    }
}
