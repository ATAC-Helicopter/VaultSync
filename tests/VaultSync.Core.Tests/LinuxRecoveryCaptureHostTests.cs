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
