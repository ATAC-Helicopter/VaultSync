using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using VaultSync.Core.Config;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class BackupCheckpointResumeTests : IDisposable
{
    private readonly TempDirectory _tempDir = new();

    [Fact]
    public void BuildArchiveResumeFingerprint_IsStableAcrossFileOrder()
    {
        string sourceDir = Path.Combine(_tempDir.Path, "source");
        Directory.CreateDirectory(sourceDir);

        string a = Path.Combine(sourceDir, "a.txt");
        string b = Path.Combine(sourceDir, "nested", "b.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.WriteAllText(a, "alpha", Encoding.UTF8);
        File.WriteAllText(b, "beta", Encoding.UTF8);

        string forward = BackupService.BuildArchiveResumeFingerprint(sourceDir, new[] { a, b });
        string reverse = BackupService.BuildArchiveResumeFingerprint(sourceDir, new[] { b, a });

        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void ValidateArchiveResumePrefix_ReturnsTrueOnlyForMatchingPrefix()
    {
        string local = Path.Combine(_tempDir.Path, "local.zip");
        string dest = Path.Combine(_tempDir.Path, "dest.zip");
        File.WriteAllBytes(local, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray());
        File.WriteAllBytes(dest, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        Assert.True(BackupService.ValidateArchiveResumePrefix(local, dest, 32, 16, CancellationToken.None));

        File.WriteAllBytes(dest, Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        Assert.False(BackupService.ValidateArchiveResumePrefix(local, dest, 32, 16, CancellationToken.None));
    }

    [Fact]
    public void CleanupIncompleteBackups_PreservesCheckpointedArchiveFolders()
    {
        string backupRoot = Path.Combine(_tempDir.Path, "backups");
        string projectDir = Path.Combine(backupRoot, "project");
        string resumableDir = Path.Combine(projectDir, "2026-03-13_10-00-00");
        string staleDir = Path.Combine(projectDir, "2026-03-13_09-00-00");
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

        int removed = BackupService.CleanupIncompleteBackups(backupRoot);

        Assert.Equal(1, removed);
        Assert.True(Directory.Exists(resumableDir));
        Assert.False(Directory.Exists(staleDir));
    }

    [Fact]
    public void CleanupIncompleteBackups_PreservesEncryptedCheckpointedArchiveFolders()
    {
        string backupRoot = Path.Combine(_tempDir.Path, "encrypted-backups");
        string resumableDir = Path.Combine(backupRoot, "project", "2026-03-13_11-00-00");
        Directory.CreateDirectory(resumableDir);

        File.WriteAllText(Path.Combine(resumableDir, ".vaultsync_inprogress"), "started");
        File.WriteAllText(Path.Combine(resumableDir, BackupArchiveCryptoService.EncryptedArchiveFileName), "partial");
        File.WriteAllText(
            Path.Combine(resumableDir, ".vaultsync_resume.json"),
            $$"""
            {
              "Version": 1,
              "Mode": "archive",
              "SourceFingerprint": "ENCRYPTED-ABC",
              "ArchiveSizeBytes": 7,
              "LastUpdatedUtc": "2026-03-13T11:00:00Z",
              "ArtifactFileName": "{{BackupArchiveCryptoService.EncryptedArchiveFileName}}"
            }
            """,
            Encoding.UTF8);

        int removed = BackupService.CleanupIncompleteBackups(backupRoot);

        Assert.Equal(0, removed);
        Assert.True(Directory.Exists(resumableDir));
    }

    [Theory]
    [InlineData(2, "archive", "ABC", "data.zip")]
    [InlineData(1, "native", "ABC", "data.zip")]
    [InlineData(1, "archive", "", "data.zip")]
    [InlineData(1, "archive", "ABC", "untrusted.partial")]
    public void CleanupIncompleteBackups_RemovesInvalidArchiveCheckpoints(
        int version,
        string mode,
        string fingerprint,
        string artifactFileName)
    {
        string backupRoot = Path.Combine(_tempDir.Path, $"invalid-{version}-{mode}-{artifactFileName}");
        string invalidDir = Path.Combine(backupRoot, "project", "2026-03-13_12-00-00");
        Directory.CreateDirectory(invalidDir);

        File.WriteAllText(Path.Combine(invalidDir, ".vaultsync_inprogress"), "started");
        File.WriteAllText(Path.Combine(invalidDir, artifactFileName), "partial");
        File.WriteAllText(
            Path.Combine(invalidDir, ".vaultsync_resume.json"),
            $$"""
            {
              "Version": {{version}},
              "Mode": "{{mode}}",
              "SourceFingerprint": "{{fingerprint}}",
              "ArchiveSizeBytes": 7,
              "LastUpdatedUtc": "2026-03-13T12:00:00Z",
              "ArtifactFileName": "{{artifactFileName}}"
            }
            """,
            Encoding.UTF8);

        int removed = BackupService.CleanupIncompleteBackups(backupRoot);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(invalidDir));
    }

    [Fact]
    public void UpdateCheckpointResumeTelemetry_StoresExpectedSummary()
    {
        var cfg = new AppConfig();

        BackupService.UpdateCheckpointResumeTelemetry(
            cfg,
            new BackupService.CheckpointResumeTelemetryUpdate
            {
                Status = "resume-attempt",
                ProjectName = "VaultSync",
                BackupFolder = @"C:\backups\vaultsync\2026-03-16_12-00-00",
                ArchivePath = @"C:\backups\vaultsync\2026-03-16_12-00-00\backup.zip",
                ResumeOffsetBytes = 5242880,
                ArchiveSizeBytes = 10485760,
                SourceFingerprint = "ABC123",
                Message = "Resuming archive upload from a validated existing prefix."
            });

        Assert.Equal("resume-attempt", cfg.Advanced.CheckpointResumeTelemetry.LastStatus);
        Assert.Equal("VaultSync", cfg.Advanced.CheckpointResumeTelemetry.LastProjectName);
        Assert.Equal(5242880, cfg.Advanced.CheckpointResumeTelemetry.LastResumeOffsetBytes);
        Assert.Equal(10485760, cfg.Advanced.CheckpointResumeTelemetry.LastArchiveSizeBytes);
        Assert.Equal("ABC123", cfg.Advanced.CheckpointResumeTelemetry.LastSourceFingerprint);
        Assert.False(string.IsNullOrWhiteSpace(cfg.Advanced.CheckpointResumeTelemetry.LastUpdatedUtc));
    }

    public void Dispose()
    {
        _tempDir.Dispose();
    }
}
