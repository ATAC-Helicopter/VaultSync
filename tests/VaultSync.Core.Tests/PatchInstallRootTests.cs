using System;
using System.IO;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class PatchInstallRootTests
{
    [Fact]
    public void ResolveInstallRoot_PreservesOrdinaryRuntimeDirectory()
    {
        string runtimeDirectory = Path.GetFullPath(Path.Combine("runtime", "files"));

        Assert.Equal(runtimeDirectory, PatchInstallService.ResolveInstallRoot(runtimeDirectory));
    }

    [Fact]
    public void ResolveInstallRoot_UsesWholeApplicationBundleOnMacOs()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string bundle = Path.GetFullPath(Path.Combine("Applications", "VaultSync.app"));
        string runtimeDirectory = Path.Combine(bundle, "Contents", "MacOS");

        Assert.Equal(bundle, PatchInstallService.ResolveInstallRoot(runtimeDirectory));
    }

    [Theory]
    [InlineData("1.8.6", "1.8.6", "", "1.8.7", false)]
    [InlineData("1.8.7", "1.8.7", "", "1.8.7", true)]
    [InlineData("1.9.0", "1.9.2", "1.9.0-beta.2", "1.9.0-beta.2", true)]
    [InlineData("1.9.0", "1.9.1", "1.9.0-beta.2", "1.9.0-beta.2", false)]
    [InlineData("1.9.0", "1.9.2", "1.9.0-beta.1", "1.9.0-beta.2", false)]
    [InlineData("1.9.0", "1.9.999", "1.9.0", "1.9.0", true)]
    public void VerifyMacBundleIdentity_BindsReleaseAndBothVersionFields(
        string shortVersion, string buildVersion, string releaseVersion, string target, bool valid)
    {
        using var root = new TempDirectory();
        string bundle = Path.Combine(root.Path, "VaultSync.app");
        string macOs = Path.Combine(bundle, "Contents", "MacOS");
        Directory.CreateDirectory(macOs);
        File.WriteAllText(Path.Combine(macOs, "VaultSync.UI"), string.Empty);
        File.WriteAllText(
            Path.Combine(bundle, "Contents", "Info.plist"),
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>CFBundleShortVersionString</key><string>{shortVersion}</string>
              <key>CFBundleVersion</key><string>{buildVersion}</string>
              <key>VaultSyncReleaseVersion</key><string>{releaseVersion}</string>
              <key>CFBundleExecutable</key><string>VaultSync.UI</string>
            </dict></plist>
            """);

        if (valid)
            PatchInstallService.VerifyMacBundleIdentity(bundle, target);
        else
            Assert.Throws<InvalidDataException>(() => PatchInstallService.VerifyMacBundleIdentity(bundle, target));
    }
}
