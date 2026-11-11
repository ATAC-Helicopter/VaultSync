#nullable enable
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxInventoryWorkerPeerChannelTests
{
    [Fact]
    public void UnsupportedHostsNeverCreateAListener()
    {
        if (LinuxInventoryWorkerPeerChannel.Supported) return;
        Assert.Throws<PlatformNotSupportedException>(() => new LinuxInventoryWorkerPeerChannel.Listener());
    }

    [Theory]
    [InlineData("relative/peer")]
    [InlineData("/tmp/wrong-name")]
    public async Task InvalidEndpointRefusesBeforeConnecting(string endpoint) =>
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LinuxInventoryWorkerPeerChannel.ConnectParentAsync(endpoint, CancellationToken.None));

    [Fact]
    public async Task PrivateListenerCleanupRemovesSocketAndDirectory()
    {
        if (!LinuxInventoryWorkerPeerChannel.Supported) return;
        string endpoint;
        using (var listener = new LinuxInventoryWorkerPeerChannel.Listener())
        {
            endpoint = listener.Endpoint;
            Assert.True(File.Exists(endpoint));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                listener.AcceptAsync(Environment.ProcessId, canceled.Token));
        }
        Assert.False(File.Exists(endpoint));
        Assert.False(Directory.Exists(Path.GetDirectoryName(endpoint)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrongStartupPhaseAndDisconnectedPeerRefuse(bool disconnect)
    {
        if (!LinuxInventoryWorkerPeerChannel.Supported) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new LinuxInventoryWorkerPeerChannel.Listener();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(listener.Endpoint), timeout.Token);
        using var peer = await listener.AcceptAsync(Environment.ProcessId, timeout.Token);
        if (disconnect) socket.Dispose();
        else await socket.SendAsync(new byte[] { 0x32 }, SocketFlags.None, timeout.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => peer.ConfirmStartupAsync(timeout.Token));
    }

    [Fact]
    public async Task WrongExpectedProcessRefusesBeforeStartup()
    {
        if (!LinuxInventoryWorkerPeerChannel.Supported) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new LinuxInventoryWorkerPeerChannel.Listener();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(listener.Endpoint), timeout.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => listener.AcceptAsync(int.MaxValue, timeout.Token));
    }

    [Fact]
    public async Task ResponseAcknowledgementRequiresLivePeerAndCorrectPhase()
    {
        if (!LinuxInventoryWorkerPeerChannel.Supported) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new LinuxInventoryWorkerPeerChannel.Listener();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(listener.Endpoint), timeout.Token);
        using var peer = await listener.AcceptAsync(Environment.ProcessId, timeout.Token);
        await socket.SendAsync(new byte[] { 0x31, 0x32 }, SocketFlags.None, timeout.Token);
        await peer.ConfirmStartupAsync(timeout.Token);
        await peer.AcceptResponseAsync(timeout.Token);
        byte[] acknowledgement = new byte[1];
        Assert.Equal(1, await socket.ReceiveAsync(acknowledgement, SocketFlags.None, timeout.Token));
        Assert.Equal(0x33, acknowledgement[0]);
    }

    [Theory]
    [InlineData(0x33, true)]
    [InlineData(0x31, false)]
    public async Task WorkerCompletionWaitsForExactAcknowledgement(byte acknowledgement, bool accepted)
    {
        if (!LinuxInventoryWorkerPeerChannel.Supported) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new LinuxInventoryWorkerPeerChannel.Listener();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(listener.Endpoint), timeout.Token);
        using var peer = await listener.AcceptAsync(Environment.ProcessId, timeout.Token);
        Task completion = peer.CompleteWorkerAsync(timeout.Token);
        byte[] ready = new byte[1];
        Assert.Equal(1, await socket.ReceiveAsync(ready, SocketFlags.None, timeout.Token));
        Assert.Equal(0x32, ready[0]);
        Assert.False(completion.IsCompleted);
        await socket.SendAsync(new[] { acknowledgement }, SocketFlags.None, timeout.Token);
        if (accepted) await completion;
        else await Assert.ThrowsAsync<InvalidDataException>(() => completion);
    }
}
