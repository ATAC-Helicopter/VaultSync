namespace VaultSync.Core.Services;

internal enum LinuxRecoverySessionPhase
{
    AwaitingReady, Ready, ImagesOffered, Staging, InventoryRequested, InventoryReceived,
    PrepareRequested, Reviewed, Confirmed, Executing, Completed, Refused, Faulted
}

/// <summary>
/// Single-use development conversation boundary, not a supervisor or writable capability.
/// Pair with exactly one channel constructed with the same operation ID. That channel
/// authenticates headers, operation and sequences; Frame alone cannot prove that pairing.
/// The caller must serialize the transcript: commit each successfully sent event
/// before applying its dependent reply, and validate incoming events before use.
/// The state lock alone does not order concurrent channel sends/receives. A send
/// failure requires Fail; do not retry this session on another connection.
/// OfferImages still requires the channel's descriptor handoff; callers own and dispose
/// frames/sources and must cancel and clean up owned work on cancellation or failure.
/// A new inventory/selection requires a new session. No observation proves freshness,
/// offline state, installed-worker trust or human authority.
/// </summary>
internal sealed class LinuxRecoverySession
{
    internal sealed record Snapshot(LinuxRecoverySessionPhase Phase, bool CancellationRequested,
        LinuxRecoveryResponses.Inventory? Inventory, LinuxRecoveryResponses.Review? Review,
        ulong AcknowledgedBytes, LinuxRecoveryResponses.Outcome? Outcome,
        LinuxRawRestoreOperationReason? Refusal);

    private readonly object gate = new();
    private readonly Guid operation;
    private LinuxRecoverySessionPhase phase = LinuxRecoverySessionPhase.AwaitingReady;
    private bool cancellationRequested;
    private LinuxRecoveryResponses.Inventory? inventory;
    private LinuxRecoveryResponses.Review? review;
    private Guid selectedObservation;
    private string? selectedKernel;
    private ulong acknowledged;
    private LinuxRecoveryResponses.Outcome? outcome;
    private LinuxRawRestoreOperationReason? refusal;

    internal LinuxRecoverySession(Guid operation)
    {
        if (operation == Guid.Empty) throw Invalid();
        this.operation = operation;
    }

    internal Snapshot State { get { lock (gate) return ReadState(); } }

    internal Snapshot Apply(LinuxRecoveryRole sender, LinuxRecoveryOperationChannel.Frame frame)
    {
        lock (gate)
        {
            // A terminal result is immutable, including when late/replayed traffic arrives.
            if (Terminal) throw Invalid();
            try
            {
                if (frame is null || frame.Sources is not null && frame.Kind != LinuxRecoveryMessage.OfferImages)
                    throw Invalid();
                _ = LinuxRecoveryProtocol.EncodeHeader(sender, frame.Kind, operation, 1, frame.Body.Length);
                if (sender == LinuxRecoveryRole.Controller) ApplyController(frame.Kind, frame.Body.Span);
                else ApplyWorker(frame.Kind, frame.Body.Span);
                return ReadState();
            }
            catch (InvalidDataException)
            {
                phase = LinuxRecoverySessionPhase.Faulted;
                throw;
            }
        }
    }

    /// <summary>
    /// Channel loss/send failure/owner failure has no trustworthy synthetic Outcome.
    /// Preserve last durable count/review; the owner must stop and clean up separately.
    /// </summary>
    internal Snapshot Fail()
    {
        lock (gate)
        {
            if (!Terminal) phase = LinuxRecoverySessionPhase.Faulted;
            return ReadState();
        }
    }

    private void ApplyController(LinuxRecoveryMessage kind, ReadOnlySpan<byte> body)
    {
        if (kind == LinuxRecoveryMessage.Heartbeat) return;
        if (cancellationRequested) throw Invalid();
        if (kind == LinuxRecoveryMessage.Cancel)
        {
            cancellationRequested = true;
            return;
        }
        switch (kind)
        {
            case LinuxRecoveryMessage.OfferImages:
                Require(LinuxRecoverySessionPhase.Ready);
                phase = LinuxRecoverySessionPhase.ImagesOffered;
                break;
            case LinuxRecoveryMessage.InventoryRequest:
                Require(LinuxRecoverySessionPhase.Staging);
                phase = LinuxRecoverySessionPhase.InventoryRequested;
                break;
            case LinuxRecoveryMessage.Prepare:
                Require(LinuxRecoverySessionPhase.InventoryReceived);
                var selection = LinuxRecoveryProtocol.DecodeSelection(body);
                if (inventory is null || inventory.State != Models.LinuxBlockInventoryState.Observed ||
                    inventory.HasUnresolvedSwaps || selection.Observation != inventory.ObservationId ||
                    !inventory.Devices.Any(device => device.KernelName == selection.KernelName)) throw Invalid();
                selectedObservation = selection.Observation;
                selectedKernel = selection.KernelName;
                phase = LinuxRecoverySessionPhase.PrepareRequested;
                break;
            case LinuxRecoveryMessage.Confirm:
                Require(LinuxRecoverySessionPhase.Reviewed);
                if (LinuxRecoveryProtocol.DecodeReviewAcknowledgment(body) != review!.ReviewId) throw Invalid();
                phase = LinuxRecoverySessionPhase.Confirmed;
                break;
            default: throw Invalid();
        }
    }

    private void ApplyWorker(LinuxRecoveryMessage kind, ReadOnlySpan<byte> body)
    {
        switch (kind)
        {
            case LinuxRecoveryMessage.Ready:
                Require(LinuxRecoverySessionPhase.AwaitingReady);
                phase = LinuxRecoverySessionPhase.Ready;
                break;
            case LinuxRecoveryMessage.Staging:
                Require(LinuxRecoverySessionPhase.ImagesOffered);
                phase = LinuxRecoverySessionPhase.Staging;
                break;
            case LinuxRecoveryMessage.Inventory:
                Require(LinuxRecoverySessionPhase.InventoryRequested);
                inventory = LinuxRecoveryResponses.DecodeInventory(body);
                phase = LinuxRecoverySessionPhase.InventoryReceived;
                break;
            case LinuxRecoveryMessage.Review:
                Require(LinuxRecoverySessionPhase.PrepareRequested);
                review = LinuxRecoveryResponses.DecodeReview(body, operation, inventory!, selectedObservation, selectedKernel!);
                phase = LinuxRecoverySessionPhase.Reviewed;
                break;
            case LinuxRecoveryMessage.Executing:
                // Cancellation can race execution after an acknowledgment. It must not
                // erase an already-started writer or permit a pre-write refusal instead.
                Require(LinuxRecoverySessionPhase.Confirmed);
                phase = LinuxRecoverySessionPhase.Executing;
                break;
            case LinuxRecoveryMessage.Progress:
                Require(LinuxRecoverySessionPhase.Executing);
                acknowledged = LinuxRecoveryResponses.DecodeProgress(body, review!, acknowledged).AcknowledgedBytes;
                break;
            case LinuxRecoveryMessage.Outcome:
                Require(LinuxRecoverySessionPhase.Executing);
                outcome = LinuxRecoveryResponses.DecodeOutcome(body, review!, acknowledged);
                acknowledged = outcome.Execution.WriteResult?.AcknowledgedBytes ?? acknowledged;
                phase = LinuxRecoverySessionPhase.Completed;
                break;
            case LinuxRecoveryMessage.Refused:
                if (phase == LinuxRecoverySessionPhase.Executing ||
                    phase == LinuxRecoverySessionPhase.AwaitingReady && !cancellationRequested) throw Invalid();
                refusal = LinuxRecoveryResponses.DecodeRefused(body);
                phase = LinuxRecoverySessionPhase.Refused;
                break;
            default: throw Invalid();
        }
    }

    private bool Terminal => phase is LinuxRecoverySessionPhase.Completed or LinuxRecoverySessionPhase.Refused or LinuxRecoverySessionPhase.Faulted;
    private void Require(LinuxRecoverySessionPhase expected) { if (phase != expected) throw Invalid(); }
    private Snapshot ReadState() => new(phase, cancellationRequested, inventory, review, acknowledged, outcome, refusal);
    private static InvalidDataException Invalid() => new("recovery-session.invalid-state");
}
