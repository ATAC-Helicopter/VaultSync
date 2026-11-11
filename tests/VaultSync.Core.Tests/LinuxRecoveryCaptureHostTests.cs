#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryCaptureHostTests
{
    [Fact]
    public async Task MonitorCancellationDrainsPrecommitCaptureBeforeReturningAndNeverDisplaysAResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool captureDrained = false, monitorDrained = false, displayed = false;
        var running = LinuxRecoveryCaptureHost.RunSessionAsync(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); return null; }
            finally { captureDrained = true; }
        }, (_, _) => displayed = true, async lifetime =>
        {
            await entered.Task;
            await lifetime.CancelAsync();
            monitorDrained = true;
        }, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(captureDrained); Assert.True(monitorDrained); Assert.False(displayed);
    }

    [Fact]
    public async Task NoSelectionDrainsItsMonitorAndAFreshSessionDoesNotInheritCancellation()
    {
        bool drained = false;
        var cancelled = await LinuxRecoveryCaptureHost.RunSessionAsync(_ => Task.FromResult<LinuxRawImageCaptureAssessment?>(null),
            (_, _) => throw new InvalidOperationException("no-selection-has-no-result"), async lifetime =>
            {
                try { await Task.Delay(Timeout.Infinite, lifetime.Token); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { drained = true; }
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(drained); Assert.Null(cancelled.Capture); Assert.Equal(76, cancelled.ExitCode);

        var refusal = new LinuxRawImageCaptureAssessment(LinuxRawImageCaptureState.Refused, "fixture");
        bool displayed = false;
        var retry = await LinuxRecoveryCaptureHost.RunSessionAsync(token =>
        {
            Assert.False(token.IsCancellationRequested);
            return Task.FromResult<LinuxRawImageCaptureAssessment?>(refusal);
        }, (result, _) => { Assert.Same(refusal, result); displayed = true; }, _ => Task.CompletedTask, CancellationToken.None);
        Assert.True(displayed); Assert.Same(refusal, retry.Capture); Assert.Equal(74, retry.ExitCode);
    }

    [Theory]
    [InlineData(LinuxRawImageCaptureState.PublishedVerified, "io")]
    [InlineData(LinuxRawImageCaptureState.PublishedVerified, "cancel")]
    [InlineData(LinuxRawImageCaptureState.PublishedVerified, "monitor")]
    [InlineData(LinuxRawImageCaptureState.PublicationUnconfirmed, "io")]
    [InlineData(LinuxRawImageCaptureState.Refused, "monitor")]
    public async Task HostFaultDoesNotRewriteExactCaptureAssessment(LinuxRawImageCaptureState state, string fault)
    {
        var assessment = new LinuxRawImageCaptureAssessment(state, "fixed", 123, "image", "root");
        var outcome = await LinuxRecoveryCaptureHost.RunSessionAsync(_ => Task.FromResult<LinuxRawImageCaptureAssessment?>(assessment),
            (_, _) => { if (fault == "io") throw new IOException(); if (fault == "cancel") throw new OperationCanceledException(); },
            _ => fault == "monitor" ? Task.FromException(new InvalidDataException()) : Task.CompletedTask, CancellationToken.None);
        Assert.Same(assessment, outcome.Capture);
        Assert.Equal(74, outcome.ExitCode);
    }

    [Fact]
    public async Task CancellationAtCommitStillDisplaysPublishedResultWithIndependentBoundedToken()
    {
        using var stop = new CancellationTokenSource();
        var assessment = new LinuxRawImageCaptureAssessment(LinuxRawImageCaptureState.PublishedVerified, "fixed");
        bool displayed = false;
        var outcome = await LinuxRecoveryCaptureHost.RunSessionAsync(_ =>
        { stop.Cancel(); return Task.FromResult<LinuxRawImageCaptureAssessment?>(assessment); },
            (_, token) => { Assert.True(token.CanBeCanceled); Assert.False(token.IsCancellationRequested); displayed = true; },
            _ => Task.CompletedTask, stop.Token);
        Assert.True(displayed); Assert.Same(assessment, outcome.Capture); Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public async Task PrecommitFailureRemainsFailureAndNeverDisplaysSuccess()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => LinuxRecoveryCaptureHost.RunSessionAsync(
            _ => Task.FromException<LinuxRawImageCaptureAssessment?>(new InvalidDataException()),
            (_, _) => throw new Exception("must not display"), _ => Task.CompletedTask, CancellationToken.None));
    }

    [Theory]
    [InlineData("1.9.0-beta.1", true)] [InlineData("unknown", false)] [InlineData("bad version", false)]
    public void CaptureUsesExactCompiledVersionAndRequiresMatchingSource(string version, bool valid)
    {
        string commit = new('a', 40);
        var build = BuildInformationService.Create(typeof(BuildInformationService).Assembly) with { Version = version, SourceCommit = commit };
        if (valid) Assert.Equal(version, LinuxRecoveryCaptureHost.RequireBuild(build, commit));
        else Assert.Throws<InvalidDataException>(() => LinuxRecoveryCaptureHost.RequireBuild(build, commit));
        Assert.Throws<InvalidDataException>(() => LinuxRecoveryCaptureHost.RequireBuild(build, new string('b', 40)));
    }
}
