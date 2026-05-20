#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupKeyRotationServiceTests
{
    [Fact]
    public void RotateEncryptedBackup_ReencryptsWithNewPassword_AndInvalidatesOldPassword()
    {
        string backupFolder = CreateEncryptedBackupFolder("old-password");
        try
        {
            var service = new BackupKeyRotationService();
            BackupKeyRotationService.RotationResult result = BackupKeyRotationService.RotateEncryptedBackup(
                backupFolder,
                oldPassword: "old-password",
                newPassword: "new-password",
                new BackupEncryptionConfig { Enabled = true });

            Assert.True(result.Success);
            Assert.True(result.TotalBytes > 0);

            var crypto = new BackupArchiveCryptoService();
            string restoredWithNew = Path.Combine(backupFolder, "restored-new.zip");
            BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, "new-password", restoredWithNew);
            Assert.True(File.Exists(restoredWithNew));

            string restoredWithOld = Path.Combine(backupFolder, "restored-old.zip");
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, "old-password", restoredWithOld));
            Assert.Equal(BackupArchiveCryptoService.InvalidPasswordOrCorruptedMessage, ex.Message);
        }
        finally
        {
            SafeDelete(backupFolder);
        }
    }

    [Fact]
    public void RotateEncryptedBackup_WithWrongOldPassword_LeavesOriginalArchiveIntact()
    {
        string backupFolder = CreateEncryptedBackupFolder("old-password");
        try
        {
            string archivePath = Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
            byte[] before = File.ReadAllBytes(archivePath);

            var service = new BackupKeyRotationService();
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                BackupKeyRotationService.RotateEncryptedBackup(
                    backupFolder,
                    oldPassword: "wrong-password",
                    newPassword: "new-password",
                    new BackupEncryptionConfig { Enabled = true }));
            Assert.Equal(BackupArchiveCryptoService.InvalidPasswordOrCorruptedMessage, ex.Message);

            byte[] after = File.ReadAllBytes(archivePath);
            Assert.Equal(before, after);

            var crypto = new BackupArchiveCryptoService();
            string restoredWithOld = Path.Combine(backupFolder, "restored-old.zip");
            BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, "old-password", restoredWithOld);
            Assert.True(File.Exists(restoredWithOld));
        }
        finally
        {
            SafeDelete(backupFolder);
        }
    }

    private static string CreateEncryptedBackupFolder(string password)
    {
        string root = Path.Combine(Path.GetTempPath(), $"vaultsync-rotate-tests-{Guid.NewGuid():N}");
        string backupFolder = Path.Combine(root, "project", "2026-02-09_00-00-00");
        Directory.CreateDirectory(backupFolder);

        string sourceFile = Path.Combine(backupFolder, "sample.txt");
        File.WriteAllText(sourceFile, "vaultsync key rotation test");

        string archivePath = Path.Combine(backupFolder, BackupArchiveCryptoService.PlainArchiveFileName);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(sourceFile, "sample.txt");
        }
        File.Delete(sourceFile);

        var crypto = new BackupArchiveCryptoService();
        BackupArchiveCryptoService.EncryptArchiveInPlace(backupFolder, password, new BackupEncryptionConfig { Enabled = true });

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
            // Best effort cleanup.
        }
    }
}
