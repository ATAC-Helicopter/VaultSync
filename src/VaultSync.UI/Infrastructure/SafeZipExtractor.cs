using System;
using System.IO;
using System.IO.Compression;

namespace VaultSync.UI.Infrastructure;

internal static class SafeZipExtractor
{
    public static void ExtractToDirectory(string archivePath, string destinationDirectory, bool overwriteFiles = true)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ExtractToDirectory(archive, destinationDirectory, overwriteFiles);
    }

    public static void ExtractToDirectory(ZipArchive archive, string destinationDirectory, bool overwriteFiles = true)
    {
        if (archive is null)
            throw new ArgumentNullException(nameof(archive));

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string destinationPath = GetSafeEntryPath(destinationDirectory, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            string? parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            entry.ExtractToFile(destinationPath, overwriteFiles);
        }
    }

    public static string GetSafeEntryPath(string destinationDirectory, string entryFullName)
    {
        string relative = GetSafeEntryRelativePath(entryFullName);
        string normalizedRoot = NormalizeRoot(destinationDirectory);
        string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        if (!candidate.StartsWith(normalizedRoot, GetPathComparison()))
            throw new InvalidDataException($"Archive entry '{entryFullName}' escapes the extraction destination.");

        return candidate;
    }

    public static string GetSafeEntryRelativePath(string entryFullName)
    {
        string normalized = (entryFullName ?? string.Empty)
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .Trim();

        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        bool hasDrivePrefix = normalized.Length >= 2 &&
                              char.IsLetter(normalized[0]) &&
                              normalized[1] == ':';
        if (Path.IsPathFullyQualified(normalized) ||
            Path.IsPathRooted(normalized) ||
            hasDrivePrefix)
            throw new InvalidDataException($"Archive entry '{entryFullName}' is absolute.");

        string root = NormalizeRoot(Path.Combine(Path.GetTempPath(), "vaultsync-archive-root"));
        string candidate = Path.GetFullPath(Path.Combine(root, normalized));
        if (!candidate.StartsWith(root, GetPathComparison()))
            throw new InvalidDataException($"Archive entry '{entryFullName}' escapes the extraction root.");

        return Path.GetRelativePath(root, candidate);
    }

    private static string NormalizeRoot(string root) =>
        Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
