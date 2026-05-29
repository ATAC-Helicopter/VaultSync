#nullable enable
using System.IO;
using System.IO.Compression;
using VaultSync.Core.Config;
using VaultSync.Core.Services;

namespace VaultSync.Core.Tests.TestSupport;

public static class BackupArchiveTestFactory
{
    public const string DefaultEntryName = "sample.txt";
    public const string DefaultContent = "vaultsync encryption test";

    public static string CreatePlainBackupFolder(string root, string content = DefaultContent)
    {
        string backupFolder = Path.Combine(root, "project", "2026-02-09_00-00-00");
        Directory.CreateDirectory(backupFolder);

        string sourceFile = Path.Combine(backupFolder, DefaultEntryName);
        File.WriteAllText(sourceFile, content);

        string archivePath = Path.Combine(backupFolder, BackupArchiveCryptoService.PlainArchiveFileName);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(sourceFile, DefaultEntryName);
        }

        File.Delete(sourceFile);
        return backupFolder;
    }

    public static string CreateEncryptedBackupFolder(
        string root,
        string password,
        string content = DefaultContent)
    {
        string backupFolder = CreatePlainBackupFolder(root, content);
        BackupArchiveCryptoService.EncryptArchiveInPlace(
            backupFolder,
            password,
            new BackupEncryptionConfig { Enabled = true });
        return backupFolder;
    }
}
