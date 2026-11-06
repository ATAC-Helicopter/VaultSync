using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskImageInspectionServiceTests
{
    [Theory]
    [InlineData((int)DiskImageInspectionWorkerOutcome.Unavailable, DiskImageInspectionState.Unavailable)]
    [InlineData((int)DiskImageInspectionWorkerOutcome.Failed, DiskImageInspectionState.Failed)]
    [InlineData((int)DiskImageInspectionWorkerOutcome.TimedOut, DiskImageInspectionState.TimedOut)]
    [InlineData((int)DiskImageInspectionWorkerOutcome.Completed, DiskImageInspectionState.Failed)]
    public void MissingReceiptsNeverProduceSuccess(int outcome, DiskImageInspectionState expected)
    {
        var result = DiskImageInspectionService.Translate(new((DiskImageInspectionWorkerOutcome)outcome));
        Assert.Equal(expected, result.State);
        Assert.Equal(0UL, result.VerifiedBytes);
    }

    [Theory]
    [InlineData(DiskImageRawProfileVerificationState.Invalid)]
    [InlineData(DiskImageRawProfileVerificationState.Incomplete)]
    [InlineData(DiskImageRawProfileVerificationState.Corrupt)]
    [InlineData(DiskImageRawProfileVerificationState.Unsupported)]
    [InlineData(DiskImageRawProfileVerificationState.Failed)]
    public void RejectedInspectionDoesNotExposeUnverifiedGeometry(DiskImageRawProfileVerificationState state)
    {
        var result = DiskImageInspectionService.Translate(new(DiskImageInspectionWorkerOutcome.Completed,
            new(state, VerifiedBytes: 1024, LogicalSectorBytes: 512)));
        Assert.Equal(new(DiskImageInspectionState.Rejected), result);
    }

    [Fact]
    public void VerifiedReceiptCarriesOnlyMeasuredGeometry()
    {
        var result = DiskImageInspectionService.Translate(new(DiskImageInspectionWorkerOutcome.Completed,
            new(DiskImageRawProfileVerificationState.ContentAndLayoutVerified, VerifiedBytes: 8192, LogicalSectorBytes: 4096)));
        Assert.Equal(new(DiskImageInspectionState.Verified, 8192, 4096), result);
    }

    [Fact]
    public async Task CancellationPrecedesPlatformAndStreamAccess()
    {
        using var metadata = new MemoryStream();
        using var payload = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DiskImageInspectionService.InspectAsync(metadata, payload, new CancellationToken(true)));
    }

    [Fact]
    public async Task UnsupportedHostDoesNotReadImageBytes()
    {
        if (OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.X64) return;
        using var source = new UnreadableStream();
        var result = await DiskImageInspectionService.InspectAsync(source, source);
        Assert.Equal(DiskImageInspectionState.UnsupportedPlatform, result.State);
    }

    private sealed class UnreadableStream : MemoryStream
    {
        public override long Length => throw new InvalidOperationException("Image must not be inspected in the host process");
    }
}
