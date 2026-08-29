using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using VaultSync.UI.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RestoreExecutionServiceTests
{
    [Fact]
    public async Task CancellationDuringApply_RestoresPreviousFilesAndRemovesCreatedFiles()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source");
        string target = Path.Combine(root.Path, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "existing.txt"), "replacement");
        File.WriteAllText(Path.Combine(source, "new.txt"), "created");
        File.WriteAllText(Path.Combine(target, "existing.txt"), "known-good");
        using var cancellation = new CancellationTokenSource();
        int progressUpdates = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RestoreExecutionService.RestoreAsync(
                source,
                target,
                encryptionPassword: null,
                selectedTopLevelTargets: null,
                _ =>
                {
                    if (Interlocked.Increment(ref progressUpdates) == 1)
                        cancellation.Cancel();
                },
                cancellation.Token));

        Assert.Equal("known-good", File.ReadAllText(Path.Combine(target, "existing.txt")));
        Assert.False(File.Exists(Path.Combine(target, "new.txt")));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories),
            path => path.Contains(".vaultsync-restore.tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationAfterFinalProgress_RollsBackLastAppliedFile()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source");
        string target = Path.Combine(root.Path, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "only.txt"), "replacement");
        File.WriteAllText(Path.Combine(target, "only.txt"), "known-good");
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RestoreExecutionService.RestoreAsync(
                source,
                target,
                encryptionPassword: null,
                selectedTopLevelTargets: null,
                _ => cancellation.Cancel(),
                cancellation.Token));

        Assert.Equal("known-good", File.ReadAllText(Path.Combine(target, "only.txt")));
    }

    [Fact]
    public async Task CancellationDuringArchiveExtraction_LeavesTargetUntouched()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source");
        string target = Path.Combine(root.Path, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "known-good.txt"), "preserve");
        string archivePath = Path.Combine(source, BackupArchiveCryptoService.PlainArchiveFileName);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "first.txt", "first");
            WriteEntry(archive, "second.txt", "second");
        }
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RestoreExecutionService.RestoreAsync(
                source,
                target,
                encryptionPassword: null,
                selectedTopLevelTargets: null,
                _ => cancellation.Cancel(),
                cancellation.Token));

        Assert.Equal(["known-good.txt"], Directory.GetFiles(target).Select(Path.GetFileName));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(target, "known-good.txt")));
    }

    [Fact]
    public async Task SuccessfulArchiveRestore_CommitsSelectedTopLevelOnly()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source");
        string target = Path.Combine(root.Path, "target");
        Directory.CreateDirectory(source);
        string archivePath = Path.Combine(source, BackupArchiveCryptoService.PlainArchiveFileName);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "selected/keep.txt", "keep");
            WriteEntry(archive, "other/skip.txt", "skip");
        }

        await RestoreExecutionService.RestoreAsync(
            source,
            target,
            encryptionPassword: null,
            selectedTopLevelTargets: ["selected"],
            progress: null,
            CancellationToken.None);

        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "selected", "keep.txt")));
        Assert.False(File.Exists(Path.Combine(target, "other", "skip.txt")));
    }

    [Fact]
    public async Task MissingSelectedArchiveTarget_FailsBeforeChangingLiveTarget()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source");
        string target = Path.Combine(root.Path, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "known-good.txt"), "preserve");
        string archivePath = Path.Combine(source, BackupArchiveCryptoService.PlainArchiveFileName);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            WriteEntry(archive, "present/file.txt", "content");

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            RestoreExecutionService.RestoreAsync(
                source,
                target,
                encryptionPassword: null,
                selectedTopLevelTargets: ["missing"],
                progress: null,
                CancellationToken.None));

        Assert.Contains("missing", error.Message, StringComparison.Ordinal);
        Assert.Equal(["known-good.txt"], Directory.GetFiles(target).Select(Path.GetFileName));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(target, "known-good.txt")));
    }

    [Fact]
    public async Task DestinationLoss_PreservesRollbackEvidenceAndDoesNotReportSuccess()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source");
        string target = Path.Combine(root.Path, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "only.txt"), "replacement");
        File.WriteAllText(Path.Combine(target, "only.txt"), "known-good");

        RestoreRecoveryException error = await Assert.ThrowsAsync<RestoreRecoveryException>(() =>
            RestoreExecutionService.RestoreAsync(
                source,
                target,
                encryptionPassword: null,
                selectedTopLevelTargets: null,
                _ => Directory.Delete(target, recursive: true),
                CancellationToken.None));

        try
        {
            Assert.Contains("Rollback files were preserved", error.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(error.RecoveryDirectory));
            Assert.Equal(
                "known-good",
                File.ReadAllText(Path.Combine(error.RecoveryDirectory, "only.txt")));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            if (Directory.Exists(error.RecoveryDirectory))
                Directory.Delete(error.RecoveryDirectory, recursive: true);
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }
}
