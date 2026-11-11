#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxBlockInventoryServiceTests
{
    private const string Root = "/sys/class/block";
    private const string Mounts = "/proc/self/mountinfo";
    private const string Swaps = "/proc/swaps";
    private const string Boot = "/proc/sys/kernel/random/boot_id";
    private const string Namespace = "/proc/self/ns/mnt";

    [Fact]
    public async Task MountedPartitionMakesContainingDiskInUseWhileUnseenDiskRemainsUnknown()
    {
        var files = Basic();
        files.Partition("vda1", "vda", "252:1", 128, 1024);
        files.Text[Mounts] = "25 0 252:1 / / rw,relatime shared:1 - ext4 /dev/root rw\n";
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryState.Observed, snapshot.State);
        Assert.NotEqual(Guid.Empty, snapshot.ObservationId);
        Assert.Equal(TimeSpan.Zero, snapshot.ObservedAtUtc.Offset);
        Assert.Equal("mnt:[4026531841]", snapshot.MountNamespace);
        var source = snapshot.Devices.Single(d => d.KernelName == "vda");
        var partition = snapshot.Devices.Single(d => d.KernelName == "vda1");
        var target = snapshot.Devices.Single(d => d.KernelName == "vdb");
        Assert.Equal(LinuxBlockUse.ObservedInUse, source.Use);
        Assert.False(source.MountObserved);
        Assert.True(partition.MountObserved);
        Assert.Equal("vda", partition.ParentKernelName);
        Assert.Equal(source.DiskSequence, partition.DiskSequence);
        Assert.Equal(LinuxBlockUse.Unknown, target.Use); // Namespace absence is not global offline proof.
        Assert.Equal(LinuxBlockIdentity.UniqueInSnapshot, target.Identity);
        Assert.DoesNotContain(files.Reads.Keys, p => p.StartsWith("/dev/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PartitionWithoutSlavesDirectoryUsesParentRelationships()
    {
        var files = Basic();
        files.Partition("vda1", "vda", "252:1", 128, 1024);
        files.Directories.Remove(Root + "/vda1/slaves");
        files.Text[Mounts] = "25 0 252:1 / / rw - ext4 /dev/root rw";
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryState.Observed, snapshot.State);
        Assert.Equal(LinuxBlockUse.ObservedInUse, snapshot.Devices.Single(d => d.KernelName == "vda").Use);
        files.Directories.Remove(Root + "/vdb/slaves");
        snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryState.Incomplete, snapshot.State);
        Assert.Empty(snapshot.Devices); // Missing whole-disk topology is not treated as empty.
    }

    [Theory]
    [InlineData(512, 4096)]
    [InlineData(4096, 4096)]
    [InlineData(8192, 8192)]
    public async Task CapacityUsesKernel512ByteSectorsEvenFor4Kn(uint logical, uint physical)
    {
        var files = Basic();
        files.Text[Root + "/vdb/queue/logical_block_size"] = logical.ToString(CultureInfo.InvariantCulture);
        files.Text[Root + "/vdb/queue/physical_block_size"] = physical.ToString(CultureInfo.InvariantCulture);
        files.Text[Root + "/vdb/ro"] = "1";
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryState.Observed, snapshot.State);
        var disk = snapshot.Devices.Single(d => d.KernelName == "vdb");
        Assert.Equal(8192UL * 512, disk.Capacity);
        Assert.Equal(logical, disk.LogicalSectorBytes);
        Assert.Equal(physical, disk.PhysicalSectorBytes);
        Assert.True(disk.IsReadOnly);
        Assert.Equal(logical != 8192, disk.ProfileGeometrySupported);
        Assert.Equal(LinuxBlockUse.Unknown, disk.Use);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedSerialIsAmbiguousEvenWhenPreferredWwidsDiffer(bool wwid)
    {
        var files = Basic();
        files.Text[Root + "/vda/serial"] = "same";
        files.Text[Root + "/vdb/serial"] = "same";
        if (wwid)
        {
            files.Text[Root + "/vda/wwid"] = "naa.a";
            files.Text[Root + "/vdb/wwid"] = "naa.b";
        }
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryState.Observed, snapshot.State);
        Assert.All(snapshot.Devices, d => Assert.Equal(LinuxBlockIdentity.Ambiguous, d.Identity));
    }

    [Fact]
    public async Task MissingPersistentIdentityAndDiskSequenceRemainUnknown()
    {
        var files = Basic();
        files.Text.Remove(Root + "/vdb/serial");
        files.Text.Remove(Root + "/vdb/diskseq");
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        var disk = snapshot.Devices.Single(d => d.KernelName == "vdb");
        Assert.Equal(LinuxBlockIdentity.Unknown, disk.Identity);
        Assert.Null(disk.HardwareIdentityCandidate);
        Assert.Null(disk.DiskSequence);
        Assert.Equal(LinuxBlockUse.Unknown, disk.Use);
    }

    [Fact]
    public async Task StackedDevicePropagatesUseThroughSlaveAndPartitionAncestry()
    {
        var files = Basic();
        files.Partition("vdb1", "vdb", "252:17", 128, 1024);
        files.Whole("dm-0", "253:0", 1024, "virtual", true);
        files.Directories[Root + "/vdb1/holders"] = ["dm-0"];
        files.Directories[Root + "/dm-0/slaves"] = ["vdb1"];
        files.Text[Mounts] = "25 0 253:0 / /mapper rw - ext4 /dev/mapper/name rw\n";
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryState.Observed, snapshot.State);
        Assert.Equal(LinuxBlockKind.VirtualOrStacked, snapshot.Devices.Single(d => d.KernelName == "dm-0").Kind);
        Assert.All(snapshot.Devices.Where(d => d.KernelName != "vda"), d => Assert.Equal(LinuxBlockUse.ObservedInUse, d.Use));
    }

    [Theory]
    [InlineData("/dev/vdb1", false)]
    [InlineData("/dev/disk/by-uuid/example", false)]
    [InlineData("/swapfile", true)]
    [InlineData("/dev/unknown", true)]
    public async Task SwapAliasesPropagateAndUnresolvedSwapNeverImpliesOffline(string name, bool unresolved)
    {
        var files = Basic();
        files.Partition("vdb1", "vdb", "252:17", 128, 1024);
        string type = name == "/swapfile" ? "file" : "partition";
        files.Text[Swaps] += $"{name}\t{type}\t1024\t0\t-2\n";
        files.Links["/dev/disk/by-uuid/example"] = "/dev/vdb1";
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryState.Observed, snapshot.State);
        Assert.Equal(unresolved, snapshot.HasUnresolvedSwaps);
        Assert.Equal(unresolved ? LinuxBlockUse.Unknown : LinuxBlockUse.ObservedInUse,
            snapshot.Devices.Single(d => d.KernelName == "vdb").Use);
    }

    [Theory]
    [InlineData("/sys/class/block/vdb/diskseq", "2")]
    [InlineData("/sys/class/block/vdb/size", "16384")]
    [InlineData("/sys/class/block/vdb/dev", "252:99")]
    [InlineData("/sys/class/block/vdb/ro", "1")]
    [InlineData("/sys/class/block/vdb/serial", "replaced")]
    [InlineData("/sys/class/block/vdb/queue/logical_block_size", "4096")]
    [InlineData("/proc/sys/kernel/random/boot_id", "f7e3d745-ea42-4b57-bb39-72ca3e2787fb")]
    [InlineData("/proc/self/mountinfo", "26 0 252:16 / /new rw - ext4 /dev/vdb rw")]
    [InlineData("/proc/swaps", "Filename Type Size Used Priority\n/dev/vdb partition 4 0 -2")]
    public async Task ChangedGenerationGeometryOrUseInvalidatesEntireSnapshot(string path, string replacement)
    {
        var files = Basic();
        files.OnRead = (p, count) => { if (p == path && count == 2) files.Text[p] = replacement; };
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryState.ChangedDuringRead, snapshot.State);
        Assert.Empty(snapshot.Devices);
    }

    [Theory]
    [InlineData("namespace")]
    [InlineData("topology")]
    [InlineData("link")]
    public async Task ChangedNamespaceOrTopologyInvalidatesSnapshot(string change)
    {
        var files = Basic();
        if (change == "namespace") files.OnLink = (path, count) => { if (path == Namespace && count == 2) files.Links[path] = "mnt:[2]"; };
        if (change == "link") files.OnResolve = (path, count) => { if (path == Root + "/vdb" && count == 2) files.Resolved[path] += "-replaced"; };
        if (change == "topology") files.OnNames = (path, count) => { if (path == Root && count == 2) files.Directories[path] = ["vda", "vdb", "vdc"]; };
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryState.ChangedDuringRead, snapshot.State);
        Assert.Empty(snapshot.Devices);
    }

    [Theory]
    [InlineData("capacity-overflow")]
    [InlineData("partition-overflow")]
    [InlineData("geometry")]
    [InlineData("readonly")]
    [InlineData("event")]
    [InlineData("number")]
    [InlineData("identity")]
    [InlineData("mount")]
    [InlineData("swap")]
    [InlineData("parent")]
    [InlineData("holder")]
    [InlineData("path")]
    public async Task InvalidOrInconsistentMetadataReturnsNoDevices(string fault)
    {
        var files = Basic();
        switch (fault)
        {
            case "capacity-overflow": files.Text[Root + "/vdb/size"] = ulong.MaxValue.ToString(CultureInfo.InvariantCulture); break;
            case "partition-overflow": files.Partition("vdb1", "vdb", "252:17", ulong.MaxValue, 1024); break;
            case "geometry": files.Text[Root + "/vdb/queue/logical_block_size"] = "0"; break;
            case "readonly": files.Text[Root + "/vdb/ro"] = "unknown"; break;
            case "event": files.Text[Root + "/vdb/uevent"] += "\nDEVTYPE=disk"; break;
            case "number": files.Text[Root + "/vdb/dev"] = "252:0"; break;
            case "identity": files.Text[Root + "/vdb/serial"] = "bad\nidentity"; break;
            case "mount": files.Text[Mounts] = "malformed"; break;
            case "swap": files.Text[Swaps] = "unexpected header"; break;
            case "parent": files.Partition("vdb1", "vdb", "252:17", 128, 1024); files.Resolved[Root + "/vdb1"] = "/sys/devices/missing/vdb1"; break;
            case "holder": files.Directories[Root + "/vdb/holders"] = ["missing"]; break;
            case "path": files.Resolved[Root + "/vdb"] = "/tmp/fake-device"; break;
        }
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryState.Incomplete, snapshot.State);
        Assert.Equal(LinuxBlockInventoryReason.InvalidMetadata, snapshot.Reason);
        Assert.Empty(snapshot.Devices);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("denied")]
    [InlineData("large-file")]
    [InlineData("device-budget")]
    public async Task MissingDeniedOrExcessiveMetadataFailsClosed(string fault)
    {
        var files = Basic();
        if (fault == "missing") files.Text.Remove(Root + "/vdb/size");
        if (fault == "denied") files.OnRead = (path, _) => { if (path == Root + "/vdb/size") throw new UnauthorizedAccessException(); };
        if (fault == "large-file") files.Text[Mounts] = new string('x', 1024 * 1024 + 1);
        if (fault == "device-budget") files.Directories[Root] = Enumerable.Range(0, LinuxBlockInventoryService.MaximumDevices + 1).Select(i => "disk" + i).ToArray();
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryState.Incomplete, snapshot.State);
        Assert.Equal(fault is "large-file" or "device-budget" ? LinuxBlockInventoryReason.ResourceLimit : LinuxBlockInventoryReason.UnreadableMetadata, snapshot.Reason);
        Assert.Empty(snapshot.Devices);
    }

    [Fact]
    public async Task ExcessiveRelationshipGraphStopsBeforeEvaluation()
    {
        var files = Basic();
        for (int index = 0; index < 62; index++) files.Whole("disk" + index, "8:" + index, 4096, "id" + index);
        string[] names = files.Directories[Root];
        foreach (string name in names)
        {
            files.Directories[Root + "/" + name + "/holders"] = names;
            files.Directories[Root + "/" + name + "/slaves"] = names;
        }
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryReason.ResourceLimit, snapshot.Reason);
        Assert.Empty(snapshot.Devices);
    }

    [Fact]
    public async Task MalformedMountNamespaceFailsClosed()
    {
        var files = Basic();
        files.Links[Namespace] = "mnt:[]";
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryReason.InvalidMetadata, snapshot.Reason);
        Assert.Empty(snapshot.Devices);
    }

    [Fact]
    public async Task OpticalAndVirtualDevicesAreNotWholeDiskIdentityCandidates()
    {
        var files = Basic();
        files.Whole("sr0", "11:0", 0, "optical");
        files.Text[Root + "/sr0/device/type"] = "5";
        files.OnRead = (p, _) => { if (p == Root + "/sr0/serial") throw new IOException(); };
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(files);
        Assert.Equal(LinuxBlockInventoryState.Observed, snapshot.State);
        Assert.Equal(LinuxBlockKind.Other, snapshot.Devices.Single(d => d.KernelName == "sr0").Kind);
    }

    [Fact]
    public async Task CancellationIsPropagatedWithoutReturningPartialObservations()
    {
        var files = Basic();
        using var cancellation = new CancellationTokenSource();
        files.OnRead = (p, _) => { if (p == Root + "/vdb/size") cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await LinuxBlockInventoryService.ObserveAsync(files, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await LinuxBlockInventoryService.ObserveAsync(cancellation.Token));
    }

    private static Files Basic()
    {
        var files = new Files();
        files.Text[Boot] = "d10a7b81-8de1-41c1-bc16-09b959ca244d\n";
        files.Text[Mounts] = "1 0 0:1 / / rw - tmpfs tmpfs rw\n";
        files.Text[Swaps] = "Filename\tType\tSize\tUsed\tPriority\n";
        files.Links[Namespace] = "mnt:[4026531841]";
        files.Whole("vda", "252:0", 4096, "source");
        files.Whole("vdb", "252:16", 8192, "target");
        return files;
    }

    private sealed class Files : LinuxBlockInventoryService.IFiles
    {
        public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string[]> Directories { get; } = new(StringComparer.Ordinal) { [Root] = [] };
        public Dictionary<string, string> Resolved { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Links { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Reads { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> names = new(StringComparer.Ordinal), resolves = new(StringComparer.Ordinal), links = new(StringComparer.Ordinal);
        public Action<string, int>? OnRead, OnNames, OnResolve, OnLink;
        private static int Count(Dictionary<string, int> counts, string path) => counts[path] = counts.GetValueOrDefault(path) + 1;
        public ValueTask<string> ReadAsync(string path, int maximumBytes, CancellationToken token)
        {
            OnRead?.Invoke(path, Count(Reads, path)); token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Text.TryGetValue(path, out var text) ? text : throw new FileNotFoundException());
        }
        public IReadOnlyList<string> Names(string path, int maximumCount)
        { OnNames?.Invoke(path, Count(names, path)); return Directories.TryGetValue(path, out var result) ? result : throw new DirectoryNotFoundException(); }
        public string ResolveBlock(string path)
        { OnResolve?.Invoke(path, Count(resolves, path)); return Resolved[path]; }
        public string ReadLink(string path)
        { OnLink?.Invoke(path, Count(links, path)); return Links.GetValueOrDefault(path) ?? path; }
        private void Base(string name, string number, ulong sectors, bool partition)
        {
            Directories[Root] = [.. Directories[Root], name];
            string path = Root + "/" + name;
            string[] fields = number.Split(':');
            Text[path + "/uevent"] = $"MAJOR={fields[0]}\nMINOR={fields[1]}\nDEVNAME={name}\nDEVTYPE={(partition ? "partition" : "disk")}\n";
            Text[path + "/dev"] = number;
            Text[path + "/size"] = sectors.ToString(CultureInfo.InvariantCulture);
            Text[path + "/ro"] = "0";
            Directories[path + "/holders"] = []; Directories[path + "/slaves"] = [];
        }
        public void Whole(string name, string number, ulong sectors, string identity, bool virtualDevice = false)
        {
            Base(name, number, sectors, false);
            string path = Root + "/" + name;
            Resolved[path] = (virtualDevice ? "/sys/devices/virtual/block/" : "/sys/devices/pci0000:00/block/") + name;
            Text[path + "/queue/logical_block_size"] = "512";
            Text[path + "/queue/physical_block_size"] = "4096";
            Text[path + "/diskseq"] = "1";
            Text[path + "/serial"] = identity;
        }
        public void Partition(string name, string parent, string number, ulong start, ulong sectors)
        {
            Base(name, number, sectors, true);
            string path = Root + "/" + name;
            Resolved[path] = Resolved[Root + "/" + parent] + "/" + name;
            Text[path + "/partition"] = "1";
            Text[path + "/start"] = start.ToString(CultureInfo.InvariantCulture);
        }
    }
}
