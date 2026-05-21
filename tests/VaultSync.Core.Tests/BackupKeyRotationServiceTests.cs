#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupKeyRotationServiceTests
{
    [Fact]
    public void RotateEncryptedBackup_ReencryptsWithNewPassword_AndInvalidatesOldPassword()
    {
        using var root = new TempDirectory();
        string backupFolder = CreateEncryptedBackupFolder(root.Path, "old-password");

        BackupKeyRotationService.RotationResult result = BackupKeyRotationService.RotateEncryptedBackup(
            backupFolder,
            oldPassword: "old-password",
            newPassword: "new-password",
            new BackupEncryptionConfig { Enabled = true });

        Assert.True(result.Success);
        Assert.True(result.TotalBytes > 0);

        string restoredWithNew = Path.Combine(backupFolder, "restored-new.zip");
        BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, "new-password", restoredWithNew);
        Assert.True(File.Exists(restoredWithNew));

        string restoredWithOld = Path.Combine(backupFolder, "restored-old.zip");
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, "old-password", restoredWithOld));
        Assert.Equal(BackupArchiveCryptoService.InvalidPasswordOrCorruptedMessage, ex.Message);
    }

    [Fact]
    public void RotateEncryptedBackup_WithWrongOldPassword_LeavesOriginalArchiveIntact()
    {
        using var root = new TempDirectory();
        string backupFolder = CreateEncryptedBackupFolder(root.Path, "old-password");
        string archivePath = Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
        byte[] before = File.ReadAllBytes(archivePath);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            BackupKeyRotationService.RotateEncryptedBackup(
                backupFolder,
                oldPassword: "wrong-password",
                newPassword: "new-password",
                new BackupEncryptionConfig { Enabled = true }));
        Assert.Equal(BackupArchiveCryptoService.InvalidPasswordOrCorruptedMessage, ex.Message);

        byte[] after = File.ReadAllBytes(archivePath);
        Assert.Equal(before, after);

        string restoredWithOld = Path.Combine(backupFolder, "restored-old.zip");
        BackupArchiveCryptoService.DecryptArchiveToPlainZip(backupFolder, "old-password", restoredWithOld);
        Assert.True(File.Exists(restoredWithOld));
    }

    private static string CreateEncryptedBackupFolder(string root, string password)
    {
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
}
