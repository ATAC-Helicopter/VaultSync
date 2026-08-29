using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using VaultSync.UI.Infrastructure;

namespace VaultSync.UI.Services;

internal readonly record struct RestoreProgressUpdate(
    double Percent,
    string CurrentFile,
    long ProcessedBytes,
    long TotalBytes);

internal sealed class RestoreRecoveryException(
    string message,
    string recoveryDirectory,
    Exception innerException) : IOException(message, innerException)
{
    public string RecoveryDirectory { get; } = recoveryDirectory;
}

internal static class RestoreExecutionService
{
    private const int CopyBufferBytes = 1024 * 1024;

    public static async Task RestoreAsync(
        string sourceDirectory,
        string targetDirectory,
        string? encryptionPassword,
        IReadOnlyList<string>? selectedTopLevelTargets,
        Action<RestoreProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
            throw new ArgumentException("Source directory is required.", nameof(sourceDirectory));
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("Target directory is required.", nameof(targetDirectory));
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"Source directory '{sourceDirectory}' does not exist.");

        cancellationToken.ThrowIfCancellationRequested();
        string stagingRoot = Path.Combine(
            Path.GetTempPath(),
            $"vaultsync-restore-{Guid.NewGuid():N}");
        string extractedRoot = Path.Combine(stagingRoot, "content");
        string rollbackRoot = Path.Combine(stagingRoot, "rollback");
        bool preserveStaging = false;

        try
        {
            string contentRoot = sourceDirectory;
            string plainArchive = Path.Combine(
                sourceDirectory,
                BackupArchiveCryptoService.PlainArchiveFileName);
            string encryptedArchive = Path.Combine(
                sourceDirectory,
                BackupArchiveCryptoService.EncryptedArchiveFileName);

            if (File.Exists(plainArchive))
            {
                Directory.CreateDirectory(extractedRoot);
                await ExtractArchiveAsync(
                    plainArchive,
                    extractedRoot,
                    selectedTopLevelTargets,
                    0,
                    75,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                contentRoot = extractedRoot;
            }
            else if (File.Exists(encryptedArchive))
            {
                if (string.IsNullOrWhiteSpace(encryptionPassword))
                    throw new InvalidOperationException("A password is required to restore encrypted backups.");

                Directory.CreateDirectory(extractedRoot);
                string stagingArchive = Path.Combine(
                    stagingRoot,
                    BackupArchiveCryptoService.PlainArchiveFileName);
                progress?.Invoke(new RestoreProgressUpdate(5, "Decrypting backup...", 0, 0));
                BackupArchiveCryptoService.DecryptArchiveToPlainZip(
                    sourceDirectory,
                    encryptionPassword,
                    stagingArchive,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Invoke(new RestoreProgressUpdate(25, "Decrypting backup...", 0, 0));
                await ExtractArchiveAsync(
                    stagingArchive,
                    extractedRoot,
                    selectedTopLevelTargets,
                    25,
                    75,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                contentRoot = extractedRoot;
            }
            else
            {
                EnsureSelectedTargetsPresent(
                    Directory.EnumerateFileSystemEntries(sourceDirectory)
                        .Select(Path.GetFileName),
                    BuildSelectedTopLevelSet(selectedTopLevelTargets));
            }

            await ApplyTransactionAsync(
                contentRoot,
                targetDirectory,
                rollbackRoot,
                selectedTopLevelTargets,
                File.Exists(plainArchive) || File.Exists(encryptedArchive) ? 75 : 0,
                100,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RestoreRecoveryException ex)
        {
            preserveStaging = IsUnderRoot(stagingRoot, ex.RecoveryDirectory);
            throw;
        }
        finally
        {
            if (!preserveStaging)
                TryDeleteDirectory(stagingRoot);
        }
    }

    private static async Task ExtractArchiveAsync(
        string archivePath,
        string destinationDirectory,
        IReadOnlyList<string>? selectedTopLevelTargets,
        double startPercent,
        double endPercent,
        Action<RestoreProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        SafeZipExtractor.ValidateArchiveShape(archive);
        HashSet<string>? selected = BuildSelectedTopLevelSet(selectedTopLevelTargets);
        EnsureSelectedTargetsPresent(
            archive.Entries.Select(entry => GetTopLevelSegment(entry.FullName)),
            selected);
        ZipArchiveEntry[] entries =
        [
            .. archive.Entries.Where(entry => ShouldInclude(entry.FullName, selected))
        ];
        long totalBytes = entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Sum(entry => Math.Max(0, entry.Length));
        long processedBytes = 0;
        int processedEntries = 0;

        foreach (ZipArchiveEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destinationPath = SafeZipExtractor.GetSafeEntryPath(
                destinationDirectory,
                entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
            }
            else
            {
                string? parent = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);

                await using Stream source = entry.Open();
                await using var destination = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferBytes,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(destination, CopyBufferBytes, cancellationToken)
                    .ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                processedBytes += Math.Max(0, entry.Length);
            }

            processedEntries++;
            double ratio = entries.Length == 0
                ? 1
                : processedEntries / (double)entries.Length;
            progress?.Invoke(new RestoreProgressUpdate(
                startPercent + ((endPercent - startPercent) * ratio),
                entry.FullName,
                processedBytes,
                totalBytes));
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (entries.Length == 0)
            progress?.Invoke(new RestoreProgressUpdate(endPercent, string.Empty, 0, 0));
    }

    private static async Task ApplyTransactionAsync(
        string sourceDirectory,
        string targetDirectory,
        string rollbackDirectory,
        IReadOnlyList<string>? selectedTopLevelTargets,
        double startPercent,
        double endPercent,
        Action<RestoreProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        HashSet<string>? selected = BuildSelectedTopLevelSet(selectedTopLevelTargets);
        string[] files = EnumerateFilesWithoutLinks(sourceDirectory)
            .Where(path => ShouldInclude(Path.GetRelativePath(sourceDirectory, path), selected))
            .ToArray();
        long totalBytes = files.Sum(path => Math.Max(0, new FileInfo(path).Length));
        long processedBytes = 0;
        var journal = new List<RestoreJournalEntry>(files.Length);
        var createdDirectories = new HashSet<string>(GetPathComparer());
        bool targetRootExisted = Directory.Exists(targetDirectory);

        try
        {
            Directory.CreateDirectory(targetDirectory);
            foreach (string sourcePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                string targetPath = CombineUnderRoot(targetDirectory, relativePath);
                SafeZipExtractor.EnsureNoLinkedPathComponents(targetDirectory, targetPath);
                CreateParentDirectories(targetDirectory, targetPath, createdDirectories);

                bool existed = File.Exists(targetPath);
                string? rollbackPath = null;
                if (existed)
                {
                    rollbackPath = CombineUnderRoot(rollbackDirectory, relativePath);
                    string? rollbackParent = Path.GetDirectoryName(rollbackPath);
                    if (!string.IsNullOrWhiteSpace(rollbackParent))
                        Directory.CreateDirectory(rollbackParent);
                    File.Copy(targetPath, rollbackPath, overwrite: false);
                }

                journal.Add(new RestoreJournalEntry(targetPath, rollbackPath));
                string temporaryTarget = $"{targetPath}.{Guid.NewGuid():N}.vaultsync-restore.tmp";
                try
                {
                    await CopyFileAsync(sourcePath, temporaryTarget, cancellationToken)
                        .ConfigureAwait(false);
                    File.Move(temporaryTarget, targetPath, overwrite: true);
                }
                finally
                {
                    TryDeleteFile(temporaryTarget);
                }

                processedBytes += Math.Max(0, new FileInfo(sourcePath).Length);
                double ratio = files.Length == 0 ? 1 : journal.Count / (double)files.Length;
                progress?.Invoke(new RestoreProgressUpdate(
                    startPercent + ((endPercent - startPercent) * ratio),
                    relativePath,
                    processedBytes,
                    totalBytes));
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(targetPath))
                {
                    throw new IOException(
                        $"Restore target disappeared before the applied file could be committed: '{targetPath}'.");
                }
            }

            if (files.Length == 0)
                progress?.Invoke(new RestoreProgressUpdate(endPercent, string.Empty, 0, 0));
        }
        catch (Exception operationError)
        {
            Exception? rollbackError = RollBack(journal, createdDirectories, targetDirectory, targetRootExisted);
            if (rollbackError is not null)
            {
                string recoveryDirectory = PreserveRollbackEvidence(rollbackDirectory);
                throw new RestoreRecoveryException(
                    $"Restore failed and the previous target state could not be fully recovered. " +
                    $"Rollback files were preserved at '{recoveryDirectory}'.",
                    recoveryDirectory,
                    new AggregateException(operationError, rollbackError));
            }

            throw;
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target, CopyBufferBytes, cancellationToken).ConfigureAwait(false);
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Exception? RollBack(
        IReadOnlyList<RestoreJournalEntry> journal,
        IEnumerable<string> createdDirectories,
        string targetDirectory,
        bool targetRootExisted)
    {
        var errors = new List<Exception>();
        foreach (RestoreJournalEntry entry in journal.Reverse())
        {
            try
            {
                if (entry.RollbackPath is null)
                    TryDeleteFile(entry.TargetPath);
                else
                    File.Copy(entry.RollbackPath, entry.TargetPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(ex);
            }
        }

        foreach (string directory in createdDirectories.OrderByDescending(path => path.Length))
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(ex);
            }
        }

        if (!targetRootExisted)
        {
            try
            {
                if (Directory.Exists(targetDirectory) &&
                    !Directory.EnumerateFileSystemEntries(targetDirectory).Any())
                {
                    Directory.Delete(targetDirectory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(ex);
            }
        }

        return errors.Count switch
        {
            0 => null,
            1 => errors[0],
            _ => new AggregateException(errors)
        };
    }

    private static string PreserveRollbackEvidence(string rollbackDirectory)
    {
        if (!Directory.Exists(rollbackDirectory))
            return rollbackDirectory;

        string recoveryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"vaultsync-restore-recovery-{Guid.NewGuid():N}");
        try
        {
            Directory.Move(rollbackDirectory, recoveryDirectory);
            return recoveryDirectory;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.WriteVerbose(
                $"[Restore] Failed to move rollback evidence to '{recoveryDirectory}': {ex.Message}");
            return rollbackDirectory;
        }
    }

    private static IEnumerable<string> EnumerateFilesWithoutLinks(string rootDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(rootDirectory);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(current))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Restore source contains a linked file: '{file}'.");
                yield return file;
            }

            foreach (string directory in Directory.EnumerateDirectories(current))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Restore source contains a linked directory: '{directory}'.");
                pending.Push(directory);
            }
        }
    }

    private static void CreateParentDirectories(
        string rootDirectory,
        string filePath,
        ISet<string> createdDirectories)
    {
        string? parent = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(parent))
            return;

        var missing = new Stack<string>();
        string? current = parent;
        while (!string.IsNullOrWhiteSpace(current) &&
               !Directory.Exists(current) &&
               IsUnderRoot(rootDirectory, current))
        {
            missing.Push(current);
            current = Path.GetDirectoryName(current);
        }

        while (missing.Count > 0)
        {
            string directory = missing.Pop();
            Directory.CreateDirectory(directory);
            createdDirectories.Add(directory);
        }
    }

    private static string CombineUnderRoot(string rootDirectory, string relativePath)
    {
        string normalizedRoot = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!candidate.StartsWith(normalizedRoot, GetPathComparison()))
            throw new InvalidDataException($"Restore path '{relativePath}' escapes the selected target.");
        return candidate;
    }

    private static bool IsUnderRoot(string rootDirectory, string candidate) =>
        Path.GetFullPath(candidate).StartsWith(
            Path.GetFullPath(rootDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar,
            GetPathComparison());

    private static HashSet<string>? BuildSelectedTopLevelSet(
        IReadOnlyList<string>? selectedTopLevelTargets)
    {
        if (selectedTopLevelTargets is null || selectedTopLevelTargets.Count == 0)
            return null;

        var result = new HashSet<string>(GetPathComparer());
        foreach (string value in selectedTopLevelTargets)
        {
            string? normalized = value?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized))
                result.Add(normalized);
        }
        return result.Count == 0 ? null : result;
    }

    private static bool ShouldInclude(string? relativePath, HashSet<string>? selected)
    {
        if (selected is null || selected.Count == 0)
            return true;
        string topLevel = GetTopLevelSegment(relativePath);
        return selected.Contains(topLevel);
    }

    internal static string GetTopLevelSegment(string? relativePath)
    {
        string normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
            return string.Empty;
        int separator = normalized.IndexOf('/');
        return separator >= 0 ? normalized[..separator] : normalized;
    }

    private static void EnsureSelectedTargetsPresent(
        IEnumerable<string?> availableTargets,
        HashSet<string>? selectedTargets)
    {
        if (selectedTargets is null || selectedTargets.Count == 0)
            return;

        var available = new HashSet<string>(
            availableTargets.Where(value => !string.IsNullOrWhiteSpace(value)).OfType<string>(),
            GetPathComparer());
        string[] missing = selectedTargets
            .Where(target => !available.Contains(target))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"Selected restore targets are missing from the backup: {string.Join(", ", missing)}.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.WriteVerbose($"[Restore] Failed to remove staging directory '{path}': {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RuntimeLog.WriteVerbose($"[Restore] Failed to remove temporary file '{path}': {ex.Message}");
        }
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private sealed record RestoreJournalEntry(string TargetPath, string? RollbackPath);
}
