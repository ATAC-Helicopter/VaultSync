using System;
using System.Collections.Concurrent;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed class BackupEncryptionSecretService
{
    private readonly Func<string?, string, string> _ensureKeyRef;
    private readonly Func<string?, string, bool, string?, string?> _getSecret;
    private readonly Action<string, string, string, bool> _saveSecret;
    private readonly Action<string?, string> _deleteSecret;
    private readonly ConcurrentDictionary<string, string> _sessionSecrets = new(StringComparer.OrdinalIgnoreCase);

    public BackupEncryptionSecretService()
        : this(
            (existing, hint) => CredentialVault.Instance.EnsureKeyRef(existing, hint),
            (keyRef, username, preferKeychain, fallback) => CredentialVault.Instance.GetSecret(keyRef, username, preferKeychain, fallback),
            (keyRef, username, secret, preferKeychain) => CredentialVault.Instance.SaveSecret(keyRef, username, secret, preferKeychain),
            (keyRef, username) => CredentialVault.Instance.DeleteSecret(keyRef, username))
    {
    }

    public BackupEncryptionSecretService(
        Func<string?, string, string> ensureKeyRef,
        Func<string?, string, bool, string?, string?> getSecret,
        Action<string, string, string, bool> saveSecret,
        Action<string?, string> deleteSecret)
    {
        _ensureKeyRef = ensureKeyRef;
        _getSecret = getSecret;
        _saveSecret = saveSecret;
        _deleteSecret = deleteSecret;
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
            _sessionSecrets.TryRemove(keyRef, out _);
            return EncryptionSecretStorageMode.SecureStore;
        }
        catch when (allowSessionFallback && fallbackConfirmed)
        {
            _sessionSecrets[keyRef] = secret;
            return EncryptionSecretStorageMode.SessionMemory;
        }
        catch (Exception ex)
        {
            var guidance = allowSessionFallback
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

        var stored = _getSecret(keyRef, username, preferKeychain, null);
        if (!string.IsNullOrWhiteSpace(stored))
            return stored;

        return _sessionSecrets.TryGetValue(keyRef, out var sessionSecret)
            ? sessionSecret
            : null;
    }

    public void DeleteSecret(string? keyRef, string username)
    {
        if (string.IsNullOrWhiteSpace(keyRef))
            return;

        _deleteSecret(keyRef, username);
        _sessionSecrets.TryRemove(keyRef, out _);
    }
}
