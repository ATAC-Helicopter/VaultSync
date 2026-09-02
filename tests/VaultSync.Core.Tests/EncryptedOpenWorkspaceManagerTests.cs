using System;
using System.IO;
using VaultSync.UI.Infrastructure;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class EncryptedOpenWorkspaceManagerTests
{
    [Fact]
    public void OwnedCleanupRemovesOnlyWorkspacesRegisteredByThisProcess()
    {
        string owned = CreateSystemTempWorkspace();
        string foreign = CreateSystemTempWorkspace();

        try
        {
            EncryptedOpenWorkspaceManager.RegisterOwnedWorkspace(owned);

            EncryptedOpenWorkspaceManager.CleanupOwnedWorkspaces();

            Assert.False(Directory.Exists(owned));
            Assert.True(Directory.Exists(foreign));
        }
        finally
        {
            TryDelete(owned);
            TryDelete(foreign);
            EncryptedOpenWorkspaceManager.ForgetOwnedWorkspace(owned);
        }
    }

    [Fact]
    public void ResolveWorkspaceRoot_AcceptsOnlyExactDirectTempWorkspaceNames()
    {
        string workspace = CreateSystemTempWorkspace();
        string child = Path.Combine(workspace, "extracted", "folder");
        Directory.CreateDirectory(child);

        try
        {
            Assert.Equal(
                Path.GetFullPath(workspace),
                EncryptedOpenWorkspaceManager.ResolveWorkspaceRoot(child));
            Assert.Null(EncryptedOpenWorkspaceManager.ResolveWorkspaceRoot(Path.GetTempPath()));
            Assert.Null(EncryptedOpenWorkspaceManager.ResolveWorkspaceRoot(
                Path.Combine(Path.GetTempPath(), "vaultsync-open-not-a-guid", "extracted")));
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [Fact]
    public void ResolveWorkspaceRoot_RejectsSiblingDirectoryWithTempPrefix()
    {
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string sibling = tempRoot + "-sibling";
        string candidate = Path.Combine(
            sibling,
            $"{EncryptedOpenWorkspaceManager.WorkspacePrefix}{Guid.NewGuid():N}",
            "extracted");

        Assert.Null(EncryptedOpenWorkspaceManager.ResolveWorkspaceRoot(candidate));
    }

    [Fact]
    public void StaleCleanupPreservesWorkspaceWhileRecordedOwnerIsActive()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vaultsync-owner-tests-{Guid.NewGuid():N}");
        string workspace = Path.Combine(root, $"{EncryptedOpenWorkspaceManager.WorkspacePrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);

        try
        {
            File.WriteAllLines(
                Path.Combine(workspace, EncryptedOpenWorkspaceManager.OwnerMarkerFileName),
                ["1", "4242", "638900000000000000"]);
            SetOldTimestamps(workspace);

            int removed = EncryptedOpenWorkspaceManager.CleanupStaleWorkspaces(
                root,
                DateTime.UtcNow,
                TimeSpan.FromMinutes(30),
                (pid, startTicks) => pid == 4242 && startTicks == 638900000000000000);

            Assert.Equal(0, removed);
            Assert.True(Directory.Exists(workspace));

            removed = EncryptedOpenWorkspaceManager.CleanupStaleWorkspaces(
                root,
                DateTime.UtcNow,
                TimeSpan.FromMinutes(30),
                static (_, _) => false);

            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(workspace));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void StaleCleanupFailsClosedForMalformedOwnershipMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vaultsync-owner-tests-{Guid.NewGuid():N}");
        string workspace = Path.Combine(root, $"{EncryptedOpenWorkspaceManager.WorkspacePrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);

        try
        {
            File.WriteAllText(
                Path.Combine(workspace, EncryptedOpenWorkspaceManager.OwnerMarkerFileName),
                "not-valid-ownership-metadata");
            SetOldTimestamps(workspace);

            int removed = EncryptedOpenWorkspaceManager.CleanupStaleWorkspaces(
                root,
                DateTime.UtcNow,
                TimeSpan.FromMinutes(30),
                static (_, _) => false);

            Assert.Equal(0, removed);
            Assert.True(Directory.Exists(workspace));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void StaleCleanupRemovesLegacyWorkspaceWithoutOwnershipMetadata()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vaultsync-owner-tests-{Guid.NewGuid():N}");
        string workspace = Path.Combine(root, $"{EncryptedOpenWorkspaceManager.WorkspacePrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);

        try
        {
            SetOldTimestamps(workspace);

            int removed = EncryptedOpenWorkspaceManager.CleanupStaleWorkspaces(
                root,
                DateTime.UtcNow,
                TimeSpan.FromMinutes(30),
                static (_, _) => false);

            Assert.Equal(1, removed);
            Assert.False(Directory.Exists(workspace));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateSystemTempWorkspace()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"{EncryptedOpenWorkspaceManager.WorkspacePrefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "payload.txt"), "test");
        return path;
    }

    private static void SetOldTimestamps(string path)
    {
        DateTime old = DateTime.UtcNow.AddDays(-2);
        foreach (string file in Directory.GetFiles(path))
            File.SetLastWriteTimeUtc(file, old);
        Directory.SetCreationTimeUtc(path, old);
        Directory.SetLastWriteTimeUtc(path, old);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Test cleanup only.
        }
    }
}
