#nullable enable
using System;
using System.Collections.Generic;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxCaptureRepositoryAdmissionTests
{
    private static LinuxBlockObservation Disk(string name, string number, bool readOnly) => new(name, number,
        null, LinuxBlockKind.WholeDisk, 65536, 512, 512, 4, readOnly, name,
        LinuxBlockIdentity.UniqueInSnapshot, false, false, false, LinuxBlockUse.Unknown);
    private static LinuxBlockInventory Inventory() => new(LinuxBlockInventoryState.Observed,
        LinuxBlockInventoryReason.NamespaceObservationOnly, Guid.NewGuid(), DateTimeOffset.UtcNow,
        Guid.NewGuid(), "mnt:[12]", false, new[] { Disk("vda", "8:0", true), Disk("vdb", "8:16", false) });

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DistinctHeldWholeDiskOrPartitionIsAdmitted(bool partition)
    {
        var initial = Inventory();
        var values = new List<LinuxBlockObservation>(initial.Devices);
        if (partition) values.Add(Disk("vdb1", "8:17", false) with
            { Kind = LinuxBlockKind.Partition, ParentKernelName = "vdb" });
        var fresh = initial with { ObservationId = Guid.NewGuid(), Devices = values };
        Assert.True(LinuxCaptureRepositoryAdmission.Allows(initial, fresh, "vda", partition ? "8:17" : "8:16"));
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate-number")] [InlineData("duplicate-parent")]
    [InlineData("stacked")] [InlineData("missing-parent")] [InlineData("same-source")]
    [InlineData("same-source-partition")] [InlineData("ambiguous-repository")]
    [InlineData("changed-source")] [InlineData("changed-boot")] [InlineData("changed-namespace")]
    [InlineData("stale")] [InlineData("future")] [InlineData("unresolved-swap")]
    [InlineData("mounted-source")] [InlineData("writable-source")]
    public void UnknownOrChangedAncestryCannotAuthorizeCreation(string fault)
    {
        var initial = Inventory(); var fresh = initial; string? number = "8:16";
        var values = new List<LinuxBlockObservation>(fresh.Devices);
        switch (fault)
        {
            case "missing": number = null; break;
            case "duplicate-number": values.Add(values[1] with { KernelName = "vdc" }); break;
            case "duplicate-parent":
                values.Add(values[1] with { DeviceNumber = "8:32" });
                values.Add(values[1] with { KernelName = "vdb1", DeviceNumber = "8:17", Kind = LinuxBlockKind.Partition, ParentKernelName = "vdb" }); number = "8:17"; break;
            case "stacked": values[1] = values[1] with { Kind = LinuxBlockKind.VirtualOrStacked }; break;
            case "missing-parent": values[1] = values[1] with { Kind = LinuxBlockKind.Partition, ParentKernelName = "absent" }; break;
            case "same-source": number = "8:0"; break;
            case "same-source-partition": values.Add(values[0] with { KernelName = "vda1", DeviceNumber = "8:1", Kind = LinuxBlockKind.Partition, ParentKernelName = "vda" }); number = "8:1"; break;
            case "ambiguous-repository": values[1] = values[1] with { Identity = LinuxBlockIdentity.Ambiguous }; break;
            case "changed-source": values[0] = values[0] with { DiskSequence = 5 }; break;
            case "changed-boot": fresh = fresh with { BootId = Guid.NewGuid() }; break;
            case "changed-namespace": fresh = fresh with { MountNamespace = "mnt:[13]" }; break;
            case "stale": fresh = fresh with { ObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1) }; break;
            case "future": fresh = fresh with { ObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) }; break;
            case "unresolved-swap": fresh = fresh with { HasUnresolvedSwaps = true }; break;
            case "mounted-source": values[0] = values[0] with { MountObserved = true }; initial = initial with { Devices = values }; break;
            case "writable-source": values[0] = values[0] with { IsReadOnly = false }; initial = initial with { Devices = values }; break;
        }
        fresh = fresh with { Devices = values };
        Assert.False(LinuxCaptureRepositoryAdmission.Allows(initial, fresh, "vda", number));
    }
}
