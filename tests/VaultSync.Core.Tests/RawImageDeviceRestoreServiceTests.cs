#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class RawImageDeviceRestoreServiceTests
{
    private sealed class Storage : LinuxRawImageStagingService.IStorage
    {
        private MemoryStream payload = new(), metadata = new();
        internal byte[]? SealedBytes;
        public Stream Payload => payload;
        public Stream Metadata => metadata;
        public bool Published { get; private set; }
        public void FlushPayload() { }
        public void FlushMetadata() { }
        public void FlushDirectories() { }
        public void Publish() => Published = true;
        public void Seal()
        {
            byte[] raw = payload.ToArray(), meta = metadata.ToArray();
            SealedBytes = raw;
            payload.Dispose(); metadata.Dispose();
            payload = new(raw, false); metadata = new(meta, false);
        }
        public ValueTask DisposeAsync() { payload.Dispose(); metadata.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class Target(LinuxBlockObservation observation, List<string> trace) : RawImageDeviceRestoreService.ITarget
    {
        internal readonly byte[] Data = new byte[(int)observation.Capacity];
        internal string? Fault;
        internal int Writes;
        internal bool Disposed;
        internal bool DisposeFault;
        internal Action<string>? Hook;
        private MemoryStream? reader;
        private bool corrupted;
        public LinuxBlockObservation Observation { get; } = observation;
        public Stream ReadBack
        {
            get
            {
                if (Fault == "read-back" && !corrupted) { Data[Data.Length / 2] ^= 1; corrupted = true; }
                return reader ??= new MemoryStream(Data, false);
            }
        }
        private void Step(string step)
        {
            trace.Add(step); Hook?.Invoke(step);
            if (Fault == step) throw new IOException("private-path-secret");
        }
        public ValueTask RecheckAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Step("recheck"); return ValueTask.CompletedTask; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, ulong offset, CancellationToken token)
        {
            Step("write"); token.ThrowIfCancellationRequested();
            if (Fault == "unexpected-write") throw new InvalidOperationException("fixture-provider-fault");
            Writes++;
            if (Fault == "partial-write")
            {
                bytes.Span[..(bytes.Length / 2)].CopyTo(Data.AsSpan((int)offset));
                throw new IOException("private-path-secret");
            }
            bytes.Span.CopyTo(Data.AsSpan((int)offset));
            return ValueTask.CompletedTask;
        }
        public void Flush() => Step("flush");
        public ValueTask DisposeAsync() { Disposed = true; reader?.Dispose(); trace.Add("dispose"); if (Fault == "dispose" || DisposeFault) throw new IOException("private-path-secret"); return ValueTask.CompletedTask; }
    }
    private sealed class Journal(List<string> trace) : RawImageDeviceRestoreService.IJournal
    {
        internal readonly List<RawImageRestoreRecord> Records = new();
        internal RawImageRestoreRecordKind? Fault;
        internal Action<RawImageRestoreRecord>? Hook;
        internal bool Invalid, StrictTerminal;
        public ValueTask AppendDurableAsync(RawImageRestoreRecord record, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); trace.Add(record.Kind.ToString());
            if (Invalid || StrictTerminal && Records.Any(r => r.Kind is RawImageRestoreRecordKind.Interrupted or RawImageRestoreRecordKind.Failed))
                throw new InvalidDataException("journal-binding-or-terminal");
            if (Fault == record.Kind) throw new IOException("private-journal-secret");
            Records.Add(record); Hook?.Invoke(record); return ValueTask.CompletedTask;
        }
    }
    private static LinuxBlockObservation Device(uint sector, ulong capacity) => new("vdb", "254:16", null,
        LinuxBlockKind.WholeDisk, capacity, sector, sector, 2, false, "serial:FIXTURE", LinuxBlockIdentity.UniqueInSnapshot,
        false, false, false, LinuxBlockUse.Unknown);
    private static async Task<LinuxRawImageStagingSession> Stage(uint sector, Storage? storage = null)
    {
        using var metadata = new MemoryStream(Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid(sector).MetadataHex));
        using var payload = new MemoryStream(DiskImageRawProfileVerificationServiceTests.Raw(sector));
        return (await LinuxRawImageStagingService.StageAsync(metadata, payload, _ => storage ?? new Storage())).Session!;
    }
    private static RawImageDeviceRestoreRequest Request(LinuxRawImageStagingSession stage, LinuxBlockObservation device) =>
        new(Guid.NewGuid(), Guid.NewGuid(), stage.Image.ImageId, stage.Image.CaptureId,
            stage.Image.Generation, stage.Image.ManifestRoot!, device);

    [Theory]
    [InlineData(512u)]
    [InlineData(4096u)]
    public async Task DurableIntentPrecedesWritesAndReadBackPrecedesSuccess(uint sector)
    {
        await using var stage = await Stage(sector);
        var trace = new List<string>(); var target = new Target(Device(sector, stage.Image.VerifiedBytes), trace);
        var journal = new Journal(trace); var request = Request(stage, target.Observation);
        var result = await RawImageDeviceRestoreService.RunAsync(stage, request, target, journal);
        Assert.Equal(RawImageDeviceRestoreState.WrittenAndReadBack, result.State);
        Assert.Equal(stage.Image.VerifiedBytes, result.AcknowledgedBytes);
        Assert.Equal(0UL, result.UncertainLength); Assert.True(result.TerminalRecordWritten); Assert.True(target.Disposed);
        Assert.Equal(DiskImageRawProfileVerificationServiceTests.Raw(sector), target.Data);
        Assert.Equal(RawImageRestoreRecordKind.ReadBackVerified, journal.Records.Last().Kind);
        var intents = journal.Records.Where(r => r.Kind == RawImageRestoreRecordKind.WriteIntent).ToArray();
        var acknowledgements = journal.Records.Where(r => r.Kind == RawImageRestoreRecordKind.RangeFlushed).ToArray();
        Assert.Equal(target.Writes, intents.Length); Assert.Equal(intents.Length, acknowledgements.Length);
        Assert.Equal(stage.Image.VerifiedBytes, (ulong)acknowledgements.Sum(r => (long)r.Length));
        foreach (var pair in intents.Zip(acknowledgements))
        {
            Assert.Equal(pair.First with { Kind = RawImageRestoreRecordKind.RangeFlushed }, pair.Second);
            Assert.InRange(pair.First.Length, 1UL, (ulong)RawImageDeviceRestoreService.ChunkBytes);
            Assert.Equal(64, pair.First.Sha256!.Length);
        }
        Assert.True(trace.IndexOf("WriteIntent") < trace.IndexOf("write"));
        Assert.True(trace.IndexOf("write") < trace.IndexOf("flush"));
        Assert.True(trace.IndexOf("flush") < trace.IndexOf("RangeFlushed"));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("review")]
    [InlineData("image")]
    [InlineData("capture")]
    [InlineData("generation")]
    [InlineData("root")]
    [InlineData("capacity")]
    [InlineData("logical")]
    [InlineData("physical")]
    [InlineData("readonly")]
    [InlineData("mounted")]
    [InlineData("swap")]
    [InlineData("holders")]
    [InlineData("in-use")]
    [InlineData("identity")]
    [InlineData("partition")]
    [InlineData("sequence")]
    public async Task BindingOrTargetMismatchRefusesBeforeJournalAndWrites(string change)
    {
        await using var stage = await Stage(512);
        var trace = new List<string>(); var target = new Target(Device(512, stage.Image.VerifiedBytes), trace);
        var journal = new Journal(trace); var r = Request(stage, target.Observation);
        r = change switch
        {
            "operation" => r with { OperationId = Guid.Empty }, "review" => r with { ReviewId = Guid.Empty },
            "image" => r with { ImageId = Guid.NewGuid() }, "capture" => r with { CaptureId = Guid.NewGuid() },
            "generation" => r with { Generation = 0 }, "root" => r with { ManifestRoot = new string('f', 64) },
            _ => r with { Target = change switch
            {
                "capacity" => r.Target with { Capacity = r.Target.Capacity + 512 },
                "logical" => r.Target with { LogicalSectorBytes = 4096 },
                "physical" => r.Target with { PhysicalSectorBytes = 4096 },
                "readonly" => r.Target with { IsReadOnly = true }, "mounted" => r.Target with { MountObserved = true },
                "swap" => r.Target with { SwapObserved = true }, "holders" => r.Target with { HasHolders = true },
                "in-use" => r.Target with { Use = LinuxBlockUse.ObservedInUse },
                "identity" => r.Target with { Identity = LinuxBlockIdentity.Ambiguous },
                "partition" => r.Target with { Kind = LinuxBlockKind.Partition, ParentKernelName = "vda" },
                _ => r.Target with { DiskSequence = null }
            }}
        };
        if (r.Target != target.Observation) target = new Target(r.Target, trace);
        var result = await RawImageDeviceRestoreService.RunAsync(stage, r, target, journal);
        Assert.Equal(RawImageDeviceRestoreState.Refused, result.State);
        Assert.Equal(0, target.Writes); Assert.Empty(journal.Records); Assert.True(target.Disposed);
    }

    [Theory]
    [InlineData("recheck", 0UL)]
    [InlineData("write", 65536UL)]
    [InlineData("partial-write", 65536UL)]
    [InlineData("flush", 65536UL)]
    [InlineData("read-back", 0UL)]
    [InlineData("dispose", 0UL)]
    public async Task IoAndChangedReadBackNeverBecomeSuccess(string fault, ulong uncertain)
    {
        await using var stage = await Stage(512);
        var trace = new List<string>(); var target = new Target(Device(512, stage.Image.VerifiedBytes), trace) { Fault = fault };
        var journal = new Journal(trace);
        var result = await RawImageDeviceRestoreService.RunAsync(stage, Request(stage, target.Observation), target, journal);
        Assert.Equal(RawImageDeviceRestoreState.Failed, result.State);
        Assert.Equal(uncertain, result.UncertainLength);
        if (fault != "dispose") Assert.DoesNotContain(journal.Records, r => r.Kind == RawImageRestoreRecordKind.ReadBackVerified);
        Assert.True(target.Disposed);
    }

    [Theory]
    [InlineData((int)RawImageRestoreRecordKind.Started, 0UL)]
    [InlineData((int)RawImageRestoreRecordKind.WriteIntent, 0UL)]
    [InlineData((int)RawImageRestoreRecordKind.RangeFlushed, 65536UL)]
    [InlineData((int)RawImageRestoreRecordKind.ReadBackVerified, 0UL)]
    public async Task JournalFailureNeverAcknowledgesUnconfirmedRanges(int faultValue, ulong uncertain)
    {
        var fault = (RawImageRestoreRecordKind)faultValue;
        await using var stage = await Stage(512);
        var trace = new List<string>(); var target = new Target(Device(512, stage.Image.VerifiedBytes), trace);
        var journal = new Journal(trace) { Fault = fault };
        var result = await RawImageDeviceRestoreService.RunAsync(stage, Request(stage, target.Observation), target, journal);
        Assert.Equal(RawImageDeviceRestoreState.Failed, result.State); Assert.Equal(uncertain, result.UncertainLength);
        if (fault is RawImageRestoreRecordKind.Started or RawImageRestoreRecordKind.WriteIntent) Assert.Equal(0, target.Writes);
        if (fault == RawImageRestoreRecordKind.RangeFlushed) Assert.Equal(0UL, result.AcknowledgedBytes);
        Assert.True(target.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRecordsUncertaintyWithoutInferringRollback(bool afterWrite)
    {
        await using var stage = await Stage(512);
        var trace = new List<string>(); var target = new Target(Device(512, stage.Image.VerifiedBytes), trace);
        var journal = new Journal(trace); using var cancel = new CancellationTokenSource();
        if (afterWrite) target.Hook = step => { if (step == "flush") cancel.Cancel(); };
        else journal.Hook = r => { if (r.Kind == RawImageRestoreRecordKind.WriteIntent) cancel.Cancel(); };
        var result = await RawImageDeviceRestoreService.RunAsync(stage, Request(stage, target.Observation), target, journal, cancel.Token);
        Assert.Equal(RawImageDeviceRestoreState.Interrupted, result.State); Assert.Equal(0UL, result.AcknowledgedBytes);
        Assert.Equal(65536UL, result.UncertainLength); Assert.True(result.TerminalRecordWritten);
        Assert.Equal(RawImageRestoreRecordKind.Interrupted, journal.Records.Last().Kind);
        Assert.Equal(afterWrite ? 1 : 0, target.Writes); Assert.True(target.Disposed);
    }

    [Fact]
    public async Task FailedFailureRecordIsReportedWithoutPretendingPersistence()
    {
        await using var stage = await Stage(512);
        var trace = new List<string>(); var target = new Target(Device(512, stage.Image.VerifiedBytes), trace) { Fault = "partial-write" };
        var journal = new Journal(trace) { Fault = RawImageRestoreRecordKind.Failed };
        var result = await RawImageDeviceRestoreService.RunAsync(stage, Request(stage, target.Observation), target, journal);
        Assert.Equal(RawImageDeviceRestoreState.Failed, result.State);
        Assert.False(result.TerminalRecordWritten); Assert.Equal(65536UL, result.UncertainLength);
    }

    [Fact]
    public async Task CancellationAfterDurableReadBackRecordDoesNotUndoObservedCompletion()
    {
        await using var stage = await Stage(512);
        var trace = new List<string>(); var target = new Target(Device(512, stage.Image.VerifiedBytes), trace);
        using var cancel = new CancellationTokenSource();
        var journal = new Journal(trace) { Hook = r => { if (r.Kind == RawImageRestoreRecordKind.ReadBackVerified) cancel.Cancel(); } };
        var result = await RawImageDeviceRestoreService.RunAsync(stage, Request(stage, target.Observation), target, journal, cancel.Token);
        Assert.Equal(RawImageDeviceRestoreState.WrittenAndReadBack, result.State); Assert.True(result.TerminalRecordWritten);
    }

    [Fact]
    public async Task OrdinaryFileCannotBecomeANativeTargetAndRefusalRetainsCallerOwnership()
    {
        await using var stage = await Stage(512);
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(path, new byte[(int)stage.Image.VerifiedBytes]);
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
            Assert.Null(LinuxRawRestoreTarget.Adopt(handle, Device(512, stage.Image.VerifiedBytes)));
            Assert.False(handle.IsClosed);
            handle.Dispose();
            Assert.Null(LinuxRawRestoreTarget.Adopt(handle, Device(512, stage.Image.VerifiedBytes)));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CorruptedOrDisposedStageRefusesBeforeWriting(bool disposed)
    {
        var storage = new Storage(); await using var stage = await Stage(512, storage);
        if (disposed) await stage.DisposeAsync();
        else storage.SealedBytes![storage.SealedBytes.Length / 2] ^= 1;
        var trace = new List<string>(); var target = new Target(Device(512, stage.Image.VerifiedBytes), trace);
        var journal = new Journal(trace);
        var result = await RawImageDeviceRestoreService.RunAsync(stage, Request(stage, target.Observation), target, journal);
        Assert.Equal(RawImageDeviceRestoreState.Refused, result.State);
        Assert.Equal(0, target.Writes); Assert.Empty(journal.Records); Assert.True(target.Disposed);
    }

    [Fact]
    public async Task SecondChunkFailureRetainsOnlyTheFirstDurablyAcknowledgedRange()
    {
        await using var stage = await Stage(4096);
        var trace = new List<string>(); var target = new Target(Device(4096, stage.Image.VerifiedBytes), trace);
        target.Hook = step => { if (step == "write" && target.Writes == 1) target.Fault = "partial-write"; };
        var journal = new Journal(trace);
        var result = await RawImageDeviceRestoreService.RunAsync(stage, Request(stage, target.Observation), target, journal);
        Assert.Equal(RawImageDeviceRestoreState.Failed, result.State);
        Assert.Equal(131072UL, result.AcknowledgedBytes); Assert.Equal(131072UL, result.UncertainOffset);
        Assert.Equal(131072UL, result.UncertainLength);
        Assert.Single(journal.Records, r => r.Kind == RawImageRestoreRecordKind.RangeFlushed);
    }

    [Fact]
    public async Task UnexpectedProviderExceptionStillReleasesTargetWithoutACompletionRecord()
    {
        await using var stage = await Stage(512);
        var trace = new List<string>(); var target = new Target(Device(512, stage.Image.VerifiedBytes), trace) { Fault = "unexpected-write" };
        var journal = new Journal(trace);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RawImageDeviceRestoreService.RunAsync(stage, Request(stage, target.Observation), target, journal));
        Assert.True(target.Disposed);
        Assert.DoesNotContain(journal.Records, r => r.Kind == RawImageRestoreRecordKind.ReadBackVerified);
        Assert.Single(journal.Records, r => r.Kind == RawImageRestoreRecordKind.WriteIntent);
    }

    [Fact]
    public async Task JournalBindingRefusalDoesNotEscapeAsAnUnhandledFailure()
    {
        await using var stage = await Stage(512);
        var trace = new List<string>(); var target = new Target(Device(512, stage.Image.VerifiedBytes), trace);
        var journal = new Journal(trace) { Invalid = true };
        var result = await RawImageDeviceRestoreService.RunAsync(stage, Request(stage, target.Observation), target, journal);
        Assert.Equal(RawImageDeviceRestoreState.Failed, result.State); Assert.Equal(0, target.Writes);
        Assert.True(target.Disposed); Assert.False(result.TerminalRecordWritten);
    }

    [Fact]
    public async Task CleanupAfterTerminalFailureCannotForceAnInvalidSecondTerminalRecord()
    {
        await using var stage = await Stage(512);
        var trace = new List<string>(); var target = new Target(Device(512, stage.Image.VerifiedBytes), trace)
            { Fault = "partial-write", DisposeFault = true };
        var journal = new Journal(trace) { StrictTerminal = true };
        var result = await RawImageDeviceRestoreService.RunAsync(stage, Request(stage, target.Observation), target, journal);
        Assert.Equal(RawImageDeviceRestoreState.Failed, result.State); Assert.False(result.TerminalRecordWritten);
        Assert.Equal(65536UL, result.UncertainLength); Assert.True(target.Disposed);
        Assert.Single(journal.Records, r => r.Kind == RawImageRestoreRecordKind.Failed);
    }
}
