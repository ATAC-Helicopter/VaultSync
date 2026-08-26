using System;
using System.IO;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.Infrastructure;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class StorageHygieneTests
{
    [Fact]
    public void ApplicationCleanupRemovesExpiredPatchArtifactsButNeverOtherData()
    {
        using var root = new TempDirectory();
        DateTime now = DateTime.UtcNow;
        string oldArchive = CreateFile(root.Path, "patches/old.zip", 32, now.AddDays(-2));
        string recentArchive = CreateFile(root.Path, "patches/recent.zip", 16, now.AddHours(-2));
        string oldHelper = CreateDirectory(root.Path, "patch-runtime/patch-helper-old", 64, now.AddDays(-2));
        string recentHelper = CreateDirectory(root.Path, "patch-runtime/patch-helper-recent", 8, now.AddHours(-2));
        string oldStaging = CreateDirectory(
            root.Path,
            $"patch-runtime/patch-{Guid.NewGuid():N}",
            24,
            now.AddHours(-2));
        string scanCache = CreateFile(root.Path, "cache/scan/1.json", 6, now.AddDays(-31));
        string mountPayload = CreateFile(root.Path, "mounts/share/project/backup.zip", 128, now.AddYears(-1));

        StorageCleanupSummary result = StorageHygieneService.PruneApplicationData(root.Path, now);

        Assert.False(File.Exists(oldArchive));
        Assert.True(File.Exists(recentArchive));
        Assert.False(Directory.Exists(oldHelper));
        Assert.True(Directory.Exists(recentHelper));
        Assert.False(Directory.Exists(oldStaging));
        Assert.False(File.Exists(scanCache));
        Assert.True(File.Exists(mountPayload));
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(2, result.DirectoriesRemoved);
        Assert.Equal(126, result.BytesReclaimed);
    }

    [Fact]
    public void ApplicationCleanupRemovesOnlyExpiredVerifiedCacheTemporaryWrites()
    {
        using var root = new TempDirectory();
        DateTime now = DateTime.UtcNow;
        string identity = new('a', 64);
        string stale = CreateFile(
            root.Path,
            $"cache/release-assets/.{identity}.json.{Guid.NewGuid():N}.tmp",
            17,
            now.AddDays(-2));
        string recent = CreateFile(
            root.Path,
            $"cache/release-assets/.{identity}.json.{Guid.NewGuid():N}.tmp",
            11,
            now.AddHours(-2));
        string unrelated = CreateFile(
            root.Path,
            "cache/release-assets/.unrelated.json.not-a-guid.tmp",
            13,
            now.AddYears(-1));

        StorageCleanupSummary result = StorageHygieneService.PruneApplicationData(root.Path, now);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(1, result.FilesRemoved);
        Assert.Equal(17, result.BytesReclaimed);
    }

    [Fact]
    public void ApplicationCleanupRemovesOnlyExpiredSupportBundleStagingDirectories()
    {
        using var root = new TempDirectory();
        DateTime now = DateTime.UtcNow;
        string stale = CreateDirectory(
            root.Path,
            $"exports/support-20260824-120000-{Guid.NewGuid():N}",
            19,
            now.AddDays(-2));
        string recent = CreateDirectory(
            root.Path,
            $"exports/support-20260826-110000-{Guid.NewGuid():N}",
            13,
            now.AddHours(-2));
        string unrelated = CreateDirectory(
            root.Path,
            "exports/support-manual-files",
            17,
            now.AddYears(-1));

        StorageCleanupSummary result = StorageHygieneService.PruneApplicationData(root.Path, now);

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(unrelated));
        Assert.Equal(1, result.DirectoriesRemoved);
        Assert.Equal(19, result.BytesReclaimed);
    }

    [Fact]
    public void LegacyCleanupPrunesOldLogsAndAbandonedConfigWrites()
    {
        using var root = new TempDirectory();
        DateTime now = DateTime.UtcNow;
        string oldLog = CreateFile(root.Path, "logs/old.log", 10, now.AddDays(-20));
        string recentLog = CreateFile(root.Path, "logs/recent.log", 10, now.AddHours(-1));
        string staleWrite = CreateFile(root.Path, "appsettings.tmp.deadbeef.json", 5, now.AddHours(-2));
        string config = CreateFile(root.Path, "appsettings.json", 20, now.AddYears(-1));

        StorageCleanupSummary result = StorageHygieneService.PruneLegacyData(root.Path, now);

        Assert.False(File.Exists(oldLog));
        Assert.True(File.Exists(recentLog));
        Assert.False(File.Exists(staleWrite));
        Assert.True(File.Exists(config));
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(15, result.BytesReclaimed);
    }

    [Fact]
    public void ConfigurationCleanupRemovesOnlyAbandonedAtomicIdentityAndCredentialWrites()
    {
        using var root = new TempDirectory();
        DateTime now = DateTime.UtcNow;
        string staleIdentity = CreateFile(
            root.Path,
            $".installation.id.{Guid.NewGuid():N}.tmp",
            7,
            now.AddHours(-2));
        string staleCredentials = CreateFile(
            root.Path,
            $".credentials.json.{Guid.NewGuid():N}.tmp",
            9,
            now.AddHours(-2));
        string recentCredentials = CreateFile(
            root.Path,
            $".credentials.json.{Guid.NewGuid():N}.tmp",
            5,
            now.AddMinutes(-30));
        string identity = CreateFile(root.Path, "installation.id", 32, now.AddYears(-1));
        string credentials = CreateFile(root.Path, "credentials.json", 24, now.AddYears(-1));
        string unrelated = CreateFile(root.Path, ".settings.json.not-a-guid.tmp", 11, now.AddYears(-1));

        StorageCleanupSummary result = StorageHygieneService.PruneConfigurationData(root.Path, now);

        Assert.False(File.Exists(staleIdentity));
        Assert.False(File.Exists(staleCredentials));
        Assert.True(File.Exists(recentCredentials));
        Assert.True(File.Exists(identity));
        Assert.True(File.Exists(credentials));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(16, result.BytesReclaimed);
    }

    [Fact]
    public void TemporaryCleanupRemovesOnlyExpiredVaultSyncWorkingData()
    {
        using var root = new TempDirectory();
        DateTime now = DateTime.UtcNow;
        string oldOpen = CreateDirectory(root.Path, "vaultsync-open-old", 12, now.AddDays(-2));
        string recentOpen = CreateDirectory(root.Path, "vaultsync-open-recent", 12, now.AddHours(-1));
        string unrelated = CreateDirectory(root.Path, "another-app", 12, now.AddYears(-1));
        string oldExclude = CreateFile(root.Path, "vaultsync_exclude_old.txt", 4, now.AddDays(-2));
        string oldInstaller = CreateFile(root.Path, "VaultSync/updates/old-installer", 10, now.AddDays(-2));
        string recentInstaller = CreateFile(root.Path, "VaultSync/updates/recent-installer", 10, now.AddHours(-2));
        string oldRecovery = CreateDirectory(root.Path, "VaultSync/recovery-tests/old", 14, now.AddDays(-2));

        StorageCleanupSummary result = StorageHygieneService.PruneTemporaryData(root.Path, now);

        Assert.False(Directory.Exists(oldOpen));
        Assert.True(Directory.Exists(recentOpen));
        Assert.True(Directory.Exists(unrelated));
        Assert.False(File.Exists(oldExclude));
        Assert.False(File.Exists(oldInstaller));
        Assert.True(File.Exists(recentInstaller));
        Assert.False(Directory.Exists(oldRecovery));
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(2, result.DirectoriesRemoved);
    }

    [Fact]
    public void TemporaryCleanupBoundsOnlyRecognizedTelemetryExports()
    {
        using var root = new TempDirectory();
        DateTime now = DateTime.UtcNow;
        string oldExport = CreateFile(
            root.Path,
            "vaultsync-telemetry-export/telemetry_20260701_120000.zip",
            23,
            now.AddDays(-31));
        string recentExport = CreateFile(
            root.Path,
            "vaultsync-telemetry-export/telemetry_20260825_120000.zip",
            17,
            now.AddDays(-1));
        string unrelated = CreateFile(
            root.Path,
            "vaultsync-telemetry-export/user-notes.zip",
            29,
            now.AddYears(-1));

        StorageCleanupSummary result = StorageHygieneService.PruneTemporaryData(root.Path, now);

        Assert.False(File.Exists(oldExport));
        Assert.True(File.Exists(recentExport));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(1, result.FilesRemoved);
        Assert.Equal(23, result.BytesReclaimed);
    }

    [Fact]
    public void TemporaryCleanupRemovesLinkedChildrenWithoutTouchingTheirTargets()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var root = new TempDirectory();
        DateTime now = DateTime.UtcNow;
        string staleWorkspace = Path.Combine(root.Path, "vaultsync-restore-stale");
        string nested = Path.Combine(staleWorkspace, "nested");
        string outside = Path.Combine(root.Path, "outside-cleanup-target");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(nested, "owned.txt"), "remove");
        string outsideFile = Path.Combine(outside, "keep.txt");
        File.WriteAllText(outsideFile, "must survive");
        Directory.CreateSymbolicLink(Path.Combine(staleWorkspace, "linked-outside"), outside);
        Directory.SetLastWriteTimeUtc(staleWorkspace, now.AddDays(-2));

        StorageCleanupSummary result = StorageHygieneService.PruneTemporaryData(root.Path, now);

        Assert.False(Directory.Exists(staleWorkspace));
        Assert.True(File.Exists(outsideFile));
        Assert.Equal("must survive", File.ReadAllText(outsideFile));
        Assert.Equal(1, result.DirectoriesRemoved);
        Assert.Equal(6, result.BytesReclaimed);
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    public void ManagedMountGuardRejectsOnlyUnbackedMacMounts(
        bool isMacOs,
        bool isManaged,
        bool isNetworkMount,
        bool expected)
    {
        Assert.Equal(
            expected,
            BackupService.ShouldRejectUnbackedManagedMount(isMacOs, isManaged, isNetworkMount));
    }

    private static string CreateDirectory(
        string root,
        string relativePath,
        int payloadBytes,
        DateTime lastWriteUtc)
    {
        string directory = Path.Combine(root, relativePath);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "payload.bin"), new byte[payloadBytes]);
        Directory.SetCreationTimeUtc(directory, lastWriteUtc);
        Directory.SetLastWriteTimeUtc(directory, lastWriteUtc);
        return directory;
    }

    private static string CreateFile(
        string root,
        string relativePath,
        int length,
        DateTime lastWriteUtc)
    {
        string path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
