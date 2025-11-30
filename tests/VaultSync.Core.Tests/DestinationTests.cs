using System;
using System.IO;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public class NetworkMountServiceTests
{
    [Fact]
    public void PrepareDestination_WithLocalPath_Succeeds()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"vaultsync-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempPath);

        try
        {
            var dest = new BackupDestination
            {
                Path = tempPath,
                Active = true,
                AutoMount = false,
                PreMounted = true
            };

            var service = new NetworkMountService();
            var result = service.PrepareDestination(dest, null);

            Assert.True(result.IsSuccess);
            Assert.Equal(tempPath, result.EffectivePath);
            Assert.False(result.MountedByUs);
            Assert.Contains("Using", result.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(tempPath, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures
            }
        }
    }

    [Fact]
    public void PrepareDestination_WithUnreachableNetworkShare_Fails()
    {
        var dest = new BackupDestination
        {
            Path = "\\\\unknown-host\\not-a-share",
            Active = true,
            AutoMount = false
        };

        var service = new NetworkMountService();
        var result = service.PrepareDestination(dest, null);

        Assert.False(result.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }
}

public class CredentialVaultTests
{
    [Fact]
    public void EnsureKeyRef_ReturnsExistingReference()
    {
        var vault = CredentialVault.Instance;
        var keyRef = "existing-ref";

        var actual = vault.EnsureKeyRef(keyRef, "test");

        Assert.Equal(keyRef, actual);
    }

    [Fact]
    public void GetSecret_MissingKey_ReturnsFallbackValue()
    {
        var vault = CredentialVault.Instance;
        var keyRef = $"vaultsync-test-{Guid.NewGuid():N}";
        vault.DeleteSecret(keyRef, "user");

        var secret = vault.GetSecret(keyRef, "user", preferKeychain: false, fallbackPlaintext: "fallback-secret");

        Assert.Equal("fallback-secret", secret);
    }
}
