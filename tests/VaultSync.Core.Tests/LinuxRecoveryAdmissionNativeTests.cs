#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

[CollectionDefinition("OwnedRecoveryAdmission", DisableParallelization = true)]
public sealed class OwnedRecoveryAdmissionCollection { }

/// <summary>Actual negative closure admission, never an installed boot or admitted lease.</summary>
[Collection("OwnedRecoveryAdmission")]
public sealed class LinuxRecoveryAdmissionNativeTests
{
    private const string Control = "/fixture-control", Results = "/results";
    private static readonly byte[] Content = { 120 };

    [Fact]
    public async Task ActualReadonlyClosureRefusalAndCancellationReleaseEveryHeldFileBeforeRetry()
    {
        string? request = Environment.GetEnvironmentVariable("VS_ADMISSION_NATIVE_REQUIRED");
        if (request is null) return; // No native claim without the wrapper's executed-case receipt.
        Assert.Equal("1", request);
        Assert.True(OperatingSystem.IsLinux());
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        await ExerciseAsync();
    }

    [SupportedOSPlatform("linux")]
    private static async Task ExerciseAsync()
    {
        var proof = ProveFixture();
        var cases = new List<string>();
        foreach (string scenario in new[] { "actual-core-location", "undeclared-file", "symlink", "payload-hash", "hardlink",
            "shared-write", "systemd-hash", "unit-hash", "pre-cancel", "enumerated-cancel" })
        {
            Reset(proof.Commit);
            string installation = Control + LinuxRecoveryMediaInstallation.Root;
            string payload = installation + "/catalogs/strings.en.json";
            switch (scenario)
            {
                case "undeclared-file": File.WriteAllBytes(installation + "/host/undeclared", Content); break;
                case "symlink": File.Delete(payload); File.CreateSymbolicLink(payload, "/etc/passwd"); break;
                case "payload-hash": File.WriteAllBytes(payload, new byte[] { 121 }); break;
                case "hardlink":
                    Assert.Equal(0, Link(payload, Control + "/held-original"));
                    Assert.Equal(2U, LinuxRecoveryNative.StatPath(payload).Links); break;
                case "shared-write": File.SetUnixFileMode(payload, UnixFileMode.UserRead | UnixFileMode.GroupWrite); break;
                case "systemd-hash": File.WriteAllBytes(Control + LinuxRecoveryMediaInstallation.Systemd, new byte[] { 121 }); break;
                case "unit-hash": File.WriteAllBytes(Control + LinuxRecoveryMediaInstallation.Unit, new byte[] { 121 }); break;
            }
            using var stop = new CancellationTokenSource();
            if (scenario == "pre-cancel") stop.Cancel();
            var previous = Console.Error;
            using var diagnostic = new AdmissionWriter(() => { if (scenario == "enumerated-cancel") stop.Cancel(); });
            Exception? failure;
            try
            {
                Console.SetError(diagnostic);
                failure = await Record.ExceptionAsync(async () =>
                {
                    using var unexpected = await LinuxRecoveryMediaInstallation.OpenAsync(
                        LinuxRecoveryMediaInstallation.Role.Controller, stop.Token);
                });
            }
            finally { Console.SetError(previous); }
            if (scenario is "pre-cancel" or "enumerated-cancel") Assert.IsAssignableFrom<OperationCanceledException>(failure);
            else Assert.IsType<InvalidDataException>(failure);
            string observed = diagnostic.ToString();
            if (scenario == "pre-cancel") Assert.Empty(observed);
            else Assert.Contains("VSREC1|2|112|0\n", observed, StringComparison.Ordinal);
            bool enumerated = scenario is not ("pre-cancel" or "undeclared-file" or "symlink");
            Assert.Equal(enumerated, observed.Contains("VSREC1|2|113|0\n", StringComparison.Ordinal));
            bool hashed = scenario is "actual-core-location" or "systemd-hash" or "unit-hash";
            Assert.Equal(hashed, observed.Contains("VSREC1|2|114|0\n", StringComparison.Ordinal));
            Assert.DoesNotContain("VSREC1|2|115|0\n", observed, StringComparison.Ordinal);
            Assert.Empty(HeldInstallationFiles());
            Assert.Empty(Directory.EnumerateFileSystemEntries("/run"));
            Assert.Empty(Directory.EnumerateFileSystemEntries("/srv"));
            Assert.Equal(proof.Root, Identity("/")); Assert.Equal(proof.Root, Identity(Control));
            if (scenario == "hardlink") Assert.Equal(Content, File.ReadAllBytes(Control + "/held-original"));
            cases.Add(scenario);
        }
        // A repaired closure still refuses the test assembly's real location. No
        // reflection, fabricated lease, role bypass or synthetic boot facts enter admission.
        Reset(proof.Commit);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            using var unexpected = await LinuxRecoveryMediaInstallation.OpenAsync(
                LinuxRecoveryMediaInstallation.Role.Host, CancellationToken.None);
        });
        Assert.Empty(HeldInstallationFiles());
        Assert.Empty(Directory.EnumerateFileSystemEntries("/run"));
        Assert.Empty(Directory.EnumerateFileSystemEntries("/srv"));
        cases.Add("repaired-closure-still-refuses-real-core");
        string receipt = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, scope = "negative-readonly-closure-admission-fixture",
            sourceCommit = proof.Commit, compiledSourceCommit = proof.Commit, checkoutRevision = proof.Commit,
            count = cases.Count, executedCaseNames = cases,
            namespaces = new { outerMount = proof.OuterMount, currentMount = proof.CurrentMount,
                outerPid = proof.OuterPid, currentPid = proof.CurrentPid },
            rootIdentity = proof.Root, readonlyExt4Root = true, actualCoreLocationRefused = true,
            runtimeAndStoreUnchanged = true, heldInstallationFilesAfterRetry = HeldInstallationFiles().Length
        });
        Assert.True(receipt.Length < 65536);
        File.WriteAllText(Results + "/native-admission-evidence.json", receipt);
    }

    private sealed record Proof(string Commit, string OuterMount, string CurrentMount, string OuterPid, string CurrentPid,
        string Root);

    [SupportedOSPlatform("linux")]
    private static Proof ProveFixture()
    {
        Assert.Equal(Architecture.X64, RuntimeInformation.ProcessArchitecture);
        Assert.True(LinuxRecoveryNative.RootIdentity); Assert.True(LinuxRecoveryNative.InitialUserNamespace());
        string outerMount = Namespace("/outer-proc/1/ns/mnt", "mnt:");
        string outerPid = Namespace("/outer-proc/1/ns/pid", "pid:");
        string currentMount = Namespace("/proc/self/ns/mnt", "mnt:");
        string currentPid = Namespace("/proc/self/ns/pid", "pid:");
        Assert.NotEqual(outerMount, currentMount); Assert.NotEqual(outerPid, currentPid);
        Assert.Equal(currentMount, Namespace("/proc/1/ns/mnt", "mnt:"));
        Assert.Equal(currentPid, Namespace("/proc/1/ns/pid", "pid:"));
        Assert.True(LinuxRecoveryNative.ReadOnlyFilesystem("/"));
        using var control = LinuxRecoveryNative.OpenDirectory(Control, false);
        Assert.True(LinuxRecoveryNative.Ext4(control));
        Assert.Equal(Identity("/"), Identity(Control));
        Assert.Equal((ushort)0x41ed, LinuxRecoveryNative.Stat(control).Mode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(LinuxRecoveryMediaInstallation.Root));
        Assert.Empty(Directory.EnumerateFileSystemEntries("/run")); Assert.Empty(Directory.EnumerateFileSystemEntries("/srv"));
        Assert.NotEqual(LinuxRecoveryMediaInstallation.ProgramPath(LinuxRecoveryMediaInstallation.Role.Controller), Environment.ProcessPath);
        Assert.NotEqual(LinuxRecoveryMediaInstallation.Root + "/controller/VaultSync.Core.dll",
            typeof(LinuxRecoveryMediaInstallation).Assembly.Location);
        string commit = Environment.GetEnvironmentVariable("VS_ADMISSION_NATIVE_SOURCE") ?? throw new InvalidDataException("fixture.source-missing");
        Assert.Equal(40, commit.Length); Assert.Equal(commit, DiskWorkerBuildIdentity.SourceCommit);
        string[] mounts = File.ReadAllLines("/proc/self/mountinfo");
        Assert.Contains(mounts, line => Mounted(line, "/", "ext4", "ro"));
        Assert.Contains(mounts, line => Mounted(line, Control, "ext4", "rw"));
        Assert.Contains(mounts, line => Mounted(line, "/proc", "proc", "ro"));
        Assert.Contains(mounts, line => Mounted(line, "/outer-proc", "proc", "ro"));
        Assert.Contains(mounts, line => Mounted(line, "/run", "tmpfs", "rw"));
        Assert.Contains(mounts, line => Mounted(line, "/srv", "tmpfs", "rw"));
        return new(commit, outerMount, currentMount, outerPid, currentPid, Identity("/"));
    }

    private static bool Mounted(string line, string path, string filesystem, string access)
    {
        string[] halves = line.Split(" - ", StringSplitOptions.None);
        if (halves.Length != 2) return false;
        string[] fields = halves[0].Split(' '), detail = halves[1].Split(' ');
        return fields.Length > 5 && detail.Length > 0 && fields[4] == path && fields[5].Split(',').Contains(access) && detail[0] == filesystem;
    }

    private static string Namespace(string path, string prefix)
    {
        string value = new FileInfo(path).LinkTarget ?? throw new InvalidDataException("fixture.namespace-missing");
        Assert.StartsWith(prefix, value, StringComparison.Ordinal); return value;
    }

    private static string Identity(string path)
    {
        var state = LinuxRecoveryNative.StatPath(path);
        Assert.Equal(0U, state.Owner);
        return string.Create(CultureInfo.InvariantCulture, $"{state.DeviceMajor}:{state.DeviceMinor}:{state.Inode}");
    }

    private static string[] HeldInstallationFiles() => Directory.EnumerateFileSystemEntries("/proc/self/fd")
        .Select(path => new FileInfo(path).LinkTarget).Where(path => path is not null &&
            (path == LinuxRecoveryMediaInstallation.Root || path.StartsWith(LinuxRecoveryMediaInstallation.Root + "/", StringComparison.Ordinal) ||
            path == LinuxRecoveryMediaInstallation.Systemd || path == LinuxRecoveryMediaInstallation.Unit ||
            path == Path.GetDirectoryName(LinuxRecoveryMediaInstallation.Systemd) ||
            path == Path.GetDirectoryName(LinuxRecoveryMediaInstallation.Unit))).Cast<string>().ToArray();

    [SupportedOSPlatform("linux")]
    private static void Reset(string commit)
    {
        string installation = Control + LinuxRecoveryMediaInstallation.Root;
        Directory.CreateDirectory(installation + "/host");
        File.Delete(installation + "/host/undeclared"); File.Delete(Control + "/held-original");
        var manifest = LinuxRecoveryMediaInstallationTests.Manifest();
        manifest["sourceCommit"] = commit;
        string hash = Convert.ToHexStringLower(SHA256.HashData(Content));
        manifest["systemdSha256"] = hash;
        foreach (var entry in (List<Dictionary<string, object>>)manifest["files"])
        {
            entry["sha256"] = hash;
            string path = installation + "/" + (string)entry["path"];
            Directory.CreateDirectory(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Delete(path); File.WriteAllBytes(path, Content);
            File.SetUnixFileMode(path, (bool)entry["executable"] ? (UnixFileMode)0x16d : (UnixFileMode)0x124);
        }
        foreach (string path in new[] { Control + LinuxRecoveryMediaInstallation.Systemd, Control + LinuxRecoveryMediaInstallation.Unit })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Content);
            File.SetUnixFileMode(path, path.EndsWith("/systemd", StringComparison.Ordinal) ? (UnixFileMode)0x16d : (UnixFileMode)0x124);
        }
        File.WriteAllBytes(installation + "/" + LinuxRecoveryMediaInstallation.ManifestName, JsonSerializer.SerializeToUtf8Bytes(manifest));
        File.SetUnixFileMode(installation + "/" + LinuxRecoveryMediaInstallation.ManifestName, (UnixFileMode)0x124);
    }

    private sealed class AdmissionWriter(Action enumerated) : StringWriter(CultureInfo.InvariantCulture)
    {
        public override void Write(string? value)
        {
            base.Write(value);
            if (value == "VSREC1|2|113|0\n") enumerated();
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Link(string existing, string created);
}
