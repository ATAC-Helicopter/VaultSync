using System.Security.Cryptography;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

internal enum RawImageDeviceRestoreState { Refused, Interrupted, Failed, WrittenAndReadBack }
internal enum RawImageRestoreRecordKind { Started, WriteIntent, RangeFlushed, ReadBackVerified, Interrupted, Failed }

// Correlation data only. Neither a review UUID nor an observation is device authority.
internal sealed record RawImageDeviceRestoreRequest(Guid OperationId, Guid ReviewId,
    Guid ImageId, Guid CaptureId, ulong Generation, string ManifestRoot, LinuxBlockObservation Target);
internal sealed record RawImageRestoreRecord(RawImageRestoreRecordKind Kind,
    RawImageDeviceRestoreRequest Request, ulong Offset, ulong Length, string? Sha256 = null);
internal sealed record RawImageDeviceRestoreResult(RawImageDeviceRestoreState State,
    ulong AcknowledgedBytes, ulong UncertainOffset, ulong UncertainLength, bool TerminalRecordWritten);

/// <summary>
/// Internal write/read-back engine. No opener, elevated endpoint or public API.
/// A future trusted broker must supply exact device authority and exclusive owned
/// target/stage access; current callers are disposable-lab and fault fixtures only.
/// </summary>
internal static class RawImageDeviceRestoreService
{
    internal const int ChunkBytes = 128 * 1024;
    internal interface ITarget : IAsyncDisposable
    {
        LinuxBlockObservation Observation { get; }
        Stream ReadBack { get; }
        ValueTask RecheckAsync(CancellationToken token);
        ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, ulong offset, CancellationToken token);
        void Flush();
    }
    internal interface IJournal
    {
        // Return ONLY after this exact record is durably committed, not buffered.
        // Storage is trusted/local/private, separately acquired outside the target.
        ValueTask AppendDurableAsync(RawImageRestoreRecord record, CancellationToken token);
    }

    internal static async ValueTask<RawImageDeviceRestoreResult> RunAsync(
        LinuxRawImageStagingSession stage, RawImageDeviceRestoreRequest request,
        ITarget target, IJournal journal, CancellationToken token = default)
    {
        RawImageDeviceRestoreResult result;
        try { result = await ExecuteAsync(stage, request, target, journal, token).ConfigureAwait(false); }
        catch
        {
            await TryReleaseAsync(target).ConfigureAwait(false);
            throw;
        }
        if (await TryReleaseAsync(target).ConfigureAwait(false)) return result;
        bool recorded = await RecordFailureAsync(journal, request, RawImageDeviceRestoreState.Failed,
            result.UncertainOffset, result.UncertainLength).ConfigureAwait(false);
        return result with { State = RawImageDeviceRestoreState.Failed, TerminalRecordWritten = recorded };
    }

    private static async ValueTask<bool> TryReleaseAsync(ITarget target)
    {
        try { await target.DisposeAsync().ConfigureAwait(false); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        { return false; }
    }

    private static async ValueTask<RawImageDeviceRestoreResult> ExecuteAsync(
        LinuxRawImageStagingSession stage, RawImageDeviceRestoreRequest request,
        ITarget target, IJournal journal, CancellationToken token)
    {
        ulong acknowledged = 0, uncertainOffset = 0, uncertainLength = 0;
        bool started = false;
        RawImageDeviceRestoreState state;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!Matches(stage, request, target))
                return new(RawImageDeviceRestoreState.Refused, 0, 0, 0, false);
            stage.Metadata.Position = 0; stage.Payload.Position = 0;
            var verified = await DiskImageRawProfileVerificationService.VerifyAsync(stage.Metadata, stage.Payload, token)
                .ConfigureAwait(false);
            if (!SameImage(verified, request))
                return new(RawImageDeviceRestoreState.Refused, 0, 0, 0, false);
            await target.RecheckAsync(token).ConfigureAwait(false);
            await journal.AppendDurableAsync(new(RawImageRestoreRecordKind.Started, request, 0, 0), token)
                .ConfigureAwait(false);
            started = true;
            stage.Payload.Position = 0;
            byte[] buffer = new byte[ChunkBytes];
            while (acknowledged < stage.Image.VerifiedBytes)
            {
                int count = (int)Math.Min((ulong)buffer.Length, stage.Image.VerifiedBytes - acknowledged);
                await stage.Payload.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                await target.RecheckAsync(token).ConfigureAwait(false);
                string hash = Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, count)));
                await journal.AppendDurableAsync(new(RawImageRestoreRecordKind.WriteIntent, request,
                    acknowledged, (ulong)count, hash), token).ConfigureAwait(false);
                // From this point a failed/cancelled write may have modified ANY
                // byte of this interval. Never infer zero bytes from an exception.
                uncertainOffset = acknowledged; uncertainLength = (ulong)count;
                token.ThrowIfCancellationRequested();
                await target.WriteAsync(buffer.AsMemory(0, count), acknowledged, token).ConfigureAwait(false);
                await target.RecheckAsync(token).ConfigureAwait(false);
                target.Flush();
                await target.RecheckAsync(token).ConfigureAwait(false);
                await journal.AppendDurableAsync(new(RawImageRestoreRecordKind.RangeFlushed, request,
                    acknowledged, (ulong)count, hash), token).ConfigureAwait(false);
                acknowledged += (ulong)count;
                uncertainOffset = 0; uncertainLength = 0;
            }
            stage.Metadata.Position = 0; target.ReadBack.Position = 0;
            var readBack = await DiskImageRawProfileVerificationService.VerifyAsync(stage.Metadata, target.ReadBack, token)
                .ConfigureAwait(false);
            if (!SameImage(readBack, request)) throw new IOException("raw-restore.read-back-refused");
            await target.RecheckAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await journal.AppendDurableAsync(new(RawImageRestoreRecordKind.ReadBackVerified, request,
                0, acknowledged), token).ConfigureAwait(false);
            return new(RawImageDeviceRestoreState.WrittenAndReadBack, acknowledged, 0, 0, true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { state = RawImageDeviceRestoreState.Interrupted; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException)
        { state = RawImageDeviceRestoreState.Failed; }

        bool recorded = started && await RecordFailureAsync(journal, request, state,
            uncertainOffset, uncertainLength).ConfigureAwait(false);
        return new(state, acknowledged, uncertainOffset, uncertainLength, recorded);
    }

    private static async ValueTask<bool> RecordFailureAsync(IJournal journal, RawImageDeviceRestoreRequest request,
        RawImageDeviceRestoreState state, ulong offset, ulong length)
    {
        try
        {
            // Caller cancellation must not prevent recording an interrupted write.
            await journal.AppendDurableAsync(new(state == RawImageDeviceRestoreState.Interrupted
                ? RawImageRestoreRecordKind.Interrupted : RawImageRestoreRecordKind.Failed,
                request, offset, length), CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ObjectDisposedException)
        { return false; }
    }

    private static bool SameImage(DiskImageRawProfileVerificationAssessment assessment, RawImageDeviceRestoreRequest request) =>
        assessment.State == DiskImageRawProfileVerificationState.ContentAndLayoutVerified &&
        assessment.Image is { } image && image.ImageId == request.ImageId && image.CaptureId == request.CaptureId &&
        image.Generation == request.Generation && image.ManifestRoot == request.ManifestRoot;

    private static bool Matches(LinuxRawImageStagingSession stage, RawImageDeviceRestoreRequest request, ITarget target)
    {
        var device = request.Target;
        var image = stage.Image;
        return stage.IsAlive && !stage.Payload.CanWrite && !stage.Metadata.CanWrite &&
            request.OperationId != Guid.Empty && request.ReviewId != Guid.Empty &&
            request.ImageId == image.ImageId && request.CaptureId == image.CaptureId &&
            request.Generation == image.Generation && request.ManifestRoot == image.ManifestRoot &&
            request.ManifestRoot is { Length: 64 } && request.ManifestRoot.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            target.Observation == device && device.Kind == LinuxBlockKind.WholeDisk && device.ParentKernelName is null &&
            device.Identity == LinuxBlockIdentity.UniqueInSnapshot && device.DiskSequence is > 0 &&
            !device.IsReadOnly && !device.MountObserved && !device.SwapObserved && !device.HasHolders &&
            device.Use == LinuxBlockUse.Unknown && device.Capacity == image.VerifiedBytes &&
            image.DeclaredOrigin is { } origin && device.LogicalSectorBytes == origin.Layout.LogicalSectorBytes &&
            device.PhysicalSectorBytes == origin.PhysicalSectorBytes;
    }
}
