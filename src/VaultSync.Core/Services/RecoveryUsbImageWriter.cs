using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VaultSync.Core.Services;

/// <summary>Copies a staged image to an already authorized disk handle and checks the readback digest.</summary>
public static class RecoveryUsbImageWriter
{
    public static async Task WriteAndVerifyAsync(FileStream source, SafeFileHandle target,
        byte[] digest, IProgress<double> progress, CancellationToken token)
    {
        source.Position = 0;
        var buffer = new byte[1024 * 1024];
        for (long written = 0; written < source.Length;)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, source.Length - written);
            await source.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            await RandomAccess.WriteAsync(target, buffer.AsMemory(0, count), written, token).ConfigureAwait(false);
            written += count;
            progress.Report(0.5 * written / source.Length);
        }
        RandomAccess.FlushToDisk(target);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long read = 0; read < source.Length;)
        {
            token.ThrowIfCancellationRequested();
            int count = (int)Math.Min(buffer.Length, source.Length - read);
            int received = await RandomAccess.ReadAsync(target, buffer.AsMemory(0, count), read, token)
                .ConfigureAwait(false);
            if (received == 0) throw new IOException("recovery-usb.short-read");
            hash.AppendData(buffer, 0, received);
            read += received;
            progress.Report(0.5 + 0.5 * read / source.Length);
        }
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), digest))
            throw new IOException("recovery-usb.readback");
    }
}
