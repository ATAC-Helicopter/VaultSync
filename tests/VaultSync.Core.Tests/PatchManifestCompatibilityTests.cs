using System.Collections.Generic;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class PatchManifestCompatibilityTests
{
    [Theory]
    [InlineData("--headless-patch")]
    [InlineData("--HEADLESS-PATCH")]
    public void IsHeadlessPatchInvocation_DetectsLinuxElevationMarker(string marker)
    {
        Assert.True(PatchInstallService.IsHeadlessPatchInvocation(
            new[] { "--apply-patch-request", "/tmp/request.json", marker }));
    }

    [Fact]
    public void IsHeadlessPatchInvocation_IgnoresNormalUpdaterInvocation()
    {
        Assert.False(PatchInstallService.IsHeadlessPatchInvocation(
            new[] { "--apply-patch-request", "/tmp/request.json" }));
    }

    [Fact]
    public void TryGetAllowedBaseVersions_UsesLegacyPreviousVersion_WhenAllowlistMissing()
    {
        var manifest = new PatchManifest
        {
            PreviousVersion = "1.6.2",
            TargetVersion = "1.7.0"
        };

        bool ok = PatchUpdateService.TryGetAllowedBaseVersions(manifest, out IReadOnlyList<string> allowed, out _, out _);

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

        bool ok = PatchUpdateService.TryGetAllowedBaseVersions(
            manifest,
            out _,
            out string statusCode,
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

        bool ok = PatchUpdateService.TryGetAllowedBaseVersions(manifest, out IReadOnlyList<string> allowed, out _, out _);

        Assert.True(ok);
        Assert.Equal(new[] { "1.6.0", "1.6.1", "1.6.2" }, allowed);
    }

    [Fact]
    public void TryGetAllowedBaseVersions_RejectsManifestWithoutAnyBaseVersion()
    {
        var manifest = new PatchManifest
        {
            TargetVersion = "1.7.0"
        };

        bool ok = PatchUpdateService.TryGetAllowedBaseVersions(
            manifest,
            out IReadOnlyList<string> allowed,
            out string statusCode,
            out string message);

        Assert.False(ok);
        Assert.Empty(allowed);
        Assert.Equal("manifest-invalid-base-allowlist", statusCode);
        Assert.Contains("does not declare", message);
    }

    [Fact]
    public void TryGetAllowedBaseVersions_RejectsEmptyAllowlistEntry()
    {
        var manifest = new PatchManifest
        {
            PreviousVersion = "1.6.1",
            BaseVersions = new List<string> { "1.6.1", " " },
            TargetVersion = "1.7.0"
        };

        bool ok = PatchUpdateService.TryGetAllowedBaseVersions(
            manifest,
            out _,
            out string statusCode,
            out string message);

        Assert.False(ok);
        Assert.Equal("manifest-invalid-base-allowlist", statusCode);
        Assert.Contains("empty", message);
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

        bool ok = PatchUpdateService.TryValidateAllowedBaseVersions(
            manifest,
            "v1.6.2",
            out IReadOnlyList<string> allowed,
            out string matched,
            out string statusCode,
            out _);

        Assert.True(ok);
        Assert.Equal("eligible", statusCode);
        Assert.Equal("1.6.2", matched);
        Assert.Equal(3, allowed.Count);
    }

    [Fact]
    public void TryValidateAllowedBaseVersions_IgnoresBuildMetadataForBaseMatch()
    {
        var manifest = new PatchManifest
        {
            PreviousVersion = "1.6.2+manifest.5",
            BaseVersions = new List<string> { "v1.6.2+manifest.5" },
            TargetVersion = "1.7.0"
        };

        bool ok = PatchUpdateService.TryValidateAllowedBaseVersions(
            manifest,
            "v1.6.2+local.9",
            out IReadOnlyList<string> allowed,
            out string matched,
            out string statusCode,
            out _);

        Assert.True(ok);
        Assert.Equal("eligible", statusCode);
        Assert.Equal("1.6.2", matched);
        Assert.Equal(new[] { "1.6.2" }, allowed);
    }

    [Fact]
    public void TryValidateAllowedBaseVersions_RejectsDifferentPrereleaseLabel()
    {
        var manifest = new PatchManifest
        {
            PreviousVersion = "1.7.0-beta.1",
            BaseVersions = new List<string> { "1.7.0-beta.1" },
            TargetVersion = "1.7.0"
        };

        bool ok = PatchUpdateService.TryValidateAllowedBaseVersions(
            manifest,
            "1.7.0-beta.2",
            out _,
            out _,
            out string statusCode,
            out string message);

        Assert.False(ok);
        Assert.Equal("base-version-not-allowed", statusCode);
        Assert.Contains("1.7.0-beta.2", message);
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

        bool ok = PatchUpdateService.TryValidateAllowedBaseVersions(
            manifest,
            "1.6.3",
            out _,
            out _,
            out string statusCode,
            out string message);

        Assert.False(ok);
        Assert.Equal("base-version-not-allowed", statusCode);
        Assert.Contains("1.6.3", message);
    }

    [Theory]
    [InlineData("1.8.7", true)]
    [InlineData("v1.8.7+installed", true)]
    [InlineData("1.8.6", false)]
    [InlineData("1.8.8", false)]
    public void ChronicleManifest_QualifiesOnlyExact187Predecessor(string installedVersion, bool expected)
    {
        var manifest = new PatchManifest
        {
            PreviousVersion = "1.8.7",
            BaseVersions = new List<string> { "1.8.7" },
            TargetVersion = "1.8.8"
        };

        bool eligible = PatchUpdateService.TryValidateAllowedBaseVersions(
            manifest,
            installedVersion,
            out IReadOnlyList<string> allowed,
            out string matched,
            out string statusCode,
            out _);

        Assert.Equal(expected, eligible);
        Assert.Equal(["1.8.7"], allowed);
        Assert.Equal(expected ? "1.8.7" : string.Empty, matched);
        Assert.Equal(expected ? "eligible" : "base-version-not-allowed", statusCode);
    }
}
