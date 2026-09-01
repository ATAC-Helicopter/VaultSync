using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxInstallerHandoffTests
{
    [Fact]
    public void SuccessfulDebianInstall_AllowsShutdownOnlyAfterCompletion()
    {
        AppViewModel.InstallerLaunchResult result = AppViewModel.ClassifyDebianInstallerExitCode(0);

        Assert.True(result.Success);
        Assert.True(result.Completed);
        Assert.True(result.ShouldShutdown);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(126)]
    [InlineData(127)]
    public void FailedOrCancelledDebianInstall_KeepsApplicationRunning(int exitCode)
    {
        AppViewModel.InstallerLaunchResult result = AppViewModel.ClassifyDebianInstallerExitCode(exitCode);

        Assert.False(result.Success);
        Assert.True(result.Completed);
        Assert.False(result.ShouldShutdown);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }
}
