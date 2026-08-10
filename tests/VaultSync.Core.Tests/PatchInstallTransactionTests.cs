using System;
using System.Collections.Generic;
using System.IO;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class PatchInstallTransactionTests
{
    [Fact]
    public void CopyIntoInstall_RollsBackReplacedAndCreatedFiles_WhenInstallFails()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vaultsync-patch-test-{Guid.NewGuid():N}");
        string stagingDir = Path.Combine(root, "staging");
        string installDir = Path.Combine(root, "install");

        try
        {
            Directory.CreateDirectory(stagingDir);
            Directory.CreateDirectory(installDir);
            File.WriteAllText(Path.Combine(stagingDir, "existing.txt"), "updated");
            Directory.CreateDirectory(Path.Combine(stagingDir, "new"));
            File.WriteAllText(Path.Combine(stagingDir, "new", "created.txt"), "created");
            File.WriteAllText(Path.Combine(installDir, "existing.txt"), "original");

            var manifest = new PatchManifest
            {
                Files = new List<PatchFileEntry>
                {
                    new() { RelativePath = "existing.txt" },
                    new() { RelativePath = "new/created.txt" }
                }
            };
            int replacements = 0;

            Assert.Throws<InvalidOperationException>(() => PatchInstallService.CopyIntoInstall(
                manifest,
                stagingDir,
                installDir,
                _ =>
                {
                    replacements++;
                    if (replacements == 2)
                        throw new InvalidOperationException("Simulated interruption.");
                }));

            Assert.Equal("original", File.ReadAllText(Path.Combine(installDir, "existing.txt")));
            Assert.False(File.Exists(Path.Combine(installDir, "new", "created.txt")));
            Assert.False(Directory.Exists(Path.Combine(installDir, "new")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CopyIntoInstall_CommitsAllFiles_WhenEveryReplacementSucceeds()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vaultsync-patch-test-{Guid.NewGuid():N}");
        string stagingDir = Path.Combine(root, "staging");
        string installDir = Path.Combine(root, "install");

        try
        {
            Directory.CreateDirectory(stagingDir);
            Directory.CreateDirectory(installDir);
            File.WriteAllText(Path.Combine(stagingDir, "app.bin"), "updated");
            File.WriteAllText(Path.Combine(installDir, "app.bin"), "original");
            var manifest = new PatchManifest
            {
                Files = new List<PatchFileEntry> { new() { RelativePath = "app.bin" } }
            };

            PatchInstallService.CopyIntoInstall(manifest, stagingDir, installDir, _ => { });

            Assert.Equal("updated", File.ReadAllText(Path.Combine(installDir, "app.bin")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
