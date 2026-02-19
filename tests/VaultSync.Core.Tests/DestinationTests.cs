using System;
using System.IO;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class NetworkMountServiceTests
{
    [Fact]
    public void PrepareDestination_WithLocalPath_ReturnsSuccessAndEffectivePath()
    {
        using var tempDir = new TempDirectory();
        var destination = new BackupDestination
        {
            Path = tempDir.Path,
            Active = true,
            AutoMount = false,
            PreMounted = false
        };

        var service = new NetworkMountService();
        var result = service.PrepareDestination(destination, null);

        Assert.True(result.IsSuccess);
        Assert.Equal(tempDir.Path, result.EffectivePath);
        Assert.False(result.MountedByUs);
        Assert.Contains("local path", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrepareDestination_WithUnreachableNetworkShareAndAutoMountDisabled_ReturnsFailure()
    {
        var destination = new BackupDestination
        {
            Path = "\\\\unknown-host\\not-a-share",
            Active = true,
            AutoMount = false,
            PreMounted = false
        };

        var service = new NetworkMountService();
        var result = service.PrepareDestination(destination, null);

        Assert.False(result.IsSuccess);
        Assert.Contains("unreachable", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vaultsync-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures in tests.
            }
        }
    }
}

public sealed class CredentialVaultTests
{
    [Fact]
    public void EnsureKeyRef_WithExistingReference_ReturnsSameReference()
    {
        var vault = CredentialVault.Instance;
        var existingRef = "existing-ref";

        var actual = vault.EnsureKeyRef(existingRef, "test");

        Assert.Equal(existingRef, actual);
    }

    [Fact]
    public void GetSecret_WithMissingReference_ReturnsFallbackValue()
    {
        var vault = CredentialVault.Instance;
        var keyRef = $"vaultsync-test-{Guid.NewGuid():N}";
        vault.DeleteSecret(keyRef, "user");

        var secret = vault.GetSecret(
            keyRef,
            "user",
            preferKeychain: false,
            fallbackPlaintext: "fallback-secret");

        Assert.Equal("fallback-secret", secret);
    }
}
