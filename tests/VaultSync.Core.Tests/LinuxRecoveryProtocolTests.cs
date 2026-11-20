#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryProtocolTests
{
    private static readonly Guid Operation = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");

    [Fact]
    public void CancelMatchesIndependentWireVector()
    {
        byte[] wire = Convert.FromHexString("5653525043303031010000000500000000112233445566778899aabbccddeeff0100000000000000");
        Assert.Equal(wire, LinuxRecoveryProtocol.EncodeHeader(LinuxRecoveryRole.Controller, LinuxRecoveryMessage.Cancel, Operation, 1, 0));
        var decoded = LinuxRecoveryProtocol.DecodeHeader(wire, LinuxRecoveryRole.Controller, Operation, 1);
        Assert.Equal(LinuxRecoveryMessage.Cancel, decoded.Kind); Assert.Equal(0, decoded.BodyBytes);
    }

    [Theory]
    [InlineData(0)] [InlineData(8)] [InlineData(12)] [InlineData(16)] [InlineData(32)] [InlineData(36)]
    public void MutatedHeadersRefuseBeforeBodyRead(int at)
    {
        byte[] wire = LinuxRecoveryProtocol.EncodeHeader(LinuxRecoveryRole.Controller, LinuxRecoveryMessage.Cancel, Operation, 1, 0);
        wire[at] ^= 0x80;
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.DecodeHeader(wire, LinuxRecoveryRole.Controller, Operation, 1));
    }

    [Fact]
    public void OperationSequenceDirectionAndUnknownRolesAreStrict()
    {
        byte[] wire = LinuxRecoveryProtocol.EncodeHeader(LinuxRecoveryRole.Controller, LinuxRecoveryMessage.Cancel, Operation, 1, 0);
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.DecodeHeader(wire, LinuxRecoveryRole.Controller, Guid.NewGuid(), 1));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.DecodeHeader(wire, LinuxRecoveryRole.Controller, Operation, 2));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.DecodeHeader(wire, LinuxRecoveryRole.Worker, Operation, 1));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.DecodeHeader(wire[..39], LinuxRecoveryRole.Controller, Operation, 1));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.EncodeHeader((LinuxRecoveryRole)99, LinuxRecoveryMessage.Cancel, Operation, 1, 0));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.EncodeHeader(LinuxRecoveryRole.Controller, LinuxRecoveryMessage.Cancel, Guid.Empty, 1, 0));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.EncodeHeader(LinuxRecoveryRole.Controller, LinuxRecoveryMessage.Cancel, Operation, 0, 0));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.EncodeHeader(LinuxRecoveryRole.Controller, (LinuxRecoveryMessage)1000, Operation, 1, 0));
    }

    [Theory]
    [InlineData(0, 1, 0)] [InlineData(0, 2, 0)] [InlineData(0, 3, 80)] [InlineData(0, 4, 16)] [InlineData(0, 5, 0)] [InlineData(0, 6, 0)]
    [InlineData(1, 257, 0)] [InlineData(1, 258, 0)] [InlineData(1, 259, 65536)] [InlineData(1, 260, 4096)]
    [InlineData(1, 261, 0)] [InlineData(1, 262, 24)] [InlineData(1, 263, 64)] [InlineData(1, 264, 4)]
    public void DeclaredSizesHaveExplicitBounds(int role, int kind, int size)
    {
        var sender = (LinuxRecoveryRole)role; var message = (LinuxRecoveryMessage)kind;
        var wire = LinuxRecoveryProtocol.EncodeHeader(sender, message, Operation, uint.MaxValue, size);
        Assert.Equal(size, LinuxRecoveryProtocol.DecodeHeader(wire, sender, Operation, uint.MaxValue).BodyBytes);
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.EncodeHeader(sender, message, Operation, 1, size + 1));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.EncodeHeader(sender, message, Operation, 1, -1));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.EncodeHeader(sender, message, Operation, 1, int.MaxValue));
    }

    [Theory]
    [InlineData("")] [InlineData("../sda")] [InlineData("/dev/sda")] [InlineData("sda\0x")]
    [InlineData("sda ")] [InlineData("sdá")] [InlineData("sda:sdb")]
    public void SelectorsCannotCarryPathsOrNormalizationAliases(string name) =>
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.Selection(Operation, name));

    [Fact]
    public void SelectionAndReviewKeepExactNetworkOrderAndZeroPadding()
    {
        byte[] selection = LinuxRecoveryProtocol.Selection(Operation, "nvme0n1");
        Assert.Equal("00112233445566778899AABBCCDDEEFF", Convert.ToHexString(selection[..16]));
        Assert.Equal((Operation, "nvme0n1"), LinuxRecoveryProtocol.DecodeSelection(selection));
        selection[^1] = 1; Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.DecodeSelection(selection));
        selection = LinuxRecoveryProtocol.Selection(Operation, "sda"); selection[16] = 128;
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.DecodeSelection(selection));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.Selection(Guid.Empty, "sda"));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.Selection(Operation, new string('x', 64)));
        Assert.Equal(Operation, LinuxRecoveryProtocol.DecodeReviewAcknowledgment(LinuxRecoveryProtocol.ReviewAcknowledgment(Operation)));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.ReviewAcknowledgment(Guid.Empty));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.DecodeReviewAcknowledgment(new byte[16]));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryProtocol.DecodeReviewAcknowledgment(new byte[17]));
    }

    [Fact]
    public async Task NativeDuplexSequencingAndConcurrentWriters()
    {
        if (!NativeSupported) return;
        using var pair = await LinuxRecoveryImageTransferTests.Pair.CreateAsync();
        if (pair.ServerPeer is null || pair.ClientPeer is null) return;
        using var worker = new LinuxRecoveryOperationChannel(pair.Server, pair.ServerPeer, Operation, LinuxRecoveryRole.Worker);
        using var controller = new LinuxRecoveryOperationChannel(pair.Client, pair.ClientPeer, Operation, LinuxRecoveryRole.Controller);
        await worker.SendAsync(LinuxRecoveryMessage.Ready, ReadOnlyMemory<byte>.Empty);
        using var ready = await controller.ReceiveAsync(); Assert.Equal(LinuxRecoveryMessage.Ready, ready.Kind);
        await controller.SendAsync(LinuxRecoveryMessage.Prepare, LinuxRecoveryProtocol.Selection(Operation, "vda"));
        using var prepare = await worker.ReceiveAsync(); Assert.Equal((Operation, "vda"), LinuxRecoveryProtocol.DecodeSelection(prepare.Body.Span));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => worker.SendAsync(LinuxRecoveryMessage.Refused, BitConverter.GetBytes(i))));
        var seen = new System.Collections.Generic.HashSet<int>();
        for (int i = 0; i < 12; i++) { using var message = await controller.ReceiveAsync(); Assert.Equal(LinuxRecoveryMessage.Refused, message.Kind); seen.Add(BinaryPrimitives.ReadInt32LittleEndian(message.Body.Span)); }
        Assert.Equal(12, seen.Count);
    }

    [Fact]
    public async Task NativeOfferKeepsAncillaryHandoffInsideTheFrame()
    {
        if (!NativeSupported) return;
        using var pair = await LinuxRecoveryImageTransferTests.Pair.CreateAsync();
        if (pair.ServerPeer is null || pair.ClientPeer is null) return;
        using var worker = new LinuxRecoveryOperationChannel(pair.Server, pair.ServerPeer, Operation, LinuxRecoveryRole.Worker);
        using var controller = new LinuxRecoveryOperationChannel(pair.Client, pair.ClientPeer, Operation, LinuxRecoveryRole.Controller);
        string first = Path.Combine(pair.Directory, "metadata"), second = Path.Combine(pair.Directory, "payload");
        await File.WriteAllBytesAsync(first, [1]); await File.WriteAllBytesAsync(second, [2]);
        using var metadata = File.OpenRead(first); using var payload = File.OpenRead(second);
        await Task.WhenAll(controller.OfferImagesAsync(metadata, payload), controller.SendAsync(LinuxRecoveryMessage.Heartbeat, ReadOnlyMemory<byte>.Empty));
        using var offer = await worker.ReceiveAsync(); Assert.Equal(LinuxRecoveryMessage.OfferImages, offer.Kind);
        Assert.NotNull(offer.Sources); Assert.Equal(1, offer.Sources.Metadata.Length); Assert.Equal(1, offer.Sources.Payload.Length);
        using var heartbeat = await worker.ReceiveAsync(); Assert.Equal(LinuxRecoveryMessage.Heartbeat, heartbeat.Kind);
        Assert.Null(heartbeat.Sources);
        await Assert.ThrowsAsync<IOException>(() => controller.SendAsync(LinuxRecoveryMessage.OfferImages, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public async Task NativeReplayPoisonsChannelInsteadOfResynchronizing()
    {
        if (!NativeSupported) return;
        using var pair = await LinuxRecoveryImageTransferTests.Pair.CreateAsync();
        if (pair.ServerPeer is null) return;
        using var worker = new LinuxRecoveryOperationChannel(pair.Server, pair.ServerPeer, Operation, LinuxRecoveryRole.Worker);
        using var raw = new NetworkStream(pair.Client, false);
        var header = LinuxRecoveryProtocol.EncodeHeader(LinuxRecoveryRole.Controller, LinuxRecoveryMessage.Cancel, Operation, 1, 0);
        await raw.WriteAsync(header); using var initial = await worker.ReceiveAsync();
        await raw.WriteAsync(header); await Assert.ThrowsAsync<InvalidDataException>(() => worker.ReceiveAsync());
        await Assert.ThrowsAsync<IOException>(() => worker.ReceiveAsync());
    }

    [Fact]
    public async Task NativeCancelledWaiterDoesNotCloseActiveReader()
    {
        if (!NativeSupported) return;
        using var pair = await LinuxRecoveryImageTransferTests.Pair.CreateAsync();
        if (pair.ServerPeer is null) return;
        using var worker = new LinuxRecoveryOperationChannel(pair.Server, pair.ServerPeer, Operation, LinuxRecoveryRole.Worker);
        var active = worker.ReceiveAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ReceiveAsync(new CancellationToken(true)));
        using var raw = new NetworkStream(pair.Client, false);
        await raw.WriteAsync(LinuxRecoveryProtocol.EncodeHeader(LinuxRecoveryRole.Controller, LinuxRecoveryMessage.Heartbeat, Operation, 1, 0));
        using var result = await active; Assert.Equal(LinuxRecoveryMessage.Heartbeat, result.Kind);
        using var cancellation = new CancellationTokenSource(); var partial = worker.ReceiveAsync(cancellation.Token);
        await raw.WriteAsync(new byte[] {1, 2, 3}); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => partial);
        await Assert.ThrowsAsync<IOException>(() => worker.ReceiveAsync());
    }
    private static bool NativeSupported => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
}
