using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

// Public regression #846: archive destinations must never overlap the source tree.
// Every test uses disposable files and an isolated SQLite repository.
public sealed class BackupTopologyRegression846Tests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Preflight_RejectsBothOverlapDirections_BeforeScanning(
        bool backupWithinSource, bool archive)
    {
        (SqliteRepository repo, Project project, string backupRoot) = Setup(backupWithinSource);
        var service = new BackupService(repo);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreflightBackupAsync(project, backupRoot, useArchiveMode: archive));

        Assert.Contains("Unsafe backup destination", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(ProjectOutputRoot(project, backupRoot)));
        Assert.Empty(repo.GetBackupsForProject(project.Id));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task RunBackup_RejectsBothOverlapDirections_BeforeCreatingArtifacts(
        bool backupWithinSource, bool archive, bool automatic)
    {
        (SqliteRepository repo, Project project, string backupRoot) = Setup(backupWithinSource);
        var service = new BackupService(repo);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(project, backupRoot, isAuto: automatic, useArchiveMode: archive));

        Assert.Contains("Unsafe backup destination", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(ProjectOutputRoot(project, backupRoot)));
        Assert.Empty(repo.GetBackupsForProject(project.Id));
    }

    [Fact]
    public async Task Preflight_DoesNotReturnCachedSuccess_ForChangedOverlappingSource()
    {
        (SqliteRepository repo, Project project, string backupRoot) = Setup(backupWithinSource: null);
        var service = new BackupService(repo);
        BackupService.BackupPreflightResult safe = await service.PreflightBackupAsync(
            project, backupRoot, useArchiveMode: true, cacheTtl: TimeSpan.FromHours(1));
        Assert.True(safe.HasEnoughSpace);

        // A project with the same ID but a now-overlapping source must be refused
        // even if the preflight cache contains a prior positive response.
        Project unsafeProject = project with { RootPath = backupRoot };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreflightBackupAsync(unsafeProject, backupRoot,
                useArchiveMode: true, cacheTtl: TimeSpan.FromHours(1)));
    }

    [Fact]
    public async Task RunBackup_RejectsOverlappingFinalDestination_EvenIfStagingIsSafe()
    {
        (SqliteRepository repo, Project project, string backupRoot) = Setup(backupWithinSource: null);
        string unsafeFinalRoot = Path.Combine(project.RootPath, "nested-final-backups");
        Directory.CreateDirectory(unsafeFinalRoot);
        var service = new BackupService(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(new BackupService.BackupRunRequest
            {
                Project = project,
                BackupRoot = backupRoot,
                PreferredFinalBackupRoot = unsafeFinalRoot,
                UseArchiveMode = true
            }));

        Assert.False(Directory.Exists(ProjectOutputRoot(project, backupRoot)));
        Assert.Empty(repo.GetBackupsForProject(project.Id));
    }

    [Fact]
    public async Task SiblingArchiveDestination_ThreeBackupsContainOnlySourceFile()
    {
        (SqliteRepository repo, Project project, string backupRoot) = Setup(backupWithinSource: null);
        var service = new BackupService(repo);

        for (int i = 0; i < 3; i++)
        {
            BackupService.BackupPreflightResult preflight = await service.PreflightBackupAsync(
                project, backupRoot, useArchiveMode: true, cacheTtl: TimeSpan.Zero);
            Assert.True(preflight.HasEnoughSpace);
            BackupService.BackupRunResult result = await service.RunBackupAsync(
                project, backupRoot, isAuto: false, useArchiveMode: true);
            Assert.False(result.Cancelled);
            Assert.True(result.BackupId > 0);
            Backup backup = repo.GetBackupById(result.BackupId)!;
            using ZipArchive zip = ZipFile.OpenRead(Path.Combine(
                backupRoot, backup.Path, BackupArchiveCryptoService.PlainArchiveFileName));
            Assert.Equal(["important.txt"], zip.Entries.Select(e => e.FullName).ToArray());
            using var reader = new StreamReader(zip.GetEntry("important.txt")!.Open());
            Assert.Equal("source contents", await reader.ReadToEndAsync());
        }
        Assert.Equal(3, repo.GetBackupsForProject(project.Id).Count());
    }

    private (SqliteRepository Repo, Project Project, string BackupRoot) Setup(bool? backupWithinSource)
    {
        string workspace = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"));
        string source = Path.Combine(workspace, "source");
        string backup = Path.Combine(workspace, "backups");
        if (backupWithinSource == true)
            backup = Path.Combine(source, "custom-backups");
        if (backupWithinSource == false)
            source = Path.Combine(backup, "source");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(source, "important.txt"), "source contents");
        SqliteRepository repo = TestRepository.Create(Path.Combine(workspace, "test.db"));
        int id = TestRepository.AddProject(repo, "Overlap Regression", source, preset: string.Empty);
        return (repo, repo.GetProjectById(id)!, backup);
    }

    private static string ProjectOutputRoot(Project project, string backupRoot) =>
        Path.Combine(backupRoot, BackupService.GetProjectBackupFolderName(project.Name));

    public void Dispose() => _temp.Dispose();
}
