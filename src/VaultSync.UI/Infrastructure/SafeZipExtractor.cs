using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VaultSync.UI.Infrastructure;

internal static class SafeZipExtractor
{
    private const int MaxEntryCount = 100_000;
    private const long MaxExtractedBytes = 8L * 1024 * 1024 * 1024;

    public static void ExtractToDirectory(string archivePath, string destinationDirectory, bool overwriteFiles = true)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ExtractToDirectory(archive, destinationDirectory, overwriteFiles);
    }

    public static void ExtractToDirectory(ZipArchive archive, string destinationDirectory, bool overwriteFiles = true)
    {
        if (archive is null)
            throw new ArgumentNullException(nameof(archive));

        ValidateArchiveShape(archive);

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

    private static void ValidateArchiveShape(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntryCount)
            throw new InvalidDataException("Archive contains too many entries.");

        var paths = new HashSet<string>(GetPathComparer());
        long extractedBytes = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string relative = GetSafeEntryRelativePath(entry.FullName)
                .Replace('\\', '/')
                .TrimEnd('/')
                .Normalize(NormalizationForm.FormC);
            if (string.IsNullOrWhiteSpace(relative) || !paths.Add(relative))
                throw new InvalidDataException($"Archive contains an empty or duplicate entry path: '{entry.FullName}'.");

            try
            {
                extractedBytes = checked(extractedBytes + entry.Length);
            }
            catch (OverflowException)
            {
                throw new InvalidDataException("Archive extracted size exceeds the supported limit.");
            }

            if (extractedBytes > MaxExtractedBytes)
                throw new InvalidDataException("Archive extracted size exceeds the supported limit.");
        }
    }

    public static string GetSafeEntryPath(string destinationDirectory, string entryFullName)
    {
        string relative = GetSafeEntryRelativePath(entryFullName);
        string normalizedRoot = NormalizeRoot(destinationDirectory);
        string candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        if (!candidate.StartsWith(normalizedRoot, GetPathComparison()))
            throw new InvalidDataException($"Archive entry '{entryFullName}' escapes the extraction destination.");

        EnsureNoLinkedPathComponents(destinationDirectory, candidate);
        return candidate;
    }

    public static void EnsureNoLinkedPathComponents(string rootDirectory, string destinationPath)
    {
        string normalizedRoot = NormalizeRoot(rootDirectory);
        string candidate = Path.GetFullPath(destinationPath);
        if (!candidate.StartsWith(normalizedRoot, GetPathComparison()))
            throw new InvalidDataException($"Destination '{destinationPath}' escapes the selected root.");

        string relative = Path.GetRelativePath(normalizedRoot, candidate);
        string current = normalizedRoot;
        foreach (string component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Destination path '{relative}' contains a linked path component.");
            }
            catch (FileNotFoundException)
            {
                // The remaining path does not exist yet.
            }
            catch (DirectoryNotFoundException)
            {
                // The remaining path does not exist yet.
            }
        }
    }

    public static string GetSafeEntryRelativePath(string entryFullName)
    {
        string normalized = (entryFullName ?? string.Empty)
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        bool hasDrivePrefix = normalized.Length >= 2 &&
                              char.IsLetter(normalized[0]) &&
                              normalized[1] == ':';
        if (Path.IsPathFullyQualified(normalized) ||
            Path.IsPathRooted(normalized) ||
            hasDrivePrefix)
            throw new InvalidDataException($"Archive entry '{entryFullName}' is absolute.");

        foreach (string component in normalized.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsPortablePathSegment(component))
                throw new InvalidDataException($"Archive entry '{entryFullName}' contains an unsafe path segment.");
        }

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

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static bool IsPortablePathSegment(string part)
    {
        if (part is "." or ".." ||
            part.EndsWith(' ') ||
            part.EndsWith('.') ||
            part.Any(char.IsControl))
        {
            return false;
        }

        string stem = part.Split('.')[0];
        return stem.ToUpperInvariant() is not
            ("CON" or "PRN" or "AUX" or "NUL" or
             "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or
             "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9");
    }
}
