#nullable enable
using VaultSync.Core.Models;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class ProjectEncryptionPolicyTests
{
    [Theory]
    [InlineData(null, ProjectEncryptionPolicy.Inherit)]
    [InlineData("", ProjectEncryptionPolicy.Inherit)]
    [InlineData("inherit", ProjectEncryptionPolicy.Inherit)]
    [InlineData("ENCRYPTED", ProjectEncryptionPolicy.Encrypted)]
    [InlineData("plain", ProjectEncryptionPolicy.Plain)]
    [InlineData("unknown", ProjectEncryptionPolicy.Inherit)]
    public void Normalize_ReturnsSupportedPolicy(string? input, string expected)
    {
        var normalized = ProjectEncryptionPolicy.Normalize(input);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(ProjectEncryptionPolicy.Encrypted, false, true)]
    [InlineData(ProjectEncryptionPolicy.Plain, true, false)]
    [InlineData(ProjectEncryptionPolicy.Inherit, true, true)]
    [InlineData(ProjectEncryptionPolicy.Inherit, false, false)]
    public void IsEncrypted_ResolvesPolicyWithGlobalFallback(string policy, bool globalEnabled, bool expected)
    {
        var result = ProjectEncryptionPolicy.IsEncrypted(policy, globalEnabled);
        Assert.Equal(expected, result);
    }
}
