using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskImagePayloadVerificationServiceTests
{
    private static readonly Guid PartId = Guid.Parse("3a3b4083-876d-4fb2-b6c3-878f54c47560");
    private const string ExpectedSha256 = "110009dcee21620b166f3abfecb5eff7a873be729d1c2d53822e7acc5f34eb9b";

    [Fact]
    public async Task ExactRawPartPassesWithoutClaimingTheWholeImage()
    {
        byte[] bytes = TestBytes();
        using var source = new MemoryStream(bytes, writable: false);
        DiskImagePayloadVerificationAssessment result = await DiskImagePayloadVerificationService.VerifyAsync(
            Request(), new Dictionary<Guid, Stream> { [PartId] = source });

        Assert.Equal(DiskImagePayloadVerificationState.PayloadsVerified, result.State);
        Assert.Equal("image.payloads-only", result.Reason);
        Assert.Equal(512UL, result.VerifiedBytes);
    }

    [Theory]
    [InlineData("mutated", DiskImagePayloadVerificationState.Corrupt, "image.part-digest-mismatch")]
    [InlineData("truncated", DiskImagePayloadVerificationState.Incomplete, "image.truncated-part")]
    [InlineData("trailing", DiskImagePayloadVerificationState.Corrupt, "image.trailing-part-bytes")]
    public async Task DamagedOrUnexpectedPartBytesNeverPass(
        string defect, DiskImagePayloadVerificationState expectedState, string expectedReason)
    {
        byte[] bytes = TestBytes();
        bytes = defect switch
        {
            "mutated" => Mutate(bytes),
            "truncated" => bytes[..^1],
            _ => [.. bytes, 0x42]
        };
        using var source = new MemoryStream(bytes, writable: false);
        DiskImagePayloadVerificationAssessment result = await DiskImagePayloadVerificationService.VerifyAsync(
            Request(), new Dictionary<Guid, Stream> { [PartId] = source });

        Assert.Equal(expectedState, result.State);
        Assert.Equal(expectedReason, result.Reason);
    }

    [Fact]
    public async Task IncompleteCoverageIsRefusedBeforeReadingAnyPart()
    {
        using var source = new MemoryStream(TestBytes(), writable: false);
        DiskImagePayloadVerificationRequest request = Request();
        request = request with { Coverage = request.Coverage with { HasCompletion = false } };

        DiskImagePayloadVerificationAssessment result = await DiskImagePayloadVerificationService.VerifyAsync(
            request, new Dictionary<Guid, Stream> { [PartId] = source });

        Assert.Equal(DiskImagePayloadVerificationState.Incomplete, result.State);
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public async Task MissingPartAndInvalidDigestFailClosed()
    {
        DiskImagePayloadVerificationAssessment missing = await DiskImagePayloadVerificationService.VerifyAsync(
            Request(), new Dictionary<Guid, Stream>());
        Assert.Equal(DiskImagePayloadVerificationState.Incomplete, missing.State);

        using var source = new MemoryStream(TestBytes(), writable: false);
        DiskImagePayloadVerificationRequest request = Request() with
        { Digests = [new(PartId, ExpectedSha256.ToUpperInvariant())] };
        DiskImagePayloadVerificationAssessment invalid = await DiskImagePayloadVerificationService.VerifyAsync(
            request, new Dictionary<Guid, Stream> { [PartId] = source });
        Assert.Equal(DiskImagePayloadVerificationState.Invalid, invalid.State);
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public async Task CancellationStopsVerification()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var source = new MemoryStream(TestBytes(), writable: false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DiskImagePayloadVerificationService.VerifyAsync(
                Request(), new Dictionary<Guid, Stream> { [PartId] = source }, cancellation.Token));
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public async Task HugeDeclaredPartStopsAtEndOfStreamWithoutLargeAllocation()
    {
        using var source = new MemoryStream(TestBytes(), writable: false);
        DiskImagePayloadVerificationRequest request = Request();
        request = request with
        { Coverage = request.Coverage with { Parts = [new(PartId, ulong.MaxValue)] } };

        DiskImagePayloadVerificationAssessment result = await DiskImagePayloadVerificationService.VerifyAsync(
            request, new Dictionary<Guid, Stream> { [PartId] = source });

        Assert.Equal(DiskImagePayloadVerificationState.Incomplete, result.State);
        Assert.Equal(512UL, result.VerifiedBytes);
    }

    private static DiskImagePayloadVerificationRequest Request() => new(
        new DiskImageCoveragePlan(1024, 512, [new(PartId, 512)],
            [new(0, 512, DiskImageExtentKind.Stored, PartId),
             new(512, 512, DiskImageExtentKind.Zero)], true),
        [new(PartId, ExpectedSha256)]);

    private static byte[] TestBytes() => Enumerable.Range(0, 512).Select(index => (byte)index).ToArray();

    private static byte[] Mutate(byte[] bytes)
    {
        bytes[217] ^= 0x80;
        return bytes;
    }
}
