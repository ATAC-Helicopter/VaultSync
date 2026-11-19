using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace VaultSync.Core.Services;

/// <summary>New private operation tree. Retains journals/failures; deletes no original image.</summary>
internal sealed class LinuxRecoveryStorageLease : IDisposable
{
    internal const string Store = "/srv/vaultsync-recovery";
    private readonly SafeFileHandle store, operations, operation, staging, journal;
    private readonly LinuxRecoveryNative.Identity operationIdentity, stagingIdentity;
    private readonly Guid id;
    private readonly string directory;
    private int completed;
    internal bool CompletionAttempted => Volatile.Read(ref completed) != 0;
    internal string StagingParent => directory + "/staging";
    internal string JournalParent => directory + "/journal";

    private LinuxRecoveryStorageLease(Guid id, string directory, SafeFileHandle store, SafeFileHandle operations,
        SafeFileHandle operation, SafeFileHandle staging, SafeFileHandle journal)
    {
        this.id = id; this.directory = directory; this.store = store; this.operations = operations;
        this.operation = operation; this.staging = staging; this.journal = journal;
        operationIdentity = LinuxRecoveryNative.Stat(operation); stagingIdentity = LinuxRecoveryNative.Stat(staging);
    }

    internal static LinuxRecoveryStorageLease Create(Guid id)
    {
        LinuxRecoveryNative.RequireSupported();
        if (!LinuxRecoveryNative.RootIdentity || id == Guid.Empty) throw LinuxRecoveryMediaInstallation.Invalid();
        var store = LinuxRecoveryNative.OpenDirectory(Store, false);
        SafeFileHandle? operations = null, operation = null, staging = null, journal = null;
        try
        {
            var storeState = LinuxRecoveryNative.Stat(store);
            if (!Private(storeState) || !LinuxRecoveryNative.Ext4(store)) throw LinuxRecoveryMediaInstallation.Invalid();
            string root = Store + "/operations";
            if (!Directory.Exists(root)) LinuxRecoveryNative.MakeDirectory(store, "operations");
            operations = LinuxRecoveryNative.OpenDirectory(root, false);
            if (!Private(LinuxRecoveryNative.Stat(operations)) || !LinuxRecoveryNative.Ext4(operations) ||
                Directory.EnumerateFileSystemEntries(root).Take(17).Count() >= 16) throw new IOException("recovery-storage.retention-bound");
            // Never adopt a prior operation. The directory and every writable
            // descendant are new, exclusive, root0700 and outside controller access.
            string name = id.ToString("N"), path = root + "/" + name;
            LinuxRecoveryNative.MakeDirectory(operations, name);
            operation = LinuxRecoveryNative.OpenDirectory(path, false);
            LinuxRecoveryNative.MakeDirectory(operation, "staging"); LinuxRecoveryNative.MakeDirectory(operation, "journal");
            staging = LinuxRecoveryNative.OpenDirectory(path + "/staging", false);
            journal = LinuxRecoveryNative.OpenDirectory(path + "/journal", false);
            foreach (var handle in new[] { operation, staging, journal })
                if (!Private(LinuxRecoveryNative.Stat(handle)) || !LinuxRecoveryNative.Ext4(handle) ||
                    LinuxRecoveryNative.Stat(handle).DeviceMajor != storeState.DeviceMajor ||
                    LinuxRecoveryNative.Stat(handle).DeviceMinor != storeState.DeviceMinor) throw LinuxRecoveryMediaInstallation.Invalid();
            LinuxRecoveryNative.Sync(operation); LinuxRecoveryNative.Sync(operations);
            return new(id, path, store, operations, operation, staging, journal);
        }
        catch
        {
            journal?.Dispose(); staging?.Dispose(); operation?.Dispose(); operations?.Dispose(); store.Dispose();
            // Partial own trees retain uncertainty. Never turn failed creation
            // into a sweep of a pre-existing or ambiguously identified path.
            throw;
        }
    }

    internal void Complete(LinuxRecoverySession.Snapshot state)
    {
        if (Interlocked.CompareExchange(ref completed, 1, 0) != 0) throw new InvalidOperationException("recovery-storage.already-completed");
        RequireOwned(directory, operation, operationIdentity);
        bool success = Successful(state);
        WriteReceipt("receipt.json", state, true, success);
        if (!success) return; // Failed/interrupted/uncertain staged bytes and journals remain explicitly owned evidence.
        RequireOwned(StagingParent, staging, stagingIdentity);
        string[] children = Directory.EnumerateFileSystemEntries(StagingParent).Take(2).ToArray();
        if (children.Length != 1) throw new IOException("recovery-storage.staging-layout");
        string child = Path.GetFileName(children[0]);
        if (child.Length != 32 || !Guid.TryParseExact(child, "N", out var value) || value == Guid.Empty || child != child.ToLowerInvariant())
            throw new IOException("recovery-storage.staging-layout");
        using var held = LinuxRecoveryNative.OpenDirectory(children[0], false);
        var childIdentity = LinuxRecoveryNative.Stat(held);
        if (!Private(childIdentity) || !SameDevice(childIdentity, stagingIdentity)) throw new IOException("recovery-storage.staging-layout");
        string[] entries = Directory.EnumerateFileSystemEntries(children[0]).Take(3).ToArray();
        if (!entries.Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(new[] { "image.metadata", "payload.raw" }))
            throw new IOException("recovery-storage.staging-layout");
        var files = new List<(string Name, FileStream Stream, LinuxRecoveryNative.Identity State)>();
        try
        {
            foreach (string entry in entries)
            {
                var file = LinuxRecoveryNative.OpenFile(entry, false);
                files.Add((Path.GetFileName(entry), file, LinuxRecoveryNative.Stat(file.SafeFileHandle)));
            }
            ValidateStagingFiles(files.ToDictionary(f => f.Name, f => f.State, StringComparer.Ordinal), stagingIdentity, state.Review!.OverwriteLength);
            // Verify ALL entries before the first unlink. Exact held identities
            // plus root-only ancestors prevent aliases/hardlinks/symlink traversal.
            RequireOwned(children[0], held, childIdentity);
            foreach (var file in files)
                if (LinuxRecoveryNative.Stat(file.Stream.SafeFileHandle) != file.State ||
                    LinuxRecoveryNative.StatPath(children[0] + "/" + file.Name) != file.State) throw new IOException("recovery-storage.changed-staging");
            foreach (var file in files) LinuxRecoveryNative.Remove(held, file.Name, false);
            LinuxRecoveryNative.Sync(held); LinuxRecoveryNative.Remove(staging, child, true); LinuxRecoveryNative.Sync(staging);
            LinuxRecoveryNative.Remove(operation, "staging", true); LinuxRecoveryNative.Sync(operation);
            WriteReceipt("receipt.final.json", state, false, false);
        }
        finally { foreach (var file in files) file.Stream.Dispose(); }
    }

    internal static void ValidateStagingFiles(IReadOnlyDictionary<string, LinuxRecoveryNative.Identity> files,
        LinuxRecoveryNative.Identity parent, ulong length)
    {
        if (files.Count != 2 || !files.TryGetValue("image.metadata", out var metadata) || !files.TryGetValue("payload.raw", out var payload) ||
            metadata.Size is 0 or > 16777216 || payload.Size != length || length == 0 ||
            new[] { metadata, payload }.Any(s => s.Mode != 0x8100 || s.Owner != 0 || s.Links != 1 || !SameDevice(s, parent)) ||
            metadata.Inode == payload.Inode) throw new IOException("recovery-storage.unsafe-staging");
    }
    internal static bool Successful(LinuxRecoverySession.Snapshot state) => state.Phase == LinuxRecoverySessionPhase.Completed &&
        state.Review is { } review && state.Outcome?.Execution is { State: RawImageDeviceRestoreState.WrittenAndReadBack,
            Reason: LinuxRawRestoreOperationReason.BytesVerified, CleanupConfirmed: true, JournalAcquired: true,
            WriteResult: { State: RawImageDeviceRestoreState.WrittenAndReadBack, TerminalRecordWritten: true,
                UncertainOffset: 0, UncertainLength: 0 } result } && result.AcknowledgedBytes == review.OverwriteLength;
    private void WriteReceipt(string name, LinuxRecoverySession.Snapshot state, bool retained, bool pending)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, sourceCommit = DiskWorkerBuildIdentity.SourceCommit, operation = id,
            phase = state.Phase.ToString(), review = state.Review?.ReviewId, image = state.Review?.ImageId,
            capture = state.Review?.CaptureId, manifestRoot = state.Review?.ManifestRoot,
            acknowledgedBytes = state.AcknowledgedBytes, execution = state.Outcome?.Execution,
            stagingRetained = retained, stagingCleanupPending = pending
        });
        if (bytes.Length > 16384) throw new IOException("recovery-storage.receipt-bound");
        using var file = new FileStream(LinuxRecoveryNative.Open(directory + "/" + name,
            2 | 0x40 | 0x80 | 0x20000 | 0x80000, 0x180), FileAccess.ReadWrite);
        file.Write(bytes); file.Flush(true); LinuxRecoveryNative.Sync(operation);
    }
    private static bool SameDevice(LinuxRecoveryNative.Identity first, LinuxRecoveryNative.Identity second) =>
        first.DeviceMajor == second.DeviceMajor && first.DeviceMinor == second.DeviceMinor;
    private static bool Private(LinuxRecoveryNative.Identity value) => value.Mode == 0x41c0 && value.Owner == 0;
    private static void RequireOwned(string path, SafeFileHandle held, LinuxRecoveryNative.Identity initial)
    {
        var current = LinuxRecoveryNative.Stat(held); var named = LinuxRecoveryNative.StatPath(path);
        if (!Private(current) || !Private(named) || !SameDevice(current, initial) || !SameDevice(named, initial) ||
            current.Inode != initial.Inode || named.Inode != initial.Inode) throw new IOException("recovery-storage.changed-owner");
    }
    public void Dispose() { journal.Dispose(); staging.Dispose(); operation.Dispose(); operations.Dispose(); store.Dispose(); }
}
