#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskGptInspectionProtocolTests
{
    private static JsonElement Fixture() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "disk-gpt-inspection-v1.json"))).RootElement;
    private static Guid Operation => Guid.Parse(Fixture().GetProperty("operation").GetString()!);
    private static byte[] Bytes(string name) => Convert.FromHexString(Fixture().GetProperty(name).GetString()!);
    private static DiskImageGptInspectionAssessment Assessment()
    {
        var fixture = Fixture();
        return new(DiskImageGptInspectionState.LayoutInspected, DiskImageGptInspectionReason.LayoutOnly,
            new(Guid.Parse(fixture.GetProperty("disk").GetString()!), 65536, 512, 34, 94,
                Array.AsReadOnly(new[] { new DiskImageGptPartition(1, Guid.Parse(fixture.GetProperty("type").GetString()!),
                    Guid.Parse(fixture.GetProperty("partition").GetString()!), 20480, 8192, 0) }), new string('1', 64)));
    }

    [Fact]
    public void FixedFramesMatchIndependentGoldenBytes()
    {
        Assert.Equal(Bytes("requestHex"), DiskGptInspectionProtocol.Request(Operation, 65536, 512));
        Assert.Equal((Operation, 65536L, 512u), DiskGptInspectionProtocol.DecodeRequest(Bytes("requestHex")));
        Assert.Equal(Bytes("resultHex"), DiskGptInspectionProtocol.Result(Operation, Assessment()));
        var decoded = DiskGptInspectionProtocol.DecodeResult(Bytes("resultHex"), Operation, 65536, 512);
        Assert.Equal(Assessment().Layout! with { Partitions = decoded.Layout!.Partitions }, decoded.Layout);
        Assert.Equal(Assessment().Layout!.Partitions.Single(), decoded.Layout.Partitions.Single());
    }

    [Theory]
    [InlineData(0)] [InlineData(8)] [InlineData(12)] [InlineData(36)] [InlineData(40)]
    public void UnknownRequestFieldsAndInvalidGeometryRefuse(int offset)
    {
        byte[] bytes = Bytes("requestHex"); bytes[offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => DiskGptInspectionProtocol.DecodeRequest(bytes));
    }

    [Theory]
    [InlineData(0)] [InlineData(7)] [InlineData(23)] [InlineData(31)] [InlineData(59)]
    [InlineData(63)] [InlineData(127)] [InlineData(128)] [InlineData(191)] [InlineData(193)]
    public void TruncatedAndTrailingResultsRefuse(int length)
    {
        byte[] bytes = Bytes("resultHex"); Array.Resize(ref bytes, length);
        Assert.Throws<InvalidDataException>(() => DiskGptInspectionProtocol.DecodeResult(bytes, Operation, 65536, 512));
    }

    [Theory]
    [InlineData(0)] [InlineData(32)] [InlineData(36)] [InlineData(40)] [InlineData(44)]
    [InlineData(48)] [InlineData(56)] [InlineData(128)] [InlineData(164)] [InlineData(172)]
    [InlineData(181)] [InlineData(188)]
    public void NoncanonicalAndInvalidPartitionResultsRefuse(int offset)
    {
        byte[] bytes = Bytes("resultHex"); bytes[offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => DiskGptInspectionProtocol.DecodeResult(bytes, Operation, 65536, 512));
    }

    [Fact]
    public void ForeignCorrelationAndGeometryCannotBecomeObservations()
    {
        Assert.Throws<InvalidDataException>(() => DiskGptInspectionProtocol.DecodeResult(Bytes("resultHex"), Guid.NewGuid(), 65536, 512));
        Assert.Throws<InvalidDataException>(() => DiskGptInspectionProtocol.DecodeResult(Bytes("resultHex"), Operation, 131072, 512));
        Assert.Throws<InvalidDataException>(() => DiskGptInspectionProtocol.DecodeResult(Bytes("resultHex"), Operation, 65536, 4096));
    }

    [Theory]
    [InlineData(129u)] [InlineData(uint.MaxValue)]
    public async Task PartitionCountRefusesBeforeReadingOrAllocatingAnUnboundedBody(uint count)
    {
        byte[] header = Bytes("resultHex")[..128]; BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), count);
        using var input = new MemoryStream(header[8..]);
        using var empty = new MemoryStream(Array.Empty<byte>(), false); using var source = new MemoryStream(new byte[65536], false);
        await Assert.ThrowsAsync<InvalidDataException>(() => DiskGptInspectionWorkerClient.ReadResultAsync(input, header[..8],
            new(empty, source, Operation, 0, source.Length), 512, default));
        Assert.Equal(120, input.Position);
    }

    [Fact]
    public void RefusalsHaveNoRetainedLayoutFacts()
    {
        var refusal = new DiskImageGptInspectionAssessment(DiskImageGptInspectionState.Invalid, DiskImageGptInspectionReason.HeaderChecksumMismatch);
        byte[] frame = DiskGptInspectionProtocol.Result(Operation, refusal);
        Assert.Equal(128, frame.Length);
        Assert.Equal(refusal, DiskGptInspectionProtocol.DecodeResult(frame, Operation, 65536, 512));
        frame[64] = 1;
        Assert.Throws<InvalidDataException>(() => DiskGptInspectionProtocol.DecodeResult(frame, Operation, 65536, 512));
    }

    [Fact]
    public async Task GptOracleCannotReadMetadataOrAcceptAnImageResult()
    {
        using var empty = new MemoryStream(Array.Empty<byte>(), false); using var source = new MemoryStream(new byte[65536], false);
        foreach (byte[] bytes in new[] { DiskImageInspectionProtocol.Read(Operation, 0, 0, 1), "VSIMRSL1"u8.ToArray() })
        {
            using var input = new MemoryStream(bytes); using var output = new MemoryStream();
            await Assert.ThrowsAsync<InvalidDataException>(() => DiskImageInspectionWorkerClient.ServeAsync(input, output,
                new(empty, source, Operation, 0, source.Length), () => { }, "VSGPRSL1"u8.ToArray(),
                (pipe, prefix, sources, token) => DiskGptInspectionWorkerClient.ReadResultAsync(pipe, prefix, sources, 512, token), default));
            Assert.Equal(0, output.Length);
        }
    }
}
