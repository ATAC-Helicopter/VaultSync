using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupCryptoDescriptorTests
{
    [Fact]
    public void Descriptor_RoundTrips_WithVersionPreserved()
    {
        var descriptor = BackupCryptoDescriptor.Encrypted(
            algorithm: "aes-256-gcm",
            kdfProfile: "argon2id-v1",
            kdfParamRef: "profile-fast",
            formatVersion: 2);

        string json = descriptor.ToMetadataJson(isEncrypted: true);
        var parsed = BackupCryptoDescriptor.FromMetadata(isEncrypted: true, descriptorJson: json);

        Assert.Equal(2, parsed.FormatVersion);
        Assert.Equal("aes-256-gcm", parsed.Algorithm);
        Assert.Equal("argon2id-v1", parsed.KdfProfile);
        Assert.Equal("profile-fast", parsed.KdfParamRef);
    }

    [Fact]
    public void Descriptor_ParsesLegacyPlainMetadata()
    {
        var descriptor = BackupCryptoDescriptor.FromMetadata(isEncrypted: false, descriptorJson: "{}");

        Assert.Equal(BackupCryptoDescriptor.CurrentFormatVersion, descriptor.FormatVersion);
        Assert.Equal("none", descriptor.Algorithm);
        Assert.Equal("none", descriptor.KdfProfile);
        Assert.Equal(string.Empty, descriptor.KdfParamRef);
        Assert.Equal(BackupCryptoDescriptor.PlainMetadataJson, descriptor.ToMetadataJson(isEncrypted: false));
    }

    [Fact]
    public void Descriptor_InvalidEncryptedJson_FallsBackWithoutThrowing()
    {
        var descriptor = BackupCryptoDescriptor.FromMetadata(isEncrypted: true, descriptorJson: "{ not-json }");

        Assert.Equal(BackupCryptoDescriptor.CurrentFormatVersion, descriptor.FormatVersion);
        Assert.Equal("unknown", descriptor.Algorithm);
        Assert.Equal("unknown", descriptor.KdfProfile);
    }

    [Fact]
    public void MetadataStore_UpsertBackup_StripsSecretCryptoFields()
    {
        using var temp = new TempDirectory();
        var store = new MetadataStore(temp.Path);
        store.EnsureSchema();

        store.UpsertBackup(new MetaBackup
        {
            ExternalId = "backup-crypto-1",
            ProjectExternalId = "proj-1",
            SnapshotExternalId = "snap-1",
            CreatedUtc = DateTime.UtcNow,
            Type = "manual",
            TotalBytes = 1234,
            PathRel = "project/backup-1",
            DestinationAlias = "Primary",
            OriginMachineName = "machine-a",
            IsProtected = false,
            IsEncrypted = true,
            KdfParamsJson = """
                            {
                              "formatVersion": 3,
                              "algorithm": "aes-256-gcm",
                              "kdfProfile": "pbkdf2-sha256-v1",
                              "kdfParamRef": "preset-2026-01",
                              "password": "do-not-store",
                              "rawKey": "do-not-store",
                              "salt": "do-not-store"
                            }
                            """
        });

        MetaBackup stored = store.ListBackups().Single(x => x.ExternalId == "backup-crypto-1");
        using var doc = JsonDocument.Parse(stored.KdfParamsJson);
        JsonElement root = doc.RootElement;
        string[] properties = root.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { "algorithm", "formatVersion", "kdfParamRef", "kdfProfile" }, properties);
        Assert.False(root.TryGetProperty("password", out _));
        Assert.False(root.TryGetProperty("rawKey", out _));
        Assert.False(root.TryGetProperty("salt", out _));
        Assert.Equal(3, root.GetProperty("formatVersion").GetInt32());
        Assert.Equal("aes-256-gcm", root.GetProperty("algorithm").GetString());
        Assert.Equal("pbkdf2-sha256-v1", root.GetProperty("kdfProfile").GetString());
        Assert.Equal("preset-2026-01", root.GetProperty("kdfParamRef").GetString());
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
