using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

/// <summary>
/// Bounded, real-filesystem fault-injection probes for public issue #846.
/// No production files, devices, network shares, disk fills or privileged mounts are used.
/// Distinguishes failures before snapshot commit from catastrophic process/power loss.
/// </summary>
public sealed class BackupFailureInjection846Tests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DestinationRenamedDuringInitialProgress_StopsWithoutHistory(bool archive, bool automatic)
    {
        Fixture f = NewFixture();
        string moved = Path.Combine(f.Workspace, "moved-destination");
        File.WriteAllText(Path.Combine(f.Destination, "sentinel.bin"), "preserve-me");
        bool didRename = false;
        var service = new BackupService(f.Repository);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(f.Project, f.Destination, isAuto: automatic,
                useArchiveMode: archive,
                progressCallback: (_, _, _) =>
                {
                    if (didRename)
                        return;
                    Directory.Move(f.Destination, moved);
                    didRename = true;
                }));

        Assert.True(didRename);
        Assert.Contains("does not exist or is not accessible", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(f.Destination));
        Assert.Equal("preserve-me", File.ReadAllText(Path.Combine(moved, "sentinel.bin")));
        AssertNoHistory(f);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingFileAtProjectBackupDirectory_RejectsWithoutMetadata(bool archive, bool automatic)
    {
        Fixture f = NewFixture();
        string blockedProjectDirectory = Path.Combine(
            f.Destination, BackupService.GetProjectBackupFolderName(f.Project.Name));
        File.WriteAllText(blockedProjectDirectory, "do-not-overwrite");
        var service = new BackupService(f.Repository);
        await Assert.ThrowsAnyAsync<IOException>(() =>
            service.RunBackupAsync(f.Project, f.Destination, isAuto: automatic,
                useArchiveMode: archive));

        Assert.Equal("do-not-overwrite", File.ReadAllText(blockedProjectDirectory));
        AssertNoHistory(f);
        Assert.Equal("source data", File.ReadAllText(Path.Combine(f.Project.RootPath, "data.txt")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationTriggeredByFirstProgressReport_CreatesNoHistory(bool archive, bool automatic)
    {
        Fixture f = NewFixture();
        using var cancellation = new CancellationTokenSource();
        int reports = 0;
        var service = new BackupService(f.Repository);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunBackupAsync(f.Project, f.Destination, isAuto: automatic,
                useArchiveMode: archive, ct: cancellation.Token,
                progressCallback: (_, _, _) =>
                {
                    Interlocked.Increment(ref reports);
                    cancellation.Cancel();
                }));

        Assert.True(reports >= 1);
        AssertNoHistory(f);
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDestinationReplacedByRegularFileAfterPreflight_RefusesRun(bool archive)
    {
        Fixture f = NewFixture();
        var service = new BackupService(f.Repository);
        BackupService.BackupPreflightResult result = await service.PreflightBackupAsync(
            f.Project, f.Destination, useArchiveMode: archive);
        Assert.Equal(1, result.TotalFiles);
        Directory.Delete(f.Destination);
        File.WriteAllText(f.Destination, "not-a-directory");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(f.Project, f.Destination, isAuto: true, useArchiveMode: archive));

        Assert.Contains("does not exist or is not accessible", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("not-a-directory", File.ReadAllText(f.Destination));
        AssertNoHistory(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuaranteedFreeSpaceThreshold_RejectsBeforeSnapshotCommit(bool archive)
    {
        Fixture f = NewFixture();
        var service = new BackupService(f.Repository);
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(f.Project, f.Destination, isAuto: false,
                useArchiveMode: archive, minimumFreeSpacePercent: 100d));

        Assert.Contains("does not have enough free space", ex.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoHistory(f);
        Assert.Equal("source data", File.ReadAllText(Path.Combine(f.Project.RootPath, "data.txt")));
    }

    [Fact]
    public void NestedDirectorySymlink_EscapeIsRejectedForWrite()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows symlink creation requires policy-dependent privileges.
        Fixture f = NewFixture();
        string nested = Path.Combine(f.Project.RootPath, "nested");
        Directory.CreateDirectory(nested);
        Directory.CreateSymbolicLink(Path.Combine(nested, "external"), f.Destination);

        Assert.False(BackupSafetyService.TryResolvePathForWriteUnderRoot(
            f.Project.RootPath, Path.Combine("nested", "external", "new-backup.zip"), out _));
    }

    [Fact]
    public void NestedDirectorySymlink_EscapeIsRejectedForRead()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows symlink creation requires policy-dependent privileges.
        Fixture f = NewFixture();
        string nested = Path.Combine(f.Project.RootPath, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(f.Destination, "external.dat"), "external");
        Directory.CreateSymbolicLink(Path.Combine(nested, "external"), f.Destination);

        Assert.False(BackupSafetyService.TryResolveExistingFileUnderRoot(
            f.Project.RootPath, Path.Combine("nested", "external", "external.dat"), out _));
    }

    private Fixture NewFixture()
    {
        string workspace = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"));
        string source = Path.Combine(workspace, "source");
        string destination = Path.Combine(workspace, "backup-target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "data.txt"), "source data");
        SqliteRepository repository = TestRepository.Create(Path.Combine(workspace, "test.db"));
        int id = TestRepository.AddProject(repository, "Fault Injection 846", source, preset: string.Empty);
        return new Fixture(workspace, repository, repository.GetProjectById(id)!, destination);
    }

    private static void AssertNoHistory(Fixture f)
    {
        Assert.Empty(f.Repository.GetBackupsForProject(f.Project.Id));
        Assert.Empty(f.Repository.GetSnapshotsForProject(f.Project.Name));
    }

    private sealed record Fixture(string Workspace, SqliteRepository Repository, Project Project, string Destination);

    public void Dispose() => _temp.Dispose();
}
