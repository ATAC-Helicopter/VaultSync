using System;
using VaultSync.UI.Infrastructure;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class PatchSecurityTests
{
    private const string Sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("../patch.zip")]
    [InlineData("folder/patch.zip")]
    [InlineData(@"folder\patch.zip")]
    [InlineData("patch.exe")]
    [InlineData("")]
    public void ArchiveName_RejectsUnsafeNames(string name)
    {
        Assert.False(PatchUpdateService.TryGetSafeArchiveName(name, out _));
    }

    [Fact]
    public void ArchiveName_AcceptsLeafZipName()
    {
        Assert.True(PatchUpdateService.TryGetSafeArchiveName("vaultsync-patch-linux.zip", out string safe));
        Assert.Equal("vaultsync-patch-linux.zip", safe);
    }

    [Theory]
    [InlineData("../VaultSync.dll")]
    [InlineData("/tmp/VaultSync.dll")]
    [InlineData(@"C:\VaultSync.dll")]
    [InlineData("bin//VaultSync.dll")]
    public void Manifest_RejectsUnsafeFilePaths(string path)
    {
        PatchManifest manifest = CreateManifest(path);

        Assert.False(PatchUpdateService.TryValidatePatchManifest(manifest, out _, out _));
    }

    [Fact]
    public void Manifest_RequiresArchiveAndFileHashes()
    {
        PatchManifest manifest = CreateManifest("bin/VaultSync.dll");
        manifest.ArchiveSha256 = string.Empty;

        Assert.False(PatchUpdateService.TryValidatePatchManifest(manifest, out _, out _));

        manifest.ArchiveSha256 = Sha256;
        manifest.Files[0].Sha256 = "not-a-sha256";
        Assert.False(PatchUpdateService.TryValidatePatchManifest(manifest, out _, out _));
    }

    [Fact]
    public void Manifest_RejectsCaseInsensitiveDuplicatePaths()
    {
        PatchManifest manifest = CreateManifest("bin/VaultSync.dll");
        manifest.Files.Add(new PatchFileEntry
        {
            RelativePath = "BIN/vaultsync.dll",
            Sha256 = Sha256,
            Size = 1
        });

        Assert.False(PatchUpdateService.TryValidatePatchManifest(manifest, out _, out _));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData(@"C:\absolute")]
    public void SafeZipExtractor_RejectsEscapingEntryPaths(string path)
    {
        Assert.ThrowsAny<Exception>(() => SafeZipExtractor.GetSafeEntryRelativePath(path));
    }

    private static PatchManifest CreateManifest(string path) => new()
    {
        PreviousVersion = "1.8.4",
        TargetVersion = "1.8.5",
        ArchiveSha256 = Sha256,
        ArchiveSize = 1,
        Files =
        [
            new PatchFileEntry
            {
                RelativePath = path,
                Sha256 = Sha256,
                Size = 1
            }
        ]
    };
}
