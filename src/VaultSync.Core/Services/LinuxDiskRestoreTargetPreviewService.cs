using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public enum LinuxDiskRestoreTargetPreviewReason
{
    ObservationOnly, InvalidImage, InvalidInventory, SelectionChanged, UnknownRepositoryDevice,
    RepositoryOnTarget, UnsupportedTarget, UnsupportedGeometry, TargetReadOnly, TargetUnavailable, Interrupted
}

public sealed record LinuxDiskRestoreTargetPreviewAssessment(LinuxDiskRestoreTargetPreviewReason Reason,
    LinuxDiskRestoreTargetPreview? Preview = null);

/// <summary>
/// Owns a read-only exclusive observation, never Offline proof, confirmation or
/// write authorization. Disposing the stage invalidates this observation too.
/// </summary>
public sealed class LinuxDiskRestoreTargetPreview : IAsyncDisposable
{
    private readonly LinuxBlockReadSession target;
    private readonly LinuxRawImageStagingSession stage;
    private readonly LinuxBlockInventory initialInventory;
    private readonly Func<CancellationToken, ValueTask<LinuxBlockInventory>> observe;
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly SemaphoreSlim reviewGate = new(1, 1);
    private LinuxDiskRestoreOverwriteReview? activeReview;
    public LinuxBlockObservation ObservedTarget { get; }
    public DiskImageManifestVerificationAssessment Image => stage.Image;
    public DateTimeOffset ObservedAtUtc { get; } = DateTimeOffset.UtcNow;
    internal LinuxDiskRestoreTargetPreview(LinuxBlockReadSession target, LinuxRawImageStagingSession stage,
        LinuxBlockObservation observation, LinuxBlockInventory initialInventory,
        Func<CancellationToken, ValueTask<LinuxBlockInventory>> observe, TimeProvider clock)
    {
        this.target = target; this.stage = stage; ObservedTarget = observation;
        this.initialInventory = initialInventory; this.observe = observe; this.clock = clock;
        ObservedAtUtc = clock.GetUtcNow(); started = clock.GetTimestamp();
    }

    public async ValueTask<bool> RecheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Alive()) { await DisposeAsync().ConfigureAwait(false); return false; }
        try
        {
            var fresh = await observe(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!LinuxDiskRestoreTargetPreviewService.MatchesReview(stage, initialInventory, fresh, ObservedTarget))
            { await DisposeAsync().ConfigureAwait(false); return false; }
            await target.ValidateBindingAsync(cancellationToken).ConfigureAwait(false);
            if (!Alive()) { await DisposeAsync().ConfigureAwait(false); return false; }
            return true;
        }
        catch (IOException) { await DisposeAsync().ConfigureAwait(false); return false; }
        catch (UnauthorizedAccessException) { await DisposeAsync().ConfigureAwait(false); return false; }
        catch (ObjectDisposedException) { return false; }
    }

    private bool Alive() => stage.IsAlive && target.CanRead && clock.GetUtcNow() >= ObservedAtUtc &&
        clock.GetUtcNow() - ObservedAtUtc <= TimeSpan.FromSeconds(30) &&
        clock.GetElapsedTime(started) <= TimeSpan.FromSeconds(30);

    /// <summary>Creates a single-use review description, not device write authorization.</summary>
    public async ValueTask<LinuxDiskRestoreOverwriteReview?> CreateOverwriteReviewAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await reviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref activeReview, null);
            if (operationId == Guid.Empty || !await RecheckAsync(cancellationToken).ConfigureAwait(false)) return null;
            var review = new LinuxDiskRestoreOverwriteReview(this, operationId);
            Interlocked.Exchange(ref activeReview, review);
            return review;
        }
        finally { reviewGate.Release(); }
    }

    internal async ValueTask<bool> AcknowledgeAsync(LinuxDiskRestoreOverwriteReview review, Guid operationId,
        Guid reviewId, CancellationToken cancellationToken)
    {
        await reviewGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Consume before any asynchronous checks: failures and retries need a new review.
            if (!ReferenceEquals(Interlocked.CompareExchange(ref activeReview, null, review), review)) return false;
            if (review.OperationId != operationId || review.ReviewId != reviewId) return false;
            return await RecheckAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { reviewGate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref activeReview, null);
        return target.DisposeAsync();
    }
}

/// <summary>Fresh native target description and repository exclusion, with no writable device opens.</summary>
public static class LinuxDiskRestoreTargetPreviewService
{
    public static ValueTask<LinuxDiskRestoreTargetPreviewAssessment> CreateAsync(LinuxRawImageStagingSession stage,
        LinuxBlockInventory expected, string kernelName, CancellationToken cancellationToken = default) =>
        CreateAsync(stage, expected, kernelName, LinuxBlockInventoryService.ObserveAsync,
            LinuxBlockReadService.OpenAsync, cancellationToken);

    internal static async ValueTask<LinuxDiskRestoreTargetPreviewAssessment> CreateAsync(LinuxRawImageStagingSession stage,
        LinuxBlockInventory expected, string kernelName, Func<CancellationToken, ValueTask<LinuxBlockInventory>> observe,
        Func<LinuxBlockInventory, string, CancellationToken, Task<LinuxBlockReadResult>> open,
        CancellationToken cancellationToken = default, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(stage); ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observe); ArgumentNullException.ThrowIfNull(open);
        LinuxBlockReadSession? held = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!stage.IsAlive || stage.Image.State != DiskImageManifestVerificationState.ContentVerified || stage.Image.DeclaredOrigin is null)
                return Refuse(LinuxDiskRestoreTargetPreviewReason.InvalidImage);
            if (!stage.RepositoryAncestryKnown)
                return Refuse(LinuxDiskRestoreTargetPreviewReason.UnknownRepositoryDevice);
            if (!InventoryValid(expected)) return Refuse(LinuxDiskRestoreTargetPreviewReason.InvalidInventory);
            var selected = Single(expected, kernelName);
            if (selected is null) return Refuse(LinuxDiskRestoreTargetPreviewReason.UnsupportedTarget);
            LinuxBlockInventory fresh = await observe(cancellationToken).ConfigureAwait(false);
            if (!InventoryValid(fresh)) return Refuse(LinuxDiskRestoreTargetPreviewReason.InvalidInventory);
            if (expected.BootId != fresh.BootId || expected.MountNamespace != fresh.MountNamespace || selected != Single(fresh, kernelName))
                return Refuse(LinuxDiskRestoreTargetPreviewReason.SelectionChanged);
            var exclusion = CheckRepository(stage, fresh, selected);
            if (exclusion is not null) return Refuse(exclusion.Value);
            if (selected.Kind != LinuxBlockKind.WholeDisk || selected.ParentKernelName is not null)
                return Refuse(LinuxDiskRestoreTargetPreviewReason.UnsupportedTarget);
            if (selected.IsReadOnly) return Refuse(LinuxDiskRestoreTargetPreviewReason.TargetReadOnly);
            var origin = stage.Image.DeclaredOrigin;
            if (selected.Capacity != stage.Image.VerifiedBytes || selected.LogicalSectorBytes != origin.Layout.LogicalSectorBytes ||
                selected.PhysicalSectorBytes != origin.PhysicalSectorBytes)
                return Refuse(LinuxDiskRestoreTargetPreviewReason.UnsupportedGeometry);
            var opened = await open(fresh, kernelName, cancellationToken).ConfigureAwait(false);
            held = opened.Source;
            if (opened.State != LinuxBlockReadState.OpenedReadOnly || held is null)
                return Refuse(LinuxDiskRestoreTargetPreviewReason.TargetUnavailable);
            if (!held.Identity.Matches(selected)) return Refuse(LinuxDiskRestoreTargetPreviewReason.SelectionChanged);
            await held.ValidateBindingAsync(cancellationToken).ConfigureAwait(false);
            if (!stage.IsAlive) return Refuse(LinuxDiskRestoreTargetPreviewReason.InvalidImage);
            var preview = new LinuxDiskRestoreTargetPreview(held, stage, selected, fresh, observe, clock ?? TimeProvider.System);
            held = null;
            return new(LinuxDiskRestoreTargetPreviewReason.ObservationOnly, preview);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Refuse(LinuxDiskRestoreTargetPreviewReason.Interrupted); }
        catch (IOException) { return Refuse(LinuxDiskRestoreTargetPreviewReason.TargetUnavailable); }
        catch (UnauthorizedAccessException) { return Refuse(LinuxDiskRestoreTargetPreviewReason.TargetUnavailable); }
        catch (ObjectDisposedException) { return Refuse(LinuxDiskRestoreTargetPreviewReason.TargetUnavailable); }
        finally { if (held is not null) await held.DisposeAsync().ConfigureAwait(false); }
    }

    private static bool InventoryValid(LinuxBlockInventory inventory) => inventory.State == LinuxBlockInventoryState.Observed &&
        inventory.Reason == LinuxBlockInventoryReason.NamespaceObservationOnly && !inventory.HasUnresolvedSwaps &&
        inventory.ObservationId != Guid.Empty && inventory.BootId != Guid.Empty && inventory.MountNamespace is { Length: >= 7 and <= 32 } &&
        inventory.Devices is { Count: <= LinuxBlockInventoryService.MaximumDevices } && inventory.Devices.All(device => device is not null) &&
        inventory.ObservedAtUtc <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - inventory.ObservedAtUtc <= TimeSpan.FromSeconds(30);

    internal static bool MatchesReview(LinuxRawImageStagingSession stage, LinuxBlockInventory initial,
        LinuxBlockInventory fresh, LinuxBlockObservation target) => InventoryValid(fresh) &&
        initial.BootId == fresh.BootId && initial.MountNamespace == fresh.MountNamespace &&
        target == Single(fresh, target.KernelName) && CheckRepository(stage, fresh, target) is null;

    private static LinuxBlockObservation? Single(LinuxBlockInventory inventory, string name)
    {
        var matches = inventory.Devices.Where(device => device.KernelName == name).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static LinuxDiskRestoreTargetPreviewReason? CheckRepository(LinuxRawImageStagingSession stage,
        LinuxBlockInventory inventory, LinuxBlockObservation target)
    {
        foreach (string number in stage.RepositoryDeviceNumbers)
        {
            var matches = inventory.Devices.Where(device => device.DeviceNumber == number).Take(2).ToArray();
            if (matches.Length != 1) return LinuxDiskRestoreTargetPreviewReason.UnknownRepositoryDevice;
            var repository = matches[0];
            var disk = repository.Kind == LinuxBlockKind.Partition && repository.ParentKernelName is not null
                ? Single(inventory, repository.ParentKernelName) : repository;
            if (disk is null || disk.Kind != LinuxBlockKind.WholeDisk || disk.ParentKernelName is not null)
                return LinuxDiskRestoreTargetPreviewReason.UnknownRepositoryDevice;
            if (disk.KernelName == target.KernelName || repository.KernelName == target.KernelName)
                return LinuxDiskRestoreTargetPreviewReason.RepositoryOnTarget;
        }
        return null;
    }

    private static LinuxDiskRestoreTargetPreviewAssessment Refuse(LinuxDiskRestoreTargetPreviewReason reason) => new(reason);
}
