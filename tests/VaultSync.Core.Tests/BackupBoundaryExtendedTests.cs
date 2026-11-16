using System;
using System.IO;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupBoundaryExtendedTests
{
    [Fact]
    public async Task PreflightRejectsBackupRootUnderProject()
    {
        using var t = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(t.Path, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "data.txt"), "test");
        string nested = Directory.CreateDirectory(Path.Combine(source, "custom-storage")).FullName;
        SqliteRepository repo = TestRepository.Create(Path.Combine(t.Path, "db.sqlite"));
        int id = TestRepository.AddProject(repo, "Demo", source, preset: "");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BackupService(repo).PreflightBackupAsync(repo.GetProjectById(id)!, nested, useArchiveMode: true));
    }

    [Fact]
    public async Task RunBackupRejectsProjectInsideDestinationRoot()
    {
        using var t = new TempDirectory();
        string root = Directory.CreateDirectory(Path.Combine(t.Path, "vault")).FullName;
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "data.txt"), "test");
        SqliteRepository repo = TestRepository.Create(Path.Combine(t.Path, "db.sqlite"));
        int id = TestRepository.AddProject(repo, "Demo", source, preset: "");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new BackupService(repo).RunBackupAsync(repo.GetProjectById(id)!, root, isAuto: false, useArchiveMode: true));
    }

    [Fact]
    public void GuardRejectsSourceRootAlias()
    {
        if (OperatingSystem.IsWindows()) return;
        using var t = new TempDirectory();
        string physicalSource = Directory.CreateDirectory(Path.Combine(t.Path, "real-source")).FullName;
        string root = Directory.CreateDirectory(Path.Combine(physicalSource, "store")).FullName;
        string alias = Path.Combine(t.Path, "linked-source");
        Directory.CreateSymbolicLink(alias, physicalSource);
        Assert.Throws<InvalidOperationException>(() =>
            BackupSafetyService.EnsureSafeBackupRoot(alias, root));
    }

    [Fact]
    public async Task SafeSiblingDestinationCreatesReadableArchive()
    {
        using var t = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(t.Path, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "important.txt"), "test");
        string dest = Directory.CreateDirectory(Path.Combine(t.Path, "separate")).FullName;
        SqliteRepository repo = TestRepository.Create(Path.Combine(t.Path, "db.sqlite"));
        int id = TestRepository.AddProject(repo, "Valid", source, preset: "");
        var svc = new BackupService(repo);
        Project project = repo.GetProjectById(id)!;
        for (int i = 0; i < 3; i++)
        {
            BackupService.BackupRunResult res = await svc.RunBackupAsync(project, dest, isAuto: false, useArchiveMode: true);
            Backup backup = repo.GetBackupById(res.BackupId)!;
            string archivePath = Path.Combine(dest, backup.Path, BackupArchiveCryptoService.PlainArchiveFileName);
            Assert.True(BackupSafetyService.IsPathUnderRoot(dest, archivePath));
            using var zip = System.IO.Compression.ZipFile.OpenRead(archivePath);
            Assert.Single(zip.Entries);
            Assert.Equal("important.txt", zip.Entries[0].FullName);
        }
    }

    [Fact]
    public void SafeSiblingGuardPasses()
    {
        using var t = new TempDirectory();
        string source = Directory.CreateDirectory(Path.Combine(t.Path, "source")).FullName;
        string dest = Directory.CreateDirectory(Path.Combine(t.Path, "separate")).FullName;
        BackupSafetyService.EnsureSafeBackupRoot(source, dest);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("----")]
    [InlineData("...")]
    public void ProjectSlugMustBeSingleSafeNonemptySegment(string name)
    {
        string slug = BackupService.GetProjectBackupFolderName(name);
        Assert.False(string.IsNullOrWhiteSpace(slug));
        Assert.NotEqual(".", slug);
        Assert.NotEqual("..", slug);
        using var t = new TempDirectory();
        string root = Directory.CreateDirectory(Path.Combine(t.Path, "destination")).FullName;
        Assert.True(BackupSafetyService.IsPathUnderRoot(root, Path.Combine(root, slug)));
    }
}
