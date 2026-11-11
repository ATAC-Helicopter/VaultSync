#nullable enable
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryControllerLaunchTests
{
    [Fact]
    public async Task ResultScreenRetentionAwaitsOwnedExitWithoutRequiringAnExitCode()
    {
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = LinuxRecoveryControllerLaunch.WaitForOwnedExitAsync(exit.Task, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(wait.IsCompleted); Assert.False(exit.Task.IsCompleted);
        exit.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(exit.Task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PresentationDeadlineAndShutdownEndWaitWithoutCancellingOwnedExit(bool shutdown)
    {
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var wait = LinuxRecoveryControllerLaunch.WaitForOwnedExitAsync(exit.Task,
            shutdown ? TimeSpan.FromMinutes(10) : TimeSpan.FromMilliseconds(10), cancellation.Token);
        if (shutdown) await cancellation.CancelAsync();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(exit.Task.IsCompleted);
        exit.SetResult(); // Owner remains responsible for the original exit task.
    }

    [Theory]
    [InlineData("io")] [InlineData("timeout")] [InlineData("cancel")]
    public async Task UnexpectedOwnedExitTaskFailureCannotBecomeCleanPresentationCompletion(string kind)
    {
        Exception failure = kind switch
        {
            "timeout" => new TimeoutException("fixture-owned-exit-failed"),
            "cancel" => new OperationCanceledException("fixture-owned-exit-failed"),
            _ => new IOException("fixture-owned-exit-failed")
        };
        using var cancellation = new CancellationTokenSource(); await cancellation.CancelAsync();
        Assert.Same(failure, await Record.ExceptionAsync(() => LinuxRecoveryControllerLaunch.WaitForOwnedExitAsync(
            Task.FromException(failure), TimeSpan.FromSeconds(5), cancellation.Token)));
    }

    [Fact]
    public void ListenerClosePreservesOwnedPathForExplicitIdentityCheckedCleanup()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || !Socket.OSSupportsUnixDomainSockets) return;
        var directory = Directory.CreateTempSubdirectory("vsr-");
        string path = Path.Combine(directory.FullName, "peer");
        try
        {
            using (var listener = LinuxRecoveryControllerLaunch.BindOwnedListener(path))
            {
                listener.Listen(1);
                using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                client.Connect(new UnixDomainSocketEndPoint(path));
                using var accepted = listener.Accept();
                Assert.True(accepted.Connected);
            }
            Assert.True(File.Exists(path));
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); directory.Delete(); }
    }

    [Fact]
    public void ListenerDisposeCannotDeleteAnUnexpectedReplacement()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || !Socket.OSSupportsUnixDomainSockets) return;
        var directory = Directory.CreateTempSubdirectory("vsr-");
        string path = Path.Combine(directory.FullName, "peer");
        try
        {
            using (var listener = LinuxRecoveryControllerLaunch.BindOwnedListener(path))
            {
                listener.Listen(1);
                File.Delete(path);
                File.WriteAllText(path, "unrelated-replacement");
            }
            Assert.Equal("unrelated-replacement", File.ReadAllText(path));
        }
        finally { File.Delete(path); directory.Delete(); }
    }

    [Fact]
    public void FailedBindCannotDeleteAnExistingEntry()
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || !Socket.OSSupportsUnixDomainSockets) return;
        var directory = Directory.CreateTempSubdirectory("vsr-");
        string path = Path.Combine(directory.FullName, "peer");
        try
        {
            File.WriteAllText(path, "preserved-before-bind");
            Assert.Throws<SocketException>(() => { using var listener = LinuxRecoveryControllerLaunch.BindOwnedListener(path); });
            Assert.Equal("preserved-before-bind", File.ReadAllText(path));
        }
        finally { File.Delete(path); directory.Delete(); }
    }

    [Fact]
    public void HelloHasIndependentFixedMagicAndNetworkUuidLayout()
    {
        Guid operation = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        byte[] wire = Convert.FromHexString("56535248454c303100112233445566778899aabbccddeeff");
        Assert.Equal(wire, LinuxRecoveryControllerLaunch.Hello(operation));
        Assert.Equal(operation, LinuxRecoveryControllerLaunch.DecodeHello(wire));
    }

    [Fact]
    public void EmptyWrongMagicTruncatedAndTrailingHellosRefuse()
    {
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerLaunch.Hello(Guid.Empty));
        byte[] valid = LinuxRecoveryControllerLaunch.Hello(Guid.NewGuid());
        for (int size = 0; size < valid.Length; size++)
        {
            byte[] shortFrame = valid[..size];
            Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerLaunch.DecodeHello(shortFrame));
        }
        byte[] extra = new byte[25]; valid.CopyTo(extra, 0);
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerLaunch.DecodeHello(extra));
        valid[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerLaunch.DecodeHello(valid));
        byte[] empty = LinuxRecoveryControllerLaunch.Hello(Guid.NewGuid()); Array.Clear(empty, 8, 16);
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerLaunch.DecodeHello(empty));
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)]
    public void ReservedOrMissingProcessCannotEnterChildValidation(int process) =>
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryControllerLaunch.ValidateNonrootProcess(process));
}
