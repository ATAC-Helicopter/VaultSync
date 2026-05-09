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
        string backupFolder = CreateBackupFolderWithZip();
        try
        {
            var service = new BackupArchiveCryptoService();
            var config = new BackupEncryptionConfig { Enabled = true };

            BackupArchiveCryptoService.EncryptionResult result = BackupArchiveCryptoService.EncryptArchiveInPlace(backupFolder, "test-password", config);

            string plainArchive = Path.Combine(backupFolder, BackupArchiveCryptoService.PlainArchiveFileName);
            string encryptedArchive = Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
            string metadataPath = Path.Combine(backupFolder, BackupArchiveCryptoService.MetadataFileName);

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
        string backupFolder = CreateBackupFolderWithZip();
        try
        {
            var service = new BackupArchiveCryptoService();
            var config = new BackupEncryptionConfig { Enabled = true };
            BackupArchiveCryptoService.EncryptionResult result = BackupArchiveCryptoService.EncryptArchiveInPlace(backupFolder, "test-password", config);

            Assert.ThrowsAny<Exception>(() =>
            {
                using ZipArchive _ = ZipFile.OpenRead(result.EncryptedArchivePath);
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
        string backupFolder = CreateBackupFolderWithZip();
        try
        {
            var service = new BackupArchiveCryptoService();
            var config = new BackupEncryptionConfig { Enabled = true };
            BackupArchiveCryptoService.EncryptArchiveInPlace(backupFolder, "test-password", config);

            bool found = BackupArchiveCryptoService.TryReadDescriptor(backupFolder, out Models.BackupCryptoDescriptor? descriptor, out bool isEncrypted);

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
        string backupFolder = CreateBackupFolderWithZip();
        try
        {
            var service = new BackupArchiveCryptoService();
            var config = new BackupEncryptionConfig { Enabled = true };
            BackupArchiveCryptoService.EncryptionResult result = BackupArchiveCryptoService.EncryptArchiveInPlace(backupFolder, "test-password", config);

            long size = BackupArchiveCryptoService.GetStoredArchiveSize(backupFolder);

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
        string backupFolder = CreateBackupFolderWithZip();
        try
        {
            var service = new BackupArchiveCryptoService();
            var config = new BackupEncryptionConfig { Enabled = true };
            BackupArchiveCryptoService.EncryptArchiveInPlace(backupFolder, "test-password", config);

            string restoredArchive = Path.Combine(backupFolder, "restored.zip");
            BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, "test-password", restoredArchive);

            Assert.True(File.Exists(restoredArchive));
            using ZipArchive archive = ZipFile.OpenRead(restoredArchive);
            ZipArchiveEntry? entry = archive.GetEntry("sample.txt");
            Assert.NotNull(entry);
            using Stream stream = entry!.Open();
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
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
        string backupFolder = CreateBackupFolderWithZip();
        try
        {
            var service = new BackupArchiveCryptoService();
            var config = new BackupEncryptionConfig { Enabled = true };
            BackupArchiveCryptoService.EncryptArchiveInPlace(backupFolder, "test-password", config);

            string restoredArchive = Path.Combine(backupFolder, "restored.zip");
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, "wrong-password", restoredArchive));

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
        string root = Path.Combine(Path.GetTempPath(), $"vaultsync-tests-{Guid.NewGuid():N}");
        string backupFolder = Path.Combine(root, "project", "2026-02-09_00-00-00");
        Directory.CreateDirectory(backupFolder);

        string sourceFile = Path.Combine(backupFolder, "sample.txt");
        File.WriteAllText(sourceFile, "vaultsync encryption test");

        string archivePath = Path.Combine(backupFolder, BackupArchiveCryptoService.PlainArchiveFileName);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
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
