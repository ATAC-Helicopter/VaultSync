using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

/// <summary>
/// Issue #846: bounded filesystem-failure and content-preservation probes.
/// All fixtures are disposable. These tests do not qualify real SMB/NAS failure,
/// filesystem race hardening, encrypted restore or device-level disk exhaustion.
/// </summary>
public sealed class BackupResilienceSupplement846Tests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Theory]
    [InlineData("equal")]
    [InlineData("destination-under-source")]
    [InlineData("source-under-destination")]
    [InlineData("normalized-equal")]
    [InlineData("normalized-child")]
    [InlineData("ancestor")]
    public void Guard_RejectsOverlappingPathsBeforeFilesystemWork(string topology)
    {
        string root = Workspace();
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        switch (topology)
        {
            case "equal":
                destination = source;
                break;
            case "destination-under-source":
                destination = Path.Combine(source, "custom", "copies");
                break;
            case "source-under-destination":
                source = Path.Combine(destination, "source");
                break;
            case "normalized-equal":
                destination = Path.Combine(source, ".", "child", "..");
                break;
            case "normalized-child":
                destination = Path.Combine(source, "child", "..", "archives");
                break;
            case "ancestor":
                destination = root;
                break;
        }
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => BackupSafetyService.EnsureSafeBackupRoot(source, destination));
        Assert.Contains("Unsafe backup destination", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("source", "source-archive")]
    [InlineData("source-archive", "source")]
    [InlineData("source", "backups")]
    [InlineData("source-v1", "source-v2")]
    public void Guard_DoesNotRejectDistinctSiblingDirectoryNames(string sourceName, string destName)
    {
        string root = Workspace();
        string source = Path.Combine(root, sourceName);
        string destination = Path.Combine(root, destName);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        BackupSafetyService.EnsureSafeBackupRoot(source, destination);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovedDestination_AfterSuccessfulPreflight_IsRejectedRatherThanRecreated(bool archive)
    {
        Fixture f = NewFixture();
        var service = new BackupService(f.Repository);
        BackupService.BackupPreflightResult first = await service.PreflightBackupAsync(
            f.Project, f.Destination, useArchiveMode: archive, cacheTtl: TimeSpan.FromHours(1));
        Assert.False(first.UsedCache);
        Directory.Delete(f.Destination);

        InvalidOperationException preflight = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreflightBackupAsync(f.Project, f.Destination,
                useArchiveMode: archive, cacheTtl: TimeSpan.FromHours(1)));
        InvalidOperationException execution = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(f.Project, f.Destination, isAuto: false, useArchiveMode: archive));
        Assert.Contains("not exist or is not accessible", preflight.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not exist or is not accessible", execution.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(f.Destination));
        AssertNoHistory(f);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AlreadyCancelledSafeBackup_NeverCreatesBackupOrSnapshot(bool archive, bool isAuto)
    {
        Fixture f = NewFixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new BackupService(f.Repository);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RunBackupAsync(new BackupService.BackupRunRequest
            {
                Project = f.Project,
                BackupRoot = f.Destination,
                IsAuto = isAuto,
                UseArchiveMode = archive,
                CancellationToken = cts.Token
            }));
        AssertNoHistory(f);
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsafeDestination_IsRejectedEvenWithCancelledToken(bool archive)
    {
        Fixture f = NewFixture();
        string unsafePath = Path.Combine(f.Project.RootPath, "nested-backup");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var service = new BackupService(f.Repository);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreflightBackupAsync(f.Project, unsafePath,
                useArchiveMode: archive, ct: cts.Token));
        Assert.Contains("Unsafe backup destination", ex.Message, StringComparison.OrdinalIgnoreCase);
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RunBackupAsync(new BackupService.BackupRunRequest
            {
                Project = f.Project,
                BackupRoot = unsafePath,
                UseArchiveMode = archive,
                CancellationToken = cts.Token
            }));
        Assert.Contains("Unsafe backup destination", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(unsafePath));
        AssertNoHistory(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preflight_UsesBoundedSourceFileSetWithoutWritingArtifacts(bool archive)
    {
        Fixture f = NewFixture();
        int fileCount = 1;
        for (int i = 0; i < 48; i++)
        {
            string directory = Path.Combine(f.Project.RootPath, "sub", (i % 4).ToString());
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"file-{i:D3}.txt"), $"payload-{i}", Encoding.UTF8);
            fileCount++;
        }
        var service = new BackupService(f.Repository);
        BackupService.BackupPreflightResult result = await service.PreflightBackupAsync(
            f.Project, f.Destination, useArchiveMode: archive, cacheTtl: TimeSpan.Zero);
        Assert.Equal(fileCount, result.TotalFiles);
        Assert.True(result.TotalBytes > 0);
        Assert.False(result.UsedCache);
        AssertNoHistory(f);
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.Destination));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archive_RemainsReadableWithNestedUnicodeAndBinaryFiles(bool isAuto)
    {
        Fixture f = NewFixture();
        string nested = Path.Combine(f.Project.RootPath, "nested-بيانات");
        Directory.CreateDirectory(nested);
        var binary = new byte[128 * 1024];
        new Random(846).NextBytes(binary);
        File.WriteAllBytes(Path.Combine(nested, "payload.bin"), binary);
        var service = new BackupService(f.Repository);

        BackupService.BackupRunResult result = await service.RunBackupAsync(
            f.Project, f.Destination, isAuto: isAuto, useArchiveMode: true);
        Assert.True(result.BackupId > 0);
        Backup backup = f.Repository.GetBackupById(result.BackupId)!;
        string zipPath = Path.Combine(
            f.Destination, backup.Path, BackupArchiveCryptoService.PlainArchiveFileName);
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        Assert.Equal(2, archive.Entries.Count);
        ZipArchiveEntry entry = archive.Entries.SingleOrDefault(e =>
            e.FullName.Replace('\\', '/') == "nested-بيانات/payload.bin");
        Assert.NotNull(entry);
        using Stream stream = entry!.Open();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        Assert.Equal(binary, memory.ToArray());
        Assert.Contains(archive.Entries, e => e.FullName == "important.txt");
        Assert.Single(f.Repository.GetBackupsForProject(f.Project.Id));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("../../outside.txt")]
    [InlineData("./../../outside.txt")]
    public void RelativeFileEscape_IsRejectedByWriteAndReadPathHelpers(string relative)
    {
        Fixture f = NewFixture();
        Assert.False(BackupSafetyService.TryResolvePathForWriteUnderRoot(
            f.Project.RootPath, relative, out _));
        Assert.False(BackupSafetyService.TryResolveExistingFileUnderRoot(
            f.Project.RootPath, relative, out _));
    }

    [Fact]
    public void LinkedParentDirectory_IsNotAllowedForBackupFileWrite()
    {
        if (OperatingSystem.IsWindows())
            return; // Windows symlink-creation privilege is runner-policy dependent.
        Fixture f = NewFixture();
        string link = Path.Combine(f.Project.RootPath, "alias");
        Directory.CreateSymbolicLink(link, f.Destination);
        Assert.False(BackupSafetyService.TryResolvePathForWriteUnderRoot(
            f.Project.RootPath, Path.Combine("alias", "archive.zip"), out _));
    }

    [Fact]
    public void LinkedExternalFile_IsNotReadFromTheProtectedSource()
    {
        if (OperatingSystem.IsWindows())
            return; // Do not assume Developer Mode or symlink privilege on Windows.
        Fixture f = NewFixture();
        string external = Path.Combine(f.Destination, "external.txt");
        File.WriteAllText(external, "outside");
        File.CreateSymbolicLink(Path.Combine(f.Project.RootPath, "alias.txt"), external);
        Assert.False(BackupSafetyService.TryResolveExistingFileUnderRoot(
            f.Project.RootPath, "alias.txt", out _));
        Assert.True(BackupSafetyService.TryResolveExistingFileUnderRoot(
            f.Project.RootPath, "important.txt", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preflight_DoesNotTraverseLinkedExternalDirectory(bool archive)
    {
        if (OperatingSystem.IsWindows())
            return; // Symlink privilege is not guaranteed on the hosted Windows runner.
        Fixture f = NewFixture();
        string external = Path.Combine(f.Destination, "external");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "not-in-source.txt"), "outside");
        Directory.CreateSymbolicLink(Path.Combine(f.Project.RootPath, "external-link"), external);
        var service = new BackupService(f.Repository);
        BackupService.BackupPreflightResult result = await service.PreflightBackupAsync(
            f.Project, f.Destination, useArchiveMode: archive, cacheTtl: TimeSpan.Zero);
        Assert.Equal(1, result.TotalFiles);
        AssertNoHistory(f);
    }

    private Fixture NewFixture()
    {
        string root = Workspace();
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "backup-destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, "important.txt"), "source contents");
        SqliteRepository repository = TestRepository.Create(Path.Combine(root, "test.db"));
        int id = TestRepository.AddProject(repository, "Resilience Supplement", source, preset: string.Empty);
        return new Fixture(repository, repository.GetProjectById(id)!, destination);
    }

    private static void AssertNoHistory(Fixture fixture)
    {
        Assert.Empty(fixture.Repository.GetBackupsForProject(fixture.Project.Id));
        Assert.Empty(fixture.Repository.GetSnapshotsForProject(fixture.Project.Name));
    }

    private string Workspace()
    {
        string directory = Path.Combine(_temp.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed record Fixture(SqliteRepository Repository, Project Project, string Destination);

    public void Dispose() => _temp.Dispose();
}
