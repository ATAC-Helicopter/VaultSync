using System;
using System.IO;
using System.Reflection;
using System.Linq;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
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
        DestinationResolution result = service.PrepareDestination(destination, null);

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
        DestinationResolution result = service.PrepareDestination(destination, null);

        Assert.False(result.IsSuccess);
        Assert.Contains("unreachable", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CredentialVaultTests
{
    [Fact]
    public void CorruptCredentialIndex_IsPreservedAndFailsClosed()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vaultsync-credentials-{Guid.NewGuid():N}");
        string storePath = Path.Combine(directory, "credentials.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(storePath, "{ definitely-not-json");
        var vault = new CredentialVault(storePath);

        try
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                vault.GetSecret("cred-test", "user", preferKeychain: false));

            Assert.Contains("corrupt", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("{ definitely-not-json", File.ReadAllText(storePath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DeleteCredential_ReplacesIndexAtomicallyWithoutLeavingTemporaryFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"vaultsync-credentials-{Guid.NewGuid():N}");
        string storePath = Path.Combine(directory, "credentials.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(storePath, """
            {
              "cred-test": {
                "Username": "user",
                "StoredInKeychain": false
              }
            }
            """);
        var vault = new CredentialVault(storePath);

        try
        {
            vault.DeleteSecret("cred-test", "user");

            Assert.Equal("{}", File.ReadAllText(storePath).Trim());
            Assert.Empty(Directory.EnumerateFiles(directory, ".credentials.json.*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EnsureKeyRef_WithExistingReference_ReturnsSameReference()
    {
        CredentialVault vault = CredentialVault.Instance;
        string existingRef = "existing-ref";

        string actual = CredentialVault.EnsureKeyRef(existingRef, "test");

        Assert.Equal(existingRef, actual);
    }

    [Fact]
    public void GetSecret_WithMissingReference_ReturnsFallbackValue()
    {
        CredentialVault vault = CredentialVault.Instance;
        string keyRef = $"vaultsync-test-{Guid.NewGuid():N}";
        vault.DeleteSecret(keyRef, "user");

        string secret = vault.GetSecret(
            keyRef,
            "user",
            preferKeychain: false,
            fallbackPlaintext: "fallback-secret");

        Assert.Equal("fallback-secret", secret);
    }

    [Fact]
    public void TryUnprotect_RejectsNonDpapiPayloads()
    {
        MethodInfo method = typeof(CredentialVault).GetMethod(
            "TryUnprotect",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("plain-secret"));
        object result = method!.Invoke(null, new object[] { encoded, false });

        Assert.Null(result);
    }

    [Theory]
    [InlineData("store", true)]
    [InlineData("lookup", false)]
    [InlineData("clear", false)]
    public void LinuxSecretServiceCommands_UseKeyRefAndAccountIdentity(string operation, bool redirectInput)
    {
        const string keyRef = "cred-project-a-0123456789abcdef0123456789abcdef";
        const string username = "vaultsync-backup-encryption";

        System.Diagnostics.ProcessStartInfo command =
            CredentialVault.BuildSecretToolStartInfo(operation, keyRef, username, redirectInput);
        string[] args = command.ArgumentList.ToArray();

        Assert.Equal("secret-tool", command.FileName);
        Assert.Equal(redirectInput, command.RedirectStandardInput);
        Assert.Contains("service", args);
        Assert.Contains("vaultsync", args);
        Assert.Equal(keyRef, args[Array.IndexOf(args, "key-ref") + 1]);
        Assert.Equal(username, args[Array.IndexOf(args, "account") + 1]);
    }

    [Fact]
    public void LinuxSecretServiceCommands_KeepDifferentKeyRefsDistinct()
    {
        string[] first = CredentialVault.BuildSecretToolStartInfo("lookup", "key-ref-a", "same-account")
            .ArgumentList.ToArray();
        string[] second = CredentialVault.BuildSecretToolStartInfo("lookup", "key-ref-b", "same-account")
            .ArgumentList.ToArray();

        Assert.NotEqual(
            first[Array.IndexOf(first, "key-ref") + 1],
            second[Array.IndexOf(second, "key-ref") + 1]);
    }

    [Theory]
    [InlineData("add-generic-password")]
    [InlineData("find-generic-password")]
    [InlineData("delete-generic-password")]
    public void MacKeychainCommands_UseStableServiceAndAccountIdentity(string operation)
    {
        const string keyRef = "cred-project-a";
        const string username = "vaultsync-backup-encryption";
        System.Diagnostics.ProcessStartInfo command =
            CredentialVault.BuildMacKeychainStartInfo(operation, keyRef, username);
        string[] args = command.ArgumentList.ToArray();

        Assert.Equal("/usr/bin/security", command.FileName);
        Assert.Equal(operation, args[0]);
        Assert.Equal(username, args[Array.IndexOf(args, "-a") + 1]);
        Assert.Equal(keyRef, args[Array.IndexOf(args, "-s") + 1]);
    }

    [Fact]
    public void MacKeychainNativeApi_RoundTripsWithoutStartingSecurityCli()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        string keyRef = $"vaultsync-test-{Guid.NewGuid():N}";
        const string username = "vaultsync-test";
        const string secret = "secret with spaces";
        MethodInfo write = typeof(CredentialVault).GetMethod(
            "TryWriteToKeychain", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo read = typeof(CredentialVault).GetMethod(
            "TryReadFromKeychain", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo delete = typeof(CredentialVault).GetMethod(
            "TryDeleteFromKeychain", BindingFlags.NonPublic | BindingFlags.Static)!;

        try
        {
            Assert.True((bool)write.Invoke(null, new object[] { keyRef, username, secret })!);
            Assert.Equal(secret, (string)read.Invoke(null, new object[] { keyRef, username })!);
        }
        finally
        {
            Assert.True((bool)delete.Invoke(null, new object[] { keyRef, username })!);
        }
    }

    [Fact]
    public void WindowsDpapi_RoundTripsForCurrentUser_WhenRunningOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        MethodInfo protect = typeof(CredentialVault).GetMethod(
            "TryProtect",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo unprotect = typeof(CredentialVault).GetMethod(
            "TryUnprotect",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        object[] protectArgs = { "windows-secret", string.Empty };

        bool protectedSuccessfully = (bool)protect.Invoke(null, protectArgs)!;
        string protectedSecret = (string)protectArgs[1];
        string restored = (string)unprotect.Invoke(null, new object[] { protectedSecret, true })!;

        Assert.True(protectedSuccessfully);
        Assert.Equal("windows-secret", restored);
    }
}
