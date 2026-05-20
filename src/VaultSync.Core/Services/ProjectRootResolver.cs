using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VaultSync.Core.Services;

public static class ProjectRootResolver
{
    public static bool TryResolveExistingProjectRoot(
        string? projectsRoot,
        string? projectName,
        string? currentRoot,
        out string resolvedRoot)
    {
        resolvedRoot = string.Empty;

        if (!string.IsNullOrWhiteSpace(currentRoot) &&
            Directory.Exists(currentRoot) &&
            !IsVaultSyncTransientTempPath(currentRoot))
        {
            resolvedRoot = Path.GetFullPath(currentRoot);
            return true;
        }

        if (string.IsNullOrWhiteSpace(projectsRoot) || !Directory.Exists(projectsRoot))
            return false;

        string fullProjectsRoot = Path.GetFullPath(projectsRoot);
        var names = GetCandidateFolderNames(projectName, currentRoot);

        try
        {
            foreach (string child in Directory.EnumerateDirectories(fullProjectsRoot))
            {
                string childName = Path.GetFileName(child);
                if (names.Any(name => string.Equals(name, childName, StringComparison.OrdinalIgnoreCase)))
                {
                    string fullChild = Path.GetFullPath(child);
                    if (IsVaultSyncTransientTempPath(fullChild))
                        continue;

                    resolvedRoot = fullChild;
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        foreach (string name in names)
        {
            string candidate = Path.Combine(fullProjectsRoot, name);
            if (Directory.Exists(candidate))
            {
                string fullCandidate = Path.GetFullPath(candidate);
                if (IsVaultSyncTransientTempPath(fullCandidate))
                    continue;

                resolvedRoot = fullCandidate;
                return true;
            }
        }

        return false;
    }

    public static string GetCrossPlatformLeafName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        string trimmed = path.Trim().TrimEnd('/', '\\');
        if (string.IsNullOrWhiteSpace(trimmed))
            return string.Empty;

        int slash = trimmed.LastIndexOf('/');
        int backslash = trimmed.LastIndexOf('\\');
        int index = Math.Max(slash, backslash);
        return index >= 0 && index + 1 < trimmed.Length
            ? trimmed[(index + 1)..]
            : trimmed;
    }

    private static IReadOnlyList<string> GetCandidateFolderNames(string? projectName, string? currentRoot)
    {
        var names = new List<string>();
        Add(projectName);
        Add(GetCrossPlatformLeafName(currentRoot));
        return names;

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            string name = value.Trim();
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return;

            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }
    }

    private static bool IsVaultSyncTransientTempPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/vaultsync-meta-import/", StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith("/vaultsync-meta-import", StringComparison.OrdinalIgnoreCase);
    }
}
