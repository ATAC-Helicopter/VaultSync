using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VaultSync.Core.Services;

/// <summary>
/// Very small helper to move secrets out of the main app config.
/// Uses the current user's native credential store on Windows, macOS, and Linux.
/// The JSON index contains references and timestamps only, never plaintext secrets.
/// </summary>
public sealed class CredentialVault
{
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private static readonly Lazy<CredentialVault> _lazy = new(() => new CredentialVault());
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };
    public static CredentialVault Instance => _lazy.Value;

    private readonly string _storePath;
    private readonly object _sync = new();
    private readonly Func<string, string, string?> _nativeSecretReader;
    private readonly Dictionary<string, byte[]> _sessionSecretCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _nativeReadAttempts = new(StringComparer.OrdinalIgnoreCase);

    private CredentialVault()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VaultSync",
            "credentials.json"))
    {
    }

    internal CredentialVault(string storePath)
        : this(storePath, ReadNativeSecret)
    {
    }

    internal CredentialVault(string storePath, Func<string, string, string?> nativeSecretReader)
    {
        string dir = Path.GetDirectoryName(storePath)
            ?? throw new ArgumentException("Credential store path must have a parent directory.", nameof(storePath));
        Directory.CreateDirectory(dir);
        _storePath = storePath;
        _nativeSecretReader = nativeSecretReader ?? throw new ArgumentNullException(nameof(nativeSecretReader));
    }

    public static string EnsureKeyRef(string? existing, string nameHint)
    {
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        string slug = Slugify(nameHint);
        return $"cred-{slug}-{Guid.NewGuid():N}";
    }

    public bool HasStoredSecret(string? keyRef)
    {
        if (string.IsNullOrWhiteSpace(keyRef))
            return false;

        lock (_sync)
        {
            Dictionary<string, StoredSecret> map = Load();
            return map.TryGetValue(keyRef, out StoredSecret? record)
                && (record.StoredInKeychain || !string.IsNullOrWhiteSpace(record.ProtectedSecret));
        }
    }

    public string? GetSecret(string? keyRef, string username, bool preferKeychain, string? fallbackPlaintext = null)
    {
        if (string.IsNullOrWhiteSpace(keyRef))
            return fallbackPlaintext;

        lock (_sync)
        {
            Dictionary<string, StoredSecret> map = Load();
            map.TryGetValue(keyRef, out StoredSecret? record);

            string? cachedSecret = TryReadCachedSecret(keyRef);
            if (!string.IsNullOrEmpty(cachedSecret))
                return TouchAndReturn(map, keyRef, cachedSecret);

            if (preferKeychain || record?.StoredInKeychain == true)
            {
                string accountName = record?.Username ?? username;
                string? nativeSecret = TryReadNativeSecretOnce(keyRef, accountName);
                if (!string.IsNullOrEmpty(nativeSecret))
                    return TouchAndReturn(map, keyRef, nativeSecret);
            }

            if (!string.IsNullOrWhiteSpace(record?.ProtectedSecret))
            {
                string? secret = TryUnprotect(record.ProtectedSecret, record.ProtectedWithDpapi);
                if (!string.IsNullOrEmpty(secret))
                {
                    CacheSecret(keyRef, secret);
                    return TouchAndReturn(map, keyRef, secret);
                }
            }
        }

        return fallbackPlaintext;
    }

    private static string? ReadNativeSecret(string keyRef, string username)
    {
        if (OperatingSystem.IsMacOS())
            return TryReadFromKeychain(keyRef, username);
        return OperatingSystem.IsLinux()
            ? TryReadFromSecretService(keyRef, username)
            : null;
    }

    private string? TryReadNativeSecretOnce(string keyRef, string username)
    {
        if (!_nativeReadAttempts.Add(keyRef))
            return null;

        string? secret = _nativeSecretReader(keyRef, username);
        if (!string.IsNullOrEmpty(secret))
            CacheSecret(keyRef, secret);
        return secret;
    }

    private string? TryReadCachedSecret(string keyRef)
    {
        return _sessionSecretCache.TryGetValue(keyRef, out byte[]? secretBytes)
            ? Encoding.UTF8.GetString(secretBytes)
            : null;
    }

    private void CacheSecret(string keyRef, string secret)
    {
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        if (_sessionSecretCache.TryGetValue(keyRef, out byte[]? previous))
            CryptographicOperations.ZeroMemory(previous);
        _sessionSecretCache[keyRef] = secretBytes;
    }

    private void RemoveCachedSecret(string keyRef)
    {
        _nativeReadAttempts.Remove(keyRef);
        if (_sessionSecretCache.Remove(keyRef, out byte[]? secretBytes))
            CryptographicOperations.ZeroMemory(secretBytes);
    }

    private string TouchAndReturn(Dictionary<string, StoredSecret> map, string keyRef, string secret)
    {
        TouchSecretIfNeeded(map, keyRef);
        return secret;
    }

    public void SaveSecret(string keyRef, string username, string secret, bool preferKeychain)
    {
        if (string.IsNullOrWhiteSpace(keyRef) || string.IsNullOrWhiteSpace(secret))
            return;

        lock (_sync)
        {
            Dictionary<string, StoredSecret> map = Load();
            bool isNewRecord = !map.TryGetValue(keyRef, out StoredSecret? existing);
            StoredSecret record = !isNewRecord
                ? existing!
                : new StoredSecret();

            record.Username = username;
            record.CreatedUtc ??= DateTime.UtcNow;
            record.LastAccessUtc = DateTime.UtcNow;

            StoreSecret(record, keyRef, username, secret, preferKeychain);

            map[keyRef] = record;
            try
            {
                Save(map);
                CacheSecret(keyRef, secret);
                _nativeReadAttempts.Add(keyRef);
            }
            catch
            {
                // A newly-created native item without an index entry could never be
                // discovered for later cleanup. Roll it back if index persistence fails.
                if (isNewRecord && record.StoredInKeychain)
                {
                    if (OperatingSystem.IsMacOS())
                        TryDeleteFromKeychain(keyRef, username);
                    else if (OperatingSystem.IsLinux())
                        TryDeleteFromSecretService(keyRef, username);
                }
                throw;
            }
        }
    }

    private static void StoreSecret(
        StoredSecret record,
        string keyRef,
        string username,
        string secret,
        bool preferKeychain)
    {
        if (preferKeychain && OperatingSystem.IsMacOS())
        {
            StoreNativeSecret(record, TryWriteToKeychain(keyRef, username, secret),
                "Failed to store secret in macOS Keychain.");
            return;
        }

        if (preferKeychain && OperatingSystem.IsLinux())
        {
            StoreNativeSecret(record, TryWriteToSecretService(keyRef, username, secret),
                "LINUX_SECRET_TOOL_MISSING");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            record.StoredInKeychain = false;
            StoreProtected(record, secret, requireProtection: true);
            return;
        }

        throw new InvalidOperationException("Secure credential storage is not available on this platform.");
    }

    private static void StoreNativeSecret(StoredSecret record, bool stored, string errorMessage)
    {
        if (!stored)
            throw new InvalidOperationException(errorMessage);

        record.StoredInKeychain = true;
        record.ProtectedSecret = null;
    }

    public void DeleteSecret(string? keyRef, string username)
    {
        if (string.IsNullOrWhiteSpace(keyRef))
            return;

        lock (_sync)
        {
            Dictionary<string, StoredSecret> map = Load();
            if (!map.TryGetValue(keyRef, out StoredSecret? record))
            {
                // Best-effort cleanup for a legacy item whose index entry was lost.
                if (OperatingSystem.IsMacOS())
                    TryDeleteFromKeychain(keyRef, username);
                else if (OperatingSystem.IsLinux())
                    TryDeleteFromSecretService(keyRef, username);
                RemoveCachedSecret(keyRef);
                return;
            }

            string accountName = record.Username ?? username;
            bool nativeDeleted = !record.StoredInKeychain
                || (OperatingSystem.IsMacOS() && TryDeleteFromKeychain(keyRef, accountName))
                || (OperatingSystem.IsLinux() && TryDeleteFromSecretService(keyRef, accountName));
            if (!nativeDeleted)
                return;

            map.Remove(keyRef);
            Save(map);
            RemoveCachedSecret(keyRef);
        }
    }

    public int CleanupUnusedSecrets(IEnumerable<string> activeKeyRefs, TimeSpan staleAge)
    {
        var activeSet = new HashSet<string>(
            activeKeyRefs
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var activeFamilies = new HashSet<string>(
            activeSet
                .Select(GetKeyFamily)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f!),
            StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            Dictionary<string, StoredSecret> map = Load();
            if (map.Count == 0)
                return 0;

            DateTime now = DateTime.UtcNow;
            int removed = 0;
            foreach (string? key in map.Keys.ToList())
            {
                if (activeSet.Contains(key))
                    continue;

                if (!map.TryGetValue(key, out StoredSecret? record))
                    continue;

                // If a credential was re-created for the same logical profile/project
                // (same "cred-<family>-<guid>" prefix), remove old unreferenced entries immediately.
                string? family = GetKeyFamily(key);
                bool forcePruneDuplicate = !string.IsNullOrWhiteSpace(family) && activeFamilies.Contains(family);

                if (!forcePruneDuplicate)
                {
                    DateTime? lastSeen = record.LastAccessUtc ?? record.CreatedUtc;
                    if (lastSeen.HasValue && now - lastSeen.Value < staleAge)
                        continue;
                }

                bool nativeDeleted = !record.StoredInKeychain
                    || (OperatingSystem.IsMacOS() && TryDeleteFromKeychain(key, record.Username ?? string.Empty))
                    || (OperatingSystem.IsLinux() && TryDeleteFromSecretService(key, record.Username ?? string.Empty));
                if (!nativeDeleted)
                    continue;

                map.Remove(key);
                RemoveCachedSecret(key);
                removed++;
            }

            if (removed > 0)
                Save(map);

            return removed;
        }
    }

    private static void StoreProtected(StoredSecret record, string secret, bool requireProtection)
    {
        if (TryProtect(secret, out string? protectedSecret))
        {
            record.ProtectedSecret   = protectedSecret;
            record.ProtectedWithDpapi = OperatingSystem.IsWindows();
            return;
        }

        if (requireProtection)
        {
            throw new InvalidOperationException("Failed to protect credential with platform APIs.");
        }
    }

    private Dictionary<string, StoredSecret> Load()
    {
        try
        {
            if (!File.Exists(_storePath))
                return new(StringComparer.OrdinalIgnoreCase);

            string json = File.ReadAllText(_storePath);
            Dictionary<string, StoredSecret>? data = JsonSerializer.Deserialize<Dictionary<string, StoredSecret>>(json);
            return data ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Credential index '{_storePath}' is corrupt. It was preserved and no credential changes were made.", ex);
        }
    }

    private void Save(Dictionary<string, StoredSecret> map)
    {
        string json = JsonSerializer.Serialize(map, IndentedJsonOptions);
        string directory = Path.GetDirectoryName(_storePath)!;
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(_storePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, json);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(tempPath, _storePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best-effort temporary-file cleanup */ }
        }
    }

    private void TouchSecretIfNeeded(Dictionary<string, StoredSecret> map, string keyRef)
    {
        if (!map.TryGetValue(keyRef, out StoredSecret? record))
            return;

        DateTime now = DateTime.UtcNow;
        if (record.LastAccessUtc.HasValue && now - record.LastAccessUtc.Value < TimeSpan.FromHours(12))
            return;

        record.LastAccessUtc = now;
        record.CreatedUtc ??= now;
        Save(map);
    }

    private static string? GetKeyFamily(string keyRef)
    {
        if (string.IsNullOrWhiteSpace(keyRef) || !keyRef.StartsWith("cred-", StringComparison.OrdinalIgnoreCase))
            return null;

        int lastDash = keyRef.LastIndexOf('-');
        if (lastDash <= 5 || lastDash >= keyRef.Length - 1)
            return null;

        string suffix = keyRef[(lastDash + 1)..];
        if (suffix.Length != 32 || !suffix.All(ch => Uri.IsHexDigit(ch)))
            return null;

        return keyRef[..lastDash];
    }

    private static bool TryProtect(string secret, out string protectedSecret)
    {
        protectedSecret = string.Empty;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(secret);
                byte[] cipher = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                protectedSecret = Convert.ToBase64String(cipher);
                return true;
            }
        }
        catch
        {
            // ignore and fall through
        }

        return false;
    }

    private static string? TryUnprotect(string protectedSecret, bool wasDpapi)
    {
        try
        {
            if (!wasDpapi || !OperatingSystem.IsWindows())
                return null;

            byte[] data = Convert.FromBase64String(protectedSecret);
            byte[] plain = ProtectedData.Unprotect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    private static string Slugify(string input)
    {
        var sb = new StringBuilder();
        foreach (char ch in input.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '_')
            {
                sb.Append('-');
            }
        }

        string slug = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "profile" : slug;
    }

    private static bool TryWriteToKeychain(string keyRef, string username, string secret)
    {
        byte[] service = Encoding.UTF8.GetBytes(keyRef);
        byte[] account = Encoding.UTF8.GetBytes(username);
        byte[] password = Encoding.UTF8.GetBytes(secret);
        IntPtr item = IntPtr.Zero;
        try
        {
            int findStatus = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                out _, out IntPtr existingPassword, out item);
            if (existingPassword != IntPtr.Zero)
                _ = SecKeychainItemFreeContent(IntPtr.Zero, existingPassword);

            if (findStatus == ErrSecSuccess)
                return SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password) == ErrSecSuccess;
            if (findStatus != ErrSecItemNotFound)
                return false;

            return SecKeychainAddGenericPassword(
                IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                (uint)password.Length, password, out item) == ErrSecSuccess;
        }
        catch
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(password);
            if (item != IntPtr.Zero)
                CFRelease(item);
        }
    }

    private static string? TryReadFromKeychain(string keyRef, string username)
    {
        byte[] service = Encoding.UTF8.GetBytes(keyRef);
        byte[] account = Encoding.UTF8.GetBytes(username);
        IntPtr passwordData = IntPtr.Zero;
        IntPtr item = IntPtr.Zero;
        try
        {
            int status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                out uint passwordLength, out passwordData, out item);
            if (status != ErrSecSuccess || passwordData == IntPtr.Zero)
                return null;

            byte[] password = new byte[passwordLength];
            try
            {
                Marshal.Copy(passwordData, password, 0, password.Length);
                return Encoding.UTF8.GetString(password);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(password);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (passwordData != IntPtr.Zero)
                _ = SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
            if (item != IntPtr.Zero)
                CFRelease(item);
        }
    }

    private static bool TryDeleteFromKeychain(string keyRef, string username)
    {
        byte[] service = Encoding.UTF8.GetBytes(keyRef);
        byte[] account = Encoding.UTF8.GetBytes(username);
        IntPtr item = IntPtr.Zero;
        try
        {
            int status = SecKeychainFindGenericPassword(
                IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                out _, out IntPtr passwordData, out item);
            if (passwordData != IntPtr.Zero)
                _ = SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
            return status == ErrSecItemNotFound
                || (status == ErrSecSuccess && SecKeychainItemDelete(item) == ErrSecSuccess);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (item != IntPtr.Zero)
                CFRelease(item);
        }
    }

    private static bool TryWriteToSecretService(string keyRef, string username, string secret)
    {
        try
        {
            ProcessStartInfo psi = BuildSecretToolStartInfo("store", keyRef, username, redirectInput: true);

            return RunProcess(psi, secret, TimeSpan.FromSeconds(30)).ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadFromSecretService(string keyRef, string username)
    {
        try
        {
            ProcessStartInfo psi = BuildSecretToolStartInfo("lookup", keyRef, username);

            ProcessResult result = RunProcess(psi, null, TimeSpan.FromSeconds(30));
            return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    internal static ProcessStartInfo BuildMacKeychainStartInfo(
        string operation,
        string keyRef,
        string username)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "/usr/bin/security",
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
            UseShellExecute        = false
        };

        psi.ArgumentList.Add(operation);
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add(username);
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(keyRef);
        return psi;
    }

    private static bool TryDeleteFromSecretService(string keyRef, string username)
    {
        try
        {
            ProcessStartInfo psi = BuildSecretToolStartInfo("clear", keyRef, username);

            return RunProcess(psi, null, TimeSpan.FromSeconds(30)).ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static ProcessResult RunProcess(ProcessStartInfo startInfo, string? standardInput, TimeSpan timeout)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
            return new ProcessResult(-1, string.Empty);

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        if (startInfo.RedirectStandardInput)
        {
            process.StandardInput.Write(standardInput ?? string.Empty);
            process.StandardInput.Close();
        }

        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            process.WaitForExitAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* process may already be gone */ }
            process.WaitForExit();
            return new ProcessResult(-1, string.Empty);
        }

        Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
        return new ProcessResult(process.ExitCode, outputTask.Result);
    }

    internal readonly record struct ProcessResult(int ExitCode, string StandardOutput);

    internal static ProcessStartInfo BuildSecretToolStartInfo(
        string operation,
        string keyRef,
        string username,
        bool redirectInput = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "secret-tool",
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
            RedirectStandardInput  = redirectInput,
            UseShellExecute        = false
        };

        psi.ArgumentList.Add(operation);
        if (string.Equals(operation, "store", StringComparison.Ordinal))
        {
            psi.ArgumentList.Add("--label");
            psi.ArgumentList.Add(keyRef);
        }

        psi.ArgumentList.Add("service");
        psi.ArgumentList.Add("vaultsync");
        psi.ArgumentList.Add("key-ref");
        psi.ArgumentList.Add(keyRef);
        psi.ArgumentList.Add("account");
        psi.ArgumentList.Add(username);
        return psi;
    }

    private sealed class StoredSecret
    {
        public string? Username { get; set; }
        public string? ProtectedSecret { get; set; }
        public bool ProtectedWithDpapi { get; set; }
        public bool StoredInKeychain { get; set; }
        public DateTime? CreatedUtc { get; set; }
        public DateTime? LastAccessUtc { get; set; }
    }

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength,
        out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength,
        byte[] passwordData, out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr cf);
}
