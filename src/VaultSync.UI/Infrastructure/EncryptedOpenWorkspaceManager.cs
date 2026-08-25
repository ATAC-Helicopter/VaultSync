using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VaultSync.UI.Infrastructure;

/// <summary>
/// Tracks decrypted-open workspaces owned by this process. Ownership metadata
/// prevents one VaultSync instance from deleting another instance's workspace.
/// </summary>
internal static class EncryptedOpenWorkspaceManager
{
    internal const string WorkspacePrefix = "vaultsync-open-";
    internal const string OwnerMarkerFileName = ".vaultsync-owner";

    private static readonly ConcurrentDictionary<string, byte> OwnedWorkspaces =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly long CurrentProcessStartUtcTicks = GetCurrentProcessStartUtcTicks();

    internal static void RegisterOwnedWorkspace(string workspacePath)
    {
        string fullPath = ValidateWorkspacePath(workspacePath);
        Directory.CreateDirectory(fullPath);
        File.WriteAllLines(
            Path.Combine(fullPath, OwnerMarkerFileName),
            [
                "1",
                Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                CurrentProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture)
            ]);
        OwnedWorkspaces[fullPath] = 0;
    }

    internal static string[] GetOwnedWorkspacePaths() => OwnedWorkspaces.Keys.ToArray();

    internal static void ForgetOwnedWorkspace(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
            return;

        try
        {
            OwnedWorkspaces.TryRemove(Path.GetFullPath(workspacePath), out _);
        }
        catch
        {
            // The path is already unusable; there is no safe cleanup action to take.
        }
    }

    internal static void CleanupOwnedWorkspaces()
    {
        foreach (string workspacePath in GetOwnedWorkspacePaths())
        {
            try
            {
                if (Directory.Exists(workspacePath))
                {
                    var directory = new DirectoryInfo(workspacePath);
                    bool isLink = (directory.Attributes & FileAttributes.ReparsePoint) != 0;
                    directory.Delete(recursive: !isLink);
                }

                ForgetOwnedWorkspace(workspacePath);
            }
            catch
            {
                // Best effort cleanup; retain ownership so a later retry can remove it.
            }
        }
    }

    internal static int CleanupStaleWorkspaces(
        string tempRoot,
        DateTime utcNow,
        TimeSpan retention,
        Func<int, long, bool>? isOwnerProcessActive = null)
    {
        int removed = 0;
        try
        {
            foreach (string workspacePath in Directory.GetDirectories(
                         tempRoot,
                         $"{WorkspacePrefix}*",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    string fullPath = Path.GetFullPath(workspacePath);
                    if (OwnedWorkspaces.ContainsKey(fullPath))
                        continue;

                    DateTime createdUtc = Directory.GetCreationTimeUtc(fullPath);
                    DateTime modifiedUtc = Directory.GetLastWriteTimeUtc(fullPath);
                    DateTime referenceUtc = createdUtc > modifiedUtc ? createdUtc : modifiedUtc;
                    if ((utcNow - referenceUtc) < retention)
                        continue;

                    string ownerMarkerPath = Path.Combine(fullPath, OwnerMarkerFileName);
                    bool hasOwnerMarker = File.Exists(ownerMarkerPath);
                    int ownerPid = 0;
                    long ownerStartUtcTicks = 0;
                    if (hasOwnerMarker &&
                        !TryReadOwner(fullPath, out ownerPid, out ownerStartUtcTicks))
                    {
                        continue;
                    }

                    if (hasOwnerMarker &&
                        (isOwnerProcessActive ?? IsOwnerProcessActive)(ownerPid, ownerStartUtcTicks))
                    {
                        continue;
                    }

                    var directory = new DirectoryInfo(fullPath);
                    bool isLink = (directory.Attributes & FileAttributes.ReparsePoint) != 0;
                    directory.Delete(recursive: !isLink);
                    removed++;
                }
                catch
                {
                    // Fail closed for unreadable ownership metadata or locked workspaces.
                }
            }
        }
        catch
        {
            // Best effort cleanup only.
        }

        return removed;
    }

    private static string ValidateWorkspacePath(string workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
            throw new ArgumentException("Workspace path is required.", nameof(workspacePath));

        string fullPath = Path.GetFullPath(workspacePath);
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string? parent = Path.GetDirectoryName(fullPath);
        string name = Path.GetFileName(fullPath);
        if (!string.Equals(parent, tempRoot, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(WorkspacePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Encrypted-open workspaces must be direct VaultSync temp children.");
        }

        return fullPath;
    }

    private static bool TryReadOwner(string workspacePath, out int processId, out long processStartUtcTicks)
    {
        processId = 0;
        processStartUtcTicks = 0;
        string markerPath = Path.Combine(workspacePath, OwnerMarkerFileName);
        if (!File.Exists(markerPath))
            return false;

        string[] lines = File.ReadAllLines(markerPath);
        return lines.Length == 3 &&
               string.Equals(lines[0], "1", StringComparison.Ordinal) &&
               int.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out processId) &&
               processId > 0 &&
               long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out processStartUtcTicks) &&
               processStartUtcTicks > 0;
    }

    private static bool IsOwnerProcessActive(int processId, long processStartUtcTicks)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks == processStartUtcTicks && !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            // Access restrictions or transient process-query failures must preserve data.
            return true;
        }
    }

    private static long GetCurrentProcessStartUtcTicks()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return DateTime.UtcNow.Ticks;
        }
    }
}
