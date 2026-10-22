#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskImageByteIntegrityTests
{
    private sealed class LengthView(byte[] bytes, long declared) : MemoryStream(bytes, false)
    {
        public override long Length => declared;
        internal int MaximumRead, TotalRead;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumRead = Math.Max(MaximumRead, buffer.Length);
            var result = base.ReadAsync(buffer, cancellationToken);
            if (!result.IsCompletedSuccessfully) return result;
            TotalRead += result.Result;
            return result;
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(4097)] [InlineData(300000)]
    public async Task HashesExactArbitraryBytesWithoutParsingAndKeepsCallerOwnership(int length)
    {
        byte[] bytes = new byte[length]; Random.Shared.NextBytes(bytes);
        using var source = new LengthView(bytes, length);
        string expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(expected, await DiskImageByteIntegrity.HashExactAsync(source, (ulong)length, default));
        Assert.InRange(source.MaximumRead, 1, 128 * 1024);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(3, 4)] [InlineData(5, 4)]
    public async Task ShortOrTrailingContentRefusesDespiteMatchingDeclaredLength(int actual, int declared)
    {
        using var source = new LengthView(new byte[actual], declared);
        Assert.Null(await DiskImageByteIntegrity.HashExactAsync(source, (ulong)declared, default));
        Assert.InRange(source.TotalRead, 0, declared + 1);
    }

    [Fact]
    public async Task MismatchedLengthRefusesBeforeReading()
    {
        using var source = new LengthView(new byte[4], 4);
        Assert.Null(await DiskImageByteIntegrity.HashExactAsync(source, 5, default));
        Assert.Equal(0, source.TotalRead);
        Assert.Null(await DiskImageByteIntegrity.HashExactAsync(source, ulong.MaxValue, default));
    }

    [Fact]
    public async Task CallerCancellationCannotReturnAHash()
    {
        using var source = new MemoryStream(new byte[8]);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DiskImageByteIntegrity.HashExactAsync(source, 8, cancel.Token));
        Assert.True(source.CanRead);
    }
}
