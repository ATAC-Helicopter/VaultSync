using System.Security.Cryptography;

namespace VaultSync.Core.Services;

public class HashService
{
    public async Task<string> Sha256Async(string file, CancellationToken ct = default)
    {
        const int Buf = 1024 * 1024;
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, Buf, useAsync: true);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash);
    }
}