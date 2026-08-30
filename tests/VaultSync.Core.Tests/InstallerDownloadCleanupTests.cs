using System.IO;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.ViewModels;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class InstallerDownloadCleanupTests
{
    [Fact]
    public void TemporaryInstallerCleanup_RemovesOnlyTheRequestedFile()
    {
        using var root = new TempDirectory();
        string temporaryDownload = Path.Combine(root.Path, "VaultSync-Setup.exe.download");
        string completedInstaller = Path.Combine(root.Path, "VaultSync-Setup.exe");
        File.WriteAllText(temporaryDownload, "incomplete");
        File.WriteAllText(completedInstaller, "verified");

        bool removed = AppViewModel.TryDeleteInstallerTemporaryDownload(temporaryDownload);

        Assert.True(removed);
        Assert.False(File.Exists(temporaryDownload));
        Assert.Equal("verified", File.ReadAllText(completedInstaller));
    }

    [Fact]
    public void TemporaryInstallerCleanup_IsBestEffort()
    {
        using var root = new TempDirectory();

        Assert.True(AppViewModel.TryDeleteInstallerTemporaryDownload(null));
        Assert.True(AppViewModel.TryDeleteInstallerTemporaryDownload(
            Path.Combine(root.Path, "already-absent.download")));
        Assert.False(AppViewModel.TryDeleteInstallerTemporaryDownload(root.Path));
        Assert.True(Directory.Exists(root.Path));
    }
}
