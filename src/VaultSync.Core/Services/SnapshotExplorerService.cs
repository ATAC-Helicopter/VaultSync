using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VaultSync.Core.Models;
using VaultSync.Core.Recoverability;

namespace VaultSync.Core.Services;

public sealed class SnapshotExplorerService
{
    private const char ArchivePathSeparator = '/';
    private const int DefaultPreviewBytes = 256 * 1024;
    private const double MaxBinaryControlCharacterRatio = 0.02;
    private const string UnsupportedPreviewMessage = "Preview is available for text-like files only.";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static SnapshotFileInventory BuildFileInventory(
        string backupRoot,
        int maxFiles = 5_000,
        CancellationToken cancellationToken = default)
    {
        if (maxFiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFiles));

        BackupSource source = ResolveSource(backupRoot);
        if (source.Kind == SnapshotExplorerSourceKind.EncryptedArchive)
            return new SnapshotFileInventory(source.Kind, [], IsTruncated: false);

        var files = new List<FileEntry>(Math.Min(maxFiles, 1_024));
        bool truncated = source.Kind == SnapshotExplorerSourceKind.Archive
            ? AddArchiveInventory(source.Path, files, maxFiles, cancellationToken)
            : AddFolderInventory(source.Path, files, maxFiles, cancellationToken);
        return new SnapshotFileInventory(source.Kind, files, truncated);
    }

    public static async Task<StoredContentEvidence> ReadStoredFileEvidenceAsync(
        string backupRoot,
        IReadOnlyCollection<FileEntry> expectedFiles,
        int maximumFiles,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedFiles);
        if (maximumFiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumFiles));
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        BackupSource source = ResolveSource(backupRoot);
        if (source.Kind == SnapshotExplorerSourceKind.EncryptedArchive)
            return new StoredContentEvidence(source.Kind, new Dictionary<string, StoredFileObservation>(), false, 0);

        return source.Kind == SnapshotExplorerSourceKind.Archive
            ? await ReadArchiveEvidenceAsync(
                source.Path,
                expectedFiles,
                maximumFiles,
                maximumBytes,
                cancellationToken).ConfigureAwait(false)
            : await ReadFolderEvidenceAsync(
                source.Path,
                expectedFiles,
                maximumFiles,
                maximumBytes,
                cancellationToken).ConfigureAwait(false);
    }

    private static async Task<StoredContentEvidence> ReadFolderEvidenceAsync(
        string root,
        IReadOnlyCollection<FileEntry> expectedFiles,
        int maximumFiles,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var observations = new Dictionary<string, StoredFileObservation>(StringComparer.Ordinal);
        long bytesRead = 0;
        int filesExamined = 0;
        bool limited = false;
        foreach (FileEntry file in expectedFiles.OrderBy(item => item.RelPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = NormalizeExplorerPath(file.RelPath, allowEmpty: false);
            if (filesExamined >= maximumFiles)
            {
                limited = true;
                observations[relative] = LimitedObservation(relative);
                continue;
            }
            filesExamined++;

            string path = ResolvePathUnderRoot(root, relative);
            if (!File.Exists(path))
            {
                observations[relative] = MissingObservation(relative);
                continue;
            }

            try
            {
                EnsureNoLinkedSourcePathComponents(root, path);
                var info = new FileInfo(path);
                if (WouldExceedLimit(bytesRead, info.Length, maximumBytes))
                {
                    limited = true;
                    observations[relative] = LimitedObservation(relative);
                    continue;
                }

                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                bytesRead += info.Length;
                observations[relative] = new StoredFileObservation(
                    relative,
                    Exists: true,
                    info.Length,
                    info.LastWriteTimeUtc,
                    Convert.ToHexString(hash),
                    WasRead: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                observations[relative] = new StoredFileObservation(
                    relative,
                    Exists: true,
                    Size: null,
                    ModifiedUtc: null,
                    HashSha256: null,
                    WasRead: false,
                    FailureCode: ClassifyReadFailure(ex));
            }
        }

        return new StoredContentEvidence(SnapshotExplorerSourceKind.Folder, observations, limited, bytesRead);
    }

    private static async Task<StoredContentEvidence> ReadArchiveEvidenceAsync(
        string archivePath,
        IReadOnlyCollection<FileEntry> expectedFiles,
        int maximumFiles,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using ZipArchive archive = await ZipFile.OpenReadAsync(archivePath, cancellationToken)
            .ConfigureAwait(false);
        ValidateUniqueArchiveFilePaths(archive);
        IReadOnlyDictionary<string, ZipArchiveEntry> entries = archive.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .ToDictionary(entry => NormalizeArchiveEntryPath(entry.FullName), StringComparer.Ordinal);
        var observations = new Dictionary<string, StoredFileObservation>(StringComparer.Ordinal);
        long bytesRead = 0;
        int filesExamined = 0;
        bool limited = false;
        foreach (FileEntry file in expectedFiles.OrderBy(item => item.RelPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = NormalizeExplorerPath(file.RelPath, allowEmpty: false);
            if (filesExamined >= maximumFiles)
            {
                limited = true;
                observations[relative] = LimitedObservation(relative);
                continue;
            }
            filesExamined++;

            if (!entries.TryGetValue(relative, out ZipArchiveEntry? entry))
            {
                observations[relative] = MissingObservation(relative);
                continue;
            }

            if (WouldExceedLimit(bytesRead, entry.Length, maximumBytes))
            {
                limited = true;
                observations[relative] = LimitedObservation(relative);
                continue;
            }

            try
            {
                await using Stream stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
                byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                bytesRead += entry.Length;
                observations[relative] = new StoredFileObservation(
                    relative,
                    Exists: true,
                    entry.Length,
                    entry.LastWriteTime.UtcDateTime,
                    Convert.ToHexString(hash),
                    WasRead: true);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                observations[relative] = new StoredFileObservation(
                    relative,
                    Exists: true,
                    Size: entry.Length,
                    ModifiedUtc: entry.LastWriteTime.UtcDateTime,
                    HashSha256: null,
                    WasRead: false,
                    FailureCode: ClassifyReadFailure(ex));
            }
        }

        return new StoredContentEvidence(SnapshotExplorerSourceKind.Archive, observations, limited, bytesRead);
    }

    private static bool WouldExceedLimit(long consumed, long next, long limit) =>
        next < 0 || consumed > limit - next;

    private static StoredFileObservation MissingObservation(string path) =>
        new(path, Exists: false, Size: null, ModifiedUtc: null, HashSha256: null, WasRead: false, "object_missing");

    private static StoredFileObservation LimitedObservation(string path) =>
        new(path, Exists: false, Size: null, ModifiedUtc: null, HashSha256: null, WasRead: false, "verification_limit_reached");

    private static string ClassifyReadFailure(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "object_access_denied",
        InvalidDataException => "object_unsafe",
        _ => "object_read_failed"
    };

    public static IReadOnlySet<string> FindTextEquivalentFiles(
        string olderBackupRoot,
        string newerBackupRoot,
        IEnumerable<string> relativePaths,
        int maxBytesPerFile = DefaultPreviewBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        if (maxBytesPerFile <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytesPerFile));

        using var olderReader = new TextContentReader(ResolveSource(olderBackupRoot));
        using var newerReader = new TextContentReader(ResolveSource(newerBackupRoot));
        if (!olderReader.CanRead || !newerReader.CanRead)
            return new HashSet<string>(StringComparer.Ordinal);

        var equivalent = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in relativePaths
                     .Select(item => NormalizeExplorerPath(item, allowEmpty: false))
                     .Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!olderReader.TryReadCompleteText(path, maxBytesPerFile, out string olderText) ||
                !newerReader.TryReadCompleteText(path, maxBytesPerFile, out string newerText))
            {
                continue;
            }

            if (string.Equals(
                    NormalizeLineEndings(olderText),
                    NormalizeLineEndings(newerText),
                    StringComparison.Ordinal))
            {
                equivalent.Add(path);
            }
        }

        return equivalent;
    }

    private static bool AddFolderInventory(
        string backupRoot,
        List<FileEntry> files,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        pending.Push(backupRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            foreach (string childDirectory in Directory.EnumerateDirectories(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0)
                    pending.Push(childDirectory);
            }

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsInternalBackupArtifact(Path.GetFileName(file)) ||
                    (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (files.Count >= maxFiles)
                    return true;

                var info = new FileInfo(file);
                files.Add(new FileEntry(
                    ToExplorerPath(Path.GetRelativePath(backupRoot, file)),
                    Math.Max(0, info.Length),
                    info.LastWriteTimeUtc,
                    string.Empty));
            }
        }

        return false;
    }

    private static bool AddArchiveInventory(
        string archivePath,
        List<FileEntry> files,
        int maxFiles,
        CancellationToken cancellationToken)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ValidateUniqueArchiveFilePaths(archive);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name) || IsInternalBackupArtifact(entry.Name))
                continue;
            if (files.Count >= maxFiles)
                return true;

            files.Add(new FileEntry(
                NormalizeArchiveEntryPath(entry.FullName),
                Math.Max(0, entry.Length),
                entry.LastWriteTime.UtcDateTime,
                string.Empty));
        }

        return false;
    }

    public static SnapshotExplorerResult List(string backupRoot, string? folderPath = null, string? search = null)
    {
        BackupSource source = ResolveSource(backupRoot);
        string safeFolder = NormalizeExplorerPath(folderPath, allowEmpty: true);
        string searchTerm = search?.Trim() ?? string.Empty;

        IReadOnlyList<SnapshotExplorerEntry> entries = source.Kind switch
        {
            SnapshotExplorerSourceKind.Folder => ListFolder(source.Path, safeFolder, searchTerm),
            SnapshotExplorerSourceKind.Archive => ListArchive(source.Path, safeFolder, searchTerm),
            SnapshotExplorerSourceKind.EncryptedArchive => [],
            _ => []
        };

        return new SnapshotExplorerResult(source.Kind, safeFolder, entries);
    }

    public static SnapshotPreviewResult PreviewText(string backupRoot, string relativePath, int maxBytes = DefaultPreviewBytes)
    {
        if (maxBytes <= 0)
            maxBytes = DefaultPreviewBytes;

        string safePath = NormalizeExplorerPath(relativePath, allowEmpty: false);
        BackupSource source = ResolveSource(backupRoot);
        if (source.Kind == SnapshotExplorerSourceKind.EncryptedArchive)
            return SnapshotPreviewResult.Failure("Encrypted backup preview is not available in Snapshot Explorer yet.");

        try
        {
            return source.Kind == SnapshotExplorerSourceKind.Archive
                ? PreviewArchiveEntry(source.Path, safePath, maxBytes)
                : PreviewFolderFile(source.Path, safePath, maxBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return SnapshotPreviewResult.Failure($"Preview failed: {ex.Message}");
        }
    }

    public static SnapshotRestoreSelectionResult RestoreSelection(
        string backupRoot,
        string targetRoot,
        IEnumerable<string> relativePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        string normalizedTargetRoot = NormalizeRoot(targetRoot);
        Directory.CreateDirectory(normalizedTargetRoot);

        string[] selectedPaths = [.. relativePaths
            .Select(path => NormalizeExplorerPath(path, allowEmpty: false))
            .Distinct(StringComparer.Ordinal)];
        if (selectedPaths.Length == 0)
            return new SnapshotRestoreSelectionResult(0, 0);

        BackupSource source = ResolveSource(backupRoot);
        if (source.Kind == SnapshotExplorerSourceKind.EncryptedArchive)
            throw new InvalidOperationException("Encrypted backup restore from Snapshot Explorer is not available yet.");

        return source.Kind == SnapshotExplorerSourceKind.Archive
            ? RestoreArchiveSelection(source.Path, normalizedTargetRoot, selectedPaths)
            : RestoreFolderSelection(source.Path, normalizedTargetRoot, selectedPaths);
    }

    public static bool IsPreviewable(string relativePath) =>
        !string.IsNullOrWhiteSpace(relativePath) &&
        !relativePath.EndsWith("/", StringComparison.Ordinal);

    private static IReadOnlyList<SnapshotExplorerEntry> ListFolder(string backupRoot, string folderPath, string search)
    {
        string folderFullPath = ResolvePathUnderRoot(backupRoot, folderPath);
        if (!Directory.Exists(folderFullPath))
            return [];
        EnsureNoLinkedSourcePathComponents(backupRoot, folderFullPath);

        List<SnapshotExplorerEntry> entries = string.IsNullOrWhiteSpace(search)
            ? ListFolderChildren(backupRoot, folderFullPath)
            : SearchFolderFiles(backupRoot, folderFullPath, search);

        return [.. entries.OrderBy(e => e.Kind).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static List<SnapshotExplorerEntry> ListFolderChildren(string backupRoot, string folderFullPath)
    {
        var entries = new List<SnapshotExplorerEntry>();
        AddFolderEntries(entries, backupRoot, folderFullPath);
        AddFolderFileEntries(entries, backupRoot, folderFullPath);
        return entries;
    }

    private static void AddFolderEntries(List<SnapshotExplorerEntry> entries, string backupRoot, string folderFullPath)
    {
        foreach (string dir in Directory.EnumerateDirectories(folderFullPath))
        {
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                continue;

            string relative = ToExplorerPath(Path.GetRelativePath(backupRoot, dir));
            entries.Add(SnapshotExplorerEntry.Folder(relative, Path.GetFileName(dir)));
        }
    }

    private static void AddFolderFileEntries(List<SnapshotExplorerEntry> entries, string backupRoot, string folderFullPath)
    {
        foreach (string file in Directory.EnumerateFiles(folderFullPath))
        {
            if (IsInternalBackupArtifact(Path.GetFileName(file)) ||
                (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                continue;

            entries.Add(CreateFolderFileEntry(backupRoot, file, displayRelativePath: false));
        }
    }

    private static List<SnapshotExplorerEntry> SearchFolderFiles(string backupRoot, string folderFullPath, string search)
    {
        var entries = new List<SnapshotExplorerEntry>();
        foreach (string file in EnumerateFolderFilesSafely(folderFullPath))
        {
            string relative = ToExplorerPath(Path.GetRelativePath(backupRoot, file));
            if (IsInternalBackupArtifact(Path.GetFileName(file)) ||
                !relative.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(CreateFolderFileEntry(backupRoot, file, displayRelativePath: true));
        }

        return entries;
    }

    private static SnapshotExplorerEntry CreateFolderFileEntry(string backupRoot, string file, bool displayRelativePath)
    {
        string relative = ToExplorerPath(Path.GetRelativePath(backupRoot, file));
        string name = displayRelativePath ? relative : Path.GetFileName(file);
        var info = new FileInfo(file);
        return SnapshotExplorerEntry.File(relative, name, info.Length, info.LastWriteTimeUtc, IsPreviewable(relative));
    }

    private static IReadOnlyList<SnapshotExplorerEntry> ListArchive(string archivePath, string folderPath, string search)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ValidateUniqueArchiveFilePaths(archive);
        string folderPrefix = string.IsNullOrWhiteSpace(folderPath) ? string.Empty : folderPath.TrimEnd('/') + "/";
        var folders = new Dictionary<string, SnapshotExplorerEntry>(StringComparer.Ordinal);
        var files = new Dictionary<string, SnapshotExplorerEntry>(StringComparer.Ordinal);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relative = NormalizeArchiveEntryPath(entry.FullName);
            if (string.IsNullOrWhiteSpace(relative))
                continue;

            if (!string.IsNullOrWhiteSpace(search) && TryAddArchiveSearchResult(files, entry, relative, search))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(search))
                continue;

            AddArchiveFolderResult(folders, files, entry, relative, folderPath, folderPrefix);
        }

        return [.. folders.Values
            .Concat(files.Values)
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static bool TryAddArchiveSearchResult(
        Dictionary<string, SnapshotExplorerEntry> files,
        ZipArchiveEntry entry,
        string relative,
        string search)
    {
        if (string.IsNullOrEmpty(entry.Name) ||
            !relative.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        files[relative] = SnapshotExplorerEntry.File(
            relative,
            relative,
            Math.Max(0, entry.Length),
            entry.LastWriteTime.UtcDateTime,
            IsPreviewable(relative));
        return true;
    }

    private static void AddArchiveFolderResult(
        Dictionary<string, SnapshotExplorerEntry> folders,
        Dictionary<string, SnapshotExplorerEntry> files,
        ZipArchiveEntry entry,
        string relative,
        string folderPath,
        string folderPrefix)
    {
        if (!relative.StartsWith(folderPrefix, StringComparison.Ordinal))
            return;

        string remaining = relative[folderPrefix.Length..];
        if (remaining.Length == 0)
            return;

        int slashIndex = remaining.IndexOf('/');
        if (slashIndex >= 0)
        {
            AddArchiveFolderEntry(folders, folderPath, remaining, slashIndex);
            return;
        }

        if (!string.IsNullOrEmpty(entry.Name))
            AddArchiveFileEntry(files, entry, relative, remaining);
    }

    private static void AddArchiveFolderEntry(
        Dictionary<string, SnapshotExplorerEntry> folders,
        string folderPath,
        string remaining,
        int slashIndex)
    {
        string childName = remaining[..slashIndex];
        string childPath = string.IsNullOrWhiteSpace(folderPath)
            ? childName
            : $"{folderPath.TrimEnd(ArchivePathSeparator)}{ArchivePathSeparator}{childName}";
        folders[childPath] = SnapshotExplorerEntry.Folder(childPath, childName);
    }

    private static void AddArchiveFileEntry(
        Dictionary<string, SnapshotExplorerEntry> files,
        ZipArchiveEntry entry,
        string relative,
        string name)
    {
        files[relative] = SnapshotExplorerEntry.File(
            relative,
            name,
            Math.Max(0, entry.Length),
            entry.LastWriteTime.UtcDateTime,
            IsPreviewable(relative));
    }

    private static SnapshotPreviewResult PreviewFolderFile(string backupRoot, string relativePath, int maxBytes)
    {
        string path = ResolvePathUnderRoot(backupRoot, relativePath);
        if (!File.Exists(path))
            return SnapshotPreviewResult.Failure("File is missing from the backup.");
        EnsureNoLinkedSourcePathComponents(backupRoot, path);

        byte[] buffer = ReadPrefix(path, maxBytes, out bool truncated);
        if (!LooksLikeText(buffer))
            return SnapshotPreviewResult.Failure(UnsupportedPreviewMessage);

        return SnapshotPreviewResult.Ok(DecodeText(buffer), truncated);
    }

    private static SnapshotPreviewResult PreviewArchiveEntry(string archivePath, string relativePath, int maxBytes)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ValidateUniqueArchiveFilePaths(archive);
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(NormalizeArchiveEntryPath(e.FullName), relativePath, StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(e.Name));
        if (entry is null)
            return SnapshotPreviewResult.Failure("File is missing from the backup archive.");

        using Stream stream = entry.Open();
        byte[] buffer = ReadPrefix(stream, maxBytes, out bool truncated, entry.Length);
        if (!LooksLikeText(buffer))
            return SnapshotPreviewResult.Failure(UnsupportedPreviewMessage);

        return SnapshotPreviewResult.Ok(DecodeText(buffer), truncated);
    }

    private static SnapshotRestoreSelectionResult RestoreFolderSelection(string backupRoot, string targetRoot, IReadOnlyList<string> selectedPaths)
    {
        int files = 0;
        long bytes = 0;
        foreach (string selectedPath in selectedPaths)
        {
            string sourcePath = ResolvePathUnderRoot(backupRoot, selectedPath);
            EnsureNoLinkedSourcePathComponents(backupRoot, sourcePath);
            if (File.Exists(sourcePath))
            {
                CopyFileUnderRoot(sourcePath, targetRoot, selectedPath);
                files++;
                bytes += new FileInfo(sourcePath).Length;
            }
            else if (Directory.Exists(sourcePath))
            {
                foreach (string file in EnumerateFolderFilesSafely(sourcePath))
                {
                    string relative = ToExplorerPath(Path.GetRelativePath(backupRoot, file));
                    CopyFileUnderRoot(file, targetRoot, relative);
                    files++;
                    bytes += new FileInfo(file).Length;
                }
            }
        }

        return new SnapshotRestoreSelectionResult(files, bytes);
    }

    private static SnapshotRestoreSelectionResult RestoreArchiveSelection(string archivePath, string targetRoot, IReadOnlyList<string> selectedPaths)
    {
        int files = 0;
        long bytes = 0;
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ValidateUniqueArchiveFilePaths(archive);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            string relative = NormalizeArchiveEntryPath(entry.FullName);
            if (!selectedPaths.Any(path => IsSelected(relative, path)))
                continue;

            string destinationPath = ResolveArchiveEntryPathUnderRoot(targetRoot, entry.FullName);
            string? parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
                EnsureNoLinkedPathComponents(targetRoot, destinationPath);
            }
            entry.ExtractToFile(destinationPath, overwrite: true);
            files++;
            bytes += Math.Max(0, entry.Length);
        }

        return new SnapshotRestoreSelectionResult(files, bytes);
    }

    private static bool IsSelected(string entryPath, string selectedPath) =>
        string.Equals(entryPath, selectedPath, StringComparison.Ordinal) ||
        entryPath.StartsWith(selectedPath.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static void CopyFileUnderRoot(string sourcePath, string targetRoot, string relativePath)
    {
        string targetPath = ResolvePathUnderRoot(targetRoot, relativePath);
        string? parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
            EnsureNoLinkedPathComponents(targetRoot, targetPath);
        }
        File.Copy(sourcePath, targetPath, overwrite: true);
    }

    private static BackupSource ResolveSource(string backupRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        string root = NormalizeRoot(backupRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Backup folder '{backupRoot}' does not exist.");

        string encryptedArchivePath = Path.Combine(root, BackupArchiveCryptoService.EncryptedArchiveFileName);
        if (File.Exists(encryptedArchivePath))
            return new BackupSource(SnapshotExplorerSourceKind.EncryptedArchive, encryptedArchivePath);

        string archivePath = Path.Combine(root, BackupArchiveCryptoService.PlainArchiveFileName);
        if (File.Exists(archivePath))
            return new BackupSource(SnapshotExplorerSourceKind.Archive, archivePath);

        return new BackupSource(SnapshotExplorerSourceKind.Folder, root);
    }

    private static string NormalizeRoot(string root)
    {
        string full = Path.GetFullPath(root);
        string pathRoot = Path.GetPathRoot(full) ?? string.Empty;
        string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.Length < pathRoot.Length ? pathRoot : trimmed;
    }

    private static string ResolvePathUnderRoot(string root, string relativePath)
    {
        string normalizedRoot = NormalizeRoot(root);
        string safeRelative = NormalizeExplorerPath(relativePath, allowEmpty: true)
            .Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, safeRelative));
        string rootWithSeparator = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(candidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path '{relativePath}' escapes the selected root.");
        }

        return candidate;
    }

    private static string ResolveArchiveEntryPathUnderRoot(string root, string entryFullName)
    {
        string normalizedRoot = NormalizeRoot(root);
        string rootWithSeparator = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        string archivePath = (entryFullName ?? string.Empty)
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(rootWithSeparator, archivePath));
        if (!candidate.StartsWith(rootWithSeparator, GetPathComparison()))
            throw new InvalidDataException($"Archive entry '{entryFullName}' escapes the selected restore root.");

        EnsureNoLinkedPathComponents(normalizedRoot, candidate);
        return candidate;
    }

    private static void EnsureNoLinkedPathComponents(string root, string destinationPath)
    {
        string normalizedRoot = NormalizeRoot(root);
        string relative = Path.GetRelativePath(normalizedRoot, destinationPath);
        string current = normalizedRoot;
        string[] components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < components.Length; index++)
        {
            current = Path.Combine(current, components[index]);
            if (index == components.Length - 1 && !File.Exists(current) && !Directory.Exists(current))
                continue;
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;

            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Restore path '{relative}' contains a linked path component.");
        }
    }

    private static void EnsureNoLinkedSourcePathComponents(string root, string sourcePath)
    {
        string normalizedRoot = NormalizeRoot(root);
        string relative = Path.GetRelativePath(normalizedRoot, sourcePath);
        if (relative is "." or "")
            return;

        string current = normalizedRoot;
        string[] components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (string component in components)
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Backup path '{relative}' contains a linked path component.");
        }
    }

    private static IEnumerable<string> EnumerateFolderFilesSafely(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string childDirectory in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0)
                    pending.Push(childDirectory);
            }

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    yield return file;
            }
        }
    }

    private static void ValidateUniqueArchiveFilePaths(ZipArchive archive)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            string path = NormalizeArchiveEntryPath(entry.FullName);
            if (!paths.Add(path))
                throw new InvalidDataException($"Archive contains duplicate file path '{path}'.");
        }
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string NormalizeExplorerPath(string? path, bool allowEmpty)
    {
        string converted = ToExplorerPath(path ?? string.Empty);
        bool hasRootPrefix = converted.StartsWith("/", StringComparison.Ordinal) ||
                             (converted.Length >= 2 && char.IsLetter(converted[0]) && converted[1] == ':');
        string normalized = converted.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            if (allowEmpty && !hasRootPrefix)
                return string.Empty;
            throw new ArgumentException("A relative path is required.", nameof(path));
        }

        if (hasRootPrefix ||
            Path.IsPathFullyQualified(normalized) ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"Path '{path}' is not a safe backup-relative path.");
        }

        return normalized;
    }

    private static string NormalizeArchiveEntryPath(string path) =>
        NormalizeExplorerPath(path, allowEmpty: true);

    private static string ToExplorerPath(string path) =>
        path.Replace('\\', '/');

    private static bool IsInternalBackupArtifact(string name) =>
        string.Equals(name, ".vaultsync_inprogress", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, ".vaultsync_complete", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, BackupArchiveCryptoService.PlainArchiveFileName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, BackupArchiveCryptoService.EncryptedArchiveFileName, StringComparison.OrdinalIgnoreCase);

    private static byte[] ReadPrefix(string path, int maxBytes, out bool truncated)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return ReadPrefix(stream, maxBytes, out truncated, stream.Length);
    }

    private static byte[] ReadPrefix(Stream stream, int maxBytes, out bool truncated, long? knownLength = null)
    {
        int capacity = (int)Math.Min(maxBytes, knownLength.GetValueOrDefault(maxBytes));
        byte[] buffer = new byte[Math.Max(0, capacity)];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
                break;
            total += read;
        }

        truncated = knownLength.HasValue
            ? knownLength.Value > total
            : stream.ReadByte() >= 0;
        return total == buffer.Length ? buffer : buffer[..total];
    }

    private static string DecodeText(byte[] buffer)
    {
        if (buffer.Length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
            return Encoding.UTF8.GetString(buffer, 3, buffer.Length - 3);

        return Encoding.UTF8.GetString(buffer);
    }

    private static bool LooksLikeText(byte[] buffer)
    {
        if (buffer.Length == 0)
            return true;

        if (buffer.Contains((byte)0))
            return false;

        int controlCharacters = 0;
        foreach (byte value in buffer)
        {
            if (value < 0x20 && value != (byte)'\t' && value != (byte)'\r' && value != (byte)'\n')
                controlCharacters++;
        }

        return controlCharacters / (double)buffer.Length <= MaxBinaryControlCharacterRatio;
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private sealed class TextContentReader : IDisposable
    {
        private readonly BackupSource _source;
        private readonly ZipArchive? _archive;
        private readonly IReadOnlyDictionary<string, ZipArchiveEntry>? _archiveEntries;

        public TextContentReader(BackupSource source)
        {
            _source = source;
            if (source.Kind != SnapshotExplorerSourceKind.Archive)
                return;

            _archive = ZipFile.OpenRead(source.Path);
            ValidateUniqueArchiveFilePaths(_archive);
            _archiveEntries = _archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name))
                .GroupBy(entry => NormalizeArchiveEntryPath(entry.FullName), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        }

        public bool CanRead => _source.Kind != SnapshotExplorerSourceKind.EncryptedArchive;

        public bool TryReadCompleteText(string relativePath, int maxBytes, out string text)
        {
            text = string.Empty;
            byte[] buffer;
            bool truncated;
            if (_source.Kind == SnapshotExplorerSourceKind.Archive)
            {
                if (_archiveEntries is null || !_archiveEntries.TryGetValue(relativePath, out ZipArchiveEntry? entry))
                    return false;

                using Stream stream = entry.Open();
                buffer = ReadPrefix(stream, maxBytes, out truncated, entry.Length);
            }
            else
            {
                string path = ResolvePathUnderRoot(_source.Path, relativePath);
                if (!File.Exists(path))
                    return false;
                EnsureNoLinkedSourcePathComponents(_source.Path, path);

                buffer = ReadPrefix(path, maxBytes, out truncated);
            }

            if (truncated || !LooksLikeText(buffer))
                return false;

            try
            {
                int start = buffer.Length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
                text = StrictUtf8.GetString(buffer, start, buffer.Length - start);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        public void Dispose() => _archive?.Dispose();
    }

    private readonly record struct BackupSource(SnapshotExplorerSourceKind Kind, string Path);
}

public enum SnapshotExplorerSourceKind
{
    Folder,
    Archive,
    EncryptedArchive
}

public enum SnapshotExplorerEntryKind
{
    Folder,
    File
}

public sealed record SnapshotExplorerEntry(
    SnapshotExplorerEntryKind Kind,
    string Path,
    string Name,
    long SizeBytes,
    DateTime? ModifiedUtc,
    bool CanPreview)
{
    public static SnapshotExplorerEntry Folder(string path, string name) =>
        new(SnapshotExplorerEntryKind.Folder, path, name, 0, null, false);

    public static SnapshotExplorerEntry File(string path, string name, long sizeBytes, DateTime? modifiedUtc, bool canPreview) =>
        new(SnapshotExplorerEntryKind.File, path, name, sizeBytes, modifiedUtc, canPreview);
}

public sealed record SnapshotExplorerResult(
    SnapshotExplorerSourceKind SourceKind,
    string FolderPath,
    IReadOnlyList<SnapshotExplorerEntry> Entries);

public sealed record SnapshotFileInventory(
    SnapshotExplorerSourceKind SourceKind,
    IReadOnlyList<FileEntry> Files,
    bool IsTruncated);

public sealed record SnapshotPreviewResult(
    bool Success,
    string Text,
    bool Truncated,
    string Error)
{
    public static SnapshotPreviewResult Ok(string text, bool truncated) =>
        new(true, text, truncated, string.Empty);

    public static SnapshotPreviewResult Failure(string error) =>
        new(false, string.Empty, false, error);
}

public sealed record SnapshotRestoreSelectionResult(int FileCount, long BytesRestored);
