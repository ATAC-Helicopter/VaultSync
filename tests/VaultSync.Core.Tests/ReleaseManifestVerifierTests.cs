using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    private static readonly string[] s_linuxInstallerExtensions = [".deb", ".AppImage", ".tar.gz"];

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

    [Theory]
    [InlineData("\"version\":\"1.8.7\"", "\"version\":null")]
    [InlineData("\"version\":\"1.8.7\"", "\"version\":\"not-a-version\"")]
    [InlineData("\"channel\":\"stable\"", "\"channel\":\"beta\"")]
    [InlineData("\"tag\":\"v1.8.7\"", "\"tag\":\"v1.8.8\"")]
    [InlineData("\"commit\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", "\"commit\":null")]
    [InlineData("\"commit\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", "\"commit\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"")]
    [InlineData("\"repository\":\"ATAC-Helicopter/VaultSync\"", "\"repository\":\"other/repository\"")]
    [InlineData("\"compatiblePredecessors\":[\"1.8.6\"]", "\"compatiblePredecessors\":[]")]
    [InlineData("\"compatiblePredecessors\":[\"1.8.6\"]", "\"compatiblePredecessors\":[\"1.8.7\"]")]
    [InlineData("\"compatiblePredecessors\":[\"1.8.6\"]", "\"compatiblePredecessors\":[\"1.8.6\",\"1.8.6\"]")]
    public void InvalidReleaseIdentity_IsRejected(string original, string replacement)
    {
        string manifest = CreateManifest().Replace(original, replacement, StringComparison.Ordinal);

        Assert.False(ReleaseManifestVerifier.TryValidate(
            manifest, Tag, prerelease: false, CreatePublishedAssets(), out _));
    }

    [Theory]
    [InlineData("\"name\":\"VaultSync-1.8.7-linux-x64.tar.gz\"", "\"name\":\"../VaultSync.tar.gz\"")]
    [InlineData("\"sizeBytes\":10", "\"sizeBytes\":0")]
    [InlineData("\"sha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"", "\"sha256\":\"ABCDEF\"")]
    [InlineData("\"platform\":\"linux\"", "\"platform\":\"other\"")]
    [InlineData("\"architecture\":\"x64\"", "\"architecture\":\"x86\"")]
    [InlineData("\"packageKind\":\"archive\"", "\"packageKind\":\"unknown\"")]
    public void InvalidManifestAssetContract_IsRejected(string original, string replacement)
    {
        string manifest = CreateManifest().Replace(original, replacement, StringComparison.Ordinal);

        Assert.False(ReleaseManifestVerifier.TryValidate(
            manifest, Tag, prerelease: false, CreatePublishedAssets(), out _));
    }

    [Fact]
    public void DuplicateOrBlankPublishedAssetNames_AreRejected()
    {
        List<PublishedReleaseAsset> duplicate = CreatePublishedAssets();
        duplicate.Add(duplicate[0]);
        List<PublishedReleaseAsset> blank = CreatePublishedAssets();
        blank.Add(new PublishedReleaseAsset("", Url, 10, $"sha256:{Hash}"));

        Assert.False(ReleaseManifestVerifier.TryValidate(
            CreateManifest(), Tag, prerelease: false, duplicate, out _));
        Assert.False(ReleaseManifestVerifier.TryValidate(
            CreateManifest(), Tag, prerelease: false, blank, out _));
    }

    [Fact]
    public void MatchingBetaReleaseIdentity_IsAccepted()
    {
        const string betaTag = "v1.8.7-beta.1";
        string manifest = CreateManifest()
            .Replace("\"version\":\"1.8.7\"", "\"version\":\"1.8.7-beta.1\"", StringComparison.Ordinal)
            .Replace("\"channel\":\"stable\"", "\"channel\":\"beta\"", StringComparison.Ordinal)
            .Replace(Tag, betaTag, StringComparison.Ordinal);
        List<PublishedReleaseAsset> published = CreatePublishedAssets()
            .Select(asset => asset with
            {
                DownloadUrl = asset.DownloadUrl?.Replace(Tag, betaTag, StringComparison.Ordinal)
            })
            .ToList();

        Assert.True(ReleaseManifestVerifier.TryValidate(
            manifest, betaTag, prerelease: true, published, out _));
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
                s_linuxInstallerExtensions,
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

    [Fact]
    public void ReleaseManifestDownloadContract_ResolvesAndValidatesExactPublishedBytes()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(CreateManifest());
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        List<GitHubUpdateService.GitHubAsset> releaseAssets = CreateGitHubAssets(bytes.Length, hash);

        bool resolved = GitHubUpdateService.TryGetReleaseManifestAsset(
            releaseAssets,
            Tag,
            out GitHubUpdateService.GitHubAsset manifestAsset,
            out Uri manifestUri,
            out string expectedHash);
        IReadOnlyCollection<ReleaseManifestAsset> verified = GitHubUpdateService.ValidateDownloadedReleaseManifest(
            bytes,
            Assert.IsType<GitHubUpdateService.GitHubAsset>(manifestAsset),
            Assert.IsType<string>(expectedHash),
            Tag,
            prerelease: false,
            releaseAssets);

        Assert.True(resolved);
        Assert.Equal($"https://github.com/ATAC-Helicopter/VaultSync/releases/download/{Tag}/{ReleaseManifestVerifier.ManifestName}", manifestUri?.AbsoluteUri);
        Assert.Single(verified!);
    }

    [Fact]
    public void ReleaseManifestDownloadContract_RejectsAmbiguousOrUntrustedDescriptors()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(CreateManifest());
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        List<GitHubUpdateService.GitHubAsset> valid = CreateGitHubAssets(bytes.Length, hash);
        GitHubUpdateService.GitHubAsset manifest = valid.Single(asset => asset.Name == ReleaseManifestVerifier.ManifestName);

        Assert.False(GitHubUpdateService.TryGetReleaseManifestAsset(null, Tag, out _, out _, out _));
        Assert.False(GitHubUpdateService.TryGetReleaseManifestAsset(
            [.. valid, CopyManifest(manifest)], Tag, out _, out _, out _));
        Assert.False(GitHubUpdateService.TryGetReleaseManifestAsset(
            [CopyManifest(manifest, size: 0)], Tag, out _, out _, out _));
        Assert.False(GitHubUpdateService.TryGetReleaseManifestAsset(
            [CopyManifest(manifest, size: 1_048_577)], Tag, out _, out _, out _));
        Assert.False(GitHubUpdateService.TryGetReleaseManifestAsset(
            [CopyManifest(manifest, url: "https://evil.example/manifest.json")], Tag, out _, out _, out _));
        Assert.False(GitHubUpdateService.TryGetReleaseManifestAsset(
            [CopyManifest(manifest, digest: "sha256:invalid")], Tag, out _, out _, out _));
        Assert.False(GitHubUpdateService.TryGetReleaseManifestAsset(
            [manifest], "v1.8.8", out _, out _, out _));
    }

    [Fact]
    public void ReleaseManifestDownloadContract_RejectsChangedOrInvalidPayloads()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(CreateManifest());
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        List<GitHubUpdateService.GitHubAsset> assets = CreateGitHubAssets(bytes.Length, hash);
        GitHubUpdateService.GitHubAsset manifest = assets.Single(asset => asset.Name == ReleaseManifestVerifier.ManifestName);

        Assert.Null(GitHubUpdateService.ValidateDownloadedReleaseManifest(
            bytes, CopyManifest(manifest, size: bytes.Length + 1), hash, Tag, false, assets));
        Assert.Null(GitHubUpdateService.ValidateDownloadedReleaseManifest(
            bytes, manifest, new string('0', 64), Tag, false, assets));

        byte[] invalidJson = Encoding.UTF8.GetBytes("{}");
        string invalidHash = Convert.ToHexString(SHA256.HashData(invalidJson)).ToLowerInvariant();
        GitHubUpdateService.GitHubAsset invalidManifest = CopyManifest(manifest, size: invalidJson.Length, digest: $"sha256:{invalidHash}");
        Assert.Null(GitHubUpdateService.ValidateDownloadedReleaseManifest(
            invalidJson, invalidManifest, invalidHash, Tag, false, [invalidManifest]));
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

    private static List<GitHubUpdateService.GitHubAsset> CreateGitHubAssets(long manifestSize, string manifestHash) =>
    [
        new()
        {
            Name = AssetName,
            BrowserDownloadUrl = Url,
            Size = 10,
            Digest = $"sha256:{Hash}"
        },
        new()
        {
            Name = ReleaseManifestVerifier.ManifestName,
            BrowserDownloadUrl = $"https://github.com/ATAC-Helicopter/VaultSync/releases/download/{Tag}/{ReleaseManifestVerifier.ManifestName}",
            Size = manifestSize,
            Digest = $"sha256:{manifestHash}"
        }
    ];

    private static GitHubUpdateService.GitHubAsset CopyManifest(
        GitHubUpdateService.GitHubAsset source,
        long? size = null,
        string url = null,
        string digest = null) => new()
    {
        Name = source.Name,
        BrowserDownloadUrl = url ?? source.BrowserDownloadUrl,
        Size = size ?? source.Size,
        Digest = digest ?? source.Digest
    };
}
