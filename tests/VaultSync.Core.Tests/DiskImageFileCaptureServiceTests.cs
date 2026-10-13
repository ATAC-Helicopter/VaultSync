#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskImageFileCaptureServiceTests
{
    private static DiskImageFileCaptureRequest Request(ulong capacity = 512, uint sector = 512) => new(
        Guid.Parse("6b7e43cf-d362-4fd2-9305-bbb5d7a387f0"),
        Guid.Parse("1c8be507-13c7-4483-9345-c51047ebf019"),
        Guid.Parse("3a3b4083-876d-4fb2-b6c3-878f54c47560"), 7, capacity, sector);

    [Fact]
    public async Task CaptureMatchesIndependentlyEncodedFixtureExactly()
    {
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "disk-image-manifest-v1.json")));
        JsonElement fixture = fixtures.RootElement[0];
        byte[] input = Convert.FromHexString(fixture.GetProperty("payloadHex").GetString()!);
        using var source = new MemoryStream(input, writable: false);
        var result = await DiskImageFileCaptureService.CaptureAsync(Request(), source);
        try
        {
            Assert.Equal(DiskImageFileCaptureState.FileFixtureCaptured, result.State);
            Assert.Equal(fixture.GetProperty("root").GetString(), result.ManifestRoot);
            Assert.Equal(Convert.FromHexString(fixture.GetProperty("metadataHex").GetString()!),
                await File.ReadAllBytesAsync(Path.Combine(result.Workspace!, DiskImageFileCaptureService.MetadataFileName)));
            Assert.Equal(input, await File.ReadAllBytesAsync(Path.Combine(result.Workspace!, DiskImageFileCaptureService.PayloadFileName)));
            Assert.Equal(Convert.FromHexString(fixture.GetProperty("payloadHex").GetString()!), input);
            Assert.True(source.CanRead);
        }
        finally { RemoveWorkspace(result); }
    }

    [Theory]
    [InlineData(512, 512)]
    [InlineData(4096, 4096)]
    [InlineData(524800, 512)]
    public async Task FileCaptureRoundTripVerifiesWithBoundedReads(int capacity, uint sector)
    {
        byte[] input = Enumerable.Range(0, capacity).Select(index => (byte)(index % 251)).ToArray();
        using var source = new ProbeSource(input);
        var request = Request((ulong)capacity, sector);
        var result = await DiskImageFileCaptureService.CaptureAsync(request, source);
        try
        {
            Assert.Equal(DiskImageFileCaptureState.FileFixtureCaptured, result.State);
            Assert.Equal((ulong)capacity, result.WrittenBytes);
            Assert.InRange(source.MaximumRead, 1, 128 * 1024);
            using var metadata = File.OpenRead(Path.Combine(result.Workspace!, DiskImageFileCaptureService.MetadataFileName));
            using var payload = File.OpenRead(Path.Combine(result.Workspace!, DiskImageFileCaptureService.PayloadFileName));
            var verification = await DiskImageManifestVerificationService.VerifyAsync(metadata,
                new Dictionary<Guid, Stream> { [request.PartId] = payload });
            Assert.Equal(DiskImageManifestVerificationState.ContentVerified, verification.State);
            Assert.Equal(result.ManifestRoot, verification.ManifestRoot);
            Assert.Equal(result.WrittenBytes, verification.VerifiedBytes);
        }
        finally { RemoveWorkspace(result); }
    }

    [Theory]
    [InlineData(511, DiskImageFileCaptureState.Incomplete, "capture.truncated-source")]
    [InlineData(513, DiskImageFileCaptureState.Invalid, "capture.trailing-source-bytes")]
    public async Task WrongSourceLengthCannotPublishCompletion(int length, DiskImageFileCaptureState state, string reason)
    {
        using var source = new MemoryStream(new byte[length], writable: false);
        var result = await DiskImageFileCaptureService.CaptureAsync(Request(), source);
        try
        {
            Assert.Equal(state, result.State);
            Assert.Equal(reason, result.Reason);
            Assert.Null(result.ManifestRoot);
            await AssertIncomplete(result);
        }
        finally { RemoveWorkspace(result); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedOrFailedReadRetainsPartialCaptureWithoutCompletion(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new ProbeSource(new byte[256 * 1024])
        { FailAfter = 128 * 1024, CancelOnFailure = cancel ? cancellation : null };
        var result = await DiskImageFileCaptureService.CaptureAsync(Request(256 * 1024), source, cancellation.Token);
        try
        {
            Assert.Equal(cancel ? DiskImageFileCaptureState.Interrupted : DiskImageFileCaptureState.Failed, result.State);
            Assert.Equal(cancel ? "capture.interrupted" : "capture.io-failure", result.Reason);
            Assert.Equal(128UL * 1024, result.WrittenBytes);
            Assert.Null(result.ManifestRoot);
            await AssertIncomplete(result);
            Assert.Equal(128 * 1024, new FileInfo(Path.Combine(result.Workspace!, DiskImageFileCaptureService.PayloadFileName)).Length);
        }
        finally { RemoveWorkspace(result); }
    }

    [Theory]
    [InlineData("capacity")]
    [InlineData("sector")]
    [InlineData("generation")]
    [InlineData("identity")]
    [InlineData("oversized")]
    public async Task InvalidRequestsCannotCreateWorkspaceOrReadSource(string fault)
    {
        var request = Request();
        request = fault switch
        {
            "capacity" => request with { Capacity = 513 },
            "sector" => request with { LogicalSectorBytes = 1024 },
            "generation" => request with { Generation = 0 },
            "identity" => request with { ImageId = Guid.Empty },
            _ => request with { Capacity = ulong.MaxValue }
        };
        using var source = new MemoryStream(new byte[512], writable: false);
        var result = await DiskImageFileCaptureService.CaptureAsync(request, source);
        Assert.NotEqual(DiskImageFileCaptureState.FileFixtureCaptured, result.State);
        Assert.Null(result.Workspace);
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public async Task CancellationBeforeCaptureCreatesNoFiles()
    {
        using var source = new MemoryStream(new byte[512], writable: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await DiskImageFileCaptureService.CaptureAsync(Request(), source, cancellation.Token);
        Assert.Equal(DiskImageFileCaptureState.Interrupted, result.State);
        Assert.Null(result.Workspace);
        Assert.Equal(0, source.Position);
    }

    private static async Task AssertIncomplete(DiskImageFileCaptureAssessment result)
    {
        using var metadata = File.OpenRead(Path.Combine(result.Workspace!, DiskImageFileCaptureService.MetadataFileName));
        Assert.Equal(DiskImageEnvelopeService.HeaderLength, metadata.Length);
        var verification = await DiskImageManifestVerificationService.VerifyAsync(metadata, new Dictionary<Guid, Stream>());
        Assert.Equal(DiskImageManifestVerificationState.Incomplete, verification.State);
    }

    private static void RemoveWorkspace(DiskImageFileCaptureAssessment result)
    {
        if (result.Workspace is not null)
            Directory.Delete(result.Workspace, recursive: true);
    }

    private sealed class ProbeSource(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public int MaximumRead { get; private set; }
        public int FailAfter { get; init; } = int.MaxValue;
        public CancellationTokenSource? CancelOnFailure { get; init; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumRead = Math.Max(MaximumRead, buffer.Length);
            if (Position >= FailAfter)
            {
                if (CancelOnFailure is null)
                    throw new IOException("Synthetic source fault");
                CancelOnFailure.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 128 * 1024)], cancellationToken);
        }
    }
}
