using System;
using System.IO;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupDestinationContainmentRegressionTests
{
    [Fact]
    public async Task RunBackupAsync_MustRejectBackupDestinationNestedInsideSourceBeforeWrites()
    {
        using var scope = new TempDirectory();
        string projectRoot = Directory.CreateDirectory(Path.Combine(scope.Path, "project")).FullName;
        File.WriteAllText(Path.Combine(projectRoot, "important.txt"), "important content");
        string unsafeBackupRoot = Directory.CreateDirectory(Path.Combine(projectRoot, "custom-backups")).FullName;
        SqliteRepository repo = TestRepository.Create(Path.Combine(scope.Path, "metadata.sqlite"));
        int id = TestRepository.AddProject(repo, "Demo", projectRoot, preset: string.Empty);
        Project project = repo.GetProjectById(id)!;

        var service = new BackupService(repo);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(project, unsafeBackupRoot, isAuto: false, useArchiveMode: true));
        Assert.Empty(Directory.EnumerateFileSystemEntries(unsafeBackupRoot));
    }

    [Fact]
    public async Task RunBackupAsync_MustRejectSymlinkDestinationThatResolvesInsideSource()
    {
        if (OperatingSystem.IsWindows()) return;
        using var scope = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(scope.Path, "source")).FullName;
        File.WriteAllText(Path.Combine(sourceRoot, "important.txt"), "important content");
        string internalRoot = Directory.CreateDirectory(Path.Combine(sourceRoot, "archive-storage")).FullName;
        string apparentlyExternalRoot = Path.Combine(scope.Path, "external-storage");
        Directory.CreateSymbolicLink(apparentlyExternalRoot, internalRoot);
        SqliteRepository repo = TestRepository.Create(Path.Combine(scope.Path, "metadata.sqlite"));
        int id = TestRepository.AddProject(repo, "Demo", sourceRoot, preset: string.Empty);
        Project project = repo.GetProjectById(id)!;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BackupService(repo).RunBackupAsync(project, apparentlyExternalRoot,
                isAuto: false, useArchiveMode: true));
        Assert.Empty(Directory.EnumerateFileSystemEntries(internalRoot));
    }

    [Fact]
    public async Task RejectedOverlappingDestinationMustNotCreateOrIngestArchives()
    {
        using var scope = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(scope.Path, "source")).FullName;
        string originalFile = Path.Combine(sourceRoot, "important.txt");
        File.WriteAllText(originalFile, "important content");
        string nestedBackupRoot = Directory.CreateDirectory(Path.Combine(sourceRoot, "custom-backups")).FullName;
        SqliteRepository repo = TestRepository.Create(Path.Combine(scope.Path, "metadata.sqlite"));
        int id = TestRepository.AddProject(repo, "Demo", sourceRoot, preset: string.Empty);
        Project project = repo.GetProjectById(id)!;
        var service = new BackupService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(project, nestedBackupRoot, isAuto: false, useArchiveMode: true));
        Assert.Empty(Directory.EnumerateFileSystemEntries(nestedBackupRoot));
        Assert.Equal("important content", File.ReadAllText(originalFile));
    }

    [Fact]
    public async Task RunBackupAsync_MustKeepDotDotProjectNameUnderConfiguredDestination()
    {
        using var scope = new TempDirectory();
        string sourceRoot = Directory.CreateDirectory(Path.Combine(scope.Path, "source")).FullName;
        File.WriteAllText(Path.Combine(sourceRoot, "important.txt"), "important content");
        string backupRoot = Directory.CreateDirectory(Path.Combine(scope.Path, "destination")).FullName;
        SqliteRepository repo = TestRepository.Create(Path.Combine(scope.Path, "metadata.sqlite"));
        int id = TestRepository.AddProject(repo, "..", sourceRoot, preset: string.Empty);
        Project project = repo.GetProjectById(id)!;

        var service = new BackupService(repo);
        BackupService.BackupRunResult result = await service.RunBackupAsync(
            project, backupRoot, isAuto: false, useArchiveMode: true);
        Assert.True(result.BackupId > 0);
        Backup backup = repo.GetBackupById(result.BackupId)!;
        string written = Path.Combine(backupRoot, backup.Path);
        Assert.True(BackupSafetyService.IsPathUnderRoot(backupRoot, written),
            $"Backup escaped configured destination: {written}");
    }

    [Fact]
    public void ProjectBackupFolderSlugMustNotContainDotDotOrBecomeEmpty()
    {
        Assert.NotEqual("..", BackupService.GetProjectBackupFolderName(".."));
        Assert.False(string.IsNullOrWhiteSpace(BackupService.GetProjectBackupFolderName("---")));
    }

    [Fact]
    public void EnsureSafeBackupRoot_MustRejectBackupDestinationSymlinkedIntoSource()
    {
        if (OperatingSystem.IsWindows()) return;
        using var scope = new TempDirectory();
        string projectRoot = Directory.CreateDirectory(Path.Combine(scope.Path, "source")).FullName;
        string internalRoot = Directory.CreateDirectory(Path.Combine(projectRoot, "stored")).FullName;
        string aliasRoot = Path.Combine(scope.Path, "apparently-external-backups");
        Directory.CreateSymbolicLink(aliasRoot, internalRoot);
        Assert.Throws<InvalidOperationException>(() =>
            BackupSafetyService.EnsureSafeBackupRoot(projectRoot, aliasRoot));
    }
}
