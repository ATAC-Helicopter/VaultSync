#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxWorkerPeerBindingTests
{
    private static LinuxWorkerPeerCredentials Expected => new(1234, 1000, 1000);
    private sealed class Lifetime : LinuxWorkerPeerBinding.IProcessLifetime
    {
        internal bool Alive = true;
        internal int Disposals;
        public bool IsAlive => Alive && Disposals == 0;
        public void Dispose() => Disposals++;
    }
    private sealed class Access : LinuxWorkerPeerBinding.IAccess
    {
        internal LinuxWorkerPeerCredentials Current = Expected;
        internal readonly Lifetime Lifetime = new();
        internal bool Connected = true;
        internal bool Readable = true;
        internal bool ProcessAvailable = true;
        internal bool ThrowOnRead;
        internal Action? OnOpen;
        internal int Opens;
        internal int Reads;
        internal Action? OnRead;
        public bool ConnectionOpen => Connected;
        public bool TryCredentials(out LinuxWorkerPeerCredentials credentials)
        { Reads++; OnRead?.Invoke(); if (ThrowOnRead) throw new IOException(); credentials = Current; return Readable; }
        public LinuxWorkerPeerBinding.IProcessLifetime? OpenPeerProcess()
        { Opens++; OnOpen?.Invoke(); return ProcessAvailable ? Lifetime : null; }
    }

    [Fact]
    public void MatchingProcessRechecksAndDisposesItsLeaseExactlyOnce()
    {
        var access = new Access();
        var binding = Assert.IsType<LinuxWorkerPeerBinding>(LinuxWorkerPeerBinding.TryCreate(access, Expected));
        Assert.True(binding.Recheck());
        binding.Dispose(); binding.Dispose();
        Assert.False(binding.Recheck());
        Assert.Equal(1, access.Lifetime.Disposals);
        Assert.True(access.Connected); // Caller owns the socket, never silently disposed.
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("uid")]
    [InlineData("gid")]
    [InlineData("disconnected")]
    [InlineData("unreadable")]
    [InlineData("io")]
    public void WrongOrUnavailablePeerNeverAcquiresProcessLease(string fault)
    {
        var access = new Access();
        switch (fault)
        {
            case "pid": access.Current = Expected with { ProcessId = 1235 }; break;
            case "uid": access.Current = Expected with { UserId = 1001 }; break;
            case "gid": access.Current = Expected with { GroupId = 1001 }; break;
            case "disconnected": access.Connected = false; break;
            case "unreadable": access.Readable = false; break;
            case "io": access.ThrowOnRead = true; break;
        }
        Assert.Null(LinuxWorkerPeerBinding.TryCreate(access, Expected));
        Assert.Equal(0, access.Opens);
    }

    [Theory]
    [InlineData(0, 1000u, 1000u)]
    [InlineData(-1, 1000u, 1000u)]
    [InlineData(1234, uint.MaxValue, 1000u)]
    [InlineData(1234, 1000u, uint.MaxValue)]
    public void InvalidExpectedIdentityNeverTouchesPeer(int pid, uint uid, uint gid)
    {
        var access = new Access { ThrowOnRead = true };
        Assert.Null(LinuxWorkerPeerBinding.TryCreate(access, new(pid, uid, gid)));
        Assert.Equal(0, access.Opens);
    }

    [Theory]
    [InlineData("dead")]
    [InlineData("changed")]
    [InlineData("closed")]
    [InlineData("io")]
    public void FailureAfterAcquisitionAlwaysReleasesLease(string fault)
    {
        var access = new Access();
        access.OnOpen = () =>
        {
            if (fault == "dead") access.Lifetime.Alive = false;
            if (fault == "changed") access.Current = Expected with { ProcessId = 1235 };
            if (fault == "closed") access.Connected = false;
            if (fault == "io") access.ThrowOnRead = true;
        };
        Assert.Null(LinuxWorkerPeerBinding.TryCreate(access, Expected));
        Assert.Equal(1, access.Lifetime.Disposals);
    }

    [Fact]
    public void MissingKernelProcessLeaseHasNoNumericPidFallback()
    {
        var access = new Access { ProcessAvailable = false };
        Assert.Null(LinuxWorkerPeerBinding.TryCreate(access, Expected));
        Assert.Equal(1, access.Opens);
        Assert.Equal(0, access.Lifetime.Disposals);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LatePeerExitOrDisconnectCannotReturnUsableBinding(bool exit)
    {
        var access = new Access();
        access.OnRead = () =>
        {
            if (access.Reads != 2) return;
            if (exit) access.Lifetime.Alive = false;
            else access.Connected = false;
        };
        Assert.Null(LinuxWorkerPeerBinding.TryCreate(access, Expected));
        Assert.Equal(1, access.Lifetime.Disposals);
    }

    [Theory]
    [InlineData("dead")]
    [InlineData("changed")]
    [InlineData("closed")]
    [InlineData("io")]
    public void ChangedOrDeadPeerInvalidatesPermanentlyAndReleasesLease(string fault)
    {
        var access = new Access();
        using var binding = Assert.IsType<LinuxWorkerPeerBinding>(LinuxWorkerPeerBinding.TryCreate(access, Expected));
        if (fault == "dead") access.Lifetime.Alive = false;
        if (fault == "changed") access.Current = Expected with { ProcessId = 1235 };
        if (fault == "closed") access.Connected = false;
        if (fault == "io") access.ThrowOnRead = true;
        Assert.False(binding.Recheck());
        access.Current = Expected; access.Connected = true; access.ThrowOnRead = false; access.Lifetime.Alive = true;
        Assert.False(binding.Recheck());
        Assert.Equal(1, access.Lifetime.Disposals);
    }

    [Fact]
    public void ForeignSocketDomainNeverAcquiresBinding()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Assert.Null(LinuxWorkerPeerBinding.TryCreate(socket, Expected));
    }

    [Fact]
    public async Task NativeUnixStreamChecksSupportedKernelOrExplicitPlatformRefusal()
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Assert.Null(LinuxWorkerPeerBinding.TryCreate(listener, Expected));
            return;
        }
        string directory = Path.Combine("/tmp", "vaultsync-peer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var address = new UnixDomainSocketEndPoint(Path.Combine(directory, "peer.sock"));
            listener.Bind(address); listener.Listen(1);
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var accept = listener.AcceptAsync();
            await client.ConnectAsync(address);
            using var server = await accept;
            byte[] credentials = new byte[12];
            Assert.Equal(12, server.GetRawSocketOption(1, 17, credentials));
            var expected = new LinuxWorkerPeerCredentials(Environment.ProcessId,
                BinaryPrimitives.ReadUInt32LittleEndian(credentials.AsSpan(4)),
                BinaryPrimitives.ReadUInt32LittleEndian(credentials.AsSpan(8)));
            // Older kernels must refuse rather than fall back to a numeric PID.
            try
            {
                using var kernelBinding = LinuxWorkerPeerBinding.TryCreate(server, expected);
                if (kernelBinding is null)
                {
                    byte[] unavailable = new byte[4];
                    var error = Assert.Throws<SocketException>(() => server.GetRawSocketOption(1, 77, unavailable));
                    Assert.Equal(SocketError.ProtocolOption, error.SocketErrorCode);
                    return;
                }
                Assert.True(kernelBinding.Recheck());
                Assert.Equal(1, await client.SendAsync(new byte[] { 42 }));
                Assert.True(kernelBinding.Recheck());
                byte[] retained = new byte[1];
                Assert.Equal(1, await server.ReceiveAsync(retained)); Assert.Equal(42, retained[0]);
                client.Dispose();
                Assert.False(kernelBinding.Recheck());
                Assert.False(server.SafeHandle.IsClosed); // Binding owns its pidfd only.
            }
            finally { listener.Dispose(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
