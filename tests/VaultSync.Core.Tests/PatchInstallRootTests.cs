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

    [Fact]
    public void VerifyInstalledBundleIdentity_RejectsStaleMacOsVersionMetadata()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        using var root = new TempDirectory();
        string bundle = Path.Combine(root.Path, "VaultSync.app");
        string macOs = Path.Combine(bundle, "Contents", "MacOS");
        Directory.CreateDirectory(macOs);
        File.WriteAllText(Path.Combine(macOs, "VaultSync.UI"), string.Empty);
        File.WriteAllText(
            Path.Combine(bundle, "Contents", "Info.plist"),
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>CFBundleShortVersionString</key><string>1.8.6</string>
              <key>CFBundleVersion</key><string>1.8.6</string>
              <key>CFBundleExecutable</key><string>VaultSync.UI</string>
            </dict></plist>
            """);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            PatchInstallService.VerifyInstalledBundleIdentity(bundle, "1.8.7"));

        Assert.Contains("1.8.7", error.Message, StringComparison.Ordinal);
    }
}
