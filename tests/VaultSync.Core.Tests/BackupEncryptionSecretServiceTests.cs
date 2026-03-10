#nullable enable
using System;
using System.Collections.Generic;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupEncryptionSecretServiceTests
{
    [Fact]
    public void SaveSecret_SecureStoreSuccess_UsesSecureStore()
    {
        var secureStore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var service = CreateService(
            ensureKeyRef: (existing, hint) => string.IsNullOrWhiteSpace(existing) ? $"ref-{hint}" : existing!,
            getSecret: (keyRef, _, _, fallback) =>
                !string.IsNullOrWhiteSpace(keyRef) && secureStore.TryGetValue(keyRef, out var value) ? value : fallback,
            saveSecret: (keyRef, _, secret, _) => secureStore[keyRef] = secret,
            deleteSecret: (_, _) => { });

        var storage = service.SaveSecret(
            keyRef: "enc-ref",
            username: "user",
            secret: "top-secret",
            allowSessionFallback: true,
            fallbackConfirmed: true);

        Assert.Equal(EncryptionSecretStorageMode.SecureStore, storage);
        Assert.Equal("top-secret", service.GetSecret("enc-ref", "user"));
    }

    [Fact]
    public void SaveSecret_WhenSecureStoreFails_RequiresExplicitFallbackConfirmation()
    {
        var service = CreateService(
            ensureKeyRef: (existing, hint) => string.IsNullOrWhiteSpace(existing) ? $"ref-{hint}" : existing!,
            getSecret: (_, _, _, fallback) => fallback,
            saveSecret: (_, _, _, _) => throw new InvalidOperationException("Secure store unavailable."),
            deleteSecret: (_, _) => { });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.SaveSecret(
                keyRef: "enc-ref",
                username: "user",
                secret: "top-secret",
                allowSessionFallback: true,
                fallbackConfirmed: false));

        Assert.Contains("requires explicit confirmation", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(service.GetSecret("enc-ref", "user"));
    }

    [Fact]
    public void SaveSecret_WhenConfirmedFallback_StoresInSessionMemory()
    {
        var service = CreateService(
            ensureKeyRef: (existing, hint) => string.IsNullOrWhiteSpace(existing) ? $"ref-{hint}" : existing!,
            getSecret: (_, _, _, fallback) => fallback,
            saveSecret: (_, _, _, _) => throw new InvalidOperationException("Secure store unavailable."),
            deleteSecret: (_, _) => { });

        var storage = service.SaveSecret(
            keyRef: "enc-ref",
            username: "user",
            secret: "session-secret",
            allowSessionFallback: true,
            fallbackConfirmed: true);

        Assert.Equal(EncryptionSecretStorageMode.SessionMemory, storage);
        Assert.Equal("session-secret", service.GetSecret("enc-ref", "user"));
    }

    [Fact]
    public void DeleteSecret_RemovesSessionCopy()
    {
        var service = CreateService(
            ensureKeyRef: (existing, hint) => string.IsNullOrWhiteSpace(existing) ? $"ref-{hint}" : existing!,
            getSecret: (_, _, _, fallback) => fallback,
            saveSecret: (_, _, _, _) => throw new InvalidOperationException("Secure store unavailable."),
            deleteSecret: (_, _) => { });

        var storage = service.SaveSecret(
            keyRef: "enc-ref",
            username: "user",
            secret: "session-secret",
            allowSessionFallback: true,
            fallbackConfirmed: true);
        Assert.Equal(EncryptionSecretStorageMode.SessionMemory, storage);

        service.DeleteSecret("enc-ref", "user");
        Assert.Null(service.GetSecret("enc-ref", "user"));
    }

    [Fact]
    public void SaveSecret_SecureStoreSuccess_ClearsPriorSessionFallback()
    {
        var secureStore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var shouldFailSave = true;
        var service = CreateService(
            ensureKeyRef: (existing, hint) => string.IsNullOrWhiteSpace(existing) ? $"ref-{hint}" : existing!,
            getSecret: (keyRef, _, _, fallback) =>
                !string.IsNullOrWhiteSpace(keyRef) && secureStore.TryGetValue(keyRef, out var value) ? value : fallback,
            saveSecret: (keyRef, _, secret, _) =>
            {
                if (shouldFailSave)
                    throw new InvalidOperationException("Secure store unavailable.");

                secureStore[keyRef] = secret;
            },
            deleteSecret: (_, _) => { });

        var fallbackStorage = service.SaveSecret(
            keyRef: "enc-ref",
            username: "user",
            secret: "session-secret",
            allowSessionFallback: true,
            fallbackConfirmed: true);
        Assert.Equal(EncryptionSecretStorageMode.SessionMemory, fallbackStorage);
        Assert.Equal("session-secret", service.GetSecret("enc-ref", "user"));

        shouldFailSave = false;
        var secureStorage = service.SaveSecret(
            keyRef: "enc-ref",
            username: "user",
            secret: "secure-secret",
            allowSessionFallback: true,
            fallbackConfirmed: true);

        Assert.Equal(EncryptionSecretStorageMode.SecureStore, secureStorage);
        Assert.Equal("secure-secret", service.GetSecret("enc-ref", "user"));
    }

    [Fact]
    public void ClearSessionSecrets_RemovesAllFallbackSecrets()
    {
        var service = CreateService(
            ensureKeyRef: (existing, hint) => string.IsNullOrWhiteSpace(existing) ? $"ref-{hint}" : existing!,
            getSecret: (_, _, _, fallback) => fallback,
            saveSecret: (_, _, _, _) => throw new InvalidOperationException("Secure store unavailable."),
            deleteSecret: (_, _) => { });

        service.SaveSecret("enc-ref-1", "user", "secret-1", allowSessionFallback: true, fallbackConfirmed: true);
        service.SaveSecret("enc-ref-2", "user", "secret-2", allowSessionFallback: true, fallbackConfirmed: true);

        service.ClearSessionSecrets();

        Assert.Null(service.GetSecret("enc-ref-1", "user"));
        Assert.Null(service.GetSecret("enc-ref-2", "user"));
    }

    [Fact]
    public void EnsureSecretRef_ReturnsExistingReferenceWhenProvided()
    {
        var service = CreateService(
            ensureKeyRef: (existing, hint) => string.IsNullOrWhiteSpace(existing) ? $"ref-{hint}" : existing!,
            getSecret: (_, _, _, fallback) => fallback,
            saveSecret: (_, _, _, _) => { },
            deleteSecret: (_, _) => { });

        var keyRef = service.EnsureSecretRef("existing-ref", "global");
        Assert.Equal("existing-ref", keyRef);
    }

    private static BackupEncryptionSecretService CreateService(
        Func<string?, string, string> ensureKeyRef,
        Func<string?, string, bool, string?, string?> getSecret,
        Action<string, string, string, bool> saveSecret,
        Action<string?, string> deleteSecret)
    {
        return new BackupEncryptionSecretService(
            ensureKeyRef,
            getSecret,
            saveSecret,
            deleteSecret);
    }
}
