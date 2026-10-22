using System.Security.Cryptography;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Verifies while staging raw parts, then reconstructs exclusively into a new temporary regular file.</summary>
public static class DiskImageFileReconstructionService
{
    public const string OutputFileName = "reconstructed.raw";
    public const ulong MaximumFixtureBytes = 1024UL * 1024 * 1024;
    public const int MaximumFixtureParts = 32;
    private const int BufferLength = 128 * 1024;

    public static async ValueTask<DiskImageFileReconstructionAssessment> ReconstructAsync(
        Stream metadata, IReadOnlyDictionary<Guid, Stream> payloads, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(payloads);
        if (payloads.Count > MaximumFixtureParts || payloads.Any(pair =>
            pair.Key == Guid.Empty || pair.Value is null || !pair.Value.CanRead))
            return new(DiskImageFileReconstructionState.Invalid, "reconstruct.invalid-payload-set");

        string? workspace = null;
        ulong written = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            workspace = Directory.CreateTempSubdirectory("vaultsync-draft-reconstruction-").FullName;
            await using var snapshots = new SnapshotSet();
            var staged = new Dictionary<Guid, Stream>();
            var sources = new Dictionary<Guid, Stream>();
            var budget = new StagingBudget();
            foreach (var pair in payloads)
            {
                var file = new FileStream(Path.Combine(workspace, pair.Key.ToString("N") + ".raw"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferLength,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                snapshots.Files.Add(file);
                staged.Add(pair.Key, file);
                sources.Add(pair.Key, new StagingReadStream(pair.Value, file, budget));
            }
            var verification = await DiskImageManifestVerificationService.VerifyWithinCapacityAsync(
                metadata, sources, MaximumFixtureBytes, cancellationToken).ConfigureAwait(false);
            if (verification.State != DiskImageManifestVerificationState.ContentVerified)
                return new(verification.State switch
                {
                    DiskImageManifestVerificationState.Unsupported => DiskImageFileReconstructionState.Unsupported,
                    DiskImageManifestVerificationState.Incomplete => DiskImageFileReconstructionState.Incomplete,
                    DiskImageManifestVerificationState.Corrupt => DiskImageFileReconstructionState.Corrupt,
                    _ => DiskImageFileReconstructionState.Invalid
                }, verification.Reason == "image.capacity-policy-limit"
                    ? "reconstruct.fixture-size-limit" : verification.Reason, Workspace: workspace);
            DiskImageCoveragePlan plan = verification.VerifiedCoverage!;
            if (plan.Capacity > MaximumFixtureBytes)
                return new(DiskImageFileReconstructionState.Invalid, "reconstruct.fixture-size-limit", Workspace: workspace);
            foreach (FileStream file in snapshots.Files)
                file.Flush(flushToDisk: true);

            cancellationToken.ThrowIfCancellationRequested();
            await using var output = new FileStream(Path.Combine(workspace, OutputFileName), FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, BufferLength, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[BufferLength];
            foreach (DiskImageExtent extent in plan.Extents)
            {
                Stream? source = extent.Kind == DiskImageExtentKind.Stored ? staged[extent.PartId!.Value] : null;
                if (source is not null)
                    source.Position = (long)extent.PartOffset;
                else
                    Array.Clear(buffer);
                ulong remaining = extent.Length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = (int)Math.Min((ulong)buffer.Length, remaining);
                    if (source is not null)
                    {
                        count = await source.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        if (count == 0)
                            return new(DiskImageFileReconstructionState.Failed, "reconstruct.truncated-snapshot", written, workspace);
                    }
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    contentHash.AppendData(buffer, 0, count);
                    remaining -= (ulong)count;
                    written += (ulong)count;
                }
            }
            output.Flush(flushToDisk: true);
            byte[] expected = contentHash.GetHashAndReset();
            output.Position = 0;
            byte[] observed = await SHA256.HashDataAsync(output, cancellationToken).ConfigureAwait(false);
            if (output.Length != (long)plan.Capacity || !CryptographicOperations.FixedTimeEquals(expected, observed))
                return new(DiskImageFileReconstructionState.Failed, "reconstruct.readback-mismatch", written, workspace);
            if (verification.DeclaredOrigin is not null)
            {
                var layout = await DiskImageGptInspectionService.InspectAsync(output, plan.Capacity,
                    plan.LogicalSectorBytes, cancellationToken).ConfigureAwait(false);
                DiskImageGptLayout expectedLayout = verification.DeclaredOrigin.Layout;
                if (layout.Layout is null || layout.Layout.DiskId != expectedLayout.DiskId ||
                    layout.Layout.MetadataSha256 != expectedLayout.MetadataSha256 ||
                    layout.Layout.FirstUsableLba != expectedLayout.FirstUsableLba || layout.Layout.LastUsableLba != expectedLayout.LastUsableLba ||
                    !layout.Layout.Partitions.SequenceEqual(expectedLayout.Partitions))
                    return new(DiskImageFileReconstructionState.Corrupt, "reconstruct.layout-mismatch", written, workspace);
            }
            return new(DiskImageFileReconstructionState.FileFixtureReconstructed, "reconstruct.file-fixture-only",
                written, workspace, verification.ManifestRoot, Convert.ToHexStringLower(observed));
        }
        catch (FixtureLimitException)
        {
            return new(DiskImageFileReconstructionState.Invalid, "reconstruct.fixture-size-limit", written, workspace);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(DiskImageFileReconstructionState.Interrupted, "reconstruct.interrupted", written, workspace);
        }
        catch (IOException)
        {
            return new(DiskImageFileReconstructionState.Failed, "reconstruct.io-failure", written, workspace);
        }
        catch (UnauthorizedAccessException)
        {
            return new(DiskImageFileReconstructionState.Failed, "reconstruct.storage-denied", written, workspace);
        }
    }

    private sealed class FixtureLimitException : IOException { }
    private sealed class StagingBudget { public ulong Bytes; }

    private sealed class SnapshotSet : IAsyncDisposable
    {
        public List<FileStream> Files { get; } = [];
        public async ValueTask DisposeAsync()
        {
            Exception? failure = null;
            foreach (FileStream file in Files)
            {
                try { await file.DisposeAsync().ConfigureAwait(false); }
                catch (IOException exception) { failure ??= exception; }
            }
            if (failure is not null)
                throw failure;
        }
    }

    private sealed class StagingReadStream(Stream source, Stream snapshot, StagingBudget budget) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int requested = (int)Math.Min((ulong)Math.Min(buffer.Length, BufferLength), MaximumFixtureBytes - budget.Bytes + 1);
            int count = await source.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
            if ((ulong)count > MaximumFixtureBytes - budget.Bytes)
                throw new FixtureLimitException();
            await snapshot.WriteAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
            budget.Bytes += (ulong)count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
