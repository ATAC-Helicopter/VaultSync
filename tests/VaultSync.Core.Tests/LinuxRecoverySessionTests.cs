#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using VaultSync.Core.Models;
using VaultSync.Core.Services;
using Xunit;
using Message = VaultSync.Core.Services.LinuxRecoveryMessage;
using Phase = VaultSync.Core.Services.LinuxRecoverySessionPhase;
using Responses = VaultSync.Core.Services.LinuxRecoveryResponses;

namespace VaultSync.Core.Tests;

public sealed class LinuxRecoverySessionTests
{
    private static readonly Guid Operation = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Observation = Guid.Parse("11112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid ReviewId = Guid.Parse("22112233-4455-6677-8899-aabbccddeeff");
    private const ulong Total = 128 * 1024 + 512;
    private static Responses.Device Disk => new("sda", null, 8, 0, Total, 17, 512, 4096,
        LinuxBlockKind.WholeDisk, LinuxBlockIdentity.UniqueInSnapshot, LinuxBlockUse.Unknown, false, false, false, false);
    private static Responses.Inventory Inventory => new(Observation, DateTimeOffset.UnixEpoch,
        LinuxBlockInventoryState.Observed, LinuxBlockInventoryReason.NamespaceObservationOnly, false, new[] { Disk });
    private static Responses.Review Review => new(Operation, ReviewId, Guid.Parse("33112233-4455-6677-8899-aabbccddeeff"),
        Guid.Parse("44112233-4455-6677-8899-aabbccddeeff"), 1, new string('a', 64), DateTimeOffset.UnixEpoch, Total, Disk, Observation);
    private static LinuxRecoverySession.Snapshot Send(LinuxRecoverySession session, LinuxRecoveryRole role, Message kind, byte[]? body = null)
    {
        using var frame = new LinuxRecoveryOperationChannel.Frame(kind, body ?? Array.Empty<byte>(), null);
        return session.Apply(role, frame);
    }
    private static LinuxRecoverySession.Snapshot Controller(LinuxRecoverySession session, Message kind, byte[]? body = null) =>
        Send(session, LinuxRecoveryRole.Controller, kind, body);
    private static LinuxRecoverySession.Snapshot Worker(LinuxRecoverySession session, Message kind, byte[]? body = null) =>
        Send(session, LinuxRecoveryRole.Worker, kind, body);
    private static LinuxRecoverySession At(Phase phase)
    {
        var session = new LinuxRecoverySession(Operation);
        if (phase == Phase.AwaitingReady) return session;
        Worker(session, Message.Ready);
        if (phase == Phase.Ready) return session;
        Controller(session, Message.OfferImages);
        if (phase == Phase.ImagesOffered) return session;
        Worker(session, Message.Staging);
        if (phase == Phase.Staging) return session;
        Controller(session, Message.InventoryRequest);
        if (phase == Phase.InventoryRequested) return session;
        Worker(session, Message.Inventory, Responses.EncodeInventory(Inventory));
        if (phase == Phase.InventoryReceived) return session;
        Controller(session, Message.Prepare, LinuxRecoveryProtocol.Selection(Observation, "sda"));
        if (phase == Phase.PrepareRequested) return session;
        Worker(session, Message.Review, Responses.EncodeReview(Review));
        if (phase == Phase.Reviewed) return session;
        Controller(session, Message.Confirm, LinuxRecoveryProtocol.ReviewAcknowledgment(ReviewId));
        if (phase == Phase.Confirmed) return session;
        Worker(session, Message.Executing);
        Assert.Equal(Phase.Executing, phase);
        return session;
    }
    private static byte[] Success => Responses.EncodeOutcome(new(ReviewId, new(RawImageDeviceRestoreState.WrittenAndReadBack,
        LinuxRawRestoreOperationReason.BytesVerified, new(RawImageDeviceRestoreState.WrittenAndReadBack, Total, 0, 0, true), true, true)), Review);
    private static byte[] Refusal => Responses.EncodeRefused(LinuxRawRestoreOperationReason.ReviewRefused);

    [Fact]
    public void CompleteConversationBindsReviewAndRetainsFinalDurableEvidence()
    {
        var session = At(Phase.Executing);
        Worker(session, Message.Progress, Responses.EncodeProgress(new(ReviewId, 131072), Review));
        var state = Worker(session, Message.Outcome, Success);
        Assert.Equal(Phase.Completed, state.Phase);
        Assert.Equal(Operation, state.Review!.OperationId);
        Assert.Equal(Observation, state.Inventory!.ObservationId);
        Assert.Equal(Total, state.AcknowledgedBytes);
        Assert.Equal(RawImageDeviceRestoreState.WrittenAndReadBack, state.Outcome!.Execution.State);
        Assert.Null(state.Refusal);
        Assert.Equal(state, session.Fail());
        Assert.Throws<InvalidDataException>(() => Worker(session, Message.Outcome, Success));
        Assert.Equal(state, session.State);
    }

    [Theory]
    [InlineData(Phase.AwaitingReady, Message.OfferImages, true)]
    [InlineData(Phase.Ready, Message.Ready, false)]
    [InlineData(Phase.ImagesOffered, Message.OfferImages, true)]
    [InlineData(Phase.Staging, Message.Executing, false)]
    [InlineData(Phase.InventoryRequested, Message.InventoryRequest, true)]
    [InlineData(Phase.InventoryReceived, Message.Staging, false)]
    [InlineData(Phase.Reviewed, Message.InventoryRequest, true)]
    [InlineData(Phase.Executing, Message.Staging, false)]
    public void OutOfOrderOrReplayedEventsPoisonSession(object phaseValue, object kindValue, bool controller)
    {
        var phase = (Phase)phaseValue;
        var kind = (Message)kindValue;
        var session = At(phase);
        var before = session.State;
        Assert.Throws<InvalidDataException>(() => Send(session,
            controller ? LinuxRecoveryRole.Controller : LinuxRecoveryRole.Worker, kind));
        Assert.Equal(before with { Phase = Phase.Faulted }, session.State);
        Assert.Throws<InvalidDataException>(() => Controller(session, Message.Heartbeat));
    }

    [Fact]
    public void WrongDirectionAndMalformedBodyCannotAdvance()
    {
        var wrongDirection = At(Phase.Ready);
        Assert.Throws<InvalidDataException>(() => Worker(wrongDirection, Message.OfferImages));
        Assert.Equal(Phase.Faulted, wrongDirection.State.Phase);
        var malformed = At(Phase.InventoryRequested);
        Assert.Throws<InvalidDataException>(() => Worker(malformed, Message.Inventory, new byte[32]));
        Assert.Null(malformed.State.Inventory);
        Assert.Equal(Phase.Faulted, malformed.State.Phase);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void PrepareMustMatchOutstandingObservationAndSelector(bool wrongObservation)
    {
        var session = At(Phase.InventoryReceived);
        var before = session.State;
        byte[] selection = LinuxRecoveryProtocol.Selection(wrongObservation ? Guid.NewGuid() : Observation,
            wrongObservation ? "sda" : "sdb");
        Assert.Throws<InvalidDataException>(() => Controller(session, Message.Prepare, selection));
        Assert.Equal(before with { Phase = Phase.Faulted }, session.State);
    }

    [Theory]
    [InlineData(0)] [InlineData(288)] [InlineData(120)]
    public void ReviewMustMatchOperationObservationAndSelectedDevice(int offset)
    {
        var session = At(Phase.PrepareRequested);
        byte[] body = Responses.EncodeReview(Review);
        body[offset] ^= 1;
        Assert.Throws<InvalidDataException>(() => Worker(session, Message.Review, body));
        Assert.Null(session.State.Review);
        Assert.Equal(Phase.Faulted, session.State.Phase);
    }

    [Fact]
    public void ConfirmRequiresExactReviewAndCannotReplay()
    {
        var session = At(Phase.Reviewed);
        Assert.Throws<InvalidDataException>(() => Controller(session, Message.Confirm,
            LinuxRecoveryProtocol.ReviewAcknowledgment(Guid.NewGuid())));
        Assert.Equal(ReviewId, session.State.Review!.ReviewId);
        var replay = At(Phase.Confirmed);
        Assert.Throws<InvalidDataException>(() => Controller(replay, Message.Confirm,
            LinuxRecoveryProtocol.ReviewAcknowledgment(ReviewId)));
        Assert.Equal(Phase.Faulted, replay.State.Phase);
    }

    [Fact]
    public void CancelBeforeConfirmRetainsInFlightReviewButForbidsConfirmation()
    {
        var session = At(Phase.PrepareRequested);
        Controller(session, Message.Cancel);
        Worker(session, Message.Review, Responses.EncodeReview(Review));
        Assert.True(session.State.CancellationRequested);
        Assert.Equal(Phase.Reviewed, session.State.Phase);
        Controller(session, Message.Heartbeat);
        Assert.Throws<InvalidDataException>(() => Controller(session, Message.Confirm,
            LinuxRecoveryProtocol.ReviewAcknowledgment(ReviewId)));
        Assert.True(session.State.CancellationRequested);
        Assert.Equal(ReviewId, session.State.Review!.ReviewId);
    }

    [Theory]
    [InlineData(Phase.AwaitingReady)] [InlineData(Phase.Ready)] [InlineData(Phase.InventoryRequested)]
    [InlineData(Phase.PrepareRequested)] [InlineData(Phase.Reviewed)] [InlineData(Phase.Confirmed)]
    public void PreExecutionCancellationRequiresExplicitTerminalRefusal(object phaseValue)
    {
        var phase = (Phase)phaseValue;
        var session = At(phase);
        Controller(session, Message.Cancel);
        Assert.Equal(phase, session.State.Phase);
        var terminal = Worker(session, Message.Refused, Refusal);
        Assert.Equal(Phase.Refused, terminal.Phase);
        Assert.True(terminal.CancellationRequested);
        Assert.Null(terminal.Outcome);
        Assert.Throws<InvalidDataException>(() => Worker(session, Message.Ready));
        Assert.Equal(terminal, session.State);
    }

    [Fact]
    public void CancelAfterConfirmAllowsExecutionRaceAndTruthfulInterruptedOutcome()
    {
        var session = At(Phase.Confirmed);
        Controller(session, Message.Cancel);
        Worker(session, Message.Executing);
        Controller(session, Message.Heartbeat);
        Worker(session, Message.Progress, Responses.EncodeProgress(new(ReviewId, 131072), Review));
        byte[] interrupted = Responses.EncodeOutcome(new(ReviewId, new(RawImageDeviceRestoreState.Interrupted,
            LinuxRawRestoreOperationReason.Interrupted, new(RawImageDeviceRestoreState.Interrupted, 131072, 131072, 512, true), true, true)), Review, 131072);
        var terminal = Worker(session, Message.Outcome, interrupted);
        Assert.True(terminal.CancellationRequested);
        Assert.Equal(RawImageDeviceRestoreState.Interrupted, terminal.Outcome!.Execution.State);
        Assert.Equal(512UL, terminal.Outcome.Execution.WriteResult!.UncertainLength);
    }

    [Theory]
    [InlineData(Phase.Reviewed)] [InlineData(Phase.Confirmed)]
    public void SuccessCannotArriveWithoutExecutingEvenAfterCancellation(object phaseValue)
    {
        var phase = (Phase)phaseValue;
        var session = At(phase);
        Controller(session, Message.Cancel);
        Assert.Throws<InvalidDataException>(() => Worker(session, Message.Outcome, Success));
        Assert.Null(session.State.Outcome);
    }

    [Fact]
    public void PostExecutionRefusalCannotEraseDurableProgress()
    {
        var session = At(Phase.Executing);
        Worker(session, Message.Progress, Responses.EncodeProgress(new(ReviewId, 131072), Review));
        Controller(session, Message.Cancel);
        var before = session.State;
        Assert.Throws<InvalidDataException>(() => Worker(session, Message.Refused, Refusal));
        Assert.Equal(before with { Phase = Phase.Faulted }, session.State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ProgressMustRemainMonotonicAndReviewBound(bool wrongReview)
    {
        var session = At(Phase.Executing);
        Worker(session, Message.Progress, Responses.EncodeProgress(new(ReviewId, 131072), Review));
        byte[] body = Responses.EncodeProgress(new(ReviewId, 0), Review);
        if (wrongReview) body[0] ^= 1;
        Assert.Throws<InvalidDataException>(() => Worker(session, Message.Progress, body));
        Assert.Equal(131072UL, session.State.AcknowledgedBytes);
        Assert.Null(session.State.Outcome);
    }

    [Fact]
    public void ChannelFailurePreservesEvidenceWithoutFabricatingCleanupOrOutcome()
    {
        var session = At(Phase.Executing);
        Worker(session, Message.Progress, Responses.EncodeProgress(new(ReviewId, 131072), Review));
        var before = session.State;
        Assert.Equal(before with { Phase = Phase.Faulted }, session.Fail());
        Assert.Null(session.State.Outcome);
        Assert.Equal(session.State, session.Fail());
    }

    [Theory]
    [InlineData(Phase.Staging)] [InlineData(Phase.InventoryRequested)] [InlineData(Phase.PrepareRequested)]
    [InlineData(Phase.Executing)]
    public void RepeatedLiveStatusPreservesPendingPhaseAndDurableCount(object phaseValue)
    {
        var phase = (Phase)phaseValue;
        var session = At(phase);
        if (phase == Phase.Executing)
            Worker(session, Message.Progress, Responses.EncodeProgress(new(ReviewId, 131072), Review));
        var before = session.State;
        var status = phase == Phase.Executing ? Message.Executing : Message.Staging;
        Assert.Equal(before, Worker(session, status));
        Assert.Equal(before, Worker(session, status));
    }

    [Fact]
    public void ConcurrentTerminalEventsAcceptExactlyOneAndPreserveIt()
    {
        var session = At(Phase.Executing);
        int accepted = 0, rejected = 0;
        Parallel.Invoke(() => Finish(), () => Finish());
        Assert.Equal(1, accepted);
        Assert.Equal(1, rejected);
        Assert.Equal(Phase.Completed, session.State.Phase);
        void Finish()
        {
            try { Worker(session, Message.Outcome, Success); Interlocked.Increment(ref accepted); }
            catch (InvalidDataException) { Interlocked.Increment(ref rejected); }
        }
    }
}
