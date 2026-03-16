using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Repositories;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupCheckpointResumeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public BackupCheckpointResumeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vaultsync-checkpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "vaultsync.db");
    }

    [Fact]
    public void BuildArchiveResumeFingerprint_IsStableAcrossFileOrder()
    {
        var sourceDir = Path.Combine(_tempDir, "source");
        Directory.CreateDirectory(sourceDir);

        var a = Path.Combine(sourceDir, "a.txt");
        var b = Path.Combine(sourceDir, "nested", "b.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.WriteAllText(a, "alpha", Encoding.UTF8);
        File.WriteAllText(b, "beta", Encoding.UTF8);

        var forward = BackupService.BuildArchiveResumeFingerprint(sourceDir, new[] { a, b });
        var reverse = BackupService.BuildArchiveResumeFingerprint(sourceDir, new[] { b, a });

        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void ValidateArchiveResumePrefix_ReturnsTrueOnlyForMatchingPrefix()
    {
        var local = Path.Combine(_tempDir, "local.zip");
        var dest = Path.Combine(_tempDir, "dest.zip");
        File.WriteAllBytes(local, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        File.WriteAllBytes(dest, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        Assert.True(BackupService.ValidateArchiveResumePrefix(local, dest, 32, 16, CancellationToken.None));

        File.WriteAllBytes(dest, Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        Assert.False(BackupService.ValidateArchiveResumePrefix(local, dest, 32, 16, CancellationToken.None));
    }

    [Fact]
    public void CleanupIncompleteBackups_PreservesCheckpointedArchiveFolders()
    {
        var backupRoot = Path.Combine(_tempDir, "backups");
        var projectDir = Path.Combine(backupRoot, "project");
        var resumableDir = Path.Combine(projectDir, "2026-03-13_10-00-00");
        var staleDir = Path.Combine(projectDir, "2026-03-13_09-00-00");
        Directory.CreateDirectory(resumableDir);
        Directory.CreateDirectory(staleDir);

        File.WriteAllText(Path.Combine(resumableDir, ".vaultsync_inprogress"), "started");
        File.WriteAllText(Path.Combine(resumableDir, BackupArchiveCryptoService.PlainArchiveFileName), "partial");
        File.WriteAllText(
            Path.Combine(resumableDir, ".vaultsync_resume.json"),
            """
            {
              "Version": 1,
              "Mode": "archive",
              "SourceFingerprint": "ABC",
              "ArchiveSizeBytes": 7,
              "LastUpdatedUtc": "2026-03-13T10:00:00Z"
            }
            """,
            Encoding.UTF8);

        File.WriteAllText(Path.Combine(staleDir, ".vaultsync_inprogress"), "started");
        File.WriteAllText(Path.Combine(staleDir, "orphan.txt"), "stale");

        var repo = new SqliteRepository(_dbPath);
        var service = new BackupService(repo);

        var removed = service.CleanupIncompleteBackups(backupRoot);

        Assert.Equal(1, removed);
        Assert.True(Directory.Exists(resumableDir));
        Assert.False(Directory.Exists(staleDir));
    }

    [Fact]
    public void UpdateCheckpointResumeTelemetry_StoresExpectedSummary()
    {
        var cfg = new AppConfig();

        BackupService.UpdateCheckpointResumeTelemetry(
            cfg,
            status: "resume-attempt",
            projectName: "VaultSync",
            backupFolder: @"C:\backups\vaultsync\2026-03-16_12-00-00",
            archivePath: @"C:\backups\vaultsync\2026-03-16_12-00-00\backup.zip",
            resumeOffsetBytes: 5242880,
            archiveSizeBytes: 10485760,
            sourceFingerprint: "ABC123",
            message: "Resuming archive upload from a validated existing prefix.");

        Assert.Equal("resume-attempt", cfg.Advanced.CheckpointResumeTelemetry.LastStatus);
        Assert.Equal("VaultSync", cfg.Advanced.CheckpointResumeTelemetry.LastProjectName);
        Assert.Equal(5242880, cfg.Advanced.CheckpointResumeTelemetry.LastResumeOffsetBytes);
        Assert.Equal(10485760, cfg.Advanced.CheckpointResumeTelemetry.LastArchiveSizeBytes);
        Assert.Equal("ABC123", cfg.Advanced.CheckpointResumeTelemetry.LastSourceFingerprint);
        Assert.False(string.IsNullOrWhiteSpace(cfg.Advanced.CheckpointResumeTelemetry.LastUpdatedUtc));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
