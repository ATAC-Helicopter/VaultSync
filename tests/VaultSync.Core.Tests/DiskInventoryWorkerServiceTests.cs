#nullable enable
using System;
using System.Buffers.Binary;
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

public sealed class DiskInventoryWorkerServiceTests
{
    private static readonly Guid Operation = Guid.Parse("12345678-1234-5678-9abc-0123456789ab");
    private static LinuxBlockInventory Snapshot => new(LinuxBlockInventoryState.UnsupportedPlatform,
        LinuxBlockInventoryReason.LinuxRequired, Guid.NewGuid(), DateTimeOffset.UtcNow,
        Guid.Empty, null, false, Array.Empty<LinuxBlockObservation>());

    // Built independently of the service: literal magic, little-endian numeric fields,
    // UUID in RFC/network order. No product request encoder is used by these fixtures.
    private static byte[] Request() => Convert.FromHexString(
        "5653494e56524551" + "01000000" + "01000000" + "12345678123456789abc0123456789ab");

    public static IEnumerable<object[]> TruncatedLengths => Enumerable.Range(0, 32).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(TruncatedLengths))]
    public async Task EveryTruncatedRequestRefusesWithoutObservationOrOutput(int length)
    {
        using var input = new MemoryStream(Request()[..length]);
        using var output = new MemoryStream();
        Assert.False(await DiskInventoryWorkerService.RunAsync(input, output, _ => throw new Exception("Must not observe")));
        Assert.Empty(output.ToArray());
    }

    [Theory]
    [InlineData(0, 0)] // Magic
    [InlineData(8, 0)] // Version zero
    [InlineData(8, 2)] // Future version
    [InlineData(11, 1)] // Full 32-bit version is checked
    [InlineData(12, 0)] // Unknown operation
    [InlineData(12, 2)] // No capture/restore operation
    [InlineData(15, 1)]
    public async Task UnsupportedHeaderNeverInvokesProvider(int index, byte value)
    {
        byte[] request = Request();
        request[index] = value;
        await Refuses(request);
    }

    [Fact]
    public async Task EmptyOperationIdRefuses()
    {
        byte[] request = Request();
        request.AsSpan(16).Clear();
        await Refuses(request);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(100000)]
    public async Task TrailingDataAndSecondRequestRefuseBeforeObservation(int extra)
    {
        byte[] request = Request().Concat(new byte[extra]).ToArray();
        using var input = new CountingInput(request);
        using var output = new MemoryStream();
        Assert.False(await DiskInventoryWorkerService.RunAsync(input, output, _ => throw new Exception("Must not observe")));
        Assert.Equal(33, input.BytesRead); // Never consumes an unbounded trailing stream.
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task PartialReadsProduceOneCorrelatedBoundedResponseAndLeaveStreamsOpen()
    {
        using var input = new CountingInput(Request(), chunk: 1);
        using var output = new FlushTrackingOutput();
        LinuxBlockInventory snapshot = Snapshot;
        int calls = 0;
        Assert.True(await DiskInventoryWorkerService.RunAsync(input, output, token =>
        {
            token.ThrowIfCancellationRequested();
            calls++;
            return ValueTask.FromResult(snapshot);
        }));
        Assert.Equal(1, calls);
        byte[] frame = output.ToArray();
        Assert.Equal("VSINVRSP"u8.ToArray(), frame[..8]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)));
        Assert.Equal(Operation, new Guid(frame.AsSpan(16, 16), bigEndian: true));
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(32));
        Assert.InRange(length, 1u, (uint)DiskInventoryWorkerService.MaximumResponseBytes);
        Assert.Equal(frame.Length - 36, (int)length);
        var decoded = JsonSerializer.Deserialize<LinuxBlockInventory>(frame.AsSpan(36));
        Assert.NotNull(decoded);
        Assert.Equal(snapshot.ObservationId, decoded.ObservationId);
        Assert.Equal(snapshot.State, decoded.State);
        Assert.Empty(decoded.Devices);
        Assert.True(output.Flushed);
        Assert.True(input.CanRead);
        Assert.True(output.CanWrite);
    }

    [Fact]
    public async Task OversizedProviderResponseFailsBeforeWritingHeader()
    {
        LinuxBlockInventory snapshot = Snapshot with { MountNamespace = new string('a', 2 * 1024 * 1024) };
        using var input = new MemoryStream(Request());
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => DiskInventoryWorkerService.RunAsync(input, output,
            _ => ValueTask.FromResult(snapshot)));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task ProviderFailureNeverWritesSuccessFrame()
    {
        using var input = new MemoryStream(Request());
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<IOException>(() => DiskInventoryWorkerService.RunAsync(input, output,
            _ => throw new IOException("Private metadata path")));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task CancellationBeforeRequestDoesNotObserve()
    {
        using var input = new MemoryStream(Request());
        using var output = new MemoryStream();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskInventoryWorkerService.RunAsync(input, output,
            _ => throw new Exception("Must not observe"), cancel.Token));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task WaitingForEofCanBeCancelledWithoutObservation()
    {
        using var cancel = new CancellationTokenSource();
        using var input = new NeverClosingInput(Request(), cancel);
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskInventoryWorkerService.RunAsync(input, output,
            _ => throw new Exception("Must not observe"), cancel.Token));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task CancellationAfterObservationNeverPublishesHeader()
    {
        using var cancel = new CancellationTokenSource();
        using var input = new MemoryStream(Request());
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiskInventoryWorkerService.RunAsync(input, output, _ =>
        {
            cancel.Cancel();
            return ValueTask.FromResult(Snapshot);
        }, cancel.Token));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public async Task BrokenOutputIsNotSuccessful()
    {
        using var input = new MemoryStream(Request());
        using var output = new BrokenOutput();
        await Assert.ThrowsAsync<IOException>(() => DiskInventoryWorkerService.RunAsync(input, output,
            _ => ValueTask.FromResult(Snapshot)));
    }

    private static async Task Refuses(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        Assert.False(await DiskInventoryWorkerService.RunAsync(input, output, _ => throw new Exception("Must not observe")));
        Assert.Empty(output.ToArray());
    }

    private sealed class CountingInput(byte[] bytes, int chunk = int.MaxValue) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = base.Read(buffer.Span[..Math.Min(buffer.Length, chunk)]);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class NeverClosingInput(byte[] bytes, CancellationTokenSource cancel) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length)
                return await base.ReadAsync(buffer, cancellationToken);
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class FlushTrackingOutput : MemoryStream
    {
        public bool Flushed { get; private set; }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushed = true;
            return base.FlushAsync(cancellationToken);
        }
    }

    private sealed class BrokenOutput : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Pipe closed"));
    }
}
