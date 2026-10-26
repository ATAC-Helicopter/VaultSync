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

public sealed class LinuxRecoveryImageTransferTests
{
    private static byte[] Control(params int[] descriptors)
    {
        var bytes = new byte[(16 + descriptors.Length * 4 + 7) & ~7];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, (ulong)(16 + descriptors.Length * 4));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 1);
        for (int i = 0; i < descriptors.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16 + i * 4), descriptors[i]);
        return bytes;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x40000000)]
    public void DecoderAcceptsOnlyAnExactPair(int flags)
    {
        var result = LinuxRecoveryImageTransfer.DecodeControl(Control(20, 21), flags);
        Assert.True(result.Exact); Assert.Equal(new[] {20, 21}, result.Descriptors);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(0x20)]
    [InlineData(1)]
    [InlineData(-1)]
    public void RejectedFlagsStillReturnDescriptorsForCleanup(int flags)
    {
        var result = LinuxRecoveryImageTransfer.DecodeControl(Control(20, 21), flags);
        Assert.False(result.Exact); Assert.Equal(new[] {20, 21}, result.Descriptors);
    }

    [Fact]
    public void ExtraDescriptorsRecordsAndNegativeSlotsRefuse()
    {
        Assert.False(LinuxRecoveryImageTransfer.DecodeControl(Control(20), 0).Exact);
        var surplus = LinuxRecoveryImageTransfer.DecodeControl(Control(20, 21, 22), 0);
        Assert.False(surplus.Exact); Assert.Equal(new[] {20, 21, 22}, surplus.Descriptors);
        Assert.False(LinuxRecoveryImageTransfer.DecodeControl(Control(20, 20), 0).Exact);
        Assert.False(LinuxRecoveryImageTransfer.DecodeControl(Control(-1, 20), 0).Exact);
        var multiple = LinuxRecoveryImageTransfer.DecodeControl(Control(20, 21).Concat(Control(22, 23)).ToArray(), 0);
        Assert.False(multiple.Exact); Assert.Equal(new[] {20, 21, 22, 23}, multiple.Descriptors);
    }

    [Fact]
    public void TruncatedControlStillPreservesDeliveredSlots()
    {
        var bytes = Control(20, 21, 22);
        var truncated = LinuxRecoveryImageTransfer.DecodeControl(bytes.AsSpan(0, 24), 8);
        Assert.False(truncated.Exact); Assert.Equal(new[] {20, 21}, truncated.Descriptors);
        bytes = Control(20, 21); BinaryPrimitives.WriteUInt64LittleEndian(bytes, ulong.MaxValue);
        var overflow = LinuxRecoveryImageTransfer.DecodeControl(bytes, 0);
        Assert.False(overflow.Exact); Assert.Equal(new[] {20, 21}, overflow.Descriptors);
    }

    [Fact]
    public void EmptyShortOverlongAndForeignRecordsRefuse()
    {
        Assert.False(LinuxRecoveryImageTransfer.DecodeControl([], 0).Exact);
        Assert.False(LinuxRecoveryImageTransfer.DecodeControl(new byte[15], 0).Exact);
        Assert.False(LinuxRecoveryImageTransfer.DecodeControl(new byte[257], 0).Exact);
        var foreign = Control(20, 21); BinaryPrimitives.WriteInt32LittleEndian(foreign.AsSpan(12), 2);
        Assert.False(LinuxRecoveryImageTransfer.DecodeControl(foreign, 0).Exact);
        var shortRecord = Control(20, 21); BinaryPrimitives.WriteUInt64LittleEndian(shortRecord, 1);
        Assert.False(LinuxRecoveryImageTransfer.DecodeControl(shortRecord, 0).Exact);
    }

    [Fact]
    public async Task NativeRoundTripAndSourceLifetime()
    {
        if (!NativeSupported) return;
        using var pair = await Pair.CreateAsync();
        if (pair.ServerPeer is null || pair.ClientPeer is null) return; // Older kernel: no PID fallback.
        var firstPath = Path.Combine(pair.Directory, "image.metadata");
        var secondPath = Path.Combine(pair.Directory, "payload.raw");
        await File.WriteAllBytesAsync(firstPath, [1, 2, 3]);
        await File.WriteAllBytesAsync(secondPath, [4, 5, 6, 7]);
        using var metadata = File.OpenRead(firstPath); using var payload = File.OpenRead(secondPath);
        var receive = LinuxRecoveryImageTransfer.ReceiveAsync(pair.Server, pair.ServerPeer);
        await LinuxRecoveryImageTransfer.SendAsync(pair.Client, pair.ClientPeer, metadata, payload);
        using var sources = await receive;
        Assert.False(sources.Metadata.CanWrite); Assert.False(sources.Payload.CanWrite);
        metadata.Dispose(); payload.Dispose(); // Receiver owns independent descriptor references.
        byte[] first = new byte[3], second = new byte[4];
        await sources.Metadata.ReadExactlyAsync(first); await sources.Payload.ReadExactlyAsync(second);
        Assert.Equal(new byte[] {1, 2, 3}, first); Assert.Equal(new byte[] {4, 5, 6, 7}, second);
        Assert.NotNull(LinuxRawImageStorage.TryFilesystemDeviceNumber(sources.Metadata));
        Assert.NotNull(LinuxRawImageStorage.TryFilesystemDeviceNumber(sources.Payload));
    }

    [Fact]
    public async Task NativeWritableSourcesAndAliasesRefuseBeforeSending()
    {
        if (!NativeSupported) return;
        using var pair = await Pair.CreateAsync();
        if (pair.ClientPeer is null) return;
        string path = Path.Combine(pair.Directory, "source"); await File.WriteAllBytesAsync(path, [1, 2]);
        using var writable = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        using var read = File.OpenRead(path); using var alias = File.OpenRead(path);
        await Assert.ThrowsAsync<IOException>(() => LinuxRecoveryImageTransfer.SendAsync(pair.Client, pair.ClientPeer, writable, read));
        await Assert.ThrowsAsync<IOException>(() => LinuxRecoveryImageTransfer.SendAsync(pair.Client, pair.ClientPeer, read, alias));
        Assert.Equal(0, pair.Server.Available);
    }

    [Fact]
    public async Task NativeWrongConnectionAndCancellationRefuse()
    {
        if (!NativeSupported) return;
        using var pair = await Pair.CreateAsync(); using var other = await Pair.CreateAsync();
        if (pair.ServerPeer is null || other.ServerPeer is null) return;
        await Assert.ThrowsAsync<IOException>(() => LinuxRecoveryImageTransfer.ReceiveAsync(pair.Server, other.ServerPeer));
        using var cancellation = new CancellationTokenSource();
        var receive = LinuxRecoveryImageTransfer.ReceiveAsync(pair.Server, pair.ServerPeer, cancellation.Token);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
        Assert.False(other.ServerPeer.OwnsConnection(pair.Server));
    }

    private static bool NativeSupported => OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    internal sealed class Pair : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "vaultsync-transfer-" + Guid.NewGuid().ToString("N"));
        internal Socket Client { get; } = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        internal Socket Server { get; private set; } = null!;
        internal LinuxWorkerPeerBinding? ClientPeer { get; private set; }
        internal LinuxWorkerPeerBinding? ServerPeer { get; private set; }
        internal static async Task<Pair> CreateAsync()
        {
            var pair = new Pair();
            try
            {
                System.IO.Directory.CreateDirectory(pair.Directory);
                using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var endpoint = new UnixDomainSocketEndPoint(Path.Combine(pair.Directory, "peer.sock"));
                listener.Bind(endpoint); listener.Listen(1);
                var accepting = listener.AcceptAsync(); await pair.Client.ConnectAsync(endpoint); pair.Server = await accepting;
                pair.ClientPeer = Bind(pair.Client); pair.ServerPeer = Bind(pair.Server);
                return pair;
            }
            catch { pair.Dispose(); throw; }
        }
        private static LinuxWorkerPeerBinding? Bind(Socket socket)
        {
            byte[] credentials = new byte[12]; Assert.Equal(12, socket.GetRawSocketOption(1, 17, credentials));
            return LinuxWorkerPeerBinding.TryCreate(socket, new(Environment.ProcessId,
                BinaryPrimitives.ReadUInt32LittleEndian(credentials.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(credentials.AsSpan(8))));
        }
        public void Dispose()
        {
            ClientPeer?.Dispose(); ServerPeer?.Dispose(); Client.Dispose(); Server?.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }
}
