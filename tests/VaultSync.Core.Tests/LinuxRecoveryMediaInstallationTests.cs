#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryMediaInstallationTests
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string Hash = new('b', 64);

    internal static Dictionary<string, object> Manifest()
    {
        var files = new List<Dictionary<string, object>>();
        void Add(string path, bool executable = false) => files.Add(new()
        { ["path"] = path, ["size"] = 1, ["sha256"] = Hash, ["executable"] = executable });
        foreach (var pair in new[] { ("host", "VaultSync.RecoveryHost"), ("controller", "VaultSync.RecoveryController") })
        {
            foreach (string name in new[] { pair.Item2, pair.Item2 + ".dll", pair.Item2 + ".deps.json", pair.Item2 + ".runtimeconfig.json",
                "VaultSync.Core.dll", "libhostfxr.so", "libhostpolicy.so", "libcoreclr.so", "System.Private.CoreLib.dll" })
                Add(pair.Item1 + "/" + name, name == pair.Item2);
        }
        Add("host/DiskWorker/worker.installation.json"); Add("host/DiskWorker/VaultSync.DiskWorker", true);
        Add("host/DiskWorker/VaultSync.Core.dll"); Add("launch/recovery-controller-launch", true);
        Add("boot/vaultsync-recovery.service"); Add("catalogs/strings.en.json");
        return new()
        {
            ["schemaVersion"] = 1, ["role"] = "offline-recovery-media-v1", ["runtimeIdentifier"] = "linux-x64",
            ["sourceCommit"] = Commit, ["consoleCulture"] = "en", ["systemdSha256"] = Hash,
            ["files"] = files.OrderBy(f => (string)f["path"], StringComparer.Ordinal).ToList()
        };
    }
    private static byte[] Encode(Dictionary<string, object> value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static List<Dictionary<string, object>> Files(Dictionary<string, object> value) => (List<Dictionary<string, object>>)value["files"];

    private static Dictionary<string, object> GraphicalManifest()
    {
        var value = Manifest(); value["schemaVersion"] = 2; value["controllerMode"] = "graphical"; value["xorgSha256"] = Hash;
        Files(value).Add(new() { ["path"] = "launch/recovery-display-launch", ["size"] = 1, ["sha256"] = Hash, ["executable"] = true });
        Files(value).Add(new() { ["path"] = "boot/vaultsync-recovery-display.service", ["size"] = 1, ["sha256"] = Hash, ["executable"] = false });
        value["files"] = Files(value).OrderBy(file => (string)file["path"], StringComparer.Ordinal).ToList(); return value;
    }

    [Fact]
    public void GraphicalModeIsImmutableAndRequiresDisplayClosureAndExecutableIdentity()
    {
        var decoded = LinuxRecoveryMediaInstallation.Decode(Encode(GraphicalManifest()), Commit);
        Assert.Equal("graphical", decoded.ControllerMode); Assert.Equal(Hash, decoded.XorgHash);
        Assert.Equal("headless", LinuxRecoveryMediaInstallation.Decode(Encode(Manifest()), Commit).ControllerMode);
        foreach (string missing in new[] { "launch/recovery-display-launch", "boot/vaultsync-recovery-display.service" })
        {
            var value = GraphicalManifest(); Files(value).RemoveAll(file => (string)file["path"] == missing);
            Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(value), Commit));
        }
        var wrong = GraphicalManifest(); wrong["xorgSha256"] = "invalid";
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(wrong), Commit));
        wrong = GraphicalManifest(); wrong["controllerMode"] = "headless";
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(wrong), Commit));
    }

    [Theory]
    [InlineData("valid")] [InlineData("runtime-owner")] [InlineData("runtime-write")]
    [InlineData("cookie-owner")] [InlineData("cookie-write")] [InlineData("cookie-hardlink")]
    [InlineData("tmp-owner")] [InlineData("socket-owner")] [InlineData("socket-link")]
    public void DisplayAdmissionRequiresRootOwnedCookieAndSeparatePrivateControllerTemporaryStorage(string change)
    {
        static LinuxRecoveryNative.Identity State(ushort mode, uint owner, uint group, ulong size = 0) => new(mode, owner, group, 1, 1, size, 0, 5, 0, 0, 0, 0);
        var facts = new LinuxRecoveryMediaInstallation.DisplayFacts(State(0x41c8, 0, 60999), State(0x81a0, 0, 60999, 45),
            State(0x41c0, 60999, 60999), State(0x43ff, 0, 0), State(0xc1ff, 0, 0));
        facts = change switch
        {
            "runtime-owner" => facts with { Runtime = facts.Runtime with { Owner = 60999 } },
            "runtime-write" => facts with { Runtime = facts.Runtime with { Mode = 0x41d8 } },
            "cookie-owner" => facts with { Authority = facts.Authority with { Owner = 60999 } },
            "cookie-write" => facts with { Authority = facts.Authority with { Mode = 0x81b0 } },
            "cookie-hardlink" => facts with { Authority = facts.Authority with { Links = 2 } },
            "tmp-owner" => facts with { Temporary = facts.Temporary with { Owner = 0 } },
            "socket-owner" => facts with { Socket = facts.Socket with { Owner = 60999 } },
            "socket-link" => facts with { Socket = facts.Socket with { Mode = 0xa1ff } },
            _ => facts
        };
        if (change == "valid") LinuxRecoveryMediaInstallation.ValidateDisplay(facts);
        else Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.ValidateDisplay(facts));
    }

    [Fact]
    public void CompleteExactRoleClosureDecodesWithSeparatePublisherGate()
    {
        var decoded = LinuxRecoveryMediaInstallation.Decode(Encode(Manifest()), Commit);
        Assert.Equal(Commit, decoded.Commit); Assert.Equal("en", decoded.Culture);
        Assert.Contains(decoded.Files, f => f.Path == "host/DiskWorker/worker.installation.json");
        Assert.Equal("offline-recovery-supervisor-v1", LinuxRecoveryMediaInstallation.RoleName(LinuxRecoveryMediaInstallation.Role.Host));
        Assert.Equal("recovery-controller-v1", LinuxRecoveryMediaInstallation.RoleName(LinuxRecoveryMediaInstallation.Role.Controller));
    }

    [Theory]
    [InlineData(LinuxRecoveryMediaInstallation.Role.Host, "/usr/lib/vaultsync-recovery/host/VaultSync.RecoveryHost", "offline-recovery-supervisor-v1")]
    [InlineData(LinuxRecoveryMediaInstallation.Role.Controller, "/usr/lib/vaultsync-recovery/controller/VaultSync.RecoveryController", "recovery-controller-v1")]
    public void InstallationIdentityMatchesCompiledSourceAndFixedRoleWithoutClaimingBootAuthority(object roleValue, string program, string role)
    {
        var value = (LinuxRecoveryMediaInstallation.Role)roleValue;
        Assert.Equal(program, LinuxRecoveryMediaInstallation.ProgramPath(value));
        using var document = JsonDocument.Parse(LinuxRecoveryMediaInstallation.BuildIdentity(value));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(role, document.RootElement.GetProperty("role").GetString());
        Assert.Equal("linux-x64", document.RootElement.GetProperty("runtimeIdentifier").GetString());
        Assert.Equal(DiskWorkerBuildIdentity.SourceCommit, document.RootElement.GetProperty("sourceCommit").GetString());
        Assert.False(document.RootElement.TryGetProperty("authority", out _));
    }

    [Theory]
    [InlineData("role", "read-only-inspection-v1")]
    [InlineData("runtimeIdentifier", "linux-arm64")]
    [InlineData("sourceCommit", "cccccccccccccccccccccccccccccccccccccccc")]
    [InlineData("consoleCulture", "unqualified-language")]
    [InlineData("systemdSha256", "invalid")]
    public void WrongRoleSourceRuntimeAndBootFactsRefuse(string key, string value)
    {
        var manifest = Manifest(); manifest[key] = value;
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(manifest), Commit));
    }

    [Fact]
    public void UnknownDuplicateMissingAndConflictingRecordsRefuse()
    {
        var unknown = Manifest(); unknown["authority"] = true;
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(unknown), Commit));
        byte[] duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Encode(Manifest())).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal));
        Assert.Throws<JsonException>(() => LinuxRecoveryMediaInstallation.Decode(duplicate, Commit));
        var missing = Manifest(); Files(missing).RemoveAll(f => (string)f["path"] == "host/VaultSync.Core.dll");
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(missing), Commit));
        var conflict = Manifest(); Files(conflict).Single(f => (string)f["path"] == "controller/VaultSync.Core.dll")["sha256"] = new string('c', 64);
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(conflict), Commit));
        var repeated = Manifest(); Files(repeated).Insert(1, Files(repeated)[0]);
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(repeated), Commit));
    }

    [Theory]
    [InlineData("../host/program")] [InlineData("/host/program")] [InlineData("host/../program")]
    [InlineData("host/.hidden")] [InlineData("host/a/b/c/d")] [InlineData("other/program")]
    [InlineData("host/space name")] [InlineData("host/a\\b")]
    public void PathAliasesCannotSelectAnotherClosure(string path) => Assert.False(LinuxRecoveryMediaInstallation.RelativePath(path));

    [Fact]
    public void FileIdentityRequiresRootSingleLinkAndNoPrivilegeOrSharedWriteBits()
    {
        var valid = new LinuxRecoveryNative.Identity(0x81a4, 0, 0, 1, 1, 10, 8, 1, 0, 0, 0, 0);
        Assert.True(LinuxRecoveryNative.TrustedFile(valid, false));
        Assert.False(LinuxRecoveryNative.TrustedFile(valid with { Owner = 60999 }, false));
        Assert.False(LinuxRecoveryNative.TrustedFile(valid with { Links = 2 }, false));
        Assert.False(LinuxRecoveryNative.TrustedFile(valid with { Mode = 0x81b4 }, false));
        Assert.False(LinuxRecoveryNative.TrustedFile(valid with { Mode = 0x89a4 }, false));
        Assert.False(LinuxRecoveryNative.TrustedFile(valid with { Mode = 0xa1a4 }, false));
        Assert.False(LinuxRecoveryNative.TrustedFile(valid, true));
    }

    [Fact]
    public void ManifestAndFileBoundsRejectUnqualifiedOrDamagedInstallations()
    {
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(new byte[1024 * 1024 + 1], Commit));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(Manifest()), Commit.ToUpperInvariant()));
        foreach (long size in new[] { 0L, -1L, LinuxRecoveryMediaInstallation.MaximumFile + 1 })
        {
            var value = Manifest(); Files(value)[0]["size"] = size;
            Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(value), Commit));
        }
        var total = Manifest();
        foreach (var file in Files(total)) file["size"] = LinuxRecoveryMediaInstallation.MaximumFile;
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(total), Commit));
        var empty = Manifest(); Files(empty).Clear();
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(empty), Commit));
        var unknown = Manifest(); Files(unknown)[0]["permission"] = true;
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(unknown), Commit));
        var invalidFlag = Manifest(); Files(invalidFlag)[0]["executable"] = "true";
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(invalidFlag), Commit));
        var invalidHash = Manifest(); Files(invalidHash)[0]["sha256"] = Hash.ToUpperInvariant();
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.Decode(Encode(invalidHash), Commit));
    }
}
