#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Recoverability;
using VaultSync.Core.Services;
using VaultSync.Core.Tests.TestSupport;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoverabilityServiceTests : IDisposable
{
    private readonly TempDirectory _root = new();

    [Fact]
    public async Task AnalyzeAsync_VerifiesFolderBytesAndProducesStableEvidence()
    {
        string backup = CreateFolderFile("src/app.txt", "healthy");
        FileEntry expected = Entry("src/app.txt", "healthy");

        RecoverabilityResult first = await Analyze(backup, [expected]);
        RecoverabilityResult second = await Analyze(backup, [expected]);

        Assert.Equal(RecoverabilitySchema.Version, first.SchemaVersion);
        Assert.Equal(RecoverabilityVerdict.FullyRecoverable, first.Verdict);
        Assert.Equal(1, first.Totals.VerifiedItems);
        Assert.Equal(RecoverabilityRestoreAction.Create, Assert.Single(first.Items).Action);
        Assert.Equal(
            first.Evidence.Select(item => item.Id),
            second.Evidence.Select(item => item.Id));
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsMissingAndCorruptedFiles()
    {
        string backup = CreateFolderFile("healthy.txt", "healthy");
        CreateFolderFile(backup, "corrupt.txt", "tampered");
        FileEntry healthy = Entry("healthy.txt", "healthy");
        FileEntry corrupt = Entry("corrupt.txt", "original");
        FileEntry missing = Entry("missing.txt", "missing");

        RecoverabilityResult result = await Analyze(backup, [healthy, corrupt, missing]);

        Assert.Equal(RecoverabilityVerdict.PartiallyRecoverable, result.Verdict);
        Assert.Equal(1, result.Totals.VerifiedItems);
        Assert.Equal(1, result.Totals.CorruptedItems);
        Assert.Equal(1, result.Totals.UnavailableItems);
        Assert.Contains(result.Evidence, item => item.Code == "hash_mismatch" && item.Path == "corrupt.txt");
        Assert.Contains(result.Evidence, item => item.Code == "object_missing" && item.Path == "missing.txt");
    }

    [Fact]
    public async Task AnalyzeAsync_VerifiesZipEntryBytes()
    {
        string backup = Path.Combine(_root.Path, "zip");
        Directory.CreateDirectory(backup);
        string archivePath = Path.Combine(backup, BackupArchiveCryptoService.PlainArchiveFileName);
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("docs/readme.md");
            await using Stream stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("zip content"));
        }

        RecoverabilityResult result = await Analyze(backup, [Entry("docs/readme.md", "zip content")]);

        Assert.Equal(RecoverabilityVerdict.FullyRecoverable, result.Verdict);
        Assert.Equal(1, result.Totals.VerifiedItems);
    }

    [Fact]
    public async Task AnalyzeAsync_EncryptedArchiveNeverClaimsVerification()
    {
        string backup = Path.Combine(_root.Path, "encrypted");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, BackupArchiveCryptoService.EncryptedArchiveFileName), "locked");

        RecoverabilityResult result = await Analyze(backup, [Entry("secret.txt", "secret")]);

        Assert.Equal(RecoverabilityVerdict.Inconclusive, result.Verdict);
        Assert.Equal(1, result.Totals.InconclusiveItems);
        Assert.Contains(result.Evidence, item => item.Code == "encrypted_content_locked");
    }

    [Fact]
    public async Task AnalyzeAsync_BoundsBytesWithoutMisreportingFilesAsMissing()
    {
        string backup = CreateFolderFile("large.txt", "0123456789");

        RecoverabilityResult result = await RecoverabilityService.AnalyzeAsync(
            new RecoverabilityRequest(42),
            backup,
            [Entry("large.txt", "0123456789")],
            maximumFiles: 5,
            maximumBytes: 5);

        Assert.True(result.IsLimited);
        Assert.Equal(RecoverabilityVerdict.Inconclusive, result.Verdict);
        Assert.Contains(result.Evidence, item => item.Code == "verification_limit_reached");
        Assert.DoesNotContain(result.Evidence, item => item.Code == "object_missing");
    }

    [Fact]
    public async Task AnalyzeAsync_BoundsMissingFileExamination()
    {
        string backup = Directory.CreateDirectory(Path.Combine(_root.Path, "bounded-missing")).FullName;

        RecoverabilityResult result = await RecoverabilityService.AnalyzeAsync(
            new RecoverabilityRequest(42),
            backup,
            [Entry("a-missing.txt", "a"), Entry("b-not-examined.txt", "b")],
            maximumFiles: 1,
            maximumBytes: 1024);

        Assert.True(result.IsLimited);
        Assert.Single(result.Items);
        Assert.Contains(result.Evidence, item =>
            item.Code == "object_missing" && item.Path == "a-missing.txt");
        Assert.Contains(result.Evidence, item =>
            item.Code == "selection_limit_reached");
        Assert.DoesNotContain(result.Evidence, item => item.Path == "b-not-examined.txt");
    }

    [Fact]
    public async Task AnalyzeAsync_OriginalLocationDetectsIdenticalAndNewerConflict()
    {
        string backup = CreateFolderFile("same.txt", "same");
        CreateFolderFile(backup, "conflict.txt", "backup");
        string destination = Path.Combine(_root.Path, "destination");
        CreateFolderFile(destination, "same.txt", "same");
        string conflictPath = CreateFolderFile(destination, "conflict.txt", "newer");
        File.SetLastWriteTimeUtc(conflictPath, DateTime.UtcNow.AddDays(1));

        RecoverabilityResult result = await RecoverabilityService.AnalyzeAsync(
            new RecoverabilityRequest(
                42,
                DestinationMode: RecoverabilityDestinationMode.OriginalLocation,
                DestinationRoot: destination),
            backup,
            [Entry("same.txt", "same"), Entry("conflict.txt", "backup")]);

        Assert.Equal(RecoverabilityRestoreAction.SkipIdentical, result.Items.Single(item => item.File.RelPath == "same.txt").Action);
        Assert.Equal(RecoverabilityRestoreAction.Conflict, result.Items.Single(item => item.File.RelPath == "conflict.txt").Action);
        Assert.Equal(1, result.Totals.Conflicts);
        Assert.DoesNotContain(
            Directory.GetFiles(destination, "*", SearchOption.AllDirectories),
            path => !path.EndsWith("same.txt", StringComparison.Ordinal) &&
                    !path.EndsWith("conflict.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnalyzeAsync_RejectsUnsafeAndDuplicateNormalizedPaths()
    {
        string backup = CreateFolderFile("safe.txt", "safe");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Analyze(backup, [Entry("../escape.txt", "unsafe")]));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Analyze(backup, [Entry("/absolute.txt", "unsafe")]));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Analyze(backup, [Entry("safe.txt", "safe"), Entry(@"safe.txt", "safe")]));
    }

    [Fact]
    public async Task AnalyzeAsync_PathSelectionIsCaseSensitiveAndSegmentBounded()
    {
        string backup = CreateFolderFile("Docs/a.txt", "a");
        CreateFolderFile(backup, "Docs-old/b.txt", "b");

        RecoverabilityResult result = await RecoverabilityService.AnalyzeAsync(
            new RecoverabilityRequest(42, "Docs"),
            backup,
            [Entry("Docs/a.txt", "a"), Entry("Docs-old/b.txt", "b")]);
        RecoverabilityResult wrongCase = await RecoverabilityService.AnalyzeAsync(
            new RecoverabilityRequest(42, "docs"),
            backup,
            [Entry("Docs/a.txt", "a")]);

        Assert.Equal("Docs/a.txt", Assert.Single(result.Items).File.RelPath);
        Assert.Equal(RecoverabilityVerdict.Unrecoverable, wrongCase.Verdict);
    }

    private static Task<RecoverabilityResult> Analyze(string backup, IReadOnlyCollection<FileEntry> entries) =>
        RecoverabilityService.AnalyzeAsync(new RecoverabilityRequest(42), backup, entries);

    private string CreateFolderFile(string relativePath, string content)
    {
        string backup = Path.Combine(_root.Path, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        CreateFolderFile(backup, relativePath, content);
        return backup;
    }

    private static string CreateFolderFile(string root, string relativePath, string content)
    {
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static FileEntry Entry(string path, string content) =>
        new(
            path,
            Encoding.UTF8.GetByteCount(content),
            DateTime.UtcNow.AddDays(-1),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))));

    public void Dispose() => _root.Dispose();
}
