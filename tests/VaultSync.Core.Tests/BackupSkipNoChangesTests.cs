using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupSkipNoChangesTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public BackupSkipNoChangesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vaultsync-skip-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "vaultsync.db");
    }

    [Fact]
    public async Task RunBackupAsync_DoesNotSkipNoChanges_WhenNoUsableBackupExists()
    {
        var repo = new SqliteRepository(_dbPath);
        repo.EnsureSchema();
        var project = CreateProject(repo);
        await CreateBaselineSnapshotAsync(repo, project);

        var backupRoot = Path.Combine(_tempDir, "backups");
        Directory.CreateDirectory(backupRoot);
        var service = new BackupService(repo);

        var result = await service.RunBackupAsync(
            project,
            backupRoot,
            isAuto: true,
            useArchiveMode: true,
            skipIfNoChanges: true);

        Assert.False(result.SkippedForNoChanges);
        Assert.True(result.BackupId > 0);
        var backup = repo.GetBackupById(result.BackupId);
        Assert.NotNull(backup);
        Assert.True(Directory.Exists(Path.Combine(backupRoot, backup!.Path)));
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(Path.Combine(backupRoot, backup.Path)));
    }

    [Fact]
    public async Task RunBackupAsync_SkipsNoChanges_WhenUsableBackupAlreadyExists()
    {
        var repo = new SqliteRepository(_dbPath);
        repo.EnsureSchema();
        var project = CreateProject(repo);
        var backupRoot = Path.Combine(_tempDir, "backups");
        Directory.CreateDirectory(backupRoot);
        var service = new BackupService(repo);

        var first = await service.RunBackupAsync(
            project,
            backupRoot,
            isAuto: true,
            useArchiveMode: true,
            skipIfNoChanges: true);

        Assert.False(first.SkippedForNoChanges);
        Assert.True(first.BackupId > 0);

        var second = await service.RunBackupAsync(
            project,
            backupRoot,
            isAuto: true,
            useArchiveMode: true,
            skipIfNoChanges: true);

        Assert.True(second.SkippedForNoChanges);
        Assert.Equal(0, second.BackupId);
        Assert.Single(repo.GetBackupsForProject(project.Id));
        var firstBackup = repo.GetBackupById(first.BackupId);
        Assert.NotNull(firstBackup);
        Assert.True(Directory.Exists(Path.Combine(backupRoot, firstBackup!.Path)));
    }

    [Fact]
    public async Task RunBackupAsync_BackupAllStyleParallelRun_CreatesUsableBackupsForAllProjects()
    {
        var repo = new SqliteRepository(_dbPath);
        repo.EnsureSchema();
        var projects = new[]
        {
            CreateProject(repo, "Project One", "one.txt", "one"),
            CreateProject(repo, "Project Two", "two.txt", "two"),
            CreateProject(repo, "Project Three", "three.txt", "three")
        };

        foreach (var project in projects)
        {
            await CreateBaselineSnapshotAsync(repo, project);
        }

        var backupRoot = Path.Combine(_tempDir, "backups");
        Directory.CreateDirectory(backupRoot);
        var service = new BackupService(repo);

        var results = await Task.WhenAll(projects.Select(project =>
            service.RunBackupAsync(
                project,
                backupRoot,
                isAuto: false,
                useArchiveMode: true,
                skipIfNoChanges: true)));

        Assert.All(results, result =>
        {
            Assert.False(result.SkippedForNoChanges);
            Assert.True(result.BackupId > 0);
        });

        foreach (var result in results)
        {
            var backup = repo.GetBackupById(result.BackupId);
            Assert.NotNull(backup);
            var backupPath = Path.Combine(backupRoot, backup!.Path);
            Assert.True(Directory.Exists(backupPath));
            Assert.NotEmpty(Directory.EnumerateFileSystemEntries(backupPath));
        }
    }

    private Project CreateProject(SqliteRepository repo)
        => CreateProject(repo, "Project One", "data.txt", "backup me");

    private Project CreateProject(SqliteRepository repo, string name, string fileName, string contents)
    {
        var sourceRoot = Path.Combine(_tempDir, name.Replace(' ', '-'));
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, fileName), contents, Encoding.UTF8);

        var projectId = repo.AddProject(new Project
        {
            Name = name,
            RootPath = sourceRoot,
            Preset = string.Empty
        });

        return repo.GetProjectById(projectId)!;
    }

    private static async Task CreateBaselineSnapshotAsync(SqliteRepository repo, Project project)
    {
        var snapshotService = new SnapshotService(repo, new HashService());
        await snapshotService.CreateSnapshotAsync(
            project,
            fullHash: false,
            hashNow: false,
            maxSnapshotsToKeep: null,
            ct: CancellationToken.None);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }
}
