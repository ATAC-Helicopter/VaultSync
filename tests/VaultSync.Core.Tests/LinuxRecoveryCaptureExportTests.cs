#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryCaptureExportTests
{
    internal static async Task<(LinuxRawImageCaptureRequest Request, LinuxRawImageCaptureAssessment Capture, byte[] Metadata, byte[] Raw)> Fixture()
    {
        byte[] metadata = Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid(512).MetadataHex);
        byte[] raw = DiskImageRawProfileVerificationServiceTests.Raw(512);
        using var meta = new MemoryStream(metadata, false); using var payload = new MemoryStream(raw, false);
        var verified = await DiskImageRawProfileVerificationService.VerifyAsync(meta, payload);
        var image = verified.Image!;
        return (new(image.ImageId, image.CaptureId, Guid.NewGuid(), image.DeclaredOrigin!.SourceId, image.Generation, "1.9.0", new string('a', 40)),
            new(LinuxRawImageCaptureState.PublishedVerified, "fixed", (ulong)raw.Length, image.ImageId.ToString("N"), image.ManifestRoot), metadata, raw);
    }

    private sealed class Copy : LinuxRecoveryCaptureExport.ICopy
    {
        internal readonly MemoryStream Raw = new(), Meta = new();
        internal readonly List<string> Steps = new();
        internal Action<string>? Hook;
        internal string? Failure;
        internal bool FailDispose;
        private bool sealedCopy;
        public Stream Metadata => sealedCopy ? new LinuxRawImageStagingService.BorrowedReadStream(Meta) : Meta;
        public Stream Payload => sealedCopy ? new LinuxRawImageStagingService.BorrowedReadStream(Raw) : Raw;
        public bool Published { get; private set; }
        private void Step(string step) { Steps.Add(step); Hook?.Invoke(step); if (Failure == step) throw new IOException("private-sentinel"); }
        public void Seal() { Step("seal"); sealedCopy = true; }
        public void Recheck() => Step("recheck");
        public void Publish() { Step("rename"); Published = true; Step("destination-sync"); Step("workspace-sync"); }
        public ValueTask DisposeAsync() => FailDispose ? ValueTask.FromException(new IOException("cleanup")) : ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ExactReadbackPrecedesExposureAndOriginalBytesRemainUnchanged()
    {
        var fixture = await Fixture(); var copy = new Copy(); int rechecks = 0;
        using var metadata = new MemoryStream(fixture.Metadata, false); using var raw = new MemoryStream(fixture.Raw, false);
        var result = await LinuxRecoveryCaptureExport.CopyAsync(fixture.Request, fixture.Capture, metadata, raw, copy,
            () => rechecks++, LinuxRawImageCaptureServiceTests.ImageFixture, CancellationToken.None);
        Assert.Equal(LinuxRecoveryCaptureExportState.PublishedVerified, result.State);
        Assert.Equal(fixture.Raw, copy.Raw.ToArray()); Assert.Equal(fixture.Metadata, copy.Meta.ToArray());
        Assert.Equal(3, rechecks); Assert.Equal(new[] { "recheck", "seal", "recheck", "rename", "destination-sync", "workspace-sync" }, copy.Steps);
        Assert.False(metadata.CanWrite); Assert.False(raw.CanWrite); Assert.Equal(fixture.Raw, raw.ToArray());
    }

    [Theory]
    [InlineData("seal", 0)]
    [InlineData("rename", 0)]
    [InlineData("destination-sync", 2)]
    [InlineData("workspace-sync", 2)]
    public async Task PublicationFaultNeverRollsBackOrClaimsVerified(string fault, int expected)
    {
        var fixture = await Fixture(); var copy = new Copy { Failure = fault };
        using var metadata = new MemoryStream(fixture.Metadata, false); using var raw = new MemoryStream(fixture.Raw, false);
        var result = await LinuxRecoveryCaptureExport.CopyAsync(fixture.Request, fixture.Capture, metadata, raw, copy,
            () => { }, LinuxRawImageCaptureServiceTests.ImageFixture, CancellationToken.None);
        Assert.Equal((LinuxRecoveryCaptureExportState)expected, result.State);
        Assert.Equal((LinuxRecoveryCaptureExportState)expected == LinuxRecoveryCaptureExportState.PublicationUnconfirmed, copy.Published);
        Assert.Equal(LinuxRawImageCaptureState.PublishedVerified, fixture.Capture.State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationBeforeExposureInterruptsAndAfterRenamePreservesPublication(bool afterRename)
    {
        var fixture = await Fixture(); using var stop = new CancellationTokenSource();
        var copy = new Copy { Hook = step => { if (step == (afterRename ? "destination-sync" : "seal")) stop.Cancel(); } };
        using var metadata = new MemoryStream(fixture.Metadata, false); using var raw = new MemoryStream(fixture.Raw, false);
        var result = await LinuxRecoveryCaptureExport.CopyAsync(fixture.Request, fixture.Capture, metadata, raw, copy,
            () => { }, LinuxRawImageCaptureServiceTests.ImageFixture, stop.Token);
        Assert.Equal(afterRename ? LinuxRecoveryCaptureExportState.PublishedVerified : LinuxRecoveryCaptureExportState.Interrupted, result.State);
        Assert.Equal(afterRename, copy.Published);
    }

    [Fact]
    public async Task CancellationExceptionAfterRenameIsPublicationUnconfirmed()
    {
        var fixture = await Fixture(); using var stop = new CancellationTokenSource();
        var copy = new Copy { Hook = step => { if (step == "destination-sync") { stop.Cancel(); throw new OperationCanceledException(stop.Token); } } };
        using var metadata = new MemoryStream(fixture.Metadata, false); using var raw = new MemoryStream(fixture.Raw, false);
        var result = await LinuxRecoveryCaptureExport.CopyAsync(fixture.Request, fixture.Capture, metadata, raw, copy,
            () => { }, LinuxRawImageCaptureServiceTests.ImageFixture, stop.Token);
        Assert.True(copy.Published); Assert.Equal(LinuxRecoveryCaptureExportState.PublicationUnconfirmed, result.State);
    }

    [Theory]
    [InlineData("bytes")] [InlineData("image")] [InlineData("capture")] [InlineData("generation")]
    [InlineData("manifest")] [InlineData("unavailable")] [InlineData("original-changed")]
    public async Task CorruptOrMismatchedReadbackNeverExposesCopy(string fault)
    {
        var fixture = await Fixture(); var copy = new Copy();
        using var metadata = new MemoryStream(fixture.Metadata, false); using var raw = new MemoryStream(fixture.Raw, false);
        int rechecks = 0;
        var result = await LinuxRecoveryCaptureExport.CopyAsync(fixture.Request, fixture.Capture, metadata, raw, copy,
            () => { if (++rechecks == 3 && fault == "original-changed") throw new IOException(); },
            async (meta, payload, token) =>
            {
                var observed = await LinuxRawImageCaptureServiceTests.ImageFixture(meta, payload, token);
                if (fault == "bytes") copy.Raw.GetBuffer()[4096] ^= 1;
                return fault switch
                {
                    "image" => observed with { Observation = observed.Observation! with { ImageId = Guid.NewGuid() } },
                    "capture" => observed with { Observation = observed.Observation! with { CaptureId = Guid.NewGuid() } },
                    "generation" => observed with { Observation = observed.Observation! with { Generation = 2 } },
                    "manifest" => observed with { Observation = observed.Observation! with { ManifestRoot = new string('b', 64) } },
                    "unavailable" => new(DiskImageInspectionWorkerOutcome.Unavailable),
                    _ => observed
                };
            }, CancellationToken.None);
        Assert.Equal(LinuxRecoveryCaptureExportState.Failed, result.State); Assert.False(copy.Published);
        Assert.DoesNotContain("rename", copy.Steps);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AsyncDisposalFailurePreservesCaptureAndReportsSeparateExportUncertainty(bool published)
    {
        var fixture = await Fixture(); var copy = new Copy { FailDispose = true };
        if (published) copy.Publish();
        LinuxRecoveryCaptureExportAssessment? exported = null;
        var outcome = await LinuxRecoveryCaptureHost.RunSessionAsync(async token =>
        {
            exported = await LinuxRecoveryCaptureHost.FinishExportAsync(fixture.Capture, copy,
                () => Task.FromResult(new LinuxRecoveryCaptureExportAssessment(LinuxRecoveryCaptureExportState.PublishedVerified)), token);
            return fixture.Capture;
        }, (_, _) => { }, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Same(fixture.Capture, outcome.Capture);
        Assert.Equal(published ? LinuxRecoveryCaptureExportState.PublicationUnconfirmed : LinuxRecoveryCaptureExportState.Failed, exported!.State);
        Assert.Equal(74, LinuxRecoveryCaptureHost.ExitCode(outcome, exported));
    }

    [Fact]
    public async Task ReaderDisposalExceptionAfterCopyPublicationCannotEraseCapture()
    {
        var fixture = await Fixture(); var copy = new Copy(); copy.Publish();
        var exported = await LinuxRecoveryCaptureHost.FinishExportAsync(fixture.Capture, copy,
            () => Task.FromException<LinuxRecoveryCaptureExportAssessment>(new IOException("reader-close")), CancellationToken.None);
        Assert.Equal(LinuxRecoveryCaptureExportState.PublicationUnconfirmed, exported!.State);
        Assert.Equal(LinuxRawImageCaptureState.PublishedVerified, fixture.Capture.State);
    }

    [Fact]
    public async Task AlreadyCancelledDoesNotCopyOrInspect()
    {
        var fixture = await Fixture(); var copy = new Copy(); using var stop = new CancellationTokenSource(); stop.Cancel();
        using var metadata = new MemoryStream(fixture.Metadata, false); using var raw = new MemoryStream(fixture.Raw, false);
        var result = await LinuxRecoveryCaptureExport.CopyAsync(fixture.Request, fixture.Capture, metadata, raw, copy,
            () => throw new Exception("must not inspect"), (_, _, _) => throw new Exception("must not inspect"), stop.Token);
        Assert.Equal(LinuxRecoveryCaptureExportState.Interrupted, result.State); Assert.Equal(0, copy.Raw.Length); Assert.Empty(copy.Steps);
    }

    [Theory]
    [InlineData("original-shared")] [InlineData("original-hardlink")] [InlineData("export-writable")] [InlineData("export-owner")]
    public void PermissionAdmissionNeverAdoptsSharedOriginalOrUserOwnedExportParent(string fault)
    {
        var original = new LinuxRecoveryNative.Identity(0x8100, 0, 0, 1, 1, 1, 8, 1, 0, 0, 0, 0);
        var export = new LinuxRecoveryNative.Identity(0x41e8, 0, 60999, 1, 2, 0, 8, 1, 0, 0, 0, 0);
        Assert.True(LinuxRecoveryCaptureExport.OriginalFile(original)); Assert.True(LinuxRecoveryCaptureExport.ExportRoot(export));
        Assert.False(LinuxRecoveryCaptureExport.ExportRoot(export with { Mode = 0x41c8 }));
        Assert.True(LinuxRecoveryCaptureExport.SharedDirectory(export with { Mode = 0x41c8 }));
        Assert.False(LinuxRecoveryCaptureExport.SharedDirectory(export));
        if (fault == "original-shared") Assert.False(LinuxRecoveryCaptureExport.OriginalFile(original with { Mode = 0x8120 }));
        if (fault == "original-hardlink") Assert.False(LinuxRecoveryCaptureExport.OriginalFile(original with { Links = 2 }));
        if (fault == "export-writable") Assert.False(LinuxRecoveryCaptureExport.ExportRoot(export with { Mode = 0x41d8 }));
        if (fault == "export-owner") Assert.False(LinuxRecoveryCaptureExport.ExportRoot(export with { Owner = 60999 }));
    }
}
