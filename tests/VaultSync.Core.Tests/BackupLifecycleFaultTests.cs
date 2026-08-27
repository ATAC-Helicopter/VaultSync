using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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

public sealed class BackupLifecycleFaultTests : IDisposable
{
    private readonly TempDirectory _tempDir = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompressionCancellation_PreservesPreviousBackupAndPublishesNoPartialState(bool encrypted)
    {
        string dbPath = Path.Combine(_tempDir.Path, "vaultsync.db");
        SqliteRepository repo = TestRepository.Create(dbPath);
        Project project = CreateProject(repo, encrypted);
        string backupRoot = Path.Combine(_tempDir.Path, "backups");
        Directory.CreateDirectory(backupRoot);

        AppConfig config = CreateConfig(encrypted);
        var service = new BackupService(
            repo,
            CreateSecretService(),
            new FixedConfigStore(config, dbPath));

        BackupService.BackupRunResult baseline = await service.RunBackupAsync(
            project,
            backupRoot,
            isAuto: false,
            useArchiveMode: true,
            enableCheckpointedRetry: true);

        Backup knownGood = repo.GetBackupById(baseline.BackupId)!;
        string knownGoodFolder = Path.Combine(backupRoot, knownGood.Path);
        string artifactName = encrypted
            ? BackupArchiveCryptoService.EncryptedArchiveFileName
            : BackupArchiveCryptoService.PlainArchiveFileName;
        string knownGoodArtifact = Path.Combine(knownGoodFolder, artifactName);
        byte[] knownGoodHash = SHA256.HashData(await File.ReadAllBytesAsync(knownGoodArtifact));

        // Force a fresh archive with enough data to guarantee a compression callback.
        byte[] changedPayload = Enumerable.Range(0, 2 * 1024 * 1024)
            .Select(index => (byte)(index % 251))
            .ToArray();
        await File.WriteAllBytesAsync(Path.Combine(project.RootPath, "changed.bin"), changedPayload);

        bool cancelledDuringCompression = false;
        BackupService.BackupRunResult interrupted = await service.RunBackupAsync(
            project,
            backupRoot,
            isAuto: false,
            progressCallback: (_, _, status) =>
            {
                if (cancelledDuringCompression || !status.Contains("Compressing", StringComparison.Ordinal))
                    return;

                cancelledDuringCompression = true;
                service.CancelBackup(project.Id);
            },
            useArchiveMode: true,
            enableCheckpointedRetry: true);

        Assert.True(cancelledDuringCompression);
        Assert.True(interrupted.Cancelled);
        Assert.Equal(0, interrupted.BackupId);

        Backup[] indexedBackups = repo.GetBackupsForProject(project.Id).ToArray();
        Backup remaining = Assert.Single(indexedBackups);
        Assert.Equal(knownGood.Id, remaining.Id);
        Assert.True(File.Exists(Path.Combine(knownGoodFolder, ".vaultsync_complete")));
        Assert.Equal(knownGoodHash, SHA256.HashData(await File.ReadAllBytesAsync(knownGoodArtifact)));

        string projectBackupRoot = Path.GetDirectoryName(knownGoodFolder)!;
        string remainingFolder = Assert.Single(Directory.GetDirectories(projectBackupRoot));
        Assert.Equal(
            Path.GetFullPath(knownGoodFolder),
            Path.GetFullPath(remainingFolder));
        Assert.Empty(Directory.EnumerateFiles(projectBackupRoot, ".vaultsync_inprogress", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(projectBackupRoot, ".vaultsync_resume.json", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceFileLossDuringCompression_FailsWithoutPublishingIncompleteBackup(bool encrypted)
    {
        string dbPath = Path.Combine(_tempDir.Path, "source-loss.db");
        SqliteRepository repo = TestRepository.Create(dbPath);
        Project project = CreateProject(repo, encrypted);
        string secondSource = Path.Combine(project.RootPath, "second.bin");
        await File.WriteAllBytesAsync(
            secondSource,
            Enumerable.Range(0, 1024 * 1024).Select(index => (byte)(index % 251)).ToArray());
        string backupRoot = Path.Combine(_tempDir.Path, "source-loss-backups");
        Directory.CreateDirectory(backupRoot);
        var service = new BackupService(
            repo,
            CreateSecretService(),
            new FixedConfigStore(CreateConfig(encrypted), dbPath));

        bool faultInjected = false;
        IOException error = await Assert.ThrowsAsync<IOException>(() => service.RunBackupAsync(
            project,
            backupRoot,
            isAuto: false,
            progressCallback: (_, currentFile, _) =>
            {
                if (faultInjected ||
                    !string.Equals(currentFile, "Preparing archive backup...", StringComparison.Ordinal))
                {
                    return;
                }

                File.Delete(secondSource);
                faultInjected = true;
            },
            useArchiveMode: true,
            enableCheckpointedRetry: true));

        Assert.True(faultInjected);
        Assert.Contains("could not read required source file", error.Message, StringComparison.Ordinal);
        Assert.Empty(repo.GetBackupsForProject(project.Id));

        string projectBackupRoot = Path.Combine(
            backupRoot,
            BackupService.GetProjectBackupFolderName(project.Name));
        Assert.True(Directory.Exists(projectBackupRoot));
        Assert.Empty(Directory.GetDirectories(projectBackupRoot));
        Assert.Empty(Directory.EnumerateFiles(projectBackupRoot, ".vaultsync_inprogress", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task HashCancellation_PreservesPreviousSnapshotAndPublishesNoPartialSnapshot()
    {
        string dbPath = Path.Combine(_tempDir.Path, "hash-cancellation.db");
        SqliteRepository repo = TestRepository.Create(dbPath);
        Project project = CreateProject(repo, encrypted: false);
        var baselineService = new SnapshotService(repo, new HashService());
        string scanCachePath = GetScanCachePath(project);

        try
        {
            int baselineId = await baselineService.CreateSnapshotAsync(project, fullHash: true);
            FileEntry[] baselineFiles = [.. repo.GetFilesForSnapshot(baselineId)];
            byte[] baselineCache = await File.ReadAllBytesAsync(scanCachePath);

            await File.WriteAllTextAsync(
                Path.Combine(project.RootPath, "pending.txt"),
                "must not enter a cancelled snapshot",
                Encoding.UTF8);
            var blockingHash = new BlockingHashService();
            var service = new SnapshotService(repo, blockingHash);
            using var cancellation = new CancellationTokenSource();

            Task<int> createTask = service.CreateSnapshotAsync(
                project,
                fullHash: true,
                maxSnapshotsToKeep: null,
                ct: cancellation.Token);
            await blockingHash.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => createTask);
            Snapshot remaining = Assert.Single(repo.GetSnapshotsForProject(project.Name));
            Assert.Equal(baselineId, remaining.Id);
            Assert.Equal(baselineFiles, repo.GetFilesForSnapshot(baselineId));
            Assert.Equal(baselineCache, await File.ReadAllBytesAsync(scanCachePath));
        }
        finally
        {
            File.Delete(scanCachePath);
        }
    }

    [Fact]
    public async Task DeferredHashCancellation_PublishesNoPartialHashes()
    {
        string dbPath = Path.Combine(_tempDir.Path, "deferred-hash-cancellation.db");
        SqliteRepository repo = TestRepository.Create(dbPath);
        Project project = CreateProject(repo, encrypted: false);
        var baselineService = new SnapshotService(repo, new HashService());
        string scanCachePath = GetScanCachePath(project);

        try
        {
            int snapshotId = await baselineService.CreateSnapshotAsync(
                project,
                fullHash: true,
                hashNow: false,
                maxSnapshotsToKeep: null,
                ct: CancellationToken.None);
            Assert.All(repo.GetFilesForSnapshot(snapshotId), file => Assert.Empty(file.HashSha256));

            var blockingHash = new BlockingHashService();
            var service = new SnapshotService(repo, blockingHash);
            using var cancellation = new CancellationTokenSource();
            Task<int> hashTask = service.HashMissingFilesAsync(project, snapshotId, cancellation.Token);
            await blockingHash.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hashTask);
            Assert.All(repo.GetFilesForSnapshot(snapshotId), file => Assert.Empty(file.HashSha256));
        }
        finally
        {
            File.Delete(scanCachePath);
        }
    }

    private Project CreateProject(SqliteRepository repo, bool encrypted)
    {
        string sourceRoot = Path.Combine(_tempDir.Path, "source");
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(Path.Combine(sourceRoot, "baseline.txt"), "known-good", Encoding.UTF8);

        int projectId = TestRepository.AddProject(repo, "Lifecycle", sourceRoot, preset: string.Empty);
        Project project = repo.GetProjectById(projectId)!;
        return encrypted
            ? project with
            {
                EncryptionPolicy = ProjectEncryptionPolicy.Encrypted,
                EncryptionKeyRef = "project-key-ref"
            }
            : project;
    }

    private static AppConfig CreateConfig(bool encrypted)
    {
        var config = new AppConfig();
        config.Backups.Encryption.Enabled = encrypted;
        config.Backups.Encryption.KeyRef = encrypted ? "global-key-ref" : string.Empty;
        return config;
    }

    private static BackupEncryptionSecretService CreateSecretService()
        => new(
            (existing, _) => existing ?? "generated-key-ref",
            (keyRef, _, _, _) => keyRef == "project-key-ref" ? "test-password" : null,
            (_, _, _, _) => { },
            (_, _) => { });

    private static string GetScanCachePath(Project project) =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VaultSync",
            "cache",
            "scan",
            $"{project.Id}.json");

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

    private sealed class BlockingHashService : HashService
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<string> Sha256Async(
            string file,
            CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return string.Empty;
        }
    }
}
