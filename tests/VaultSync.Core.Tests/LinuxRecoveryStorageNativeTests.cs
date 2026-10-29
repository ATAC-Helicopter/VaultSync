#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

[CollectionDefinition("OwnedRecoveryStorage", DisableParallelization = true)]
public sealed class OwnedRecoveryStorageCollection { }

/// <summary>
/// Filesystem component fixture only, never a Host/media or raw-device qualification.
/// The required wrapper creates real kernel isolation; environment values only
/// request execution/name reports and cannot satisfy the isolation checks.
/// </summary>
[Collection("OwnedRecoveryStorage")]
public sealed class LinuxRecoveryStorageNativeTests
{
    [Fact]
    public void ActualFactoryReceiptsRetentionAndDeletionRequireProvenOwnedIsolatedFilesystem()
    {
        string? request = Environment.GetEnvironmentVariable("VS_STORAGE_NATIVE_REQUIRED");
        if (request is null) return; // Ordinary suite is not a native fixture run.
        Assert.Equal("1", request);
        Assert.True(OperatingSystem.IsLinux());
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var proof = ProveFixture();
        var cases = new List<string>();
        SuccessDeletesOnlyStaging(proof); cases.Add(nameof(SuccessDeletesOnlyStaging));
        FailureRetainsEvidence(proof); cases.Add(nameof(FailureRetainsEvidence));
        ExistingOperationIsNeverAdopted(proof); cases.Add(nameof(ExistingOperationIsNeverAdopted));
        HardlinkPreservesOriginal(proof); cases.Add(nameof(HardlinkPreservesOriginal));
        SymlinkPreservesOriginal(proof); cases.Add(nameof(SymlinkPreservesOriginal));
        UnexpectedEntryPreventsEveryUnlink(proof); cases.Add(nameof(UnexpectedEntryPreventsEveryUnlink));
        ReplacedDirectoryCannotRedirectCleanup(proof); cases.Add(nameof(ReplacedDirectoryCannotRedirectCleanup));
        WriteProof(proof, cases);
    }

    private sealed record Proof(string SourceCommit, string CheckoutRevision, string Scratch, string Results, string OuterMount,
        string PrivateMount, string OuterPid, string PrivatePid, LinuxRecoveryNative.Identity Source,
        LinuxRecoveryNative.Identity Store, LinuxRecoveryNative.Identity ResultsIdentity);

    [SupportedOSPlatform("linux")]
    private static Proof ProveFixture()
    {
        Assert.Equal(Architecture.X64, RuntimeInformation.ProcessArchitecture);
        Assert.True(LinuxRecoveryNative.RootIdentity);
        Assert.True(LinuxRecoveryNative.InitialUserNamespace());
        string scratch = RequestedPath("VS_STORAGE_NATIVE_SCRATCH"), results = RequestedPath("VS_STORAGE_NATIVE_RESULTS");
        Assert.NotEqual(scratch, results);
        using var scratchHandle = LinuxRecoveryNative.Open(scratch, 0x10000 | 0x20000 | 0x80000);
        Assert.Equal((ushort)0x41c0, LinuxRecoveryNative.Stat(scratchHandle).Mode);
        Assert.Equal(0U, LinuxRecoveryNative.Stat(scratchHandle).Owner);
        using var outerProc = LinuxRecoveryNative.Open(scratch + "/outer-proc", 0x10000 | 0x20000 | 0x80000);
        Assert.Equal(0x9fa0, FilesystemType(outerProc));
        string outerMount = Link(scratch + "/outer-proc/1/ns/mnt", "mnt:");
        string privateMount = Link("/proc/self/ns/mnt", "mnt:");
        string outerPid = Link(scratch + "/outer-proc/1/ns/pid", "pid:");
        string privatePid = Link("/proc/self/ns/pid", "pid:");
        Assert.NotEqual(outerMount, privateMount); Assert.NotEqual(outerPid, privatePid);
        Assert.Equal(privateMount, Link("/proc/1/ns/mnt", "mnt:"));
        Assert.Equal(privatePid, Link("/proc/1/ns/pid", "pid:"));
        Assert.True(LinuxRecoveryNative.SameMountNamespace()); // Fresh namespace PID1, not global PID1.
        using var srv = LinuxRecoveryNative.OpenDirectory("/srv", false);
        Assert.Equal(0x01021994, FilesystemType(srv));
        using var store = LinuxRecoveryNative.OpenDirectory(LinuxRecoveryStorageLease.Store, false);
        using var source = LinuxRecoveryNative.Open(scratch + "/store", 0x10000 | 0x20000 | 0x80000);
        var storeIdentity = LinuxRecoveryNative.Stat(store); var sourceIdentity = LinuxRecoveryNative.Stat(source);
        Assert.True(LinuxRecoveryNative.Ext4(store)); Assert.True(LinuxRecoveryNative.Ext4(source));
        Assert.False(LinuxRecoveryNative.ReadOnly(store));
        Assert.Equal((ushort)0x41c0, storeIdentity.Mode); Assert.Equal(0U, storeIdentity.Owner);
        Assert.Equal((sourceIdentity.DeviceMajor, sourceIdentity.DeviceMinor, sourceIdentity.Inode),
            (storeIdentity.DeviceMajor, storeIdentity.DeviceMinor, storeIdentity.Inode));
        var mounts = File.ReadAllLines("/proc/self/mountinfo");
        Assert.Contains(mounts, line => Mount(line, "/srv", "tmpfs"));
        Assert.Contains(mounts, line => Mount(line, LinuxRecoveryStorageLease.Store, "ext4"));
        Assert.Contains(mounts, line => Mount(line, scratch + "/outer-proc", "proc"));
        var resultsIdentity = LinuxRecoveryNative.StatPath(results);
        Assert.Equal((ushort)0x41c0, resultsIdentity.Mode);
        Assert.NotEqual(0U, resultsIdentity.Owner);
        string sourceCommit = DiskWorkerBuildIdentity.SourceCommit ?? throw new InvalidDataException("fixture.source-unidentified");
        string checkout = Environment.GetEnvironmentVariable("VS_STORAGE_NATIVE_SOURCE") ?? throw new InvalidDataException("fixture.checkout-unidentified");
        Assert.Equal(40, sourceCommit.Length); Assert.Equal(40, checkout.Length);
        Assert.Equal(checkout, sourceCommit); // Reject stale binaries; neither value claims the scanner PR-head revision.
        return new(sourceCommit, checkout, scratch, results, outerMount, privateMount, outerPid, privatePid, sourceIdentity, storeIdentity, resultsIdentity);
    }

    private static string RequestedPath(string key)
    {
        string path = Environment.GetEnvironmentVariable(key) ?? throw new InvalidDataException("fixture.request-missing");
        Assert.StartsWith("/", path, StringComparison.Ordinal);
        Assert.Equal(path, Path.GetFullPath(path)); Assert.DoesNotContain("..", path.Split('/'));
        return path;
    }
    private static string Link(string path, string prefix)
    {
        string value = new FileInfo(path).LinkTarget ?? throw new InvalidDataException("fixture.kernel-link-missing");
        Assert.StartsWith(prefix, value, StringComparison.Ordinal); return value;
    }
    private static bool Mount(string line, string path, string type)
    {
        string[] parts = line.Split(' '); int separator = Array.IndexOf(parts, "-");
        return parts.Length > 7 && separator > 5 && separator + 1 < parts.Length && parts[4] == path && parts[separator + 1] == type;
    }

    private static LinuxRecoverySession.Snapshot State(Guid operation, bool completed)
    {
        var reviewId = Guid.NewGuid();
        var device = new LinuxRecoveryResponses.Device("fixture-disk", null, 1, 1, 65536, 1, 512, 512,
            LinuxBlockKind.WholeDisk, LinuxBlockIdentity.UniqueInSnapshot, LinuxBlockUse.Unknown, false, false, false, false);
        var review = new LinuxRecoveryResponses.Review(operation, reviewId, Guid.NewGuid(), Guid.NewGuid(), 1,
            new string('a', 64), DateTimeOffset.UnixEpoch, 65536, device, Guid.NewGuid());
        // Simulated session outcome drives ONLY owned filesystem cleanup. No raw
        // target is opened; this is not execution permission or byte verification.
        var outcome = completed ? new LinuxRecoveryResponses.Outcome(reviewId, new(RawImageDeviceRestoreState.WrittenAndReadBack,
            LinuxRawRestoreOperationReason.BytesVerified, new(RawImageDeviceRestoreState.WrittenAndReadBack, 65536, 0, 0, true), true, true)) : null;
        return new(completed ? LinuxRecoverySessionPhase.Completed : LinuxRecoverySessionPhase.Faulted, false, null, review,
            completed ? 65536UL : 0UL, outcome, null);
    }

    [SupportedOSPlatform("linux")]
    private static string Stage(LinuxRecoveryStorageLease lease)
    {
        string path = Path.Combine(lease.StagingParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllBytes(path + "/image.metadata", new byte[596]); File.WriteAllBytes(path + "/payload.raw", new byte[65536]);
        File.SetUnixFileMode(path + "/image.metadata", UnixFileMode.UserRead);
        File.SetUnixFileMode(path + "/payload.raw", UnixFileMode.UserRead);
        return path;
    }

    [SupportedOSPlatform("linux")]
    private static void SuccessDeletesOnlyStaging(Proof proof)
    {
        Guid id = Guid.NewGuid(); using var lease = LinuxRecoveryStorageLease.Create(id);
        using (var held = LinuxRecoveryNative.OpenDirectory(lease.JournalParent, false)) LinuxRecoveryNative.SetGroup(held, 0, 0x1c0);
        string stage = Stage(lease), operation = Path.GetDirectoryName(lease.StagingParent)!;
        File.WriteAllText(lease.JournalParent + "/fixture-journal", "retained-durable-evidence");
        lease.Complete(State(id, true));
        Assert.False(Directory.Exists(stage)); Assert.False(Directory.Exists(lease.StagingParent));
        Assert.Equal("retained-durable-evidence", File.ReadAllText(lease.JournalParent + "/fixture-journal"));
        using var initial = JsonDocument.Parse(File.ReadAllBytes(operation + "/receipt.json"));
        using var final = JsonDocument.Parse(File.ReadAllBytes(operation + "/receipt.final.json"));
        Assert.True(initial.RootElement.GetProperty("stagingCleanupPending").GetBoolean());
        Assert.False(final.RootElement.GetProperty("stagingRetained").GetBoolean());
        Assert.False(final.RootElement.GetProperty("stagingCleanupPending").GetBoolean());
        Assert.Equal(proof.SourceCommit, final.RootElement.GetProperty("sourceCommit").GetString());
        Assert.Throws<InvalidOperationException>(() => lease.Complete(State(id, true)));
    }

    [SupportedOSPlatform("linux")]
    private static void FailureRetainsEvidence(Proof proof)
    {
        Guid id = Guid.NewGuid(); using var lease = LinuxRecoveryStorageLease.Create(id); string stage = Stage(lease);
        lease.Complete(State(id, false)); Assert.True(File.Exists(stage + "/payload.raw"));
        string operation = Path.GetDirectoryName(lease.StagingParent)!;
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(operation + "/receipt.json"));
        Assert.True(receipt.RootElement.GetProperty("stagingRetained").GetBoolean());
        Assert.False(receipt.RootElement.GetProperty("stagingCleanupPending").GetBoolean());
        Assert.Equal(proof.SourceCommit, receipt.RootElement.GetProperty("sourceCommit").GetString());
        Assert.False(File.Exists(operation + "/receipt.final.json"));
    }

    [SupportedOSPlatform("linux")]
    private static void ExistingOperationIsNeverAdopted(Proof proof)
    {
        Guid id = Guid.NewGuid(); using var lease = LinuxRecoveryStorageLease.Create(id);
        string stage = Stage(lease);
        Assert.Throws<IOException>(() => { using var duplicate = LinuxRecoveryStorageLease.Create(id); });
        Assert.Equal(65536L, new FileInfo(stage + "/payload.raw").Length);
        Assert.Equal(proof.Store.Inode, LinuxRecoveryNative.StatPath(LinuxRecoveryStorageLease.Store).Inode);
    }

    [SupportedOSPlatform("linux")]
    private static void HardlinkPreservesOriginal(Proof proof)
    {
        Guid id = Guid.NewGuid(); using var lease = LinuxRecoveryStorageLease.Create(id); string stage = Stage(lease);
        // link(2) rejects distinct mount IDs, even aliases of the same ext4 inode
        // tree. Keep both fixture names on the proved /srv bind mount, while the
        // original remains outside the operation whose staging may be removed.
        string original = LinuxRecoveryStorageLease.Store + "/hardlink-original-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(original, Enumerable.Repeat((byte)0x5a, 65536).ToArray()); File.SetUnixFileMode(original, UnixFileMode.UserRead);
        var originalIdentity = LinuxRecoveryNative.StatPath(original);
        Assert.Equal((proof.Store.DeviceMajor, proof.Store.DeviceMinor), (originalIdentity.DeviceMajor, originalIdentity.DeviceMinor));
        Assert.Equal(0U, originalIdentity.Owner); Assert.Equal((ushort)0x8100, originalIdentity.Mode);
        File.Delete(stage + "/payload.raw");
        if (LinkFile(original, stage + "/payload.raw") != 0)
            throw new IOException("fixture.hardlink-failed errno=" + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture));
        Assert.Equal(2U, LinuxRecoveryNative.StatPath(original).Links);
        Assert.Equal(2U, LinuxRecoveryNative.StatPath(stage + "/payload.raw").Links);
        Assert.Throws<IOException>(() => lease.Complete(State(id, true)));
        Assert.True(File.Exists(stage + "/image.metadata")); Assert.True(File.Exists(stage + "/payload.raw"));
        Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(stage + "/payload.raw"));
        Assert.All(File.ReadAllBytes(original), value => Assert.Equal((byte)0x5a, value));
    }

    [SupportedOSPlatform("linux")]
    private static void SymlinkPreservesOriginal(Proof proof)
    {
        Guid id = Guid.NewGuid(); using var lease = LinuxRecoveryStorageLease.Create(id); string stage = Stage(lease);
        string original = proof.Scratch + "/symlink-original"; File.WriteAllText(original, "preserved-original");
        File.Delete(stage + "/payload.raw"); File.CreateSymbolicLink(stage + "/payload.raw", original);
        Assert.Throws<InvalidDataException>(() => lease.Complete(State(id, true)));
        Assert.Equal("preserved-original", File.ReadAllText(original)); Assert.True(File.Exists(stage + "/image.metadata"));
    }

    [SupportedOSPlatform("linux")]
    private static void UnexpectedEntryPreventsEveryUnlink(Proof proof)
    {
        Guid id = Guid.NewGuid(); using var lease = LinuxRecoveryStorageLease.Create(id); string stage = Stage(lease);
        File.WriteAllText(stage + "/unexpected", "retain-all");
        Assert.Throws<IOException>(() => lease.Complete(State(id, true)));
        Assert.True(File.Exists(stage + "/payload.raw")); Assert.True(File.Exists(stage + "/image.metadata"));
        Assert.Equal("retain-all", File.ReadAllText(stage + "/unexpected"));
        Assert.Equal(proof.Store.DeviceMajor, LinuxRecoveryNative.StatPath(stage).DeviceMajor);
    }

    [SupportedOSPlatform("linux")]
    private static void ReplacedDirectoryCannotRedirectCleanup(Proof proof)
    {
        Guid id = Guid.NewGuid(); using var lease = LinuxRecoveryStorageLease.Create(id); string stage = Stage(lease);
        string saved = lease.StagingParent + ".saved", original = proof.Scratch + "/directory-original";
        Directory.CreateDirectory(original); File.WriteAllText(original + "/untouched", "preserved-original");
        Directory.Move(lease.StagingParent, saved); Directory.CreateSymbolicLink(lease.StagingParent, original);
        Assert.Throws<IOException>(() => lease.Complete(State(id, true)));
        Assert.Equal("preserved-original", File.ReadAllText(original + "/untouched"));
        Assert.True(File.Exists(stage.Replace(lease.StagingParent, saved, StringComparison.Ordinal) + "/payload.raw"));
    }

    private static void WriteProof(Proof proof, IReadOnlyList<string> cases)
    {
        Assert.NotEmpty(cases);
        var current = LinuxRecoveryNative.StatPath(proof.Results);
        Assert.Equal((proof.ResultsIdentity.DeviceMajor, proof.ResultsIdentity.DeviceMinor, proof.ResultsIdentity.Inode,
            proof.ResultsIdentity.Mode, proof.ResultsIdentity.Owner),
            (current.DeviceMajor, current.DeviceMinor, current.Inode, current.Mode, current.Owner));
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, scope = "owned-filesystem-storage-fixture", sourceCommit = proof.SourceCommit,
            compiledSourceCommit = proof.SourceCommit, checkoutRevision = proof.CheckoutRevision,
            executedCaseNames = cases, count = cases.Count,
            namespaces = new { outerMount = proof.OuterMount, currentMount = proof.PrivateMount, outerPid = proof.OuterPid, currentPid = proof.PrivatePid },
            mounts = new { srv = "tmpfs", store = "ext4", outerProc = "proc" },
            sourceInode = proof.Source.Inode, storeInode = proof.Store.Inode,
            sourceDeviceMajor = proof.Source.DeviceMajor, sourceDeviceMinor = proof.Source.DeviceMinor,
            storeDeviceMajor = proof.Store.DeviceMajor, storeDeviceMinor = proof.Store.DeviceMinor
        });
        using var file = new FileStream(proof.Results + "/native-storage-evidence.json", FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes); file.Flush(true);
        using var parent = LinuxRecoveryNative.Open(proof.Results, 0x10000 | 0x20000 | 0x80000); LinuxRecoveryNative.Sync(parent);
    }

    private static long FilesystemType(SafeFileHandle handle)
    { Assert.Equal(0, StatFs(handle, out var value)); return value.Type; }
    [StructLayout(LayoutKind.Explicit, Size = 120)] private struct Filesystem { [FieldOffset(0)] public long Type; }
    [DllImport("libc", EntryPoint = "fstatfs")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int StatFs(SafeFileHandle handle, out Filesystem value);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int LinkFile(string source, string destination);
}
