#nullable enable
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupEncryptionPolicyResolverTests
{
    [Fact]
    public void Resolve_ProjectPlain_OverridesGlobalEnabled()
    {
        var project = new Project
        {
            Name = "Project A",
            RootPath = "C:\\tmp\\a",
            Preset = "unity",
            EncryptionPolicy = ProjectEncryptionPolicy.Plain
        };
        var cfg = new BackupEncryptionConfig { Enabled = true, KeyRef = "global-key" };

        var resolved = BackupEncryptionPolicyResolver.Resolve(project, cfg);

        Assert.False(resolved.EncryptionRequested);
        Assert.Null(resolved.EffectiveKeyRef);
        Assert.Equal("none", resolved.KeySource);
    }

    [Fact]
    public void Resolve_ProjectEncrypted_OverridesGlobalDisabled_UsesProjectKeyRef()
    {
        var project = new Project
        {
            Name = "Project B",
            RootPath = "C:\\tmp\\b",
            Preset = "unity",
            EncryptionPolicy = ProjectEncryptionPolicy.Encrypted,
            EncryptionKeyRef = "project-key"
        };
        var cfg = new BackupEncryptionConfig { Enabled = false, KeyRef = "global-key" };

        var resolved = BackupEncryptionPolicyResolver.Resolve(project, cfg);

        Assert.True(resolved.EncryptionRequested);
        Assert.Equal("project-key", resolved.EffectiveKeyRef);
        Assert.Equal("project", resolved.KeySource);
    }

    [Fact]
    public void Resolve_ProjectEncrypted_WithoutProjectKeyRef_FallsBackToGlobalKeyRef()
    {
        var project = new Project
        {
            Name = "Project C",
            RootPath = "C:\\tmp\\c",
            Preset = "unity",
            EncryptionPolicy = ProjectEncryptionPolicy.Encrypted
        };
        var cfg = new BackupEncryptionConfig { Enabled = false, KeyRef = "global-key" };

        var resolved = BackupEncryptionPolicyResolver.Resolve(project, cfg);

        Assert.True(resolved.EncryptionRequested);
        Assert.Equal("global-key", resolved.EffectiveKeyRef);
        Assert.Equal("global", resolved.KeySource);
    }

    [Fact]
    public void ResolveRestoreKeyRefs_ProjectThenGlobal_WhenBothAvailable()
    {
        var project = new Project
        {
            Name = "Project D",
            RootPath = "C:\\tmp\\d",
            Preset = "unity",
            EncryptionKeyRef = "project-key"
        };
        var cfg = new BackupEncryptionConfig { KeyRef = "global-key" };

        var refs = BackupEncryptionPolicyResolver.ResolveRestoreKeyRefs(project, cfg);

        Assert.Equal(2, refs.Count);
        Assert.Equal("project-key", refs[0]);
        Assert.Equal("global-key", refs[1]);
    }

    [Fact]
    public void ResolveRestoreKeyRefs_DeduplicatesMatchingRefs()
    {
        var project = new Project
        {
            Name = "Project E",
            RootPath = "C:\\tmp\\e",
            Preset = "unity",
            EncryptionKeyRef = "same-key"
        };
        var cfg = new BackupEncryptionConfig { KeyRef = "same-key" };

        var refs = BackupEncryptionPolicyResolver.ResolveRestoreKeyRefs(project, cfg);

        Assert.Single(refs);
        Assert.Equal("same-key", refs[0]);
    }

    [Fact]
    public void ResolveRestoreKeyRefs_ReturnsGlobal_WhenProjectKeyMissing()
    {
        var project = new Project
        {
            Name = "Project F",
            RootPath = "C:\\tmp\\f",
            Preset = "unity"
        };
        var cfg = new BackupEncryptionConfig { KeyRef = "global-key" };

        var refs = BackupEncryptionPolicyResolver.ResolveRestoreKeyRefs(project, cfg);

        Assert.Single(refs);
        Assert.Equal("global-key", refs[0]);
    }
}
