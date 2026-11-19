namespace VaultSync.Core.Models;

// Review models only: observations are supplied by a caller, never device authorization.
public enum DiskRestoreDeviceKind { Unknown, PhysicalDisk, RegularFile }
public enum DiskRestoreDeviceUse { Unknown, Offline, InUse }
public enum DiskRestoreIdentity { Unknown, Unique, Ambiguous }
public enum DiskRestorePreviewReason
{
    PreviewOnly, InvalidOperation, InvalidImage, UnknownRepositoryDevice, InvalidDevice,
    UnsupportedDevice, UncertainIdentity, DeviceNotOffline, DeviceNotWritable,
    RepositoryOnTarget, UnsupportedGeometry, TargetTooSmall, LargerTargetUnqualified,
    PreviewMissing, PreviewChanged
}

public sealed record DiskRestoreImageDescriptor(Guid ImageId, ulong Generation,
    string ManifestRoot, ulong Capacity, uint LogicalSectorBytes, Guid RepositoryDeviceId);

public sealed record DiskRestoreDeviceObservation(Guid DeviceId, Guid DeviceGeneration,
    string HardwareIdentity, ulong Capacity, uint LogicalSectorBytes,
    DiskRestoreDeviceKind Kind, DiskRestoreDeviceUse Use, DiskRestoreIdentity Identity,
    bool? IsReadOnly);

/// <summary>Immutable whole-disk overwrite description; not an execution capability.</summary>
public sealed class DiskRestorePreview
{
    internal DiskRestorePreview(Guid operationId, DiskRestoreImageDescriptor image,
        DiskRestoreDeviceObservation target)
    {
        OperationId = operationId;
        Image = image;
        Target = target;
    }

    public Guid OperationId { get; }
    public DiskRestoreImageDescriptor Image { get; }
    public DiskRestoreDeviceObservation Target { get; }
    public ulong OverwriteOffset => 0;
    public ulong OverwriteLength => Image.Capacity;
}

public sealed record DiskRestorePreviewAssessment(DiskRestorePreviewReason Reason,
    DiskRestorePreview? Preview = null);
