#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryStorageLeaseTests
{
    private static readonly LinuxRecoveryNative.Identity Parent = new(0x41c0, 0, 0, 2, 10, 0, 8, 1, 0, 0, 0, 0);
    private static Dictionary<string, LinuxRecoveryNative.Identity> Files() => new(StringComparer.Ordinal)
    {
        ["image.metadata"] = new(0x8100, 0, 0, 1, 11, 596, 8, 1, 0, 0, 0, 0),
        ["payload.raw"] = new(0x8100, 0, 0, 1, 12, 65536, 8, 1, 0, 0, 0, 0)
    };

    [Fact]
    public void OnlyExactSealedOwnedFilesCanBecomeDeletionPlan()
    {
        LinuxRecoveryStorageLease.ValidateStagingFiles(Files(), Parent, 65536);
        foreach (string name in new[] { "image.metadata", "payload.raw" })
        {
            var files = Files(); files.Remove(name);
            Assert.Throws<IOException>(() => LinuxRecoveryStorageLease.ValidateStagingFiles(files, Parent, 65536));
        }
        var extra = Files(); extra["personal.backup"] = extra["payload.raw"];
        Assert.Throws<IOException>(() => LinuxRecoveryStorageLease.ValidateStagingFiles(extra, Parent, 65536));
    }

    [Fact]
    public void HardlinksAliasesOtherOwnersDevicesAndWritableFilesRefuseBeforeUnlink()
    {
        var baseFile = Files()["payload.raw"];
        foreach (var changed in new[]
        {
            baseFile with { Links = 2 }, baseFile with { Owner = 60999 }, baseFile with { DeviceMinor = 2 },
            baseFile with { Mode = 0x8180 }, baseFile with { Mode = 0xa100 }, baseFile with { Size = 65535 },
            baseFile with { Inode = 11 }
        })
        {
            var files = Files(); files["payload.raw"] = changed;
            Assert.Throws<IOException>(() => LinuxRecoveryStorageLease.ValidateStagingFiles(files, Parent, 65536));
        }
    }

    [Theory]
    [InlineData(LinuxRecoverySessionPhase.Faulted)] [InlineData(LinuxRecoverySessionPhase.Refused)]
    [InlineData(LinuxRecoverySessionPhase.Executing)] [InlineData(LinuxRecoverySessionPhase.Completed)]
    public void MissingDurableOutcomeAlwaysRetainsEvidence(object phase)
    {
        var state = new LinuxRecoverySession.Snapshot((LinuxRecoverySessionPhase)phase, false, null, null, 0, null, null);
        Assert.False(LinuxRecoveryStorageLease.Successful(state));
    }
}
