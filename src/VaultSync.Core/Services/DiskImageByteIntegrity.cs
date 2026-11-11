using System.Security.Cryptography;

namespace VaultSync.Core.Services;

// Fixed bounded hashing only. The privileged consumer never parses image records.
internal static class DiskImageByteIntegrity
{
    internal static async ValueTask<string?> HashExactAsync(Stream source, ulong length, CancellationToken token)
    {
        if (!source.CanRead || !source.CanSeek || length > long.MaxValue || source.Length != (long)length)
            return null;
        source.Position = 0;
        byte[] buffer = new byte[128 * 1024];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ulong remaining = length;
        while (remaining != 0)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min((ulong)buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (read == 0) return null;
            hash.AppendData(buffer, 0, read);
            remaining -= (ulong)read;
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0 || source.Length != (long)length)
            return null;
        token.ThrowIfCancellationRequested();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
