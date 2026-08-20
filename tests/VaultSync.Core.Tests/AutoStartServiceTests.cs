using System.IO;
using VaultSync.UI.Infrastructure;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class AutoStartServiceTests
{
    [Fact]
    public void ResolveMacUserHome_PrefersUserProfileOverDocumentsAndEnvironmentFallbacks()
    {
        string expected = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "Users", "test"));

        string actual = AutoStartService.ResolveMacUserHome(
            expected,
            Path.Combine(expected, "fallback"));

        Assert.Equal(expected, actual);
        Assert.Equal(
            Path.Combine(expected, "Library", "LaunchAgents"),
            Path.Combine(actual, "Library", "LaunchAgents"));
    }

    [Fact]
    public void ResolveMacUserHome_UsesHomeEnvironmentWhenUserProfileIsUnavailable()
    {
        string expected = Path.GetFullPath(Path.Combine(Path.DirectorySeparatorChar.ToString(), "Users", "fallback"));

        string actual = AutoStartService.ResolveMacUserHome(null, expected);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LaunchCtlInspection_IsSilentAndDoesNotUseTheShell()
    {
        var startInfo = AutoStartService.CreateLaunchCtlStartInfo("print gui/501/com.vaultsync.autostart");

        Assert.Equal("launchctl", startInfo.FileName);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Theory]
    [InlineData("/Users/test/source/VaultSync/src/VaultSync.UI/bin/Debug/net10.0/")]
    [InlineData("C:\\source\\VaultSync\\src\\VaultSync.UI\\bin\\Release\\net10.0\\publish")]
    public void DevelopmentOutputDirectory_IsRecognized(string directory)
    {
        Assert.True(AutoStartService.IsDevelopmentOutputDirectory(directory));
    }

    [Theory]
    [InlineData("/Applications/VaultSync.app/Contents/MacOS/")]
    [InlineData("C:\\Program Files\\VaultSync\\")]
    [InlineData("/opt/vaultsync/")]
    public void InstalledOutputDirectory_IsNotTreatedAsDevelopment(string directory)
    {
        Assert.False(AutoStartService.IsDevelopmentOutputDirectory(directory));
    }
}
