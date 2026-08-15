using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ReleaseManifestVerifierTests
{
    private const string AssetName = "VaultSync-1.8.7-linux-x64.tar.gz";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Tag = "v1.8.7";
    private const string Url = "https://github.com/ATAC-Helicopter/VaultSync/releases/download/v1.8.7/VaultSync-1.8.7-linux-x64.tar.gz";

    [Fact]
    public void ExactPublishedManifest_IsAccepted()
    {
        bool valid = ReleaseManifestVerifier.TryValidate(
            CreateManifest(),
            Tag,
            prerelease: false,
            CreatePublishedAssets(),
            out IReadOnlyDictionary<string, ReleaseManifestAsset> assets);

        Assert.True(valid);
        ReleaseManifestAsset asset = Assert.Single(assets).Value;
        Assert.Equal(Hash, asset.Sha256);
        Assert.Equal(Url, asset.DownloadUrl);
    }

    [Theory]
    [InlineData(11, Hash, Url)]
    [InlineData(10, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Url)]
    [InlineData(10, Hash, "https://evil.example/VaultSync.tar.gz")]
    public void PublishedMetadataMismatch_IsRejected(long size, string hash, string url)
    {
        List<PublishedReleaseAsset> published = CreatePublishedAssets(size, hash, url);

        Assert.False(ReleaseManifestVerifier.TryValidate(
            CreateManifest(), Tag, prerelease: false, published, out _));
    }

    [Theory]
    [InlineData("v1.8.8", false)]
    [InlineData(Tag, true)]
    public void ReleaseIdentityMismatch_IsRejected(string tag, bool prerelease)
    {
        Assert.False(ReleaseManifestVerifier.TryValidate(
            CreateManifest(), tag, prerelease, CreatePublishedAssets(), out _));
    }

    [Fact]
    public void UnknownSchemaFields_AreRejected()
    {
        string json = CreateManifest().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"unexpected\":true");

        Assert.False(ReleaseManifestVerifier.TryValidate(
            json, Tag, prerelease: false, CreatePublishedAssets(), out _));
    }

    [Fact]
    public void UnsupportedSchemaVersion_IsRejected()
    {
        string json = CreateManifest().Replace("\"schemaVersion\":1", "\"schemaVersion\":2");

        Assert.False(ReleaseManifestVerifier.TryValidate(
            json, Tag, prerelease: false, CreatePublishedAssets(), out _));
    }

    [Fact]
    public void AssetsMissingFromGitHub_AreRejected()
    {
        Assert.False(ReleaseManifestVerifier.TryValidate(
            CreateManifest(), Tag, prerelease: false, [], out _));
    }

    [Fact]
    public void PlatformAssets_AreSelectedOnlyFromVerifiedManifestEntries()
    {
        List<ReleaseManifestAsset> assets = CreatePlatformAssets();

        var patch = GitHubUpdateService.GetPatchAssets(assets);
        var installer = GitHubUpdateService.GetInstallerAsset(assets);

        Assert.NotNull(patch.ManifestUrl);
        Assert.NotNull(patch.ArchiveUrl);
        Assert.Equal(Hash, patch.ManifestSha256);
        Assert.Equal(Hash, patch.ArchiveSha256);
        Assert.Equal(10, patch.ManifestSize);
        Assert.Equal(20, patch.ArchiveSize);
        Assert.EndsWith(".zip", patch.ArchiveName, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(installer.InstallerUrl);
        Assert.Equal(Hash, installer.InstallerSha256);
        Assert.Equal(30, installer.InstallerSize);

        if (OperatingSystem.IsWindows())
            Assert.EndsWith(".exe", installer.InstallerName, StringComparison.OrdinalIgnoreCase);
        else if (OperatingSystem.IsMacOS())
            Assert.EndsWith(".dmg", installer.InstallerName, StringComparison.OrdinalIgnoreCase);
        else
            Assert.Contains(
                new[] { ".deb", ".AppImage", ".tar.gz" },
                extension => installer.InstallerName!.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PlatformAssets_RejectUntrustedUrlsAndEmptyCollections()
    {
        List<ReleaseManifestAsset> untrusted = CreatePlatformAssets()
            .Select(asset => new ReleaseManifestAsset
            {
                Name = asset.Name,
                Platform = asset.Platform,
                Architecture = asset.Architecture,
                PackageKind = asset.PackageKind,
                SizeBytes = asset.SizeBytes,
                Sha256 = asset.Sha256,
                DownloadUrl = $"https://evil.example/{asset.Name}"
            })
            .ToList();

        Assert.Null(GitHubUpdateService.GetPatchAssets([]).ArchiveUrl);
        Assert.Null(GitHubUpdateService.GetInstallerAsset([]).InstallerUrl);
        Assert.Null(GitHubUpdateService.GetPatchAssets(untrusted).ArchiveUrl);
        Assert.Null(GitHubUpdateService.GetInstallerAsset(untrusted).InstallerUrl);
    }

    private static string CreateManifest()
    {
        var manifest = new
        {
            schemaVersion = 1,
            release = new
            {
                version = "1.8.7",
                channel = "stable",
                tag = Tag,
                commit = new string('a', 40),
                repository = "ATAC-Helicopter/VaultSync",
                compatiblePredecessors = new[] { "1.8.6" }
            },
            assets = new[]
            {
                new
                {
                    name = AssetName,
                    platform = "linux",
                    architecture = "x64",
                    packageKind = "archive",
                    sizeBytes = 10,
                    sha256 = Hash,
                    downloadUrl = Url
                }
            }
        };
        return JsonSerializer.Serialize(manifest);
    }

    private static List<PublishedReleaseAsset> CreatePublishedAssets(
        long size = 10,
        string hash = Hash,
        string url = Url) =>
        [
            new(AssetName, url, size, $"sha256:{hash}"),
            new(
                ReleaseManifestVerifier.ManifestName,
                $"https://github.com/ATAC-Helicopter/VaultSync/releases/download/{Tag}/{ReleaseManifestVerifier.ManifestName}",
                100,
                $"sha256:{Hash}")
        ];

    private static List<ReleaseManifestAsset> CreatePlatformAssets()
    {
        var assets = new List<ReleaseManifestAsset>();
        foreach (string suffix in new[]
                 {
                     "windows", "macos", "macos-apple-silicon", "macos-intel",
                     "linux", "linux-x64", "linux-arm64"
                 })
        {
            assets.Add(CreateAsset($"vaultsync-patch-{suffix}.json", 10));
            assets.Add(CreateAsset($"vaultsync-patch-{suffix}.zip", 20));
        }

        assets.Add(CreateAsset("VaultSync-1.8.7-setup.exe", 30));
        assets.Add(CreateAsset("VaultSync-1.8.7-macos.dmg", 30));
        foreach (string suffix in new[] { "linux-x64", "linux-arm64" })
        {
            assets.Add(CreateAsset($"VaultSync-1.8.7-{suffix}.deb", 30));
            assets.Add(CreateAsset($"VaultSync-1.8.7-{suffix}.AppImage", 30));
            assets.Add(CreateAsset($"VaultSync-1.8.7-{suffix}.tar.gz", 30));
        }

        return assets;
    }

    private static ReleaseManifestAsset CreateAsset(string name, long size) => new()
    {
        Name = name,
        Platform = "test",
        Architecture = "test",
        PackageKind = "test",
        SizeBytes = size,
        Sha256 = Hash,
        DownloadUrl = $"https://github.com/ATAC-Helicopter/VaultSync/releases/download/{Tag}/{name}"
    };
}
