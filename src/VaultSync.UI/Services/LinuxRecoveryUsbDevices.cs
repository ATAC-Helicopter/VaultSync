using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;
using VaultSync.Core.Models;
using VaultSync.Core.Services;

namespace VaultSync.UI.Services;

internal static class LinuxRecoveryUsbDevices
{
    private const string Service = "org.freedesktop.UDisks2";
    private const string Block = Service + ".Block";
    private const string Drive = Service + ".Drive";
    private const int OpenFlags = 0x80 | 0x80000; // O_EXCL | O_CLOEXEC
    private static readonly TimeSpan ReplyLimit = TimeSpan.FromSeconds(5);

    internal sealed record Target(LinuxBlockObservation Disk, string Model, string Serial)
    {
        public string KernelPath => "/dev/" + Disk.KernelName;
        public override string ToString() =>
            $"{Model} · {Serial} · {KernelPath} · {(Disk.Capacity / 1073741824d).ToString("N2", CultureInfo.CurrentCulture)} GiB";
    }

    internal static async Task<IReadOnlyList<Target>> ListAsync(CancellationToken token)
    {
        var inventory = await LinuxBlockInventoryService.ObserveAsync(token).ConfigureAwait(false);
        if (inventory.State != LinuxBlockInventoryState.Observed || inventory.HasUnresolvedSwaps) return [];

        using var bus = Connect();
        await bus.ConnectAsync().AsTask().WaitAsync(ReplyLimit, token).ConfigureAwait(false);
        List<Target> targets = [];
        foreach (var disk in inventory.Devices)
        {
            if (!LinuxRecoveryUsbMedia.Eligible(disk)) continue;
            try
            {
                if (await ReadTargetAsync(bus, disk, token).ConfigureAwait(false) is { } target)
                    targets.Add(target);
            }
            catch (Exception error) when (error is DBusErrorReplyException or KeyNotFoundException)
            {
                // A device can disappear or change interfaces during enumeration.
                continue;
            }
        }
        return targets.GroupBy(target => target.Serial, StringComparer.Ordinal)
            .Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
    }

    internal static async Task<SafeFileHandle> OpenAsync(Target reviewed, CancellationToken token)
    {
        using var bus = Connect();
        await bus.ConnectAsync().AsTask().WaitAsync(ReplyLimit, token).ConfigureAwait(false);
        try
        {
            if (await ReadTargetAsync(bus, reviewed.Disk, token).ConfigureAwait(false) != reviewed)
                throw new IOException("recovery-usb.changed");
        }
        catch (KeyNotFoundException error) { throw new IOException("recovery-usb.changed", error); }

        var pending = bus.CallMethodAsync(OpenMessage(bus, reviewed.Disk),
            static (message, _) => message.GetBodyReader().ReadHandle<SafeFileHandle>());
        // Dispose any descriptor that arrives after the operator cancels.
        try { return await pending.WaitAsync(TimeSpan.FromMinutes(2), token).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            _ = pending.ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully) task.Result.Dispose();
                else _ = task.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private static MessageBuffer OpenMessage(DBusConnection bus, LinuxBlockObservation disk)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, BlockPath(disk), Block, "OpenDevice", "sa{sv}",
            MessageFlags.AllowInteractiveAuthorization);
        writer.WriteString("rw");
        var options = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("flags");
        writer.WriteVariantInt32(OpenFlags);
        writer.WriteDictionaryEnd(options);
        return writer.CreateMessage();
    }

    private static DBusConnection Connect() => new("unix:path=/run/dbus/system_bus_socket");
    private static string BlockPath(LinuxBlockObservation disk) =>
        "/org/freedesktop/UDisks2/block_devices/" + disk.KernelName;

    private static async Task<Target?> ReadTargetAsync(DBusConnection bus, LinuxBlockObservation disk, CancellationToken token)
    {
        var block = await GetAllAsync(bus, BlockPath(disk), Block, token).ConfigureAwait(false);
        if (block["HintSystem"].GetBool() || block["ReadOnly"].GetBool() ||
            block["Size"].GetUInt64() != disk.Capacity) return null;

        string drivePath = block["Drive"].GetObjectPathAsString();
        if (!drivePath.StartsWith("/org/freedesktop/UDisks2/drives/", StringComparison.Ordinal)) return null;
        var drive = await GetAllAsync(bus, drivePath, Drive, token).ConfigureAwait(false);
        if (drive["ConnectionBus"].GetString() != "usb" || !drive["Removable"].GetBool()) return null;

        string model = drive["Model"].GetString();
        string serial = drive["Serial"].GetString();
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(serial) ||
            model.Length > 256 || serial.Length > 256 ||
            model.Any(char.IsControl) || serial.Any(char.IsControl)) return null;
        return new(disk, model, serial);
    }

    private static Task<Dictionary<string, VariantValue>> GetAllAsync(DBusConnection bus, string path,
        string contract, CancellationToken token)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(Service, path, "org.freedesktop.DBus.Properties", "GetAll", "s");
        writer.WriteString(contract);
        return bus.CallMethodAsync(writer.CreateMessage(),
            static (message, _) => message.GetBodyReader().ReadDictionaryOfStringToVariantValue()).WaitAsync(ReplyLimit, token);
    }
}
