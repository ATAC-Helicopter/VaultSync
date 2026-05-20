using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VaultSync.Core.Services;

/// <summary>
/// Very small helper to move secrets out of the main app config.
/// On macOS it writes to the user keychain; elsewhere it stores
/// DPAPI-protected blobs under ApplicationData/VaultSync.
/// </summary>
public sealed class CredentialVault
{
    private static readonly Lazy<CredentialVault> _lazy = new(() => new CredentialVault());
    public static CredentialVault Instance => _lazy.Value;

    private readonly string _storePath;
    private readonly object _sync = new();

    private CredentialVault()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VaultSync");
        Directory.CreateDirectory(dir);
        _storePath = Path.Combine(dir, "credentials.json");
    }

    public string EnsureKeyRef(string? existing, string nameHint)
    {
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        string slug = Slugify(nameHint);
        return $"cred-{slug}-{Guid.NewGuid():N}";
    }

    public string? GetSecret(string? keyRef, string username, bool preferKeychain, string? fallbackPlaintext = null)
    {
        if (string.IsNullOrWhiteSpace(keyRef))
            return fallbackPlaintext;

        lock (_sync)
        {
            Dictionary<string, StoredSecret> map = Load();

            if (OperatingSystem.IsMacOS() && preferKeychain)
            {
                string? kc = TryReadFromKeychain(keyRef, username);
                if (!string.IsNullOrEmpty(kc))
                {
                    TouchSecretIfNeeded(map, keyRef);
                    return kc;
                }
            }
            else if (OperatingSystem.IsLinux() && preferKeychain)
            {
                string? sec = TryReadFromSecretService(keyRef, username);
                if (!string.IsNullOrEmpty(sec))
                {
                    TouchSecretIfNeeded(map, keyRef);
                    return sec;
                }
            }

            if (!map.TryGetValue(keyRef, out StoredSecret? record))
                return fallbackPlaintext;

            if (record.StoredInKeychain && OperatingSystem.IsMacOS())
            {
                string? kc = TryReadFromKeychain(keyRef, record.Username ?? username);
                if (!string.IsNullOrEmpty(kc))
                {
                    TouchSecretIfNeeded(map, keyRef);
                    return kc;
                }
            }
            if (record.StoredInKeychain && OperatingSystem.IsLinux())
            {
                string? sec = TryReadFromSecretService(keyRef, record.Username ?? username);
                if (!string.IsNullOrEmpty(sec))
                {
                    TouchSecretIfNeeded(map, keyRef);
                    return sec;
                }
            }

            if (!string.IsNullOrWhiteSpace(record.ProtectedSecret))
            {
                string? secret = TryUnprotect(record.ProtectedSecret, record.ProtectedWithDpapi);
                if (!string.IsNullOrEmpty(secret))
                {
                    TouchSecretIfNeeded(map, keyRef);
                    return secret;
                }
            }
        }

        return fallbackPlaintext;
    }

    public void SaveSecret(string keyRef, string username, string secret, bool preferKeychain)
    {
        if (string.IsNullOrWhiteSpace(keyRef) || string.IsNullOrWhiteSpace(secret))
            return;

        lock (_sync)
        {
            Dictionary<string, StoredSecret> map = Load();
            StoredSecret record = map.TryGetValue(keyRef, out StoredSecret? existing)
                ? existing
                : new StoredSecret();

            record.Username = username;
            record.CreatedUtc ??= DateTime.UtcNow;
            record.LastAccessUtc = DateTime.UtcNow;

            if (preferKeychain && OperatingSystem.IsMacOS())
            {
                if (TryWriteToKeychain(keyRef, username, secret))
                {
                    record.StoredInKeychain = true;
                    record.ProtectedSecret  = null;
                }
                else
                {
                    throw new InvalidOperationException("Failed to store secret in macOS Keychain.");
                }
            }
            else if (preferKeychain && OperatingSystem.IsLinux())
            {
                if (TryWriteToSecretService(keyRef, username, secret))
                {
                    record.StoredInKeychain = true;
                    record.ProtectedSecret  = null;
                }
                else
                {
                    //Linux secret-tool failed. 99% not installed secret-tool packages.
                    throw new InvalidOperationException("LINUX_SECRET_TOOL_MISSING");
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                record.StoredInKeychain = false;
                StoreProtected(record, secret, requireProtection: true);
            }
            else
            {
                // Strict mode: do not store secrets on unsupported platforms without a secure store.
                throw new InvalidOperationException("Secure credential storage is not available on this platform.");
            }

            map[keyRef] = record;
            Save(map);
        }
    }

    public void DeleteSecret(string? keyRef, string username)
    {
        if (string.IsNullOrWhiteSpace(keyRef))
            return;

        lock (_sync)
        {
            Dictionary<string, StoredSecret> map = Load();
            if (map.Remove(keyRef))
            {
                Save(map);
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            TryDeleteFromKeychain(keyRef, username);
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

                map.Remove(key);
                removed++;

                if (OperatingSystem.IsMacOS())
                {
                    TryDeleteFromKeychain(key, record.Username ?? string.Empty);
                }
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
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save(Dictionary<string, StoredSecret> map)
    {
        string json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_storePath, json);
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
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "/usr/bin/security",
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false
            };

            psi.ArgumentList.Add("add-generic-password");
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(username);
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(keyRef);
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(secret);
            psi.ArgumentList.Add("-U");

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5_000);
            return proc is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private static string? TryReadFromKeychain(string keyRef, string username)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "/usr/bin/security",
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false
            };

            psi.ArgumentList.Add("find-generic-password");
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(username);
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(keyRef);
            psi.ArgumentList.Add("-w");

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);
            return proc.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFromKeychain(string keyRef, string username)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "/usr/bin/security",
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false
            };

            psi.ArgumentList.Add("delete-generic-password");
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(username);
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(keyRef);

            using var proc = Process.Start(psi);
            proc?.WaitForExit(3_000);
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    private static bool TryWriteToSecretService(string keyRef, string username, string secret)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "secret-tool",
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                UseShellExecute        = false
            };

            psi.ArgumentList.Add("store");
            psi.ArgumentList.Add("--label");
            psi.ArgumentList.Add(keyRef);
            psi.ArgumentList.Add("service");
            psi.ArgumentList.Add("vaultsync");
            psi.ArgumentList.Add("account");
            psi.ArgumentList.Add(username);

            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            proc.StandardInput.Write(secret);
            proc.StandardInput.Close();
            proc.WaitForExit(30_000);
            return proc.ExitCode == 0;
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
            var psi = new ProcessStartInfo
            {
                FileName               = "secret-tool",
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
                UseShellExecute        = false
            };

            psi.ArgumentList.Add("lookup");
            psi.ArgumentList.Add("service");
            psi.ArgumentList.Add("vaultsync");
            psi.ArgumentList.Add("account");
            psi.ArgumentList.Add(username);

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(30_000);
            return proc.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class StoredSecret
    {
        public string? Username { get; set; }
        public string? ProtectedSecret { get; set; }
        public bool ProtectedWithDpapi { get; set; }
        public bool StoredInKeychain { get; set; }
        public string? LegacyPlaintext { get; set; }
        public DateTime? CreatedUtc { get; set; }
        public DateTime? LastAccessUtc { get; set; }
    }
}
