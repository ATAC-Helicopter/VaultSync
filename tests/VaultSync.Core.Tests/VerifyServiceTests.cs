using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class VerifyServiceTests : IDisposable
{
    private readonly TempDirectory _tempDir = new();

    [Fact]
    public async Task VerifyAsync_WhenHashingIsCancelled_PropagatesCancellation()
    {
        VerificationFixture fixture = CreateFixture();
        var hash = new BlockingHashService();
        var service = new VerifyService(fixture.Repository, hash);
        using var cancellation = new CancellationTokenSource();

        Task<VerifyResult> verification = service.VerifyAsync(
            fixture.Project,
            fixture.Destination,
            percent: 100,
            full: true,
            cancellation.Token);
        await hash.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification);
    }

    [Fact]
    public async Task VerifyAsync_WhenHashingFails_ReportsActionableMismatch()
    {
        VerificationFixture fixture = CreateFixture();
        var service = new VerifyService(fixture.Repository, new FailingHashService());

        VerifyResult result = await service.VerifyAsync(
            fixture.Project,
            fixture.Destination,
            percent: 100,
            full: true);

        VerifyMismatch failure = Assert.Single(result.Failures);
        Assert.Equal("data.txt", failure.RelPath);
        Assert.Contains("read failed", failure.Reason, StringComparison.Ordinal);
        Assert.Equal(1, result.Checked);
        Assert.Equal(0, result.Passed);
    }

    public void Dispose() => _tempDir.Dispose();

    [Theory]
    [InlineData("original", true)]
    [InlineData("changed!", false)]
    public async Task VerifyStoredBackupAsync_ReadsZipEntriesUsingTheBackupSnapshot(string payload, bool valid)
    {
        VerificationFixture fixture = CreateFixture();
        string folder = Path.Combine(fixture.Destination, "backup");
        Directory.CreateDirectory(folder);
        string archive = Path.Combine(folder, BackupArchiveCryptoService.PlainArchiveFileName);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("data.txt").Open()))
            writer.Write(payload);

        string originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData("original"u8));
        int selected = fixture.Repository.CreateSnapshot(fixture.Project.Id, 1, 8);
        fixture.Repository.InsertFiles(selected, [new FileEntry("data.txt", 8, DateTime.UtcNow, originalHash)]);
        int newer = fixture.Repository.CreateSnapshot(fixture.Project.Id, 1, 3);
        fixture.Repository.InsertFiles(newer, [new FileEntry("new.txt", 3, DateTime.UtcNow, "EXPECTED")]);
        var backup = new Backup { ProjectId = fixture.Project.Id, SnapshotId = selected, Path = "backup" };

        VerifyResult result = Assert.IsType<VerifyResult>(
            await new VerifyService(fixture.Repository, new HashService()).VerifyStoredBackupAsync(backup, fixture.Destination));

        Assert.Equal(1, result.Checked);
        Assert.Equal(valid ? 1 : 0, result.Passed);
        if (valid)
            Assert.Empty(result.Failures);
        else
            Assert.Equal("hash-mismatch", Assert.Single(result.Failures).Reason);
    }

    [Fact]
    public async Task VerifyStoredBackupAsync_TreatsAMatchingSourceNamedDataZipAsARegularFile()
    {
        VerificationFixture fixture = CreateFixture();
        string folder = Path.Combine(fixture.Destination, "backup");
        Directory.CreateDirectory(folder);
        string name = BackupArchiveCryptoService.PlainArchiveFileName;
        byte[] content = "source bytes"u8.ToArray();
        File.WriteAllBytes(Path.Combine(folder, name), content);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));
        int snapshot = fixture.Repository.CreateSnapshot(fixture.Project.Id, 1, content.Length);
        fixture.Repository.InsertFiles(snapshot, [new FileEntry(name, content.Length, DateTime.UtcNow, hash)]);
        var backup = new Backup { ProjectId = fixture.Project.Id, SnapshotId = snapshot, Path = "backup" };

        VerifyResult result = Assert.IsType<VerifyResult>(
            await new VerifyService(fixture.Repository, new HashService()).VerifyStoredBackupAsync(backup, fixture.Destination));

        Assert.Equal(1, result.Passed);
        Assert.Empty(result.Failures);
    }

    private VerificationFixture CreateFixture()
    {
        string root = Path.Combine(_tempDir.Path, Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "data.txt"), "verification payload");

        var repository = TestRepository.Create(Path.Combine(root, "vaultsync.db"));
        int projectId = TestRepository.AddProject(repository, "Verify", source);
        var project = new Project
        {
            Id = projectId,
            Name = "Verify",
            RootPath = source,
            Preset = "dotnet"
        };
        int snapshotId = repository.CreateSnapshot(projectId, DateTime.UtcNow.Ticks, 20);
        repository.InsertFiles(snapshotId, [new FileEntry("data.txt", 20, DateTime.UtcNow, "EXPECTED")]);
        return new VerificationFixture(repository, project, destination);
    }

    private sealed class BlockingHashService : HashService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<string> Sha256Async(string file, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return string.Empty;
        }
    }

    private sealed class FailingHashService : HashService
    {
        public override Task<string> Sha256Async(string file, CancellationToken ct = default) =>
            throw new IOException("read failed");
    }

    private sealed record VerificationFixture(
        VaultSync.Core.Repositories.SqliteRepository Repository,
        Project Project,
        string Destination);
}
