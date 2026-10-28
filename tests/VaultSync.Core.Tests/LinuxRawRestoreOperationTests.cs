#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;

namespace VaultSync.Core.Tests;

public sealed class LinuxRawRestoreOperationTests
{
    // Ordinary-file/byte-array fixtures exercise lifecycle and fault ordering only;
    // they establish neither native block identity nor user/offline authority.
    private sealed class Storage : LinuxRawImageStagingService.IStorage, RawImageRestoreJournal.IStorage
    {
        internal MemoryStream Raw = new(), Meta = new();
        internal byte[]? SealedPayload;
        internal int Disposals;
        internal bool FailClose, FailIntentFlush;
        public Stream Payload => Raw;
        public Stream Metadata => Meta;
        public string? FilesystemDeviceNumber => "254:1";
        public bool Published { get; private set; }
        public void FlushPayload()
        {
            if (FailIntentFlush && Raw.Length >= RawImageRestoreJournalCodec.RecordBytes &&
                BinaryPrimitives.ReadUInt32LittleEndian(Raw.ToArray().AsSpan((int)Raw.Length - RawImageRestoreJournalCodec.RecordBytes + 12)) ==
                (uint)RawImageRestoreRecordKind.WriteIntent) throw new IOException("fixture-intent-flush");
        }
        public void FlushMetadata() { }
        public void FlushDirectories() { }
        public void Publish() => Published = true;
        public void Seal()
        {
            SealedPayload = Raw.ToArray(); byte[] metadata = Meta.ToArray();
            ForceClose(); Raw = new(SealedPayload, false); Meta = new(metadata, false);
        }
        internal void ForceClose() { Raw.Dispose(); Meta.Dispose(); }
        public ValueTask DisposeAsync()
        {
            Disposals++; ForceClose();
            if (FailClose) throw new IOException("fixture-close");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReadAccess(string path, LinuxBlockReadService.HandleIdentity identity) : LinuxBlockReadService.IDeviceAccess
    {
        internal SafeFileHandle? Held;
        public SafeFileHandle? Open(string name, out LinuxBlockReadState failure)
        { failure = LinuxBlockReadState.OpenedReadOnly; return Held = File.OpenHandle(path, FileMode.Open, FileAccess.Read); }
        public bool Inspect(SafeFileHandle handle, out LinuxBlockReadService.HandleIdentity current)
        { current = identity; return !handle.IsClosed; }
    }

    private sealed class Target(LinuxBlockObservation observation) : RawImageDeviceRestoreService.ITarget
    {
        internal readonly byte[] Bytes = Enumerable.Repeat((byte)0xa5, (int)observation.Capacity).ToArray();
        internal int Writes, Disposals;
        internal bool FailPartialWrite, FailClose, CorruptReadBack, UnexpectedWriteError;
        internal TaskCompletionSource? WriteGate;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinuxBlockObservation Observation => observation;
        public Stream ReadBack => new MemoryStream(Bytes, false);
        public ValueTask RecheckAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, ulong offset, CancellationToken token)
        {
            Writes++; Entered.TrySetResult();
            if (WriteGate is not null) await WriteGate.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (UnexpectedWriteError) throw new InvalidOperationException("fixture-programming-error");
            if (FailPartialWrite)
            { bytes.Span[..512].CopyTo(Bytes.AsSpan((int)offset)); throw new IOException("fixture-partial-write"); }
            bytes.Span.CopyTo(Bytes.AsSpan((int)offset));
        }
        public void Flush() { if (CorruptReadBack) Bytes[^1] ^= 1; }
        public ValueTask DisposeAsync()
        { Disposals++; if (FailClose) throw new IOException("fixture-close"); return ValueTask.CompletedTask; }
    }

    private sealed class Fixture : LinuxRawRestoreOperation.IPlatform, IDisposable
    {
        internal readonly Storage StageStorage = new(), JournalStorage = new();
        internal LinuxRawImageStagingSession Stage = null!;
        internal LinuxBlockInventory Snapshot = null!;
        internal LinuxBlockObservation Device = null!;
        internal Target Destination = null!;
        internal RawImageDeviceRestoreRequest? Request;
        internal ReadAccess Access = null!;
        internal int Previews, Observations, Journals, Opens;
        internal Action<int>? OnObserve;
        internal bool FailJournal, RefuseOpen, ReturnTargetOnRefusal, BlockOpen, FailPreview;
        internal readonly TaskCompletionSource OpenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string path = Path.GetTempFileName();
        internal static async Task<Fixture> Create(uint sector = 512)
        {
            var f = new Fixture();
            using var metadata = new MemoryStream(Convert.FromHexString(DiskImageRawProfileVerificationServiceTests.Valid(sector).MetadataHex));
            using var payload = new MemoryStream(DiskImageRawProfileVerificationServiceTests.Raw(sector));
            var staged = await LinuxRawImageStagingService.StageAsync(metadata, payload, _ => f.StageStorage, DiskImageInspectionFixture.InspectAsync);
            var image = staged.Session!;
            f.Stage = new(f.StageStorage, image.Inspection, image.MetadataSha256, image.MetadataLength, "254:1", "254:1");
            f.Device = new("vdb", "254:16", null, LinuxBlockKind.WholeDisk, image.Image.VerifiedBytes,
                sector, sector, 2, false, "serial:RESTORE-OP-FIXTURE", LinuxBlockIdentity.UniqueInSnapshot,
                false, false, false, LinuxBlockUse.Unknown);
            var root = f.Device with { KernelName = "vda", DeviceNumber = "254:0", HardwareIdentityCandidate = "serial:OS",
                MountObserved = true, Use = LinuxBlockUse.ObservedInUse };
            var partition = root with { KernelName = "vda1", DeviceNumber = "254:1", Kind = LinuxBlockKind.Partition, ParentKernelName = "vda" };
            f.Snapshot = new(LinuxBlockInventoryState.Observed, LinuxBlockInventoryReason.NamespaceObservationOnly,
                Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), "mnt:[123]", false, new[] { f.Device, root, partition });
            f.Access = new(f.path, new(true, 254, 16, f.Device.Capacity, sector, sector, 2, false));
            f.Destination = new(f.Device);
            return f;
        }
        public ValueTask<LinuxBlockInventory> ObserveAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); OnObserve?.Invoke(++Observations);
            return ValueTask.FromResult(Snapshot with { ObservedAtUtc = DateTimeOffset.UtcNow });
        }
        public ValueTask<LinuxDiskRestoreTargetPreviewAssessment> PreviewAsync(LinuxRawImageStagingSession stage,
            LinuxBlockInventory expected, string kernelName, CancellationToken token)
        {
            Previews++;
            if (FailPreview) return ValueTask.FromResult(new LinuxDiskRestoreTargetPreviewAssessment(LinuxDiskRestoreTargetPreviewReason.TargetUnavailable));
            return LinuxDiskRestoreTargetPreviewService.CreateAsync(stage, expected, kernelName, ObserveAsync,
                (inventory, name, ct) => LinuxBlockReadService.OpenAsync(inventory, name, ObserveAsync, Access, ct), token);
        }
        public async ValueTask<RawImageRestoreJournal> CreateJournalAsync(string parent, RawImageDeviceRestoreRequest request,
            LinuxBlockInventory expected, CancellationToken token)
        {
            Assert.True(Access.Held!.IsClosed); Journals++; Request = request;
            if (FailJournal) throw new IOException("fixture-journal");
            return await RawImageRestoreJournal.CreateAsync(request, expected, (_, allow) =>
            { Assert.True(allow("254:1")); return JournalStorage; }, ObserveAsync, token);
        }
        public async ValueTask<LinuxRawRestoreOpenResult> OpenAsync(LinuxRawImageStagingSession stage,
            RawImageDeviceRestoreRequest request, RawImageRestoreJournal journal, LinuxBlockInventory expected, CancellationToken token)
        {
            Opens++; OpenEntered.TrySetResult();
            Assert.True(await journal.MatchesUnstartedAsync(request, expected, token));
            Assert.True(Access.Held!.IsClosed);
            if (BlockOpen) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RefuseOpen ? new(LinuxRawRestoreOpenState.InUse, ReturnTargetOnRefusal ? Destination : null)
                : new(LinuxRawRestoreOpenState.OpenedExclusive, Destination);
        }
        internal ValueTask<LinuxRawRestorePreparation> Prepare(TimeSpan? time = null, CancellationToken token = default,
            string? parent = null) => LinuxRawRestoreOperation.PrepareAsync(Stage, Snapshot, "vdb", parent ?? Path.GetTempPath(),
                this, time ?? LinuxRawRestoreOperation.MaximumExecutionTime, token);
        internal async Task<LinuxRawRestoreOperation> Ready(TimeSpan? time = null)
        {
            var prepared = await Prepare(time);
            Assert.Equal(LinuxRawRestoreOperationReason.ReviewReady, prepared.Reason);
            Assert.NotNull(prepared.Operation); return prepared.Operation;
        }
        internal async Task<RawImageRestoreJournalAssessment> InspectJournal()
        {
            using var metadata = new MemoryStream(JournalStorage.Meta.ToArray(), false);
            using var records = new MemoryStream(JournalStorage.Raw.ToArray(), false);
            return await RawImageRestoreJournalCodec.InspectAsync(metadata, records, Request!);
        }
        public void Dispose()
        { Access?.Held?.Dispose(); StageStorage.ForceClose(); JournalStorage.ForceClose(); File.Delete(path); }
    }

    private static ValueTask<LinuxRawRestoreExecution> Execute(LinuxRawRestoreOperation operation, CancellationToken token = default) =>
        operation.ExecuteReviewedAsync(operation.Review.OperationId, operation.Review.ReviewId, token);

    [Fact]
    public async Task SuppliedOperationBindsActualReviewSessionExecutionAndJournal()
    {
        using var f = await Fixture.Create();
        Guid id = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var prepared = await LinuxRawRestoreOperation.PrepareAsync(id, f.Stage, f.Snapshot, "vdb", Path.GetTempPath(),
            f, LinuxRawRestoreOperation.MaximumExecutionTime);
        Assert.Equal(LinuxRawRestoreOperationReason.ReviewReady, prepared.Reason);
        await using var operation = prepared.Operation!;
        Assert.Equal(id, operation.Review.OperationId);
        // The wire fixture displays only its synthetic destination. Native prepare
        // and execution retain the full native fixture inventory/ancestry context.
        var inventory = LinuxRecoveryResponses.ProjectInventory(f.Snapshot with { Devices = new[] { f.Device } });
        var review = LinuxRecoveryResponses.ProjectReview(operation.Review, inventory);
        var session = new LinuxRecoverySession(id);
        void Apply(LinuxRecoveryRole role, LinuxRecoveryMessage kind, byte[]? body = null)
        {
            using var frame = new LinuxRecoveryOperationChannel.Frame(kind, body ?? Array.Empty<byte>(), null);
            session.Apply(role, frame);
        }
        Apply(LinuxRecoveryRole.Worker, LinuxRecoveryMessage.Ready);
        Apply(LinuxRecoveryRole.Controller, LinuxRecoveryMessage.OfferImages);
        Apply(LinuxRecoveryRole.Worker, LinuxRecoveryMessage.Staging);
        Apply(LinuxRecoveryRole.Controller, LinuxRecoveryMessage.InventoryRequest);
        Apply(LinuxRecoveryRole.Worker, LinuxRecoveryMessage.Inventory, LinuxRecoveryResponses.EncodeInventory(inventory));
        Apply(LinuxRecoveryRole.Controller, LinuxRecoveryMessage.Prepare, LinuxRecoveryProtocol.Selection(inventory.ObservationId, "vdb"));
        Apply(LinuxRecoveryRole.Worker, LinuxRecoveryMessage.Review, LinuxRecoveryResponses.EncodeReview(review));
        Apply(LinuxRecoveryRole.Controller, LinuxRecoveryMessage.Confirm, LinuxRecoveryProtocol.ReviewAcknowledgment(review.ReviewId));
        Apply(LinuxRecoveryRole.Worker, LinuxRecoveryMessage.Executing);
        var execution = await operation.ExecuteReviewedAsync(id, review.ReviewId);
        Apply(LinuxRecoveryRole.Worker, LinuxRecoveryMessage.Outcome,
            LinuxRecoveryResponses.EncodeOutcome(new(review.ReviewId, execution), review));
        Assert.Equal(LinuxRecoverySessionPhase.Completed, session.State.Phase);
        Assert.Equal(RawImageDeviceRestoreState.WrittenAndReadBack, session.State.Outcome!.Execution.State);
        Assert.Equal(id, f.Request!.OperationId);
        Assert.Equal(RawImageRestoreJournalState.ReadBackVerified, (await f.InspectJournal()).State);
        Assert.True(session.State.Outcome.Execution.CleanupConfirmed);
    }

    [Fact]
    public async Task EmptySuppliedOperationRefusesBeforePreviewAndDisposesTransferredStage()
    {
        using var f = await Fixture.Create();
        var prepared = await LinuxRawRestoreOperation.PrepareAsync(Guid.Empty, f.Stage, f.Snapshot, "vdb", Path.GetTempPath(),
            f, LinuxRawRestoreOperation.MaximumExecutionTime);
        Assert.Equal(LinuxRawRestoreOperationReason.ReviewRefused, prepared.Reason);
        Assert.Null(prepared.Operation);
        Assert.Equal(0, f.Previews);
        Assert.Equal(0, f.Journals);
        Assert.Equal(0, f.Opens);
        Assert.Equal(1, f.StageStorage.Disposals);
        Assert.False(f.Stage.IsAlive);
    }

    [Fact]
    public async Task WrongSuppliedExecutionOperationCannotAcquireJournalOrWrite()
    {
        using var f = await Fixture.Create();
        Guid id = Guid.NewGuid();
        var prepared = await LinuxRawRestoreOperation.PrepareAsync(id, f.Stage, f.Snapshot, "vdb", Path.GetTempPath(),
            f, LinuxRawRestoreOperation.MaximumExecutionTime);
        await using var operation = prepared.Operation!;
        var execution = await operation.ExecuteReviewedAsync(Guid.NewGuid(), operation.Review.ReviewId);
        Assert.Equal(LinuxRawRestoreOperationReason.ReviewRefused, execution.Reason);
        Assert.Equal(0, f.Journals);
        Assert.Equal(0, f.Opens);
        Assert.Equal(0, f.Destination.Writes);
        Assert.True(execution.CleanupConfirmed);
        Assert.Equal(1, f.StageStorage.Disposals);
        Assert.False(f.Stage.IsAlive);
    }

    [Fact]
    public async Task DefaultNativePreparationNeverPromotesSyntheticObservationsIntoDeviceAuthority()
    {
        using var f = await Fixture.Create();
        // The real platform must reobserve its own boot/namespace, not trust this
        // fixture's invented identity. It refuses before any native target open.
        var result = await LinuxRawRestoreOperation.PrepareAsync(f.Stage, f.Snapshot, "vdb", Path.GetTempPath());
        Assert.Equal(LinuxRawRestoreOperationReason.ReviewRefused, result.Reason);
        Assert.Null(result.Operation); Assert.Equal(1, f.StageStorage.Disposals);
    }

    [Theory] [InlineData(512u)] [InlineData(4096u)]
    public async Task WholeLifecyclePreservesReviewBindingWritesExactBytesAndClosesEveryOwner(uint sector)
    {
        using var f = await Fixture.Create(sector); await using var operation = await f.Ready();
        Assert.Equal(0, f.Journals); Assert.Equal(0, f.Opens); Assert.Equal(0, f.Destination.Writes);
        var result = await Execute(operation);
        Assert.Equal(RawImageDeviceRestoreState.WrittenAndReadBack, result.State);
        Assert.Equal(LinuxRawRestoreOperationReason.BytesVerified, result.Reason);
        Assert.True(result.CleanupConfirmed); Assert.True(result.JournalAcquired);
        Assert.Equal(f.Device.Capacity, result.WriteResult!.AcknowledgedBytes);
        Assert.Equal(DiskImageRawProfileVerificationServiceTests.Raw(sector), f.Destination.Bytes);
        Assert.Equal(operation.Review.OperationId, f.Request!.OperationId); Assert.Equal(operation.Review.ReviewId, f.Request.ReviewId);
        Assert.Equal(RawImageRestoreJournalState.ReadBackVerified, (await f.InspectJournal()).State);
        Assert.Equal(1, f.Destination.Disposals); Assert.Equal(1, f.StageStorage.Disposals); Assert.Equal(1, f.JournalStorage.Disposals);
        Assert.False(f.Stage.IsAlive);
        Assert.False(await operation.Review.AcknowledgeAsync(operation.Review.OperationId, operation.Review.ReviewId));
        Assert.Equal(LinuxRawRestoreOperationReason.AlreadyConsumed, (await Execute(operation)).Reason);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task ForeignReviewOrOperationConsumesAttemptWithoutCreatingJournalOrOpeningTarget(bool operationId)
    {
        using var f = await Fixture.Create(); await using var operation = await f.Ready();
        var result = await operation.ExecuteReviewedAsync(operationId ? Guid.NewGuid() : operation.Review.OperationId,
            operationId ? operation.Review.ReviewId : Guid.NewGuid());
        Assert.Equal(LinuxRawRestoreOperationReason.ReviewRefused, result.Reason);
        Assert.True(result.CleanupConfirmed); Assert.Equal(0, f.Journals); Assert.Equal(0, f.Opens);
        Assert.Equal(0, f.Destination.Writes); Assert.Equal(1, f.StageStorage.Disposals);
        Assert.Equal(LinuxRawRestoreOperationReason.AlreadyConsumed, (await Execute(operation)).Reason);
    }

    [Fact]
    public async Task ChangedImageRefusesBeforeJournalAllocationAndNativeWriteClaim()
    {
        using var f = await Fixture.Create(); await using var operation = await f.Ready();
        f.StageStorage.SealedPayload![100] ^= 1;
        var result = await Execute(operation);
        Assert.Equal(LinuxRawRestoreOperationReason.ImageChanged, result.Reason);
        Assert.Equal(0, f.Journals); Assert.Equal(0, f.Opens); Assert.Equal(0, f.Destination.Writes);
        Assert.True(f.Access.Held!.IsClosed); Assert.Equal(1, f.StageStorage.Disposals);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DeviceChangeBeforeOrAfterAcknowledgementNeverOpensWritableTarget(bool gap)
    {
        using var f = await Fixture.Create(); await using var operation = await f.Ready();
        void Change() => f.Snapshot = f.Snapshot with { Devices = f.Snapshot.Devices.Select(d =>
            d.KernelName == "vdb" ? d with { DiskSequence = 99 } : d).ToArray() };
        if (!gap) Change();
        else { int observed = f.Observations; f.OnObserve = count => { if (count == observed + 2) Change(); }; }
        var result = await Execute(operation);
        Assert.Equal(gap ? LinuxRawRestoreOperationReason.SelectionChanged : LinuxRawRestoreOperationReason.ReviewRefused, result.Reason);
        Assert.Equal(0, f.Journals); Assert.Equal(0, f.Opens); Assert.Equal(0, f.Destination.Writes);
        Assert.True(result.CleanupConfirmed);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task JournalFailureOrOpenRefusalClosesStageAndNeverWrites(bool journal)
    {
        using var f = await Fixture.Create(); await using var operation = await f.Ready();
        f.FailJournal = journal; f.RefuseOpen = !journal; f.ReturnTargetOnRefusal = !journal;
        var result = await Execute(operation);
        Assert.Equal(journal ? RawImageDeviceRestoreState.Failed : RawImageDeviceRestoreState.Refused, result.State);
        Assert.Equal(!journal, result.JournalAcquired); Assert.True(result.CleanupConfirmed);
        Assert.Equal(journal ? 0 : 1, f.Opens); Assert.Equal(0, f.Destination.Writes);
        Assert.Equal(journal ? 0 : 1, f.Destination.Disposals); Assert.Equal(1, f.StageStorage.Disposals);
        Assert.Equal(journal ? 0 : 1, f.JournalStorage.Disposals);
    }

    [Fact]
    public async Task ConcurrentDuplicateDoesNotCancelOrDisposeRunningOperation()
    {
        using var f = await Fixture.Create(); await using var operation = await f.Ready();
        f.Destination.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<LinuxRawRestoreExecution> first = Execute(operation).AsTask(); await f.Destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = await Execute(operation);
        Assert.Equal(LinuxRawRestoreOperationReason.AlreadyConsumed, duplicate.Reason);
        Assert.False(duplicate.CleanupConfirmed); Assert.True(f.Stage.IsAlive); Assert.Equal(0, f.Destination.Disposals);
        f.Destination.WriteGate.SetResult();
        Assert.Equal(RawImageDeviceRestoreState.WrittenAndReadBack, (await first).State);
        Assert.Equal(1, f.Journals); Assert.Equal(1, f.Opens); Assert.Equal(1, f.Destination.Disposals);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CancellationBeforePreparationOrExecutionReleasesTransferredStage(bool prepared)
    {
        using var f = await Fixture.Create(); using var cancel = new CancellationTokenSource();
        if (!prepared)
        {
            cancel.Cancel(); var result = await f.Prepare(token: cancel.Token);
            Assert.Equal(LinuxRawRestoreOperationReason.Interrupted, result.Reason); Assert.Null(result.Operation);
        }
        else
        {
            await using var operation = await f.Ready(); cancel.Cancel();
            Assert.Equal(RawImageDeviceRestoreState.Interrupted, (await Execute(operation, cancel.Token)).State);
        }
        Assert.Equal(1, f.StageStorage.Disposals); Assert.Equal(0, f.Opens); Assert.Equal(0, f.Journals);
    }

    [Fact]
    public async Task DisposalCancelsPendingAcquisitionAndClosesJournalAndStage()
    {
        using var f = await Fixture.Create(); var operation = await f.Ready(); f.BlockOpen = true;
        var running = Execute(operation).AsTask(); await f.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await operation.DisposeAsync(); await operation.DisposeAsync();
        var result = await running;
        Assert.Equal(RawImageDeviceRestoreState.Interrupted, result.State); Assert.True(result.CleanupConfirmed);
        Assert.True(result.JournalAcquired); Assert.Equal(1, f.StageStorage.Disposals); Assert.Equal(1, f.JournalStorage.Disposals);
        Assert.Equal(0, f.Destination.Writes);
    }

    [Fact]
    public async Task DeadlineRetainsUncertainRangeAndInterruptedJournalInsteadOfSuccess()
    {
        using var f = await Fixture.Create(); await using var operation = await f.Ready(TimeSpan.FromSeconds(1));
        f.Destination.WriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = Execute(operation).AsTask(); await f.Destination.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(LinuxRawRestoreOperationReason.TimedOut, result.Reason);
        Assert.Equal(RawImageDeviceRestoreState.Interrupted, result.State); Assert.True(result.CleanupConfirmed);
        Assert.Equal(0UL, result.WriteResult!.AcknowledgedBytes); Assert.Equal(f.Device.Capacity, result.WriteResult.UncertainLength);
        Assert.True(result.WriteResult.TerminalRecordWritten);
        Assert.Equal(RawImageRestoreJournalState.Interrupted, (await f.InspectJournal()).State);
        Assert.Equal(1, f.Destination.Disposals);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task PartialWriteOrCorruptReadBackPreservesFailureEvidence(bool partial)
    {
        using var f = await Fixture.Create(); await using var operation = await f.Ready();
        f.Destination.FailPartialWrite = partial; f.Destination.CorruptReadBack = !partial;
        var result = await Execute(operation);
        Assert.Equal(RawImageDeviceRestoreState.Failed, result.State); Assert.True(result.CleanupConfirmed);
        Assert.Equal(partial ? 0UL : f.Device.Capacity, result.WriteResult!.AcknowledgedBytes);
        Assert.Equal(partial ? f.Device.Capacity : 0UL, result.WriteResult.UncertainLength);
        Assert.Equal(RawImageRestoreJournalState.Failed, (await f.InspectJournal()).State);
    }

    [Fact]
    public async Task UnconfirmedIntentFlushDoesNotStartWritingOrReportDurableTerminal()
    {
        using var f = await Fixture.Create(); await using var operation = await f.Ready(); f.JournalStorage.FailIntentFlush = true;
        var result = await Execute(operation);
        Assert.Equal(RawImageDeviceRestoreState.Failed, result.State); Assert.Equal(0, f.Destination.Writes);
        Assert.False(result.WriteResult!.TerminalRecordWritten); Assert.True(result.CleanupConfirmed);
    }

    [Theory] [InlineData("stage")] [InlineData("journal")] [InlineData("target")]
    public async Task CleanupFailureAttemptsEveryOwnerAndCannotReportOverallSuccess(string resource)
    {
        using var f = await Fixture.Create(); await using var operation = await f.Ready();
        f.StageStorage.FailClose = resource == "stage"; f.JournalStorage.FailClose = resource == "journal";
        f.Destination.FailClose = resource == "target";
        var result = await Execute(operation);
        Assert.Equal(RawImageDeviceRestoreState.Failed, result.State);
        Assert.Equal(LinuxRawRestoreOperationReason.CleanupUnconfirmed, result.Reason); Assert.False(result.CleanupConfirmed);
        Assert.Equal(1, f.StageStorage.Disposals); Assert.Equal(1, f.JournalStorage.Disposals);
        Assert.Equal(resource == "target" ? 2 : 1, f.Destination.Disposals);
        Assert.Equal(f.Device.Capacity, result.WriteResult!.AcknowledgedBytes);
    }

    [Fact]
    public async Task UnexpectedExecutionFaultStillClosesAllOwnersBeforePropagating()
    {
        using var f = await Fixture.Create(); await using var operation = await f.Ready(); f.Destination.UnexpectedWriteError = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(operation).AsTask());
        Assert.Equal(1, f.Destination.Disposals); Assert.Equal(1, f.StageStorage.Disposals); Assert.Equal(1, f.JournalStorage.Disposals);
    }

    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(90000)]
    public async Task InvalidExecutionBoundsRefuseBeforePreviewAndReleaseStage(int seconds)
    {
        using var f = await Fixture.Create(); var result = await f.Prepare(TimeSpan.FromSeconds(seconds));
        Assert.Null(result.Operation); Assert.Equal(LinuxRawRestoreOperationReason.ReviewRefused, result.Reason);
        Assert.Equal(0, f.Previews); Assert.Equal(1, f.StageStorage.Disposals);
    }

    [Fact]
    public async Task RelativeJournalParentAndUnavailablePreviewNeverRetainStage()
    {
        using (var f = await Fixture.Create())
        { Assert.Null((await f.Prepare(parent: "relative")).Operation); Assert.Equal(0, f.Previews); Assert.Equal(1, f.StageStorage.Disposals); }
        using (var f = await Fixture.Create())
        { f.FailPreview = true; Assert.Null((await f.Prepare()).Operation); Assert.Equal(1, f.StageStorage.Disposals); }
    }

    [Fact]
    public async Task AbandonedReviewCanBeDisposedConcurrentlyWithoutOpeningOrWriting()
    {
        using var f = await Fixture.Create(); var operation = await f.Ready();
        await Task.WhenAll(operation.DisposeAsync().AsTask(), operation.DisposeAsync().AsTask());
        Assert.Equal(1, f.StageStorage.Disposals); Assert.True(f.Access.Held!.IsClosed);
        Assert.Equal(0, f.Journals); Assert.Equal(0, f.Opens);
        Assert.Equal(LinuxRawRestoreOperationReason.AlreadyConsumed, (await Execute(operation)).Reason);
    }
}
