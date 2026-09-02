using System;
using System.Diagnostics;
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
        Assert.False(result.RelaunchAfterShutdown);
        Assert.Null(result.RelaunchPath);
    }

    [Fact]
    public void SuccessfulDebianInstall_RestartsInstalledExecutableWhenKnown()
    {
        AppViewModel.InstallerLaunchResult result = AppViewModel.ClassifyDebianInstallerExitCode(
            0,
            "/opt/vaultsync/VaultSync.UI");

        Assert.True(result.Success);
        Assert.True(result.Completed);
        Assert.True(result.ShouldShutdown);
        Assert.True(result.RelaunchAfterShutdown);
        Assert.Equal("/opt/vaultsync/VaultSync.UI", result.RelaunchPath);
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
        Assert.False(result.RelaunchAfterShutdown);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
    }

    [Fact]
    public void DeferredRelaunch_WaitsForCurrentProcessBeforeStartingExecutable()
    {
        ProcessStartInfo startInfo = AppViewModel.CreateDeferredRelaunchStartInfo(
            "/opt/vaultsync/VaultSync.UI",
            12345);

        Assert.Equal("/bin/sh", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal("/opt/vaultsync", startInfo.WorkingDirectory);
        Assert.Contains("kill -0", string.Join(" ", startInfo.ArgumentList));
        Assert.Contains("12345", startInfo.ArgumentList);
        Assert.Contains("/opt/vaultsync/VaultSync.UI", startInfo.ArgumentList);
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

    [Fact]
    public void MacOsDiskImageInstaller_DoesNotCloseRunningApplication()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        Assert.False(AppViewModel.InstallerMediaRequiresShutdown("/tmp/VaultSync-1.8.8-macos-apple-silicon.dmg"));
    }

    [Theory]
    [InlineData("/tmp/VaultSync-1.8.8-windows-x64-setup.exe")]
    [InlineData("/tmp/VaultSync-1.8.8-linux-x64.AppImage")]
    public void NonDiskImageInstaller_ClosesRunningApplication(string installerPath)
    {
        Assert.True(AppViewModel.InstallerMediaRequiresShutdown(installerPath));
    }
}
