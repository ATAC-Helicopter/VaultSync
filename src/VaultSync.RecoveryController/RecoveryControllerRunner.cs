using VaultSync.Core.Services;

namespace VaultSync.RecoveryController;

/// <summary>Installed nonroot source owner. Presentation never receives a writer or privileged opener.</summary>
internal static class RecoveryControllerRunner
{
    internal static async Task<LinuxRecoveryControllerClient.Result> RunAsync(LinuxRecoveryMediaInstallation installation,
        IRecoveryPresentation presentation, CancellationToken token)
    {
        var result = LinuxRecoveryControllerClient.Result.Failed;
        try
        {
            await using var interaction = new Interaction(presentation);
            result = await LinuxRecoveryControllerClient.RunInstalledAsync(installation, interaction, token, presentation.Observe).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException) { result = LinuxRecoveryControllerClient.Result.AccessDenied; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { result = LinuxRecoveryControllerClient.Result.Cancelled; }
        catch (Exception) { result = LinuxRecoveryControllerClient.Result.Failed; }
        // Dispose/drain precedes terminal presentation, including failure paths.
        presentation.Complete(result);
        return result;
    }

    private sealed class Interaction(IRecoveryPresentation presentation) : LinuxRecoveryControllerClient.IInteraction, IAsyncDisposable
    {
        private FileStream? metadata, payload;
        public CancellationToken CancellationRequested => presentation.CancellationRequested;
        public async Task<LinuxRecoveryControllerClient.ImageSources?> SelectImagesAsync(Guid operation, CancellationToken token)
        {
            string? folder = await presentation.ChooseImageFolderAsync(operation, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (folder is null) return null;
            if (!Path.IsPathFullyQualified(folder) || folder.Length > 4096 || folder.Any(char.IsControl) ||
                folder.Split('/').Any(part => part is "." or "..")) throw LinuxRecoveryMediaInstallation.Invalid();
            // Both handles are opened by this admitted, unprivileged controller.
            // A partial open is still owned here and closed by DisposeAsync.
            metadata = File.OpenRead(Path.Combine(folder, "image.metadata"));
            payload = File.OpenRead(Path.Combine(folder, "payload.raw"));
            token.ThrowIfCancellationRequested();
            return new(operation, metadata, payload);
        }
        public async Task<LinuxRecoveryControllerClient.TargetSelection?> SelectTargetAsync(Guid operation,
            LinuxRecoveryResponses.Inventory inventory, CancellationToken token)
        {
            string? selected = await presentation.ChooseTargetAsync(operation, inventory, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return selected is null ? null : new(operation, inventory.ObservationId, selected);
        }
        public async Task<LinuxRecoveryControllerClient.ReviewAcknowledgment?> AcknowledgeReviewAsync(
            LinuxRecoveryResponses.Review review, CancellationToken token)
        {
            bool selected = await presentation.AcknowledgeReviewAsync(review, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return selected ? new(review.OperationId, review.ReviewId) : null;
        }
        public async ValueTask DisposeAsync()
        {
            try { if (payload is not null) await payload.DisposeAsync().ConfigureAwait(false); }
            finally { if (metadata is not null) await metadata.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
