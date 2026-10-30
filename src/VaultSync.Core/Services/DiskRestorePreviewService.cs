using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>
/// Pure whole-disk preview checks. Does not enumerate/open devices, verify an image,
/// establish observation freshness or authorize writes. No UI/worker integration yet.
/// </summary>
public static class DiskRestorePreviewService
{
    public static DiskRestorePreviewAssessment Create(Guid operationId,
        DiskRestoreImageDescriptor? image, DiskRestoreDeviceObservation? target)
    {
        if (operationId == Guid.Empty)
            return new(DiskRestorePreviewReason.InvalidOperation);
        if (image is null || image.ImageId == Guid.Empty || image.Generation == 0 ||
            !IsDigest(image.ManifestRoot) || !Aligned(image.Capacity, image.LogicalSectorBytes))
            return new(DiskRestorePreviewReason.InvalidImage);
        if (image.RepositoryDeviceId == Guid.Empty)
            return new(DiskRestorePreviewReason.UnknownRepositoryDevice);
        if (target is null || target.DeviceId == Guid.Empty || target.DeviceGeneration == Guid.Empty ||
            !ValidHardwareIdentity(target.HardwareIdentity) || target.Capacity == 0)
            return new(DiskRestorePreviewReason.InvalidDevice);
        if (target.Kind != DiskRestoreDeviceKind.PhysicalDisk)
            return new(DiskRestorePreviewReason.UnsupportedDevice);
        if (target.Identity != DiskRestoreIdentity.Unique)
            return new(DiskRestorePreviewReason.UncertainIdentity);
        if (target.Use != DiskRestoreDeviceUse.Offline)
            return new(DiskRestorePreviewReason.DeviceNotOffline);
        if (target.IsReadOnly != false)
            return new(DiskRestorePreviewReason.DeviceNotWritable);
        if (target.DeviceId == image.RepositoryDeviceId)
            return new(DiskRestorePreviewReason.RepositoryOnTarget);
        if (!Aligned(target.Capacity, target.LogicalSectorBytes) ||
            target.LogicalSectorBytes != image.LogicalSectorBytes)
            return new(DiskRestorePreviewReason.UnsupportedGeometry);
        if (target.Capacity < image.Capacity)
            return new(DiskRestorePreviewReason.TargetTooSmall);
        // GPT backup-header relocation on larger targets is not qualified by the ADR.
        if (target.Capacity > image.Capacity)
            return new(DiskRestorePreviewReason.LargerTargetUnqualified);
        return new(DiskRestorePreviewReason.PreviewOnly, new(operationId, image, target));
    }

    public static DiskRestorePreviewAssessment Recheck(DiskRestorePreview? preview,
        Guid operationId, DiskRestoreImageDescriptor? image, DiskRestoreDeviceObservation? target)
    {
        if (preview is null)
            return new(DiskRestorePreviewReason.PreviewMissing);
        DiskRestorePreviewAssessment current = Create(operationId, image, target);
        if (current.Preview is null)
            return current;
        if (preview.OperationId != operationId || preview.Image != image || preview.Target != target)
            return new(DiskRestorePreviewReason.PreviewChanged);
        return current;
    }

    private static bool Aligned(ulong capacity, uint sector) =>
        sector is 512 or 4096 && capacity != 0 && capacity % sector == 0;

    private static bool IsDigest(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidHardwareIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);
}
