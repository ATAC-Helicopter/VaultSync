using System;
using System.IO;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class MacOsLegacyBundleMigrationServiceTests
{
    [Theory]
    [InlineData("VaultSync-macos-arm64.app")]
    [InlineData("VaultSync-macos-x64.app")]
    [InlineData("vaultsync-MACOS-ARM64.APP")]
    public void RecognizesOnlySupportedLegacyBundleNames(string bundleName)
    {
        Assert.True(MacOsLegacyBundleMigrationService.IsLegacyBundleName(bundleName));
        Assert.False(MacOsLegacyBundleMigrationService.IsLegacyBundleName("VaultSync.app"));
        Assert.False(MacOsLegacyBundleMigrationService.IsLegacyBundleName("Other.app"));
    }

    [Fact]
    public void ResolvesLegacyBundleOnlyFromItsContentsMacOsDirectory()
    {
        string bundle = Path.GetFullPath(Path.Combine("Applications", "VaultSync-macos-arm64.app"));
        string runtime = Path.Combine(bundle, "Contents", "MacOS");

        Assert.Equal(bundle, MacOsLegacyBundleMigrationService.ResolveLegacyBundle(runtime));
        Assert.Null(MacOsLegacyBundleMigrationService.ResolveLegacyBundle(Path.Combine(bundle, "Contents")));
        Assert.Null(MacOsLegacyBundleMigrationService.ResolveLegacyBundle(
            Path.Combine("Applications", "VaultSync.app", "Contents", "MacOS")));
    }

    [Theory]
    [InlineData("1.8.7")]
    [InlineData("1.8.7+abcdef")]
    public void MigrationRunsOnlyForTheTransitionBuild(string version)
    {
        Assert.True(MacOsLegacyBundleMigrationService.IsTransitionBuild(version));
        Assert.False(MacOsLegacyBundleMigrationService.IsTransitionBuild("1.8.6"));
        Assert.False(MacOsLegacyBundleMigrationService.IsTransitionBuild("1.8.8"));
    }
}
