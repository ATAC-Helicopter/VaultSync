using System;
using System.IO;
using System.Threading;
using VaultSync.Core.Config;

namespace VaultSync.Core.Services;

public sealed class BackupKeyRotationService
{
    private readonly BackupArchiveCryptoService _cryptoService;

    public sealed record RotationResult(
        bool Success,
        string CryptoDescriptorJson,
        long TotalBytes);

    public BackupKeyRotationService(BackupArchiveCryptoService? cryptoService = null)
    {
        _cryptoService = cryptoService ?? new BackupArchiveCryptoService();
    }

    public RotationResult RotateEncryptedBackup(
        string backupFolder,
        string oldPassword,
        string newPassword,
        BackupEncryptionConfig targetConfig,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(backupFolder))
            throw new ArgumentException("Backup folder is required.", nameof(backupFolder));
        if (string.IsNullOrWhiteSpace(oldPassword))
            throw new ArgumentException("Old password is required.", nameof(oldPassword));
        if (string.IsNullOrWhiteSpace(newPassword))
            throw new ArgumentException("New password is required.", nameof(newPassword));
        if (!Directory.Exists(backupFolder))
            throw new DirectoryNotFoundException($"Backup folder '{backupFolder}' was not found.");

        var sourceEncryptedPath = Path.Combine(backupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
        if (!File.Exists(sourceEncryptedPath))
            throw new FileNotFoundException("Encrypted backup artifact not found.", sourceEncryptedPath);

        var stagingRoot = Path.Combine(Path.GetTempPath(), $"vaultsync-rotate-{Guid.NewGuid():N}");
        var stagingBackupFolder = Path.Combine(stagingRoot, "backup");
        var rollbackSuffix = $".rotatebak-{Guid.NewGuid():N}";
        var rollbackEncryptedPath = sourceEncryptedPath + rollbackSuffix;
        var sourceMetadataPath = Path.Combine(backupFolder, BackupArchiveCryptoService.MetadataFileName);
        var rollbackMetadataPath = sourceMetadataPath + rollbackSuffix;
        var movedEncryptedToRollback = false;
        var movedMetadataToRollback = false;
        var wroteNewEncrypted = false;
        var wroteNewMetadata = false;

        try
        {
            Directory.CreateDirectory(stagingBackupFolder);
            var stagingPlainArchive = Path.Combine(stagingBackupFolder, BackupArchiveCryptoService.PlainArchiveFileName);
            _cryptoService.DecryptArchiveToPlainZip(backupFolder, oldPassword, stagingPlainArchive, ct);

            var encryptionResult = _cryptoService.EncryptArchiveInPlace(
                stagingBackupFolder,
                newPassword,
                targetConfig,
                ct);

            var stagedEncryptedPath = Path.Combine(stagingBackupFolder, BackupArchiveCryptoService.EncryptedArchiveFileName);
            var stagedMetadataPath = Path.Combine(stagingBackupFolder, BackupArchiveCryptoService.MetadataFileName);
            if (!File.Exists(stagedEncryptedPath))
                throw new InvalidOperationException("Staging rotation output is missing encrypted archive.");

            // Swap atomically with rollback safety: original files are restored on any write failure.
            File.Move(sourceEncryptedPath, rollbackEncryptedPath);
            movedEncryptedToRollback = true;
            if (File.Exists(sourceMetadataPath))
            {
                File.Move(sourceMetadataPath, rollbackMetadataPath);
                movedMetadataToRollback = true;
            }

            File.Move(stagedEncryptedPath, sourceEncryptedPath);
            wroteNewEncrypted = true;
            if (File.Exists(stagedMetadataPath))
            {
                File.Move(stagedMetadataPath, sourceMetadataPath);
                wroteNewMetadata = true;
            }

            TryDeleteFile(rollbackEncryptedPath);
            TryDeleteFile(rollbackMetadataPath);

            return new RotationResult(
                true,
                encryptionResult.Descriptor.ToMetadataJson(isEncrypted: true),
                encryptionResult.EncryptedBytes);
        }
        catch
        {
            // Atomic rollback: if we moved originals out, always restore them on any failure.
            if (movedEncryptedToRollback && File.Exists(rollbackEncryptedPath))
            {
                if (wroteNewEncrypted && File.Exists(sourceEncryptedPath))
                    TryDeleteFile(sourceEncryptedPath);
                TryMoveForRollback(rollbackEncryptedPath, sourceEncryptedPath);
            }

            if (movedMetadataToRollback && File.Exists(rollbackMetadataPath))
            {
                if (wroteNewMetadata && File.Exists(sourceMetadataPath))
                    TryDeleteFile(sourceMetadataPath);
                TryMoveForRollback(rollbackMetadataPath, sourceMetadataPath);
            }

            throw;
        }
        finally
        {
            TryDeleteFile(rollbackEncryptedPath);
            TryDeleteFile(rollbackMetadataPath);
            TryDeleteDirectory(stagingRoot);
        }
    }

    private static void TryMoveForRollback(string from, string to)
    {
        try
        {
            if (File.Exists(from))
                File.Move(from, to);
        }
        catch
        {
            // Best effort rollback.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static void TryDeleteDirectory(string path)
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
