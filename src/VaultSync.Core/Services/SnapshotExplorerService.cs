using System.IO.Compression;
using System.Text;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public sealed class SnapshotExplorerService
{
    private const int DefaultPreviewBytes = 256 * 1024;
    private const double MaxBinaryControlCharacterRatio = 0.02;
    private const string UnsupportedPreviewMessage = "Preview is available for text-like files only.";

    public SnapshotExplorerResult List(string backupRoot, string? folderPath = null, string? search = null)
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

    public SnapshotPreviewResult PreviewText(string backupRoot, string relativePath, int maxBytes = DefaultPreviewBytes)
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

    public SnapshotRestoreSelectionResult RestoreSelection(
        string backupRoot,
        string targetRoot,
        IEnumerable<string> relativePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRoot);
        string normalizedTargetRoot = NormalizeRoot(targetRoot);
        Directory.CreateDirectory(normalizedTargetRoot);

        string[] selectedPaths = [.. relativePaths
            .Select(path => NormalizeExplorerPath(path, allowEmpty: false))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
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

        var entries = new List<SnapshotExplorerEntry>();

        if (string.IsNullOrWhiteSpace(search))
        {
            foreach (string dir in Directory.EnumerateDirectories(folderFullPath))
            {
                string relative = ToExplorerPath(Path.GetRelativePath(backupRoot, dir));
                entries.Add(SnapshotExplorerEntry.Folder(relative, Path.GetFileName(dir)));
            }

            foreach (string file in Directory.EnumerateFiles(folderFullPath))
            {
                string name = Path.GetFileName(file);
                if (IsInternalBackupArtifact(name))
                    continue;

                var info = new FileInfo(file);
                string relative = ToExplorerPath(Path.GetRelativePath(backupRoot, file));
                entries.Add(SnapshotExplorerEntry.File(relative, name, info.Length, info.LastWriteTimeUtc, IsPreviewable(relative)));
            }
        }
        else
        {
            foreach (string file in Directory.EnumerateFiles(folderFullPath, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (IsInternalBackupArtifact(name))
                    continue;

                string relative = ToExplorerPath(Path.GetRelativePath(backupRoot, file));
                if (!relative.Contains(search, StringComparison.OrdinalIgnoreCase))
                    continue;

                var info = new FileInfo(file);
                entries.Add(SnapshotExplorerEntry.File(relative, relative, info.Length, info.LastWriteTimeUtc, IsPreviewable(relative)));
            }
        }

        return [.. entries.OrderBy(e => e.Kind).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IReadOnlyList<SnapshotExplorerEntry> ListArchive(string archivePath, string folderPath, string search)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        string folderPrefix = string.IsNullOrWhiteSpace(folderPath) ? string.Empty : folderPath.TrimEnd('/') + "/";
        var folders = new Dictionary<string, SnapshotExplorerEntry>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, SnapshotExplorerEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relative = NormalizeArchiveEntryPath(entry.FullName);
            if (string.IsNullOrWhiteSpace(relative))
                continue;

            if (!string.IsNullOrWhiteSpace(search))
            {
                if (string.IsNullOrEmpty(entry.Name) || !relative.Contains(search, StringComparison.OrdinalIgnoreCase))
                    continue;

                files[relative] = SnapshotExplorerEntry.File(
                    relative,
                    relative,
                    Math.Max(0, entry.Length),
                    entry.LastWriteTime.UtcDateTime,
                    IsPreviewable(relative));
                continue;
            }

            if (!relative.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            string remaining = relative[folderPrefix.Length..];
            if (remaining.Length == 0)
                continue;

            int slashIndex = remaining.IndexOf('/');
            if (slashIndex >= 0)
            {
                string childName = remaining[..slashIndex];
                string childPath = string.IsNullOrWhiteSpace(folderPath)
                    ? childName
                    : folderPath.TrimEnd('/') + "/" + childName;
                folders[childPath] = SnapshotExplorerEntry.Folder(childPath, childName);
            }
            else if (!string.IsNullOrEmpty(entry.Name))
            {
                files[relative] = SnapshotExplorerEntry.File(
                    relative,
                    remaining,
                    Math.Max(0, entry.Length),
                    entry.LastWriteTime.UtcDateTime,
                    IsPreviewable(relative));
            }
        }

        return [.. folders.Values
            .Concat(files.Values)
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static SnapshotPreviewResult PreviewFolderFile(string backupRoot, string relativePath, int maxBytes)
    {
        string path = ResolvePathUnderRoot(backupRoot, relativePath);
        if (!File.Exists(path))
            return SnapshotPreviewResult.Failure("File is missing from the backup.");

        byte[] buffer = ReadPrefix(path, maxBytes, out bool truncated);
        if (!LooksLikeText(buffer))
            return SnapshotPreviewResult.Failure(UnsupportedPreviewMessage);

        return SnapshotPreviewResult.Ok(DecodeText(buffer), truncated);
    }

    private static SnapshotPreviewResult PreviewArchiveEntry(string archivePath, string relativePath, int maxBytes)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(NormalizeArchiveEntryPath(e.FullName), relativePath, StringComparison.OrdinalIgnoreCase) &&
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
            if (File.Exists(sourcePath))
            {
                CopyFileUnderRoot(sourcePath, targetRoot, selectedPath);
                files++;
                bytes += new FileInfo(sourcePath).Length;
            }
            else if (Directory.Exists(sourcePath))
            {
                foreach (string file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
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
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            string relative = NormalizeArchiveEntryPath(entry.FullName);
            if (!selectedPaths.Any(path => IsSelected(relative, path)))
                continue;

            string destinationPath = ResolvePathUnderRoot(targetRoot, relative);
            string? parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            entry.ExtractToFile(destinationPath, overwrite: true);
            files++;
            bytes += Math.Max(0, entry.Length);
        }

        return new SnapshotRestoreSelectionResult(files, bytes);
    }

    private static bool IsSelected(string entryPath, string selectedPath) =>
        string.Equals(entryPath, selectedPath, StringComparison.OrdinalIgnoreCase) ||
        entryPath.StartsWith(selectedPath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    private static void CopyFileUnderRoot(string sourcePath, string targetRoot, string relativePath)
    {
        string targetPath = ResolvePathUnderRoot(targetRoot, relativePath);
        string? parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);
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
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string ResolvePathUnderRoot(string root, string relativePath)
    {
        string normalizedRoot = NormalizeRoot(root);
        string safeRelative = NormalizeExplorerPath(relativePath, allowEmpty: true)
            .Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, safeRelative));
        string rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(candidate, normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Path '{relativePath}' escapes the selected root.");
        }

        return candidate;
    }

    private static string NormalizeExplorerPath(string? path, bool allowEmpty)
    {
        string normalized = ToExplorerPath(path ?? string.Empty).Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            if (allowEmpty)
                return string.Empty;
            throw new ArgumentException("A relative path is required.", nameof(path));
        }

        if (Path.IsPathFullyQualified(normalized) ||
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
