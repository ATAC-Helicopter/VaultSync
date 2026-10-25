using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

internal sealed record DiskGptInspectionWorkerResult(DiskImageInspectionWorkerOutcome Outcome,
    DiskImageGptInspectionAssessment? Assessment = null);

internal static class DiskGptInspectionWorkerClient
{
    internal static Task<DiskGptInspectionWorkerResult> InspectAsync(Stream source, uint sector, CancellationToken token = default) =>
        InspectAsync(source, sector, Path.Combine(AppContext.BaseDirectory, "VaultSync.DiskWorker"), TimeSpan.FromSeconds(130), token);

    // Only local process tests can override the fixed adjacent worker/deadline.
    internal static async Task<DiskGptInspectionWorkerResult> InspectAsync(Stream source, uint sector,
        string worker, TimeSpan timeout, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!source.CanRead || !source.CanSeek || source.CanWrite)
            return new(DiskImageInspectionWorkerOutcome.Failed);
        Guid operation = Guid.NewGuid(); byte[] request;
        try { request = DiskGptInspectionProtocol.Request(operation, source.Length, sector); }
        catch (InvalidDataException) { return new(DiskImageInspectionWorkerOutcome.Failed); }
        using var empty = new MemoryStream(Array.Empty<byte>(), false);
        var result = await DiskImageInspectionWorkerClient.RunAsync(
            new(empty, source, operation, 0, source.Length), request, worker, timeout, "VSGPRSL1"u8.ToArray(),
            (input, prefix, sources, cancellation) => ReadResultAsync(input, prefix, sources, sector, cancellation), token).ConfigureAwait(false);
        return new(result.Outcome, result.Observation);
    }

    internal static async Task<DiskImageGptInspectionAssessment> ReadResultAsync(Stream input, byte[] prefix,
        DiskImageInspectionWorkerClient.Sources sources, uint sector, CancellationToken token)
    {
        byte[] header = new byte[DiskGptInspectionProtocol.HeaderBytes]; prefix.CopyTo(header, 0);
        await input.ReadExactlyAsync(header.AsMemory(8), token).ConfigureAwait(false);
        byte[] result = new byte[DiskGptInspectionProtocol.ResultLength(header)]; header.CopyTo(result, 0);
        await input.ReadExactlyAsync(result.AsMemory(header.Length), token).ConfigureAwait(false);
        return DiskGptInspectionProtocol.DecodeResult(result, sources.Operation, sources.PayloadLength, sector);
    }
}
