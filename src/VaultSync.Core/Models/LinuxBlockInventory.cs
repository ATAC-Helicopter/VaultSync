namespace VaultSync.Core.Models;

public enum LinuxBlockInventoryState { Observed, UnsupportedPlatform, Incomplete, ChangedDuringRead }
public enum LinuxBlockInventoryReason { NamespaceObservationOnly, LinuxRequired, UnreadableMetadata, InvalidMetadata, ResourceLimit, MetadataChanged }
public enum LinuxBlockKind { WholeDisk, Partition, VirtualOrStacked, Other }
public enum LinuxBlockIdentity { Unknown, UniqueInSnapshot, Ambiguous }
public enum LinuxBlockUse { Unknown, ObservedInUse }

/// <summary>Kernel metadata observations, never physical identity proof or device access permission.</summary>
public sealed record LinuxBlockObservation(string KernelName, string DeviceNumber, string? ParentKernelName,
    LinuxBlockKind Kind, ulong Capacity, uint LogicalSectorBytes, uint PhysicalSectorBytes,
    ulong? DiskSequence, bool IsReadOnly, string? HardwareIdentityCandidate, LinuxBlockIdentity Identity,
    bool MountObserved, bool SwapObserved, bool HasHolders, LinuxBlockUse Use)
{
    public bool ProfileGeometrySupported => LogicalSectorBytes is 512 or 4096 && PhysicalSectorBytes is 512 or 4096;
}

/// <summary>Current mount-namespace view. Even an observed snapshot cannot prove a disk is offline.</summary>
public sealed record LinuxBlockInventory(LinuxBlockInventoryState State, LinuxBlockInventoryReason Reason,
    Guid ObservationId, DateTimeOffset ObservedAtUtc, Guid BootId, string? MountNamespace,
    bool HasUnresolvedSwaps, IReadOnlyList<LinuxBlockObservation> Devices);
