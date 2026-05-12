using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed class BackupEncryptionSecretService(
    Func<string?, string, string> ensureKeyRef,
    Func<string?, string, bool, string?, string?> getSecret,
    Action<string, string, string, bool> saveSecret,
    Action<string?, string> deleteSecret)
{
    private readonly Func<string?, string, string> _ensureKeyRef = ensureKeyRef;
    private readonly Func<string?, string, bool, string?, string?> _getSecret = getSecret;
    private readonly Action<string, string, string, bool> _saveSecret = saveSecret;
    private readonly Action<string?, string> _deleteSecret = deleteSecret;
    private readonly ConcurrentDictionary<string, byte[]> _sessionSecrets = new(StringComparer.OrdinalIgnoreCase);

    public BackupEncryptionSecretService()
        : this(
            (existing, hint) => CredentialVault.Instance.EnsureKeyRef(existing, hint),
            (keyRef, username, preferKeychain, fallback) => CredentialVault.Instance.GetSecret(keyRef, username, preferKeychain, fallback),
            (keyRef, username, secret, preferKeychain) => CredentialVault.Instance.SaveSecret(keyRef, username, secret, preferKeychain),
            (keyRef, username) => CredentialVault.Instance.DeleteSecret(keyRef, username))
    {
    }

    public string EnsureSecretRef(string? existingRef, string nameHint) =>
        _ensureKeyRef(existingRef, nameHint);

    public EncryptionSecretStorageMode SaveSecret(
        string keyRef,
        string username,
        string secret,
        bool allowSessionFallback,
        bool fallbackConfirmed,
        bool preferKeychain = true)
    {
        if (string.IsNullOrWhiteSpace(keyRef))
            throw new ArgumentException("Secret reference is required.", nameof(keyRef));
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("Secret value is required.", nameof(secret));

        try
        {
            _saveSecret(keyRef, username, secret, preferKeychain);
            RemoveSessionSecret(keyRef);
            return EncryptionSecretStorageMode.SecureStore;
        }
        catch when (allowSessionFallback && fallbackConfirmed)
        {
            StoreSessionSecret(keyRef, secret);
            return EncryptionSecretStorageMode.SessionMemory;
        }
        catch (Exception ex)
        {
            string guidance = allowSessionFallback
                ? "Session fallback is allowed but requires explicit confirmation."
                : "Session fallback is disabled for this operation.";
            throw new InvalidOperationException(
                $"Secure-store save failed. {guidance}",
                ex);
        }
    }

    public string? GetSecret(string? keyRef, string username, bool preferKeychain = true)
    {
        if (string.IsNullOrWhiteSpace(keyRef))
            return null;

        string? stored = _getSecret(keyRef, username, preferKeychain, null);
        if (!string.IsNullOrWhiteSpace(stored))
            return stored;

        return TryReadSessionSecret(keyRef);
    }

    public void DeleteSecret(string? keyRef, string username)
    {
        if (string.IsNullOrWhiteSpace(keyRef))
            return;

        _deleteSecret(keyRef, username);
        RemoveSessionSecret(keyRef);
    }

    public void ClearSessionSecrets()
    {
        foreach (string keyRef in _sessionSecrets.Keys)
            RemoveSessionSecret(keyRef);
    }

    private void StoreSessionSecret(string keyRef, string secret)
    {
        byte[] secretBytes = Encoding.UTF8.GetBytes(secret);
        if (_sessionSecrets.TryGetValue(keyRef, out byte[]? existing))
        {
            _sessionSecrets[keyRef] = secretBytes;
            ClearBuffer(existing);
            return;
        }

        _sessionSecrets[keyRef] = secretBytes;
    }

    private string? TryReadSessionSecret(string keyRef)
    {
        if (!_sessionSecrets.TryGetValue(keyRef, out byte[]? sessionSecret) || sessionSecret.Length == 0)
            return null;

        return Encoding.UTF8.GetString(sessionSecret);
    }

    private void RemoveSessionSecret(string keyRef)
    {
        if (_sessionSecrets.TryRemove(keyRef, out byte[]? existing))
            ClearBuffer(existing);
    }

    private static void ClearBuffer(byte[]? buffer)
    {
        if (buffer is null || buffer.Length == 0)
            return;

        CryptographicOperations.ZeroMemory(buffer);
    }
}
