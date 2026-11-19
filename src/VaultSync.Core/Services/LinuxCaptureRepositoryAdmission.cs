using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Held output filesystem ancestry, never a caller path or device authorization.</summary>
internal static class LinuxCaptureRepositoryAdmission
{
    internal static bool Allows(LinuxBlockInventory expected, LinuxBlockInventory fresh,
        string sourceName, string? heldFilesystemDeviceNumber)
    {
        // The original source selection was admitted before its held source was
        // opened. Isolated GPT inspection can take time; require a fresh CURRENT
        // observation here while retaining the original exact identity binding.
        if (!Valid(expected, false) || !Valid(fresh, true) || heldFilesystemDeviceNumber is null ||
            expected.BootId != fresh.BootId || expected.MountNamespace != fresh.MountNamespace)
            return false;
        var source = Single(expected, value => value.KernelName == sourceName);
        if (source is null || source != Single(fresh, value => value.KernelName == sourceName) ||
            source.Kind != LinuxBlockKind.WholeDisk || source.ParentKernelName is not null || !source.IsReadOnly ||
            source.Identity != LinuxBlockIdentity.UniqueInSnapshot || source.MountObserved || source.SwapObserved ||
            source.HasHolders || source.Use != LinuxBlockUse.Unknown)
            return false;
        var repository = Single(fresh, value => value.DeviceNumber == heldFilesystemDeviceNumber);
        if (repository is null) return false;
        var disk = repository.Kind == LinuxBlockKind.Partition && repository.ParentKernelName is not null
            ? Single(fresh, value => value.KernelName == repository.ParentKernelName) : repository;
        return disk is { Kind: LinuxBlockKind.WholeDisk, ParentKernelName: null,
            Identity: LinuxBlockIdentity.UniqueInSnapshot, IsReadOnly: false } &&
            disk.KernelName != source.KernelName && disk.DeviceNumber != source.DeviceNumber;
    }

    private static LinuxBlockObservation? Single(LinuxBlockInventory inventory, Func<LinuxBlockObservation, bool> predicate)
    {
        var matches = inventory.Devices.Where(predicate).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool Valid(LinuxBlockInventory value, bool requireFresh) => value.State == LinuxBlockInventoryState.Observed &&
        value.Reason == LinuxBlockInventoryReason.NamespaceObservationOnly && !value.HasUnresolvedSwaps &&
        value.ObservationId != Guid.Empty && value.BootId != Guid.Empty && value.MountNamespace is { Length: >= 7 and <= 32 } &&
        value.Devices is { Count: <= LinuxBlockInventoryService.MaximumDevices } && value.Devices.All(device => device is not null) &&
        value.ObservedAtUtc <= DateTimeOffset.UtcNow && (!requireFresh || DateTimeOffset.UtcNow - value.ObservedAtUtc <= TimeSpan.FromSeconds(30));
}
