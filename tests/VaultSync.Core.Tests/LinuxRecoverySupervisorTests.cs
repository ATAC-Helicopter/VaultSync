#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoverySupervisorTests
{
    internal sealed class Confirmation : LinuxRecoverySupervisor.IConfirmation
    {
        internal int Calls;
        internal Func<LinuxDiskRestoreOverwriteReview, CancellationToken, Task> Request = (_, _) =>
            throw new InvalidOperationException("fixture-unexpected-console");
        public Task ConfirmAsync(LinuxDiskRestoreOverwriteReview review, CancellationToken token)
        { Interlocked.Increment(ref Calls); return Request(review, token); }
    }

    [Fact]
    public async Task ChannelFailureBeforeReviewNeverRequestsConsoleAndClosesWorker()
    {
        var transport = new LinuxRecoveryWorkerOperationTests.Transport();
        var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, new LinuxRecoveryWorkerOperationTests.Platform());
        var confirmation = new Confirmation();
        var supervisor = new LinuxRecoverySupervisor(worker, confirmation);
        var run = supervisor.RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.LosePeer(new IOException("fixture-peer-loss"));
        var terminal = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LinuxRecoverySessionPhase.Faulted, terminal.Phase);
        Assert.Equal(0, confirmation.Calls);
        Assert.Equal(1, transport.Disposals);
        Assert.Null(terminal.Outcome);
        transport.CleanupFrames();
    }

    [Fact]
    public async Task DuplicateSupervisorStartCannotCancelTheRunningOwner()
    {
        var transport = new LinuxRecoveryWorkerOperationTests.Transport();
        var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, new LinuxRecoveryWorkerOperationTests.Platform());
        var confirmation = new Confirmation();
        var supervisor = new LinuxRecoverySupervisor(worker, confirmation);
        using var cancellation = new CancellationTokenSource();
        var run = supervisor.RunAsync(cancellation.Token);
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        Assert.Throws<InvalidOperationException>(() => { _ = supervisor.RunAsync(); });
        await transport.Barrier();
        Assert.False(run.IsCompleted);
        Assert.Equal(0, transport.Disposals);
        await cancellation.CancelAsync();
        Assert.Equal(LinuxRecoverySessionPhase.Faulted, (await run.WaitAsync(TimeSpan.FromSeconds(10))).Phase);
        Assert.Equal(1, transport.Disposals);
        Assert.Equal(0, confirmation.Calls);
        transport.CleanupFrames();
    }

    [Fact]
    public async Task ProgrammingFaultCompletesSupervisorOnlyAfterClosingSourceAndWorker()
    {
        var transport = new LinuxRecoveryWorkerOperationTests.Transport();
        using var sources = new LinuxRecoveryWorkerOperationTests.Sources();
        var platform = new LinuxRecoveryWorkerOperationTests.Platform
        { Stage = (_, _, _) => throw new InvalidOperationException("fixture-programming-fault") };
        var worker = new LinuxRecoveryWorkerOperation(Guid.NewGuid(), transport, platform);
        var confirmation = new Confirmation();
        var run = new LinuxRecoverySupervisor(worker, confirmation).RunAsync();
        using (await transport.Next(LinuxRecoveryMessage.Ready)) { }
        transport.Controller(LinuxRecoveryMessage.OfferImages, sources: sources.Lease);
        using (await transport.Next(LinuxRecoveryMessage.Staging)) { }
        await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(sources.Lease.Metadata.CanRead);
        Assert.Equal(1, transport.Disposals);
        Assert.Equal(0, confirmation.Calls);
        transport.CleanupFrames();
    }
}
