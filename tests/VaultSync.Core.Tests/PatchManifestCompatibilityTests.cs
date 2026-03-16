using System.Collections.Generic;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class PatchManifestCompatibilityTests
{
    [Fact]
    public void TryGetAllowedBaseVersions_UsesLegacyPreviousVersion_WhenAllowlistMissing()
    {
        var manifest = new PatchManifest
        {
            PreviousVersion = "1.6.2",
            TargetVersion = "1.7.0"
        };

        var ok = PatchUpdateService.TryGetAllowedBaseVersions(manifest, out var allowed, out _, out _);

        Assert.True(ok);
        Assert.Equal(new[] { "1.6.2" }, allowed);
    }

    [Fact]
    public void TryGetAllowedBaseVersions_RejectsInconsistentLegacyAndAllowlist()
    {
        var manifest = new PatchManifest
        {
            PreviousVersion = "1.6.2",
            BaseVersions = new List<string> { "1.6.0", "1.6.1" },
            TargetVersion = "1.7.0"
        };

        var ok = PatchUpdateService.TryGetAllowedBaseVersions(
            manifest,
            out _,
            out var statusCode,
            out _);

        Assert.False(ok);
        Assert.Equal("manifest-invalid-base-allowlist", statusCode);
    }

    [Fact]
    public void TryGetAllowedBaseVersions_DeduplicatesAndSortsAllowlist()
    {
        var manifest = new PatchManifest
        {
            PreviousVersion = "1.6.1",
            BaseVersions = new List<string> { "1.6.1", "1.6.0", "1.6.1", "v1.6.2" },
            TargetVersion = "1.7.0"
        };

        var ok = PatchUpdateService.TryGetAllowedBaseVersions(manifest, out var allowed, out _, out _);

        Assert.True(ok);
        Assert.Equal(new[] { "1.6.0", "1.6.1", "1.6.2" }, allowed);
    }

    [Fact]
    public void TryValidateAllowedBaseVersions_AllowsExactListedBase()
    {
        var manifest = new PatchManifest
        {
            PreviousVersion = "1.6.1",
            BaseVersions = new List<string> { "1.6.0", "1.6.1", "1.6.2" },
            TargetVersion = "1.7.0"
        };

        var ok = PatchUpdateService.TryValidateAllowedBaseVersions(
            manifest,
            "v1.6.2",
            out var allowed,
            out var matched,
            out var statusCode,
            out _);

        Assert.True(ok);
        Assert.Equal("eligible", statusCode);
        Assert.Equal("1.6.2", matched);
        Assert.Equal(3, allowed.Count);
    }

    [Fact]
    public void TryValidateAllowedBaseVersions_RejectsNonListedBase()
    {
        var manifest = new PatchManifest
        {
            PreviousVersion = "1.6.1",
            BaseVersions = new List<string> { "1.6.0", "1.6.1", "1.6.2" },
            TargetVersion = "1.7.0"
        };

        var ok = PatchUpdateService.TryValidateAllowedBaseVersions(
            manifest,
            "1.6.3",
            out _,
            out _,
            out var statusCode,
            out var message);

        Assert.False(ok);
        Assert.Equal("base-version-not-allowed", statusCode);
        Assert.Contains("1.6.3", message);
    }
}
