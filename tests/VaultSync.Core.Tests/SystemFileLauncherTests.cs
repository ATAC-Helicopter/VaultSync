using VaultSync.UI.Infrastructure;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class SystemFileLauncherTests
{
    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    [InlineData("mailto")]
    [InlineData("ms-windows-store")]
    public void ExternalNavigationAllowsExpectedSchemes(string scheme)
    {
        Assert.True(SystemFileLauncher.IsAllowedExternalScheme(scheme));
    }

    [Theory]
    [InlineData("file")]
    [InlineData("javascript")]
    [InlineData("data")]
    [InlineData("shell")]
    public void ExternalNavigationRejectsUnsafeSchemes(string scheme)
    {
        Assert.False(SystemFileLauncher.IsAllowedExternalScheme(scheme));
    }
}
