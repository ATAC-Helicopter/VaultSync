using System;
using System.IO;
using System.IO.Compression;
using System.Text;
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
    [InlineData("bin/VaultSync.dll.")]
    [InlineData("bin/VaultSync.dll ")]
    [InlineData("bin/CON.dll")]
    [InlineData("bin/control\u0001.dll")]
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

    [Fact]
    public void Manifest_RejectsUnicodeEquivalentDuplicatePaths()
    {
        PatchManifest manifest = CreateManifest("bin/caf\u00e9.dll");
        manifest.Files.Add(new PatchFileEntry
        {
            RelativePath = "bin/cafe\u0301.dll",
            Sha256 = Sha256,
            Size = 1
        });

        Assert.False(PatchUpdateService.TryValidatePatchManifest(manifest, out _, out _));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData(@"C:\absolute")]
    [InlineData("bin/CON.dll")]
    [InlineData("bin/file.dll.")]
    public void SafeZipExtractor_RejectsEscapingEntryPaths(string path)
    {
        Assert.ThrowsAny<Exception>(() => SafeZipExtractor.GetSafeEntryRelativePath(path));
    }

    [Fact]
    public void SafeZipExtractor_RejectsLinkedDestinationComponents()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vaultsync-zip-root-{Guid.NewGuid():N}");
        string outside = Path.Combine(Path.GetTempPath(), $"vaultsync-zip-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);

            Assert.Throws<InvalidDataException>(() =>
                SafeZipExtractor.GetSafeEntryPath(root, "linked/escaped.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void SafeZipExtractor_RejectsDuplicateNormalizedPaths()
    {
        string archivePath = Path.Combine(Path.GetTempPath(), $"vaultsync-duplicate-{Guid.NewGuid():N}.zip");
        string target = Path.Combine(Path.GetTempPath(), $"vaultsync-duplicate-target-{Guid.NewGuid():N}");
        try
        {
            using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                archive.CreateEntry("bin/caf\u00e9.dll");
                archive.CreateEntry("bin/cafe\u0301.dll");
            }

            Assert.Throws<InvalidDataException>(() =>
                SafeZipExtractor.ExtractToDirectory(archivePath, target));
        }
        finally
        {
            File.Delete(archivePath);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
    }

    [Theory]
    [InlineData("sha256:0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("SHA256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public void InstallerDigest_AcceptsGitHubSha256Format(string digest)
    {
        Assert.Equal(Sha256, GitHubUpdateService.TryParseSha256Digest(digest));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("md5:0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:not-a-hash")]
    public void InstallerDigest_RejectsMissingOrMalformedValues(string digest)
    {
        Assert.Null(GitHubUpdateService.TryParseSha256Digest(digest));
    }

    [Theory]
    [InlineData("http://github.com/ATAC-Helicopter/VaultSync/releases/download/v1.8.5/setup.exe")]
    [InlineData("https://evil.example/ATAC-Helicopter/VaultSync/releases/download/v1.8.5/setup.exe")]
    [InlineData("https://github.com/other/VaultSync/releases/download/v1.8.5/setup.exe")]
    [InlineData("https://github.com:444/ATAC-Helicopter/VaultSync/releases/download/v1.8.5/setup.exe")]
    public void ReleaseAssetUrl_RejectsUntrustedOrigins(string value)
    {
        Assert.False(GitHubUpdateService.TryGetTrustedReleaseAssetUri(value, out _));
    }

    [Fact]
    public void ReleaseAssetUrl_AcceptsRepositoryReleaseDownload()
    {
        const string value = "https://github.com/ATAC-Helicopter/VaultSync/releases/download/v1.8.5/setup.exe";

        Assert.True(GitHubUpdateService.TryGetTrustedReleaseAssetUri(value, out Uri uri));
        Assert.Equal(value, uri.AbsoluteUri);
    }

    [Fact]
    public void InstallerIntegrityVerifier_RejectsTamperedPayloadAndWrongSize()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vaultsync-installer-{Guid.NewGuid():N}.bin");
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes("trusted installer payload");
            File.WriteAllBytes(path, payload);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();

            Assert.True(InstallerIntegrityVerifier.Verify(path, payload.Length, hash));
            Assert.False(InstallerIntegrityVerifier.Verify(path, payload.Length + 1, hash));

            File.AppendAllText(path, "tampered");
            Assert.False(InstallerIntegrityVerifier.Verify(path, new FileInfo(path).Length, hash));
        }
        finally
        {
            File.Delete(path);
        }
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
