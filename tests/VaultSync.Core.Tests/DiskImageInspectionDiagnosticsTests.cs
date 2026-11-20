#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class DiskImageInspectionDiagnosticsTests
{
    [Fact]
    public void InvalidOrOutOfBoundsLocalRecordsAreNotWritten()
    {
        var valid = new DiskImageInspectionDiagnostics().Snapshot(DiskImageInspectionWorkerOutcome.Completed,
            LinuxRecoveryStartupDiagnostics.Error.None, 0);
        var invalid = new[] { valid with { Phase = (DiskImageInspectionPhase)999 },
            valid with { Outcome = (DiskImageInspectionWorkerOutcome)999 },
            valid with { Error = (LinuxRecoveryStartupDiagnostics.Error)999 },
            valid with { ElapsedMilliseconds = -1 }, valid with { ElapsedMilliseconds = 86400001 },
            valid with { WorkerExitCode = 256 }, valid with { ReadCalls = -1 },
            valid with { ReadCalls = 9000000001 }, valid with { ReadBytes = -1 },
            valid with { ReadBytes = long.MaxValue } };
        // Sink exceptions are swallowed by design, so count calls independently.
        int calls = 0;
        foreach (var value in invalid) DiskImageInspectionDiagnostics.Write(value, _ => calls++);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 124)]
    [InlineData(3, -1)]
    [InlineData(1, -1)]
    public void TerminalEvidenceIsFixedNumericLocalAndSinkFailuresAreHarmless(int outcomeCode, int exit)
    {
        var outcome = (DiskImageInspectionWorkerOutcome)outcomeCode;
        var tracking = new DiskImageInspectionDiagnostics { Phase = DiskImageInspectionPhase.Exchange };
        tracking.ReadServed(128 * 1024); tracking.ReadServed(37);
        var value = tracking.Snapshot(outcome, LinuxRecoveryStartupDiagnostics.Error.Timeout, exit);
        Assert.Equal(outcome, value.Outcome); Assert.Equal(2, value.ReadCalls); Assert.Equal(131109, value.ReadBytes);
        Assert.Equal(exit < 0 ? null : (int?)exit, value.WorkerExitCode);
        Assert.InRange(value.ElapsedMilliseconds, 0, 86400000);
        string? line = null;
        DiskImageInspectionDiagnostics.Write(value, text => line = text);
        Assert.Matches("^VSINS1\\|4\\|[0-3]\\|8\\|[0-9]+\\|-?[0-9]+\\|2\\|131109\\n$", line!);
        Assert.InRange(line!.Length, 1, 128);
        DiskImageInspectionDiagnostics.Write(value, _ => throw new IOException("private-path-secret"));
        DiskImageInspectionDiagnostics.Write(null, _ => throw new Exception("must not call"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BrokerCountersIncludeOnlyCompletedBoundedRepliesAndPreserveRefusal(bool invalid)
    {
        Guid operation = Guid.NewGuid();
        byte[] read = DiskImageInspectionProtocol.Read(operation, 1, invalid ? 65535UL : 0, 16);
        byte[] end = new byte[8]; "VSIMRSL1"u8.CopyTo(end);
        using var input = new MemoryStream(read.Concat(end).ToArray());
        using var output = new MemoryStream();
        using var metadata = new MemoryStream(new byte[16], false);
        using var payload = new MemoryStream(new byte[65536], false);
        var tracking = new DiskImageInspectionDiagnostics();
        Task<string> Run() => DiskImageInspectionWorkerClient.ServeAsync(input, output,
            new(metadata, payload, operation, 16, 65536), () => { }, end,
            (_, _, _, _) => Task.FromResult("fixed"), CancellationToken.None, tracking.ReadServed);
        if (invalid) await Assert.ThrowsAsync<InvalidDataException>(Run);
        else Assert.Equal("fixed", await Run());
        var value = tracking.Snapshot(DiskImageInspectionWorkerOutcome.Failed, LinuxRecoveryStartupDiagnostics.Error.None, null);
        Assert.Equal(invalid ? 0 : 1, value.ReadCalls);
        Assert.Equal(invalid ? 0 : 16, value.ReadBytes);
        Assert.Equal(invalid ? 0 : 24, output.Length);
    }
}
