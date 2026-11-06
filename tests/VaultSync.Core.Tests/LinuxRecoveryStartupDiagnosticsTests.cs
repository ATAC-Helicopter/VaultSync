#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Status = VaultSync.Core.Services.LinuxRecoveryStartupDiagnostics;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoveryStartupDiagnosticsTests
{
    private sealed class Input(byte[] bytes) : MemoryStream(bytes)
    {
        internal bool Closed;
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task OnlyValidatedControllerEnumsAreForwardedAndOwnedInputIsClosed()
    {
        var input = new Input(Encoding.ASCII.GetBytes("VSREC1|2|120|0\nVSREC1|2|112|0\nVSREC1|2|113|0\nVSREC1|2|114|0\nVSREC1|2|115|0\nVSREC1|2|104|0\nVSREC1|2|111|5\n"));
        var records = new List<(Status.Phase, Status.Error)>();
        await Status.ReadControllerAsync(input, (phase, error) => records.Add((phase, error)), CancellationToken.None);
        Assert.Equal(new[] { (Status.Phase.NativePipe, Status.Error.None),
            (Status.Phase.ControllerManifestDecoded, Status.Error.None), (Status.Phase.ControllerClosureEnumerated, Status.Error.None),
            (Status.Phase.ControllerClosureHashed, Status.Error.None), (Status.Phase.ControllerBootChecked, Status.Error.None),
            (Status.Phase.PlatformActivation, Status.Error.None),
            (Status.Phase.ControllerFailed, Status.Error.MissingMethod) }, records);
        Assert.True(input.Closed);
    }

    [Theory]
    [InlineData("VSREC1|1|1|0\n")] [InlineData("VSREC1|2|1|0\n")]
    [InlineData("VSREC1|2|39|0\n")] // Host worker milestones cannot enter the controller drain.
    [InlineData("VSREC1|2|999|0\n")] [InlineData("VSREC1|2|104|999\n")]
    [InlineData("VSREC1|2|104|0|private\n")] [InlineData("VSREC1|2|104|0\r\n")]
    [InlineData("VSREC1|2|104|0")] [InlineData("untrusted raw text\n")]
    [InlineData("VSREC1|2|0104|0\n")] [InlineData("VSREC1|2|104|00\n")]
    [InlineData("VSREC1|2|+104|0\n")] [InlineData("VSREC1|2|104|-0\n")]
    [InlineData("VSREC1|2|104|0\0\n")] [InlineData("\n")]
    public async Task MalformedForeignTruncatedAndUnknownRecordsNeverForwardPayload(string text)
    {
        var input = new Input(Encoding.ASCII.GetBytes(text));
        int forwarded = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => Status.ReadControllerAsync(input, (_, _) => forwarded++, CancellationToken.None));
        Assert.Equal(0, forwarded); Assert.True(input.Closed);
    }

    [Fact]
    public async Task RecordAndLineBoundsRejectBeforeForwardingAnExtraRecord()
    {
        const string record = "VSREC1|2|104|0\n";
        var input = new Input(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(record, Status.MaximumRecords + 1))));
        int forwarded = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => Status.ReadControllerAsync(input, (_, _) => forwarded++, CancellationToken.None));
        Assert.Equal(Status.MaximumRecords, forwarded); Assert.True(input.Closed);
        var oversized = new Input(Enumerable.Repeat((byte)'x', Status.MaximumRecordBytes + 1).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => Status.ReadControllerAsync(oversized, (_, _) => Assert.Fail("Unvalidated record forwarded"), CancellationToken.None));
        Assert.True(oversized.Closed);
    }

    private sealed class Fragmented(byte[] bytes) : MemoryStream(bytes)
    {
        internal bool Closed;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }

    [Fact]
    public async Task FragmentedRecordsPreserveBoundariesAndTheExactRecordLimit()
    {
        var input = new Fragmented(Encoding.ASCII.GetBytes(string.Concat(
            Enumerable.Repeat("VSREC1|2|104|0\n", Status.MaximumRecords))));
        int forwarded = 0;
        await Status.ReadControllerAsync(input, (phase, error) =>
        {
            Assert.Equal(Status.Phase.PlatformActivation, phase);
            Assert.Equal(Status.Error.None, error); forwarded++;
        }, CancellationToken.None);
        Assert.Equal(Status.MaximumRecords, forwarded); Assert.True(input.Closed);
    }

    [Fact]
    public async Task ForwardFailureClosesTheOwnedInputWithoutReplacingTheFailure()
    {
        var input = new Input(Encoding.ASCII.GetBytes("VSREC1|2|104|0\n"));
        var failure = new IOException("fixture-forward-failed");
        var actual = await Record.ExceptionAsync(() => Status.ReadControllerAsync(input,
            (_, _) => throw failure, CancellationToken.None));
        Assert.Same(failure, actual); Assert.True(input.Closed);
    }

    private sealed class Pending : Stream
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Closed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
        protected override void Dispose(bool disposing) => Closed = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task CancellationDrainsPendingReadAndClosesOnlyOwnedInput()
    {
        var input = new Pending(); using var cancellation = new CancellationTokenSource();
        var run = Status.ReadControllerAsync(input, (_, _) => Assert.Fail("No input was supplied"), cancellation.Token);
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(input.Closed);
    }

    [Fact]
    public void ExceptionClassificationDoesNotDependOnTechnicalPayload()
    {
        Assert.Equal(Status.Error.MissingMethod, Status.Classify(new System.Reflection.TargetInvocationException(new MissingMethodException("private"))));
        Assert.Equal(Status.Error.NativeLibrary, Status.Classify(new DllNotFoundException("private")));
        Assert.Equal(Status.Error.Denied, Status.Classify(new UnauthorizedAccessException("private")));
    }
}
