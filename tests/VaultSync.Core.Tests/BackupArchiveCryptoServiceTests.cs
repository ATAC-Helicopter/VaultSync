#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupArchiveCryptoServiceTests
{
    [Fact]
    public void EncryptArchiveInPlace_WritesEncryptedArtifact_AndRemovesPlainArchive()
    {
        var backupFolder = CreateBackupFolderWithZip();
        try
        {
            var service = new BackupArchiveCryptoService();
            var config = new BackupEncryptionConfig { Enabled = true };

            var result = service.EncryptArchiveInPlace(backupFolder, "test-password", config);

            var plainArchive = Path.Combine(backupFolder, BackupArchiveCryptoService.PlainArchiveFileName);
            var encryptedArchive = Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
            var metadataPath = Path.Combine(backupFolder, BackupArchiveCryptoService.MetadataFileName);

            Assert.True(result.IsEncrypted);
            Assert.False(File.Exists(plainArchive));
            Assert.True(File.Exists(encryptedArchive));
            Assert.True(File.Exists(metadataPath));
            Assert.NotEqual("none", result.Descriptor.Algorithm);
        }
        finally
        {
            SafeDelete(backupFolder);
        }
    }

    [Fact]
    public void EncryptedArchive_IsNotReadableAsPlainZip()
    {
        var backupFolder = CreateBackupFolderWithZip();
        try
        {
            var service = new BackupArchiveCryptoService();
            var config = new BackupEncryptionConfig { Enabled = true };
            var result = service.EncryptArchiveInPlace(backupFolder, "test-password", config);

            Assert.ThrowsAny<Exception>(() =>
            {
                using var _ = ZipFile.OpenRead(result.EncryptedArchivePath);
            });
        }
        finally
        {
            SafeDelete(backupFolder);
        }
    }

    [Fact]
    public void TryReadDescriptor_WhenEncryptedMetadataExists_ReturnsEncryptedDescriptor()
    {
        var backupFolder = CreateBackupFolderWithZip();
        try
        {
            var service = new BackupArchiveCryptoService();
            var config = new BackupEncryptionConfig { Enabled = true };
            service.EncryptArchiveInPlace(backupFolder, "test-password", config);

            var found = BackupArchiveCryptoService.TryReadDescriptor(backupFolder, out var descriptor, out var isEncrypted);

            Assert.True(found);
            Assert.True(isEncrypted);
            Assert.NotEqual("none", descriptor.Algorithm);
        }
        finally
        {
            SafeDelete(backupFolder);
        }
    }

    [Fact]
    public void GetStoredArchiveSize_PrefersEncryptedArtifact()
    {
        var backupFolder = CreateBackupFolderWithZip();
        try
        {
            var service = new BackupArchiveCryptoService();
            var config = new BackupEncryptionConfig { Enabled = true };
            var result = service.EncryptArchiveInPlace(backupFolder, "test-password", config);

            var size = BackupArchiveCryptoService.GetStoredArchiveSize(backupFolder);

            Assert.Equal(result.EncryptedBytes, size);
            Assert.True(size > 0);
        }
        finally
        {
            SafeDelete(backupFolder);
        }
    }

    [Fact]
    public void DecryptArchiveToPlainZip_WithValidPassword_RecreatesReadableArchive()
    {
        var backupFolder = CreateBackupFolderWithZip();
        try
        {
            var service = new BackupArchiveCryptoService();
            var config = new BackupEncryptionConfig { Enabled = true };
            service.EncryptArchiveInPlace(backupFolder, "test-password", config);

            var restoredArchive = Path.Combine(backupFolder, "restored.zip");
            service.DecryptArchiveToPlainZip(backupFolder, "test-password", restoredArchive);

            Assert.True(File.Exists(restoredArchive));
            using var archive = ZipFile.OpenRead(restoredArchive);
            var entry = archive.GetEntry("sample.txt");
            Assert.NotNull(entry);
            using var stream = entry!.Open();
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            Assert.Equal("vaultsync encryption test", text);
        }
        finally
        {
            SafeDelete(backupFolder);
        }
    }

    [Fact]
    public void DecryptArchiveToPlainZip_WithWrongPassword_FailsWithExplicitError_AndNoPartialOutput()
    {
        var backupFolder = CreateBackupFolderWithZip();
        try
        {
            var service = new BackupArchiveCryptoService();
            var config = new BackupEncryptionConfig { Enabled = true };
            service.EncryptArchiveInPlace(backupFolder, "test-password", config);

            var restoredArchive = Path.Combine(backupFolder, "restored.zip");
            var ex = Assert.Throws<InvalidOperationException>(() =>
                service.DecryptArchiveToPlainZip(backupFolder, "wrong-password", restoredArchive));

            Assert.Equal(BackupArchiveCryptoService.InvalidPasswordOrCorruptedMessage, ex.Message);
            Assert.False(File.Exists(restoredArchive));
            Assert.False(File.Exists(restoredArchive + ".tmp"));
        }
        finally
        {
            SafeDelete(backupFolder);
        }
    }

    private static string CreateBackupFolderWithZip()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vaultsync-tests-{Guid.NewGuid():N}");
        var backupFolder = Path.Combine(root, "project", "2026-02-09_00-00-00");
        Directory.CreateDirectory(backupFolder);

        var sourceFile = Path.Combine(backupFolder, "sample.txt");
        File.WriteAllText(sourceFile, "vaultsync encryption test");

        var archivePath = Path.Combine(backupFolder, BackupArchiveCryptoService.PlainArchiveFileName);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(sourceFile, "sample.txt");
        }
        File.Delete(sourceFile);

        return backupFolder;
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
