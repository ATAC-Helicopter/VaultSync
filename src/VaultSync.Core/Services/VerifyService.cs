using System.Security.Cryptography;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Services;

public class VerifyService(SqliteRepository repo, HashService hash)
{
    private readonly SqliteRepository _repo = repo;
    private readonly HashService _hash = hash;

    public Task<VerifyResult> VerifyAsync(Project project, string destination, int percent, bool full, CancellationToken ct = default) =>
        ProductDiagnostics.TraceBetaAsync(DiagnosticOperation.Verification, () => VerifyCoreAsync(project, destination, percent, full, ct), recordCompletion: false);

    // Encrypted payloads need an explicit credentialed verification workflow.
    public async Task<VerifyResult?> VerifyStoredBackupAsync(Backup backup, string backupRoot, CancellationToken ct = default)
    {
        string folder = Path.IsPathFullyQualified(backup.Path)
            ? backup.Path
            : Path.Combine(backupRoot, backup.Path);
        if (!BackupSafetyService.IsExistingPathSafeUnderRoot(backupRoot, folder))
            throw new DirectoryNotFoundException("The stored backup is unavailable under its recorded destination.");

        Snapshot? snapshot = _repo.GetSnapshotById(backup.SnapshotId);
        if (snapshot is null || snapshot.ProjectId != backup.ProjectId)
            throw new InvalidDataException("The stored backup has no matching snapshot.");

        if (backup.IsEncrypted)
            return null;

        List<FileEntry> files = await _repo.GetFilesForSnapshotAsync(snapshot.Id, ct).ConfigureAwait(false);
        // A folder backup can itself contain a source file named data.zip.
        FileEntry? archiveNamedFile = files.Find(file =>
            string.Equals(file.RelPath, BackupArchiveCryptoService.PlainArchiveFileName, StringComparison.Ordinal));
        if (archiveNamedFile is not null && await VerifyFileAsync(folder, archiveNamedFile, ct).ConfigureAwait(false) is null)
        {
            var folderFailures = new List<VerifyMismatch>();
            foreach (FileEntry file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (file != archiveNamedFile && await VerifyFileAsync(folder, file, ct).ConfigureAwait(false) is { } failure)
                    folderFailures.Add(failure);
            }
            return new VerifyResult(files.Count, files.Count - folderFailures.Count, folderFailures);
        }

        var evidence = await SnapshotExplorerService.ReadStoredFileEvidenceAsync(
            folder, files, int.MaxValue, long.MaxValue, ct).ConfigureAwait(false);
        if (evidence.SourceKind == SnapshotExplorerSourceKind.EncryptedArchive)
            return null;

        var failures = new List<VerifyMismatch>();
        foreach (FileEntry file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!evidence.Files.TryGetValue(file.RelPath.Replace('\\', '/'), out var observed) || !observed.Exists)
                failures.Add(new VerifyMismatch(file.RelPath, "missing", file.HashSha256, null));
            else if (!observed.WasRead)
                failures.Add(new VerifyMismatch(file.RelPath, observed.FailureCode ?? "unreadable", file.HashSha256, null));
            else if (string.IsNullOrWhiteSpace(file.HashSha256) ||
                     !string.Equals(file.HashSha256, observed.HashSha256, StringComparison.OrdinalIgnoreCase))
                failures.Add(new VerifyMismatch(file.RelPath, "hash-mismatch", file.HashSha256, observed.HashSha256));
        }

        return new VerifyResult(files.Count, files.Count - failures.Count, failures);
    }

    private async Task<VerifyResult> VerifyCoreAsync(Project project, string destination, int percent, bool full, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Snapshot snap = _repo.GetLatestSnapshot(project.Id) ?? throw new InvalidOperationException("No snapshot found for project.");
        List<FileEntry> files = await _repo.GetFilesForSnapshotAsync(snap.Id, ct).ConfigureAwait(false);
        if (files.Count == 0)
        {
            if (ProductDiagnostics.IsBeta)
                ProductDiagnostics.Record(DiagnosticOperation.Verification, DiagnosticOutcome.Completed,
                    phase: DiagnosticPhase.Verify, duration: clock.Elapsed);
            return new VerifyResult(0, 0, []);
        }

        List<FileEntry> sample = ChooseSample(files, ClampPercent(percent), full);

        int checkedCount = 0;
        var mismatches = new List<VerifyMismatch>();

        foreach (FileEntry f in sample)
        {
            ct.ThrowIfCancellationRequested();
            VerifyMismatch? mismatch = await VerifyFileAsync(destination, f, ct);
            if (mismatch is not null)
                mismatches.Add(mismatch);
            checkedCount++;
        }

        ProductDiagnostics.Record(DiagnosticOperation.Verification, mismatches.Count == 0 ? DiagnosticOutcome.Completed : DiagnosticOutcome.Failed,
            phase: DiagnosticPhase.Verify, quantity: checkedCount,
            duration: ProductDiagnostics.IsBeta ? clock.Elapsed : default,
            failure: mismatches.Count == 0 ? DiagnosticFailure.None : DiagnosticFailure.IntegrityMismatch);
        return new VerifyResult(sample.Count, checkedCount - mismatches.Count, mismatches);
    }

    private static int ClampPercent(int percent) =>
        Math.Clamp(percent, 1, 100);

    private static List<FileEntry> ChooseSample(IReadOnlyList<FileEntry> files, int percent, bool full)
    {
        if (full || percent >= 100)
            return [.. files];

        int take = Math.Max(1, (int)Math.Round(files.Count * (percent / 100.0)));
        return ReservoirSample(files, take);
    }

    private static List<FileEntry> ReservoirSample(IReadOnlyList<FileEntry> files, int take)
    {
        var sample = new List<FileEntry>(take);
        for (int i = 0; i < files.Count; i++)
        {
            if (i < take)
            {
                sample.Add(files[i]);
                continue;
            }

            int j = RandomNumberGenerator.GetInt32(i + 1);
            if (j < take)
                sample[j] = files[i];
        }

        return sample;
    }

    private async Task<VerifyMismatch?> VerifyFileAsync(string destination, FileEntry file, CancellationToken ct)
    {
        if (!BackupSafetyService.TryResolveExistingFileUnderRoot(destination, file.RelPath, out string destPath))
            return new VerifyMismatch(file.RelPath, "missing", Expected: file.HashSha256, Actual: null);

        try
        {
            string actual = await _hash.Sha256Async(destPath, ct);
            return actual.Equals(file.HashSha256, StringComparison.OrdinalIgnoreCase)
                ? null
                : new VerifyMismatch(file.RelPath, "hash-mismatch", Expected: file.HashSha256, Actual: actual);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new VerifyMismatch(file.RelPath, "error: " + ex.Message, Expected: file.HashSha256, Actual: null);
        }
    }
}

public record VerifyMismatch(string RelPath, string Reason, string? Expected, string? Actual);
public record VerifyResult(int Checked, int Passed, List<VerifyMismatch> Failures);
