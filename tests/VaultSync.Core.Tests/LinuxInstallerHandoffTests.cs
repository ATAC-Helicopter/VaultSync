using System;
using System.IO;
using VaultSync.Core.Tests.TestSupport;
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

    [Fact]
    public void ProtectedLinuxRuntime_RequiresInstallerFallback()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var root = new TempDirectory();
        string runtimeDirectory = Path.Combine(root.Path, "protected-runtime");
        Directory.CreateDirectory(runtimeDirectory);
        File.SetUnixFileMode(
            runtimeDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        try
        {
            Assert.True(AppViewModel.PatchInstallRequiresInstallerFallback(runtimeDirectory));
        }
        finally
        {
            File.SetUnixFileMode(
                runtimeDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void WritableLinuxRuntime_AllowsPatchInstall()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var root = new TempDirectory();
        string runtimeDirectory = Path.Combine(root.Path, "writable-runtime");
        Directory.CreateDirectory(runtimeDirectory);

        Assert.False(AppViewModel.PatchInstallRequiresInstallerFallback(runtimeDirectory));
    }
}
