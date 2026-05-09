using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Services;

public class VerifyService(SqliteRepository repo, HashService hash)
{
    private readonly SqliteRepository _repo = repo;
    private readonly HashService _hash = hash;

    public async Task<VerifyResult> VerifyAsync(Project project, string destination, int percent, bool full, CancellationToken ct = default)
    {
        if (percent < 1) percent = 1;
        if (percent > 100) percent = 100;

        Snapshot snap = _repo.GetLatestSnapshot(project.Id) ?? throw new Exception("No snapshot found for project.");
        var files = _repo.GetFilesForSnapshot(snap.Id).ToList();
        if (files.Count == 0) return new VerifyResult(0, 0, []);

        // choose sample
        List<FileEntry> sample = files;
        if (!full && percent < 100)
        {
            int take = Math.Max(1, (int)Math.Round(files.Count * (percent / 100.0)));
            var rnd = new Random(42); // deterministic sample
            sample = new List<FileEntry>(take);
            for (int i = 0; i < files.Count; i++)
            {
                if (i < take)
                {
                    sample.Add(files[i]);
                    continue;
                }

                int j = rnd.Next(i + 1);
                if (j < take)
                {
                    sample[j] = files[i];
                }
            }
        }

        int checkedCount = 0;
        var mismatches = new List<VerifyMismatch>();

        foreach (FileEntry f in sample)
        {
            ct.ThrowIfCancellationRequested();
            string destPath = Path.Combine(destination, f.RelPath);
            if (!File.Exists(destPath))
            {
                mismatches.Add(new VerifyMismatch(f.RelPath, "missing", Expected: f.HashSha256, Actual: null));
                continue;
            }

            try
            {
                string actual = await _hash.Sha256Async(destPath, ct);
                if (!actual.Equals(f.HashSha256, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add(new VerifyMismatch(f.RelPath, "hash-mismatch", Expected: f.HashSha256, Actual: actual));
            }
            catch (Exception ex)
            {
                mismatches.Add(new VerifyMismatch(f.RelPath, "error: " + ex.Message, Expected: f.HashSha256, Actual: null));
            }

            checkedCount++;
        }

        return new VerifyResult(sample.Count, checkedCount - mismatches.Count, mismatches);
    }
}

public record VerifyMismatch(string RelPath, string Reason, string? Expected, string? Actual);
public record VerifyResult(int Checked, int Passed, List<VerifyMismatch> Failures);
