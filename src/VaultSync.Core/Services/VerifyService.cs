using System.Security.Cryptography;
using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Services;

public class VerifyService(SqliteRepository repo, HashService hash)
{
    private readonly SqliteRepository _repo = repo;
    private readonly HashService _hash = hash;

    public async Task<VerifyResult> VerifyAsync(Project project, string destination, int percent, bool full, CancellationToken ct = default)
    {
        Snapshot snap = _repo.GetLatestSnapshot(project.Id) ?? throw new InvalidOperationException("No snapshot found for project.");
        List<FileEntry> files = await _repo.GetFilesForSnapshotAsync(snap.Id, ct).ConfigureAwait(false);
        if (files.Count == 0) return new VerifyResult(0, 0, []);

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
        catch (Exception ex)
        {
            return new VerifyMismatch(file.RelPath, "error: " + ex.Message, Expected: file.HashSha256, Actual: null);
        }
    }
}

public record VerifyMismatch(string RelPath, string Reason, string? Expected, string? Actual);
public record VerifyResult(int Checked, int Passed, List<VerifyMismatch> Failures);
