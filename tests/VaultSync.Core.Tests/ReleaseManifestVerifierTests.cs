using System.Collections.Generic;
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
}
