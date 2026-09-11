#nullable enable

using System;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class VersionHelperTests
{
    [Theory]
    [InlineData(null, null, 0)]
    [InlineData("", "1.8.9", -1)]
    [InlineData("1.8.9", "", 1)]
    [InlineData("v1.8.9+build.4", "1.8.9", 0)]
    [InlineData("1.8.10", "1.8.9", 1)]
    [InlineData("1.8.9-beta", "1.8.9", -1)]
    [InlineData("1.8.9", "1.8.9-rc.1", 1)]
    [InlineData("1.8.9-beta.2", "1.8.9-beta.11", -1)]
    [InlineData("1.8.9-beta.2", "1.8.9-beta.alpha", -1)]
    [InlineData("1.8.9-rc.1", "1.8.9-rc.1.extra", -1)]
    [InlineData("preview", "stable", -1)]
    public void CompareReleaseIdentities_OrdersExpected(string? left, string? right, int expectedSign)
    {
        int comparison = VersionHelper.CompareReleaseIdentities(left, right);

        Assert.Equal(expectedSign, Math.Sign(comparison));
    }

    [Theory]
    [InlineData("v1.8.9-beta.1+build.7", "1.8.9-beta.1")]
    [InlineData(" 1.8.9 ", "1.8.9")]
    [InlineData(null, "")]
    public void NormalizeIdentity_RemovesPrefixAndBuildMetadata(string? value, string expected)
    {
        Assert.Equal(expected, VersionHelper.NormalizeIdentity(value));
    }
}
