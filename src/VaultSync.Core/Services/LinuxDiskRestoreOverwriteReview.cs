using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>
/// Immutable description for a future translated overwrite dialog. Acknowledging
/// records one review exchange only; it grants no offline, privilege or write authority.
/// </summary>
public sealed class LinuxDiskRestoreOverwriteReview
{
    private readonly LinuxDiskRestoreTargetPreview owner;
    internal LinuxDiskRestoreOverwriteReview(LinuxDiskRestoreTargetPreview owner, Guid operationId)
    {
        this.owner = owner;
        OperationId = operationId;
        Image = owner.Image;
        Target = owner.ObservedTarget;
        ObservedAtUtc = owner.ObservedAtUtc;
    }

    public Guid ReviewId { get; } = Guid.NewGuid();
    public Guid OperationId { get; }
    public DiskImageManifestVerificationAssessment Image { get; }
    public LinuxBlockObservation Target { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public ulong OverwriteOffset => 0;
    public ulong OverwriteLength => Image.VerifiedBytes;

    /// <summary>
    /// Consumes this review once, rechecking topology and the held read-only target.
    /// A true result is not a writable capability or proof of a user interaction.
    /// </summary>
    public ValueTask<bool> AcknowledgeAsync(Guid operationId, Guid reviewId,
        CancellationToken cancellationToken = default) => owner.AcknowledgeAsync(this, operationId, reviewId, cancellationToken);
}
