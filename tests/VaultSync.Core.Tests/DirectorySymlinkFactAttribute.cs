using System;
using System.IO;
using Xunit;

namespace VaultSync.Core.Tests;

/// <summary>Reports missing Windows symlink privileges as a skip, rather than a passed check.</summary>
public sealed class DirectorySymlinkFactAttribute : FactAttribute
{
    public DirectorySymlinkFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string root = Path.Combine(Path.GetTempPath(), $"vaultsync-symlink-capability-{Guid.NewGuid():N}");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), target);
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) == 1314)
        {
            Skip = "Windows directory symlink creation requires Developer Mode or elevated privileges.";
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
