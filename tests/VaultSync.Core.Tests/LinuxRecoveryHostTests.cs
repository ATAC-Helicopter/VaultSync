#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryHostTests
{
    private static LinuxRecoveryMediaInstallation.BootFacts Valid => new(true, 1, true, true, true, true, true, true,
        "0::" + LinuxRecoveryMediaInstallation.ServiceGroup + "\n");

    [Fact]
    public void AdmissionRequiresCombinedNativeBootFacts()
    {
        LinuxRecoveryMediaInstallation.ValidateHostBoot(Valid);
        var invalid = new List<LinuxRecoveryMediaInstallation.BootFacts>
        {
            Valid with { RootIdentity = false }, Valid with { Parent = 42 }, Valid with { ReadOnlyRoot = false },
            Valid with { SameMount = false }, Valid with { SameNetwork = false }, Valid with { InitialUser = false },
            Valid with { SystemdMatches = false }, Valid with { NetworkDown = false },
            Valid with { Group = "0::/system.slice/ordinary.service\n" }, Valid with { Group = "" }
        };
        foreach (var value in invalid)
            Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.ValidateHostBoot(value));
    }

    [Fact]
    public void ReadonlyMountOrUnitNameAloneCannotAuthorizeEntry()
    {
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.ValidateHostBoot(default));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryMediaInstallation.ValidateHostBoot(default(LinuxRecoveryMediaInstallation.BootFacts)
            with { ReadOnlyRoot = true, Group = Valid.Group }));
    }

    [Fact]
    public async Task UnsupportedPlatformRefusesBeforeCreatingRuntimeOrStore()
    {
        if (OperatingSystem.IsLinux()) return;
        await Assert.ThrowsAsync<InvalidDataException>(() => LinuxRecoveryHost.RunAsync(CancellationToken.None));
    }
}
