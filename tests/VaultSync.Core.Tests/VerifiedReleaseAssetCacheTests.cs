using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class VerifiedReleaseAssetCacheTests
{
    private const string AssetUrl =
        "https://github.com/ATAC-Helicopter/VaultSync/releases/download/v1.8.7/vaultsync-release-manifest.json";

    [Fact]
    public void VerifiedPayloadSurvivesCacheRecreation()
    {
        using var directory = new TempDirectory();
        byte[] payload = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
        string hash = Sha256(payload);
        var writer = new VerifiedReleaseAssetCache(directory.Path);

        writer.Write(AssetUrl, hash, payload.LongLength, 1024, payload);

        var reader = new VerifiedReleaseAssetCache(directory.Path);
        Assert.True(reader.TryRead(AssetUrl, hash, payload.LongLength, 1024, out byte[] cached));
        Assert.Equal(payload, cached);
        Assert.Single(Directory.EnumerateFiles(directory.Path, "*.json"));
    }

    [Fact]
    public void CacheIdentityIncludesUrlDigestAndSize()
    {
        using var directory = new TempDirectory();
        byte[] payload = Encoding.UTF8.GetBytes("{\"release\":\"1.8.7\"}");
        string hash = Sha256(payload);
        var cache = new VerifiedReleaseAssetCache(directory.Path);
        cache.Write(AssetUrl, hash, payload.LongLength, 1024, payload);

        Assert.False(cache.TryRead(AssetUrl + ".other", hash, payload.LongLength, 1024, out _));
        Assert.False(cache.TryRead(AssetUrl, new string('0', 64), payload.LongLength, 1024, out _));
        Assert.False(cache.TryRead(AssetUrl, hash, payload.LongLength + 1, 1024, out _));
    }

    [Fact]
    public void TamperedCacheEntryIsRejected()
    {
        using var directory = new TempDirectory();
        byte[] payload = Encoding.UTF8.GetBytes("{\"trusted\":true}");
        string hash = Sha256(payload);
        var cache = new VerifiedReleaseAssetCache(directory.Path);
        cache.Write(AssetUrl, hash, payload.LongLength, 1024, payload);
        string cachePath = Assert.Single(Directory.EnumerateFiles(directory.Path, "*.json"));
        File.WriteAllBytes(cachePath, Encoding.UTF8.GetBytes("{\"trusted\":fals}"));

        Assert.False(cache.TryRead(AssetUrl, hash, payload.LongLength, 1024, out _));
    }

    [Theory]
    [InlineData("not-a-url", 64, 10, 1024)]
    [InlineData(AssetUrl, 63, 10, 1024)]
    [InlineData(AssetUrl, 64, 0, 1024)]
    [InlineData(AssetUrl, 64, 1025, 1024)]
    public void InvalidCacheIdentityIsIgnored(string url, int hashLength, long size, long maximumSize)
    {
        using var directory = new TempDirectory();
        var cache = new VerifiedReleaseAssetCache(directory.Path);
        var payload = new byte[10];

        cache.Write(url, new string('a', hashLength), size, maximumSize, payload);

        Assert.False(cache.TryRead(url, new string('a', hashLength), size, maximumSize, out _));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void LinkedCacheEntryIsRejectedWithoutChangingItsTarget()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var directory = new TempDirectory();
        using var targetDirectory = new TempDirectory();
        byte[] payload = Encoding.UTF8.GetBytes("{\"trusted\":true}");
        string hash = Sha256(payload);
        var cache = new VerifiedReleaseAssetCache(directory.Path);
        cache.Write(AssetUrl, hash, payload.LongLength, 1024, payload);
        string cachePath = Assert.Single(Directory.EnumerateFiles(directory.Path, "*.json"));
        File.Delete(cachePath);
        string targetPath = Path.Combine(targetDirectory.Path, "target.json");
        File.WriteAllText(targetPath, "external");
        File.CreateSymbolicLink(cachePath, targetPath);

        cache.Write(AssetUrl, hash, payload.LongLength, 1024, payload);

        Assert.False(cache.TryRead(AssetUrl, hash, payload.LongLength, 1024, out _));
        Assert.Equal("external", File.ReadAllText(targetPath));
    }

    private static string Sha256(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
}
