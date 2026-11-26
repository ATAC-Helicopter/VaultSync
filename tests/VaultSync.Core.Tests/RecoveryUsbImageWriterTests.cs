using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RecoveryUsbImageWriterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WritesEveryChunkAndRequiresTheExpectedReadbackDigest(bool incorrectDigest)
    {
        var directory = Directory.CreateTempSubdirectory("recovery-usb-writer-test-");
        try
        {
            byte[] image = new byte[2 * 1024 * 1024 + 512];
            new Random(494).NextBytes(image);
            byte[] digest = SHA256.HashData(image);
            if (incorrectDigest) digest[0] ^= 1;
            string sourcePath = Path.Combine(directory.FullName, "source");
            string targetPath = Path.Combine(directory.FullName, "target");
            await File.WriteAllBytesAsync(sourcePath, image);

            using var source = File.OpenRead(sourcePath);
            source.Position = source.Length;
            using (var target = File.OpenHandle(targetPath, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                Task write = RecoveryUsbImageWriter.WriteAndVerifyAsync(source, target, digest,
                    new Progress<double>(), CancellationToken.None);

                if (incorrectDigest)
                {
                    var failure = await Assert.ThrowsAsync<IOException>(() => write);
                    Assert.Equal("recovery-usb.readback", failure.Message);
                }
                else
                {
                    await write;
                }
            }
            Assert.Equal(image, await File.ReadAllBytesAsync(targetPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
