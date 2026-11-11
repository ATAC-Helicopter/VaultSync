using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public enum LinuxBlockReadState
{
    OpenedReadOnly, UnsupportedPlatform, Refused, StaleObservation, Changed,
    InUse, AccessDenied, Unavailable, UnsupportedDevice, Unverifiable, Failed
}

/// <summary>A same-privilege read handle, never a privileged capability or restore authorization.</summary>
public sealed record LinuxBlockReadResult(LinuxBlockReadState State, LinuxBlockReadSession? Source = null);

public static class LinuxBlockReadService
{
    public const ulong MaximumCapacity = 8UL * 1024 * 1024 * 1024 * 1024;

    public static Task<LinuxBlockReadResult> OpenAsync(LinuxBlockInventory expected, string kernelName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return Task.FromResult(new LinuxBlockReadResult(LinuxBlockReadState.UnsupportedPlatform));
        return OpenAsync(expected, kernelName, LinuxBlockInventoryService.ObserveAsync,
            new LinuxBlockReadNative(), cancellationToken);
    }

    internal readonly record struct HandleIdentity(bool IsBlock, uint Major, uint Minor, ulong Capacity,
        uint LogicalSectorBytes, uint PhysicalSectorBytes, ulong DiskSequence, bool IsReadOnly)
    {
        internal bool Matches(LinuxBlockObservation device) => IsBlock &&
            device.DeviceNumber == Major.ToString(CultureInfo.InvariantCulture) + ":" + Minor.ToString(CultureInfo.InvariantCulture) &&
            Capacity == device.Capacity && LogicalSectorBytes == device.LogicalSectorBytes &&
            PhysicalSectorBytes == device.PhysicalSectorBytes && DiskSequence == device.DiskSequence &&
            IsReadOnly == device.IsReadOnly;
    }

    internal interface IDeviceAccess
    {
        SafeFileHandle? Open(string kernelName, out LinuxBlockReadState failure);
        bool Inspect(SafeFileHandle handle, out HandleIdentity identity);
    }

    // Tests can supply ordinary-file handles and synthetic observations. No wire request
    // can select a provider, path, native operation or executable.
    internal static async Task<LinuxBlockReadResult> OpenAsync(LinuxBlockInventory expected, string kernelName,
        Func<CancellationToken, ValueTask<LinuxBlockInventory>> observe, IDeviceAccess deviceAccess,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidSnapshot(expected) || !Name(kernelName))
            return new(LinuxBlockReadState.Refused);
        if (!Fresh(expected))
            return new(LinuxBlockReadState.StaleObservation);
        LinuxBlockObservation? selected = Select(expected, kernelName);
        if (selected is null || !Candidate(selected))
            return new(LinuxBlockReadState.Refused);
        if (InUse(selected))
            return new(LinuxBlockReadState.InUse);

        SafeFileHandle? handle = null;
        try
        {
            LinuxBlockInventory before = await observe(cancellationToken).ConfigureAwait(false);
            if (!ValidSnapshot(before) || !Fresh(before))
                return new(LinuxBlockReadState.Unverifiable);
            if (!SameObservation(expected, before, selected))
                return new(LinuxBlockReadState.Changed);
            cancellationToken.ThrowIfCancellationRequested();
            handle = deviceAccess.Open(kernelName, out LinuxBlockReadState failure);
            if (handle is null)
                return new(failure);
            if (handle.IsInvalid || !deviceAccess.Inspect(handle, out HandleIdentity first))
                return new(LinuxBlockReadState.Unverifiable);
            if (!first.Matches(selected))
                return new(LinuxBlockReadState.Changed);
            LinuxBlockInventory after = await observe(cancellationToken).ConfigureAwait(false);
            if (!ValidSnapshot(after) || !Fresh(after))
                return new(LinuxBlockReadState.Unverifiable);
            if (!SameObservation(expected, after, selected))
                return new(LinuxBlockReadState.Changed);
            if (!deviceAccess.Inspect(handle, out HandleIdentity second))
                return new(LinuxBlockReadState.Unverifiable);
            if (first != second)
                return new(LinuxBlockReadState.Changed);
            cancellationToken.ThrowIfCancellationRequested();
            var source = new LinuxBlockReadSession(handle, first, deviceAccess);
            handle = null; // Ownership transfers only after both observations and handle checks.
            return new(LinuxBlockReadState.OpenedReadOnly, source);
        }
        catch (UnauthorizedAccessException) { return new(LinuxBlockReadState.AccessDenied); }
        catch (IOException) { return new(LinuxBlockReadState.Failed); }
        catch (EntryPointNotFoundException) { return new(LinuxBlockReadState.UnsupportedPlatform); }
        catch (DllNotFoundException) { return new(LinuxBlockReadState.UnsupportedPlatform); }
        finally { handle?.Dispose(); }
    }

    private static bool ValidSnapshot(LinuxBlockInventory value) =>
        value.State == LinuxBlockInventoryState.Observed && value.Reason == LinuxBlockInventoryReason.NamespaceObservationOnly &&
        value.ObservationId != Guid.Empty && value.BootId != Guid.Empty && value.MountNamespace is { Length: >= 7 and <= 32 } &&
        !value.HasUnresolvedSwaps && value.Devices is { Count: <= LinuxBlockInventoryService.MaximumDevices };

    private static bool Fresh(LinuxBlockInventory value)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return value.ObservedAtUtc <= now && now - value.ObservedAtUtc <= TimeSpan.FromSeconds(30);
    }

    private static LinuxBlockObservation? Select(LinuxBlockInventory inventory, string name)
    {
        LinuxBlockObservation? selected = null;
        foreach (LinuxBlockObservation device in inventory.Devices)
            if (device is not null && device.KernelName == name)
            {
                if (selected is not null) return null;
                selected = device;
            }
        return selected;
    }

    private static bool SameObservation(LinuxBlockInventory expected, LinuxBlockInventory current, LinuxBlockObservation selected) =>
        ValidSnapshot(current) && Fresh(current) && expected.BootId == current.BootId &&
        expected.MountNamespace == current.MountNamespace && Select(current, selected.KernelName) == selected;

    private static bool Candidate(LinuxBlockObservation device) => Name(device.KernelName) &&
        device.Kind == LinuxBlockKind.WholeDisk && device.ParentKernelName is null &&
        device.Identity == LinuxBlockIdentity.UniqueInSnapshot && device.HardwareIdentityCandidate is { Length: > 0 and <= 263 } &&
        !device.HardwareIdentityCandidate.Any(char.IsControl) && device.DiskSequence is > 0 &&
        device.Capacity is > 0 and <= MaximumCapacity && device.LogicalSectorBytes is 512 or 4096 &&
        device.PhysicalSectorBytes is 512 or 4096 && device.PhysicalSectorBytes >= device.LogicalSectorBytes &&
        device.Capacity % device.LogicalSectorBytes == 0 && device.Use is LinuxBlockUse.Unknown or LinuxBlockUse.ObservedInUse;

    private static bool InUse(LinuxBlockObservation device) => device.MountObserved || device.SwapObserved ||
        device.HasHolders || device.Use == LinuxBlockUse.ObservedInUse;

    private static bool Name(string? value) => value is { Length: > 0 and <= 128 } && value[0] != '.' &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '!');
}
