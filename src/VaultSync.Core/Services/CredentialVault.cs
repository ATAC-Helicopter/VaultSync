using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VaultSync");
        Directory.CreateDirectory(dir);
        _storePath = Path.Combine(dir, "credentials.json");
    }

    public string EnsureKeyRef(string? existing, string nameHint)
    {
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var slug = Slugify(nameHint);
        return $"cred-{slug}-{Guid.NewGuid():N}";
    }

        public string? GetSecret(string? keyRef, string username, bool preferKeychain, string? fallbackPlaintext = null)
        {
            if (string.IsNullOrWhiteSpace(keyRef))
                return fallbackPlaintext;

        lock (_sync)
        {
            var map = Load();

            if (OperatingSystem.IsMacOS() && preferKeychain)
            {
                var kc = TryReadFromKeychain(keyRef, username);
                if (!string.IsNullOrEmpty(kc))
                    return kc;
            }
            else if (OperatingSystem.IsLinux() && preferKeychain)
            {
                var sec = TryReadFromSecretService(keyRef, username);
                if (!string.IsNullOrEmpty(sec))
                    return sec;
            }

            if (!map.TryGetValue(keyRef, out var record))
                return fallbackPlaintext;

            if (record.StoredInKeychain && OperatingSystem.IsMacOS())
            {
                var kc = TryReadFromKeychain(keyRef, record.Username ?? username);
                if (!string.IsNullOrEmpty(kc))
                    return kc;
            }
            if (record.StoredInKeychain && OperatingSystem.IsLinux())
            {
                var sec = TryReadFromSecretService(keyRef, record.Username ?? username);
                if (!string.IsNullOrEmpty(sec))
                    return sec;
            }

            if (!string.IsNullOrWhiteSpace(record.ProtectedSecret))
            {
                var secret = TryUnprotect(record.ProtectedSecret, record.ProtectedWithDpapi);
                if (!string.IsNullOrEmpty(secret))
                    return secret;
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
            var map = Load();
            var record = map.TryGetValue(keyRef, out var existing)
                ? existing
                : new StoredSecret();

            record.Username = username;

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
                    throw new InvalidOperationException("Failed to store secret in secret service (libsecret).");
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
            var map = Load();
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

    private static void StoreProtected(StoredSecret record, string secret, bool requireProtection)
    {
        if (TryProtect(secret, out var protectedSecret))
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

            var json = File.ReadAllText(_storePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, StoredSecret>>(json);
            return data ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save(Dictionary<string, StoredSecret> map)
    {
        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_storePath, json);
    }

    private static bool TryProtect(string secret, out string protectedSecret)
    {
        protectedSecret = string.Empty;

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var bytes = Encoding.UTF8.GetBytes(secret);
                var cipher = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
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
            var data = Convert.FromBase64String(protectedSecret);
            if (wasDpapi && OperatingSystem.IsWindows())
            {
                var plain = ProtectedData.Unprotect(data, optionalEntropy: null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }

            // Not DPAPI: treat as base64 of plaintext.
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            return null;
        }
    }

    private static string Slugify(string input)
    {
        var sb = new StringBuilder();
        foreach (var ch in input.ToLowerInvariant())
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

        var slug = sb.ToString().Trim('-');
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

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5_000);
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
            proc.WaitForExit(5_000);
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
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5_000);
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
    }
}
