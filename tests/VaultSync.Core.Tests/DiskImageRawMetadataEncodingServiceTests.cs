#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskImageRawMetadataEncodingServiceTests
{
    private static async Task<(DiskImageRawMetadataRequest Request, byte[] Expected)> Request(uint sector = 512)
    {
        byte[] expected = Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid(sector).MetadataHex);
        using var metadata = new MemoryStream(expected, false);
        using var payload = new MemoryStream(DiskImageRawProfileVerificationServiceTests.Raw(sector), false);
        var read = await DiskImageRawProfileVerificationService.VerifyAsync(metadata, payload);
        Assert.Equal(DiskImageRawProfileVerificationState.ContentAndLayoutVerified, read.State);
        const int part = 24 + 64 + 216 + 2 * 64;
        return (new(new Guid(expected.AsSpan(28, 16), bigEndian: true),
            new Guid(expected.AsSpan(44, 16), bigEndian: true), new Guid(expected.AsSpan(part, 16), bigEndian: true),
            BinaryPrimitives.ReadUInt64LittleEndian(expected.AsSpan(60)), read.Image!.DeclaredOrigin!,
            Convert.ToHexStringLower(expected.AsSpan(part + 24, 32))), expected);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public async Task EncodedBytesExactlyMatchIndependentPermanentWireFixture(uint sector)
    {
        var (request, expected) = await Request(sector);
        var result = DiskImageRawMetadataEncodingService.Encode(request);
        Assert.Equal(DiskImageRawMetadataEncodingState.MetadataEncoded, result.State);
        Assert.Equal(expected, result.Metadata);
        Assert.Equal(Convert.ToHexStringLower(expected.AsSpan(expected.Length - 32)), result.ManifestRoot);
        Assert.Equal(expected, DiskImageRawMetadataEncodingService.Encode(request).Metadata);
    }

    [Theory]
    [InlineData("image-id")]
    [InlineData("capture-id")]
    [InlineData("part-id")]
    [InlineData("generation")]
    [InlineData("source-id")]
    [InlineData("observation-id")]
    [InlineData("fixture-kind")]
    [InlineData("future-kind")]
    [InlineData("capacity")]
    [InlineData("geometry")]
    [InlineData("tool")]
    [InlineData("digest-short")]
    [InlineData("digest-case")]
    [InlineData("digest-invalid")]
    [InlineData("partition-id")]
    [InlineData("partition-entry")]
    [InlineData("partition-count")]
    [InlineData("partition-overlap")]
    [InlineData("timestamp")]
    [InlineData("missing-origin")]
    public async Task InvalidClaimsNeverProduceMetadata(string fault)
    {
        var (request, _) = await Request();
        var origin = request.Origin;
        var layout = origin.Layout;
        var partition = layout.Partitions[0];
        request = fault switch
        {
            "image-id" => request with { ImageId = Guid.Empty },
            "capture-id" => request with { CaptureId = Guid.Empty },
            "part-id" => request with { PartId = Guid.Empty },
            "generation" => request with { Generation = 0 },
            "source-id" => request with { Origin = origin with { SourceId = Guid.Empty } },
            "observation-id" => request with { Origin = origin with { ObservationId = Guid.Empty } },
            "fixture-kind" => request with { Origin = origin with { SourceKind = DiskImageSourceKind.RegularFileFixture } },
            "future-kind" => request with { Origin = origin with { SourceKind = (DiskImageSourceKind)3 } },
            "capacity" => request with { Origin = origin with { Layout = layout with { Capacity = DiskImageRawProfileVerificationService.MaximumCapacity + 512 } } },
            "geometry" => request with { Origin = origin with { PhysicalSectorBytes = 1024 } },
            "tool" => request with { Origin = origin with { ToolVersion = "private/path" } },
            "digest-short" => request with { PayloadSha256 = "00" },
            "digest-case" => request with { PayloadSha256 = new string('A', 64) },
            "digest-invalid" => request with { PayloadSha256 = new string('g', 64) },
            "partition-id" => request with { Origin = origin with { Layout = layout with { Partitions = new[] { partition with { PartitionId = Guid.Empty } } } } },
            "partition-entry" => request with { Origin = origin with { Layout = layout with { Partitions = new[] { partition with { EntryNumber = 129 } } } } },
            "partition-count" => request with { Origin = origin with { Layout = layout with { Partitions = Enumerable.Repeat(partition, 129).ToArray() } } },
            "partition-overlap" => request with { Origin = origin with { Layout = layout with { Partitions = new[] { partition, partition with { EntryNumber = 2, PartitionId = Guid.NewGuid() } } } } },
            "timestamp" => request with { Origin = origin with { CapturedAtUtc = DateTimeOffset.UnixEpoch.AddMilliseconds(-1) } },
            "missing-origin" => request with { Origin = null! },
            _ => throw new InvalidOperationException()
        };
        var result = DiskImageRawMetadataEncodingService.Encode(request);
        Assert.Equal(DiskImageRawMetadataEncodingState.Invalid, result.State);
        Assert.Null(result.Metadata);
        Assert.Null(result.ManifestRoot);
    }

    [Fact]
    public async Task CapacityCeilingIsInclusiveForSerializationClaimsOnly()
    {
        var (request, _) = await Request();
        request = request with { Origin = request.Origin with { Layout = request.Origin.Layout with
            { Capacity = DiskImageRawProfileVerificationService.MaximumCapacity } } };
        var result = DiskImageRawMetadataEncodingService.Encode(request);
        Assert.Equal(DiskImageRawMetadataEncodingState.MetadataEncoded, result.State);
        using var metadata = new MemoryStream(result.Metadata!, false);
        using var actualPayload = new MemoryStream(DiskImageRawProfileVerificationServiceTests.Raw(512), false);
        // Encoding a claim cannot turn a short payload into an 8 TiB image.
        Assert.Equal(DiskImageRawProfileVerificationState.Incomplete,
            (await DiskImageRawProfileVerificationService.VerifyAsync(metadata, actualPayload)).State);
    }
}
