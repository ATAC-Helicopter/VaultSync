using VaultSync.Core.Services;

namespace VaultSync.RecoveryController;

internal interface IRecoveryPresentation
{
    CancellationToken CancellationRequested { get; }
    Task<string?> ChooseImageFolderAsync(Guid operation, CancellationToken token);
    Task<string?> ChooseTargetAsync(Guid operation, LinuxRecoveryResponses.Inventory inventory, CancellationToken token);
    Task<bool> AcknowledgeReviewAsync(LinuxRecoveryResponses.Review review, CancellationToken token);
    void Observe(LinuxRecoverySession.Snapshot snapshot);
    void Complete(LinuxRecoveryControllerClient.Result result);
}
