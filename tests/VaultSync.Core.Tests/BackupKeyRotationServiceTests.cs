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
        var backupFolder = CreateEncryptedBackupFolder("old-password");
        try
        {
            var service = new BackupKeyRotationService();
            var result = service.RotateEncryptedBackup(
                backupFolder,
                oldPassword: "old-password",
                newPassword: "new-password",
                new BackupEncryptionConfig { Enabled = true });

            Assert.True(result.Success);
            Assert.True(result.TotalBytes > 0);

            var crypto = new BackupArchiveCryptoService();
            var restoredWithNew = Path.Combine(backupFolder, "restored-new.zip");
            crypto.DecryptArchiveToPlainZip(backupFolder, "new-password", restoredWithNew);
            Assert.True(File.Exists(restoredWithNew));

            var restoredWithOld = Path.Combine(backupFolder, "restored-old.zip");
            var ex = Assert.Throws<InvalidOperationException>(() =>
                crypto.DecryptArchiveToPlainZip(backupFolder, "old-password", restoredWithOld));
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
        var backupFolder = CreateEncryptedBackupFolder("old-password");
        try
        {
            var archivePath = Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
            var before = File.ReadAllBytes(archivePath);

            var service = new BackupKeyRotationService();
            var ex = Assert.Throws<InvalidOperationException>(() =>
                service.RotateEncryptedBackup(
                    backupFolder,
                    oldPassword: "wrong-password",
                    newPassword: "new-password",
                    new BackupEncryptionConfig { Enabled = true }));
            Assert.Equal(BackupArchiveCryptoService.InvalidPasswordOrCorruptedMessage, ex.Message);

            var after = File.ReadAllBytes(archivePath);
            Assert.Equal(before, after);

            var crypto = new BackupArchiveCryptoService();
            var restoredWithOld = Path.Combine(backupFolder, "restored-old.zip");
            crypto.DecryptArchiveToPlainZip(backupFolder, "old-password", restoredWithOld);
            Assert.True(File.Exists(restoredWithOld));
        }
        finally
        {
            SafeDelete(backupFolder);
        }
    }

    private static string CreateEncryptedBackupFolder(string password)
    {
        var root = Path.Combine(Path.GetTempPath(), $"vaultsync-rotate-tests-{Guid.NewGuid():N}");
        var backupFolder = Path.Combine(root, "project", "2026-02-09_00-00-00");
        Directory.CreateDirectory(backupFolder);

        var sourceFile = Path.Combine(backupFolder, "sample.txt");
        File.WriteAllText(sourceFile, "vaultsync key rotation test");

        var archivePath = Path.Combine(backupFolder, BackupArchiveCryptoService.PlainArchiveFileName);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(sourceFile, "sample.txt");
        }
        File.Delete(sourceFile);

        var crypto = new BackupArchiveCryptoService();
        crypto.EncryptArchiveInPlace(backupFolder, password, new BackupEncryptionConfig { Enabled = true });

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
