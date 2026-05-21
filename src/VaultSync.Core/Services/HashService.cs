using System.Security.Cryptography;

namespace VaultSync.Core.Services;

public class HashService
{
    public static string FormatHex(byte[] bytes) => Convert.ToHexString(bytes);

    public static string FormatHexLower(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static string FormatSha256(byte[] hash) => FormatHex(hash);

    public static string FormatSha256Lower(byte[] hash) => FormatHexLower(hash);

    public async Task<string> Sha256Async(string file, CancellationToken ct = default)
    {
        const int Buf = 1024 * 1024;
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, Buf, useAsync: true);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(fs, ct);
        return FormatSha256(hash);
    }
}
