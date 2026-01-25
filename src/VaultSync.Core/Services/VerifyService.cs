using VaultSync.Core.Models;
using VaultSync.Core.Repositories;

namespace VaultSync.Core.Services;

public class VerifyService
{
    private readonly SqliteRepository _repo;
    private readonly HashService _hash;

    public VerifyService(SqliteRepository repo, HashService hash)
    {
        _repo = repo; _hash = hash;
    }

    public async Task<VerifyResult> VerifyAsync(Project project, string destination, int percent, bool full, CancellationToken ct = default)
    {
        if (percent < 1) percent = 1;
        if (percent > 100) percent = 100;

        var snap = _repo.GetLatestSnapshot(project.Id) ?? throw new Exception("No snapshot found for project.");
        var files = _repo.GetFilesForSnapshot(snap.Id).ToList();
        if (files.Count == 0) return new VerifyResult(0, 0, new());

        // choose sample
        List<FileEntry> sample = files;
        if (!full && percent < 100)
        {
            var take = Math.Max(1, (int)Math.Round(files.Count * (percent / 100.0)));
            var rnd = new Random(42); // deterministic sample
            sample = new List<FileEntry>(take);
            for (var i = 0; i < files.Count; i++)
            {
                if (i < take)
                {
                    sample.Add(files[i]);
                    continue;
                }

                var j = rnd.Next(i + 1);
                if (j < take)
                {
                    sample[j] = files[i];
                }
            }
        }

        var checkedCount = 0;
        var mismatches = new List<VerifyMismatch>();

        foreach (var f in sample)
        {
            ct.ThrowIfCancellationRequested();
            var destPath = Path.Combine(destination, f.RelPath);
            if (!File.Exists(destPath))
            {
                mismatches.Add(new VerifyMismatch(f.RelPath, "missing", expected: f.HashSha256, actual: null));
                continue;
            }

            try
            {
                var actual = await _hash.Sha256Async(destPath, ct);
                if (!actual.Equals(f.HashSha256, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add(new VerifyMismatch(f.RelPath, "hash-mismatch", expected: f.HashSha256, actual: actual));
            }
            catch (Exception ex)
            {
                mismatches.Add(new VerifyMismatch(f.RelPath, "error: " + ex.Message, expected: f.HashSha256, actual: null));
            }

            checkedCount++;
        }

        return new VerifyResult(sample.Count, checkedCount - mismatches.Count, mismatches);
    }
}

public record VerifyMismatch(string RelPath, string Reason, string? expected, string? actual);
public record VerifyResult(int Checked, int Passed, List<VerifyMismatch> Failures);
