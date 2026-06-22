using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Config;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupSkipNoChangesTests : IDisposable
{
    private readonly TempDirectory _tempDir = new();
    private readonly string _dbPath;

    public BackupSkipNoChangesTests()
    {
        _dbPath = Path.Combine(_tempDir.Path, "vaultsync.db");
    }

    [Fact]
    public async Task RunBackupAsync_DoesNotSkipNoChanges_WhenNoUsableBackupExists()
    {
        SqliteRepository repo = TestRepository.Create(_dbPath);
        Project project = CreateProject(repo);
        await CreateBaselineSnapshotAsync(repo, project);

        string backupRoot = Path.Combine(_tempDir.Path, "backups");
        Directory.CreateDirectory(backupRoot);
        var service = new BackupService(repo);

        BackupService.BackupRunResult result = await service.RunBackupAsync(
            project,
            backupRoot,
            isAuto: true,
            useArchiveMode: true,
            skipIfNoChanges: true);

        Assert.False(result.SkippedForNoChanges);
        Assert.True(result.BackupId > 0);
        Backup backup = repo.GetBackupById(result.BackupId);
        Assert.NotNull(backup);
        Assert.True(Directory.Exists(Path.Combine(backupRoot, backup!.Path)));
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(Path.Combine(backupRoot, backup.Path)));
    }

    [Fact]
    public async Task RunBackupAsync_SkipsNoChanges_WhenUsableBackupAlreadyExists()
    {
        SqliteRepository repo = TestRepository.Create(_dbPath);
        Project project = CreateProject(repo);
        string backupRoot = Path.Combine(_tempDir.Path, "backups");
        Directory.CreateDirectory(backupRoot);
        var service = new BackupService(repo);

        BackupService.BackupRunResult first = await service.RunBackupAsync(
            project,
            backupRoot,
            isAuto: true,
            useArchiveMode: true,
            skipIfNoChanges: true);

        Assert.False(first.SkippedForNoChanges);
        Assert.True(first.BackupId > 0);

        BackupService.BackupRunResult second = await service.RunBackupAsync(
            project,
            backupRoot,
            isAuto: true,
            useArchiveMode: true,
            skipIfNoChanges: true);

        Assert.True(second.SkippedForNoChanges);
        Assert.Equal(0, second.BackupId);
        Assert.Single(repo.GetBackupsForProject(project.Id));
        Backup firstBackup = repo.GetBackupById(first.BackupId);
        Assert.NotNull(firstBackup);
        Assert.True(Directory.Exists(Path.Combine(backupRoot, firstBackup!.Path)));
    }

    [Fact]
    public async Task RunBackupAsync_BackupAllStyleParallelRun_CreatesUsableBackupsForAllProjects()
    {
        SqliteRepository repo = TestRepository.Create(_dbPath);
        Project[] projects = new[]
        {
            CreateProject(repo, "Project One", "one.txt", "one"),
            CreateProject(repo, "Project Two", "two.txt", "two"),
            CreateProject(repo, "Project Three", "three.txt", "three")
        };

        foreach (Project project in projects)
        {
            await CreateBaselineSnapshotAsync(repo, project);
        }

        string backupRoot = Path.Combine(_tempDir.Path, "backups");
        Directory.CreateDirectory(backupRoot);
        var service = new BackupService(repo);

        BackupService.BackupRunResult[] results = await Task.WhenAll(projects.Select(project =>
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

        foreach (BackupService.BackupRunResult result in results)
        {
            Backup backup = repo.GetBackupById(result.BackupId);
            Assert.NotNull(backup);
            string backupPath = Path.Combine(backupRoot, backup!.Path);
            Assert.True(Directory.Exists(backupPath));
            Assert.NotEmpty(Directory.EnumerateFileSystemEntries(backupPath));
        }
    }

    [Fact]
    public async Task RunBackupAsync_EncryptedArchive_UploadsOnlyEncryptedArtifact()
    {
        SqliteRepository repo = TestRepository.Create(_dbPath);
        Project project = CreateProject(repo) with
        {
            EncryptionPolicy = ProjectEncryptionPolicy.Encrypted,
            EncryptionKeyRef = "project-key-ref"
        };

        string backupRoot = Path.Combine(_tempDir.Path, "encrypted-backups");
        Directory.CreateDirectory(backupRoot);
        var config = new AppConfig();
        config.Backups.Encryption.Enabled = true;
        config.Backups.Encryption.KeyRef = "global-key-ref";
        var configStore = new FixedConfigStore(config, _dbPath);
        var secrets = new BackupEncryptionSecretService(
            (existing, _) => existing ?? "generated-key-ref",
            (keyRef, _, _, _) => keyRef == "project-key-ref" ? "test-password" : null,
            (_, _, _, _) => { },
            (_, _) => { });
        var service = new BackupService(repo, secrets, configStore);

        BackupService.BackupRunResult result = await service.RunBackupAsync(
            project,
            backupRoot,
            isAuto: false,
            useArchiveMode: true,
            enableCheckpointedRetry: true);

        Backup backup = repo.GetBackupById(result.BackupId)!;
        string backupPath = Path.Combine(backupRoot, backup.Path);
        Assert.True(backup.IsEncrypted);
        Assert.True(File.Exists(Path.Combine(backupPath, BackupArchiveCryptoService.EncryptedArchiveFileName)));
        Assert.True(File.Exists(Path.Combine(backupPath, BackupArchiveCryptoService.MetadataFileName)));
        Assert.False(File.Exists(Path.Combine(backupPath, BackupArchiveCryptoService.PlainArchiveFileName)));
        Assert.False(File.Exists(Path.Combine(backupPath, ".vaultsync_resume.json")));
    }

    private Project CreateProject(SqliteRepository repo)
        => CreateProject(repo, "Project One", "data.txt", "backup me");

    private Project CreateProject(SqliteRepository repo, string name, string fileName, string contents)
    {
        string sourceRoot = Path.Combine(_tempDir.Path, name.Replace(' ', '-'));
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, fileName), contents, Encoding.UTF8);

        int projectId = TestRepository.AddProject(repo, name, sourceRoot, preset: string.Empty);

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
        _tempDir.Dispose();
    }

    private sealed class FixedConfigStore(AppConfig config, string dbPath) : IAppConfigStore
    {
        public bool WasConfigMissingOnFirstLoad => false;
        public AppConfig GetSnapshot() => config;
        public AppConfig Load() => config;
        public void Save(AppConfig value) { }
        public Task SaveAsync(AppConfig value, CancellationToken ct = default) => Task.CompletedTask;
        public string GetDefaultDbPath() => dbPath;
        public string ResolveDbPath(AppConfig value = null) => dbPath;
    }
}
