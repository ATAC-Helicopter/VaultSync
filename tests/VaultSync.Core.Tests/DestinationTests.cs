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

    [Fact]
    public void PrepareDestination_DoesNotReadCredentialForUnsupportedAutoMountPlatform()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            return;

        int readCount = 0;
        var service = new NetworkMountService(_ =>
        {
            readCount++;
            return "secret";
        });
        var destination = new BackupDestination
        {
            Path = "smb://example.invalid/share",
            Active = true,
            AutoMount = true
        };

        DestinationResolution result = service.PrepareDestination(
            destination,
            new NetworkCredentialProfile { Name = "test", Username = "user", KeyRef = "cred-test" });

        Assert.False(result.IsSuccess);
        Assert.Equal(0, readCount);
    }

    [Fact]
    public void PrepareDestination_DoesNotReadCredentialForUnsupportedMacNfsAutoMount()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        int readCount = 0;
        var service = new NetworkMountService(_ =>
        {
            readCount++;
            return "secret";
        });
        var destination = new BackupDestination
        {
            Path = "nfs://example.invalid/share",
            Active = true,
            AutoMount = true
        };

        DestinationResolution result = service.PrepareDestination(
            destination,
            new NetworkCredentialProfile { Name = "test", Username = "user", KeyRef = "cred-test" });

        Assert.False(result.IsSuccess);
        Assert.Equal(0, readCount);
    }
}

[Collection("Credential environment")]
public sealed class CredentialVaultTests
{
    [Fact]
    public void LinuxSecretService_RoundTripsWithoutWritingPlaintextToTheIndex()
    {
        if (!OperatingSystem.IsLinux())
            return;

        using var tempDir = new TempDirectory();
        string toolPath = Path.Combine(tempDir.Path, "secret-tool");
        string secretPath = Path.Combine(tempDir.Path, "native-secret");
        string indexPath = Path.Combine(tempDir.Path, "credentials.json");
        File.WriteAllText(toolPath, """
            #!/bin/sh
            case "$1" in
              store) cat > "$VAULTSYNC_FAKE_SECRET_FILE" ;;
              lookup) cat "$VAULTSYNC_FAKE_SECRET_FILE" ;;
              clear) rm -f "$VAULTSYNC_FAKE_SECRET_FILE" ;;
              *) exit 2 ;;
            esac
            """);
        File.SetUnixFileMode(
            toolPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string previousPath = Environment.GetEnvironmentVariable("PATH");
        string previousSecretFile = Environment.GetEnvironmentVariable("VAULTSYNC_FAKE_SECRET_FILE");
        Environment.SetEnvironmentVariable("PATH", $"{tempDir.Path}{Path.PathSeparator}{previousPath}");
        Environment.SetEnvironmentVariable("VAULTSYNC_FAKE_SECRET_FILE", secretPath);
        try
        {
            var vault = new CredentialVault(indexPath);
            const string keyRef = "cred-project-0123456789abcdef0123456789abcdef";
            const string username = "backup-user";
            const string secret = "native secret with spaces";

            vault.SaveSecret(keyRef, username, secret, preferKeychain: true);

            string indexJson = File.ReadAllText(indexPath);
            Assert.DoesNotContain(secret, indexJson, StringComparison.Ordinal);
            Assert.Contains("\"StoredInKeychain\": true", indexJson, StringComparison.Ordinal);
            Assert.Equal(secret, vault.GetSecret(keyRef, username, preferKeychain: true));

            vault.DeleteSecret(keyRef, username);

            Assert.False(File.Exists(secretPath));
            Assert.Equal("{}", File.ReadAllText(indexPath).Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable("VAULTSYNC_FAKE_SECRET_FILE", previousSecretFile);
        }
    }

    [Fact]
    public void RunProcess_ForSecretInput_DrainsOutputAndKeepsInputOutOfArguments()
    {
        if (!OperatingSystem.IsLinux())
            return;

        const string secret = "secret with spaces and symbols $'\"";
        var command = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        command.ArgumentList.Add("-c");
        command.ArgumentList.Add("cat; printf ignored-error >&2");

        CredentialVault.ProcessResult result = CredentialVault.RunProcess(
            command,
            secret,
            TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(secret, result.StandardOutput);
        Assert.DoesNotContain(secret, command.ArgumentList);
    }

    [Fact]
    public void RunProcess_WhenCommandExceedsDeadline_TerminatesIt()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var command = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        command.ArgumentList.Add("-c");
        command.ArgumentList.Add("sleep 10");

        CredentialVault.ProcessResult result = CredentialVault.RunProcess(
            command,
            standardInput: null,
            timeout: TimeSpan.FromMilliseconds(100));

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

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
    public void GetSecret_CoalescesRepeatedNativeReadsForTheSession()
    {
        using var tempDir = new TempDirectory();
        string storePath = Path.Combine(tempDir.Path, "credentials.json");
        File.WriteAllText(storePath, """
            {
              "cred-test": {
                "Username": "stored-account",
                "StoredInKeychain": true
              }
            }
            """);
        int readCount = 0;
        string observedAccount = null;
        var vault = new CredentialVault(storePath, (_, account) =>
        {
            readCount++;
            observedAccount = account;
            return "session-secret";
        });

        Assert.Equal("session-secret", vault.GetSecret("cred-test", "caller-account", preferKeychain: true));
        Assert.Equal("session-secret", vault.GetSecret("cred-test", "caller-account", preferKeychain: true));
        Assert.Equal("session-secret", vault.GetSecret("cred-test", "other-account", preferKeychain: true));
        Assert.Equal(1, readCount);
        Assert.Equal("stored-account", observedAccount);
    }

    [Fact]
    public void GetSecret_DoesNotRetryDeniedOrUnavailableNativeReadDuringTheSession()
    {
        using var tempDir = new TempDirectory();
        string storePath = Path.Combine(tempDir.Path, "credentials.json");
        int readCount = 0;
        var vault = new CredentialVault(storePath, (_, _) =>
        {
            readCount++;
            return null;
        });

        Assert.Equal("fallback", vault.GetSecret("cred-test", "user", preferKeychain: true, fallbackPlaintext: "fallback"));
        Assert.Equal("fallback", vault.GetSecret("cred-test", "user", preferKeychain: true, fallbackPlaintext: "fallback"));
        Assert.Equal(1, readCount);
    }

    [Fact]
    public void HasStoredSecret_UsesIndexMetadataWithoutReadingNativeStore()
    {
        using var tempDir = new TempDirectory();
        string storePath = Path.Combine(tempDir.Path, "credentials.json");
        File.WriteAllText(storePath, """
            {
              "cred-test": {
                "Username": "stored-account",
                "StoredInKeychain": true
              }
            }
            """);
        int readCount = 0;
        var vault = new CredentialVault(storePath, (_, _) =>
        {
            readCount++;
            return "secret";
        });

        Assert.True(vault.HasStoredSecret("cred-test"));
        Assert.False(vault.HasStoredSecret("cred-missing"));
        Assert.Equal(0, readCount);
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

[CollectionDefinition("Credential environment", DisableParallelization = true)]
public sealed class CredentialEnvironmentCollection;
