using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

internal enum LinuxRecoveryCaptureExportState { Failed, Interrupted, PublicationUnconfirmed, PublishedVerified }
internal sealed record LinuxRecoveryCaptureExportAssessment(LinuxRecoveryCaptureExportState State);

/// <summary>Create-only local-interface copy. Never grants access to or modifies a private original.</summary>
internal static class LinuxRecoveryCaptureExport
{
    internal const string Root = "/srv/vaultsync-recovery-exports";
    internal const uint ControllerGroup = 60999;

    internal interface ICopy : IAsyncDisposable
    {
        Stream Metadata { get; }
        Stream Payload { get; }
        bool Published { get; }
        void Seal();
        void Recheck();
        void Publish();
    }

    internal static async Task<LinuxRecoveryCaptureExportAssessment> CopyAsync(
        LinuxRawImageCaptureRequest request, LinuxRawImageCaptureAssessment capture,
        Stream metadata, Stream payload, ICopy copy, Action recheckOriginal,
        Func<Stream, Stream, CancellationToken, Task<DiskImageInspectionWorkerResult>> inspect,
        CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (capture.State != LinuxRawImageCaptureState.PublishedVerified ||
                capture.ImageDirectoryName != request.ImageId.ToString("N") || !RawImageRestoreJournalCodec.Hash(capture.ManifestRoot) ||
                capture.CopiedBytes is 0 or > DiskImageRawProfileVerificationService.MaximumCapacity ||
                !Source(metadata, DiskImageRawProfileVerificationService.MaximumMetadataBytes) ||
                !Source(payload, capture.CopiedBytes) || (ulong)payload.Length != capture.CopiedBytes ||
                ReferenceEquals(metadata, payload) || !copy.Metadata.CanWrite || !copy.Payload.CanWrite ||
                copy.Metadata.Length != 0 || copy.Payload.Length != 0 ||
                ReferenceEquals(copy.Metadata, metadata) || ReferenceEquals(copy.Metadata, payload) ||
                ReferenceEquals(copy.Payload, metadata) || ReferenceEquals(copy.Payload, payload) ||
                ReferenceEquals(copy.Payload, copy.Metadata)) throw Refused();
            recheckOriginal(); copy.Recheck();
            string metadataHash = await CopyExactAsync(metadata, copy.Metadata, token).ConfigureAwait(false);
            string payloadHash = await CopyExactAsync(payload, copy.Payload, token).ConfigureAwait(false);
            recheckOriginal();
            copy.Seal();
            if (copy.Metadata.CanWrite || copy.Payload.CanWrite) throw Refused();
            copy.Metadata.Position = 0; copy.Payload.Position = 0;
            var result = await inspect(copy.Metadata, copy.Payload, token).ConfigureAwait(false);
            if (result.Outcome != DiskImageInspectionWorkerOutcome.Completed || result.Observation is not { } observed)
                throw Refused();
            Guid operation = Guid.NewGuid();
            var verified = DiskImageInspectionProtocol.DecodeResult(DiskImageInspectionProtocol.Result(operation, observed),
                operation, payload.Length);
            if (verified.State != DiskImageRawProfileVerificationState.ContentAndLayoutVerified ||
                verified.ImageId != request.ImageId || verified.CaptureId != request.CaptureId || verified.Generation != request.Generation ||
                verified.ManifestRoot != capture.ManifestRoot || verified.VerifiedBytes != capture.CopiedBytes || verified.PayloadSha256 != payloadHash ||
                await DiskImageByteIntegrity.HashExactAsync(copy.Metadata, (ulong)metadata.Length, token).ConfigureAwait(false) != metadataHash ||
                await DiskImageByteIntegrity.HashExactAsync(copy.Payload, capture.CopiedBytes, token).ConfigureAwait(false) != payloadHash)
                throw Refused();
            recheckOriginal(); copy.Recheck(); token.ThrowIfCancellationRequested();
            copy.Publish(); // No cancellation beyond atomic exposure; failed parent fsync is unconfirmed.
            return new(LinuxRecoveryCaptureExportState.PublishedVerified);
        }
        catch (OperationCanceledException) when (copy.Published)
        { return new(LinuxRecoveryCaptureExportState.PublicationUnconfirmed); }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !copy.Published)
        { return new(LinuxRecoveryCaptureExportState.Interrupted); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        { return new(copy.Published ? LinuxRecoveryCaptureExportState.PublicationUnconfirmed : LinuxRecoveryCaptureExportState.Failed); }
    }

    private static bool Source(Stream stream, ulong maximum) => stream.CanRead && stream.CanSeek && !stream.CanWrite &&
        stream.Length > 0 && (ulong)stream.Length <= maximum;

    private static async Task<string> CopyExactAsync(Stream source, Stream target, CancellationToken token)
    {
        source.Position = 0;
        long length = source.Length, copied = 0;
        byte[] buffer = new byte[128 * 1024];
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        while (copied < length)
        {
            token.ThrowIfCancellationRequested();
            int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), token).ConfigureAwait(false);
            if (read == 0) throw Refused();
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read); copied += read;
        }
        if (source.Length != length || await source.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0) throw Refused();
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Both held repository ancestries are admitted before any creation; no caller path.</summary>
    internal static Reservation Reserve(LinuxRawImageCaptureRequest request, ulong capacity,
        LinuxBlockInventory expected, LinuxBlockInventory fresh, string sourceName)
    {
        LinuxRecoveryNative.RequireSupported();
        if (!LinuxRecoveryNative.RootIdentity || request.ImageId == Guid.Empty ||
            capacity is 0 or > DiskImageRawProfileVerificationService.MaximumCapacity) throw Refused();
        var original = LinuxRecoveryNative.OpenDirectory(LinuxRecoveryCaptureHost.OutputParent, false);
        SafeFileHandle? exports = null, workspace = null;
        LinuxRawImageStorage? storage = null;
        try
        {
            exports = LinuxRecoveryNative.OpenDirectory(Root, false);
            var originalState = LinuxRecoveryNative.Stat(original); var exportState = LinuxRecoveryNative.Stat(exports);
            if (!Private(originalState) || !ExportRoot(exportState) || !LinuxRecoveryNative.Ext4(original) || !LinuxRecoveryNative.Ext4(exports) ||
                !LinuxCaptureRepositoryAdmission.Allows(expected, fresh, sourceName, Device(originalState)) ||
                !LinuxCaptureRepositoryAdmission.Allows(expected, fresh, sourceName, Device(exportState))) throw Refused();
            if (Directory.EnumerateFileSystemEntries(Root).Take(17).Count() >= 16) throw Refused();
            ulong required = checked(capacity + DiskImageRawProfileVerificationService.MaximumMetadataBytes);
            if (!Space(original, SameDevice(originalState, exportState) ? checked(required * 2) : required) || !Space(exports, required)) throw Refused();
            string temporary = ".vaultsync-export-incomplete-" + Guid.NewGuid().ToString("N");
            LinuxRecoveryNative.MakeDirectory(exports, temporary);
            string path = Root + "/" + temporary;
            workspace = LinuxRecoveryNative.OpenDirectory(path, false);
            if (!Private(LinuxRecoveryNative.Stat(workspace)) || !SameDevice(exportState, LinuxRecoveryNative.Stat(workspace))) throw Refused();
            LinuxRecoveryNative.Sync(exports);
            storage = LinuxRawImageStorage.CreateCapture(path, request.ImageId, capacity,
                number => LinuxCaptureRepositoryAdmission.Allows(expected, fresh, sourceName, number));
            var reservation = new Reservation(request.ImageId, original, exports, workspace, temporary, storage);
            original = null!; exports = null; workspace = null; storage = null;
            return reservation;
        }
        finally
        {
            if (storage is not null) storage.DisposeAsync().AsTask().GetAwaiter().GetResult();
            workspace?.Dispose(); exports?.Dispose(); original?.Dispose();
        }
    }

    internal sealed class Reservation : ICopy
    {
        private readonly Guid id;
        private readonly SafeFileHandle original, exports, workspace;
        private readonly LinuxRecoveryNative.Identity originalState, exportState, workspaceState;
        private readonly string temporary;
        private readonly LinuxRawImageStorage storage;
        private bool sealedCopy;
        internal Reservation(Guid id, SafeFileHandle original, SafeFileHandle exports, SafeFileHandle workspace,
            string temporary, LinuxRawImageStorage storage)
        {
            this.id = id; this.original = original; this.exports = exports; this.workspace = workspace;
            this.temporary = temporary; this.storage = storage;
            originalState = LinuxRecoveryNative.Stat(original); exportState = LinuxRecoveryNative.Stat(exports);
            workspaceState = LinuxRecoveryNative.Stat(workspace);
        }
        public Stream Metadata => storage.Metadata;
        public Stream Payload => storage.Payload;
        public bool Published { get; private set; }
        public void Recheck()
        {
            if (!SameDirectory(originalState, LinuxRecoveryNative.Stat(original)) ||
                !SameDirectory(exportState, LinuxRecoveryNative.Stat(exports)) ||
                !SameDirectory(workspaceState, LinuxRecoveryNative.Stat(workspace)) ||
                !SameDirectory(originalState, LinuxRecoveryNative.StatPath(LinuxRecoveryCaptureHost.OutputParent)) ||
                !SameDirectory(exportState, LinuxRecoveryNative.StatPath(Root)) ||
                !SameDirectory(workspaceState, LinuxRecoveryNative.StatPath(Root + "/" + temporary))) throw Refused();
        }
        public void Seal() { storage.FlushPayload(); storage.FlushMetadata(); storage.Seal(); storage.FlushDirectories(); sealedCopy = true; }
        public void Publish()
        {
            if (!sealedCopy || Published) throw Refused();
            Recheck();
            // Inner publication remains hidden behind the root0700 outer workspace.
            storage.Publish();
            string name = id.ToString("N");
            using var child = LinuxRecoveryNative.OpenDirectory(Root + "/" + temporary + "/" + name, false);
            CheckCopyNames(child, name, false);
            foreach (var stream in new[] { (FileStream)storage.Metadata, (FileStream)storage.Payload })
            {
                var state = LinuxRecoveryNative.Stat(stream.SafeFileHandle);
                if (!OriginalFile(state) || !SameDevice(exportState, state)) throw Refused();
                LinuxRecoveryNative.SetGroup(stream.SafeFileHandle, ControllerGroup, 0x120); //0440, copies only
                LinuxRecoveryNative.Sync(stream.SafeFileHandle);
            }
            LinuxRecoveryNative.SetGroup(child, ControllerGroup, 0x1c8); //0710, copies only
            LinuxRecoveryNative.Sync(child); LinuxRecoveryNative.Sync(workspace);
            Recheck(); CheckCopyNames(child, name, true);
            if (Rename(workspace, name, exports, name, 1) != 0) throw Refused();
            Published = true;
            LinuxRecoveryNative.Sync(exports); LinuxRecoveryNative.Sync(workspace);
        }
        private void CheckCopyNames(SafeFileHandle child, string name, bool shared)
        {
            string path = Root + "/" + temporary + "/" + name;
            var held = LinuxRecoveryNative.Stat(child);
            if (held != storage.HeldDirectoryIdentity || held != LinuxRecoveryNative.StatPath(path) ||
                !(shared ? SharedDirectory(held) : Private(held)) || !SameDevice(exportState, held)) throw Refused();
            var metadata = LinuxRecoveryNative.Stat(((FileStream)storage.Metadata).SafeFileHandle);
            var payload = LinuxRecoveryNative.Stat(((FileStream)storage.Payload).SafeFileHandle);
            if (metadata != LinuxRecoveryNative.StatPath(path + "/image.metadata") ||
                payload != LinuxRecoveryNative.StatPath(path + "/payload.raw") || metadata.Inode == payload.Inode ||
                !SameDevice(held, metadata) || !SameDevice(held, payload) ||
                !(shared ? SharedFile(metadata) && SharedFile(payload) : OriginalFile(metadata) && OriginalFile(payload))) throw Refused();
        }
        internal Original OpenOriginal()
        {
            Recheck();
            return Original.Open(id, originalState);
        }
        public async ValueTask DisposeAsync()
        {
            try { await storage.DisposeAsync().ConfigureAwait(false); }
            finally { workspace.Dispose(); exports.Dispose(); original.Dispose(); }
        }
    }

    internal sealed class Original : IDisposable
    {
        private readonly SafeFileHandle directory;
        private readonly string path;
        private readonly LinuxRecoveryNative.Identity directoryState, metadataState, payloadState;
        internal FileStream Metadata { get; }
        internal FileStream Payload { get; }
        private Original(string path, SafeFileHandle directory, FileStream metadata, FileStream payload)
        {
            this.path = path; this.directory = directory; Metadata = metadata; Payload = payload;
            directoryState = LinuxRecoveryNative.Stat(directory);
            metadataState = LinuxRecoveryNative.Stat(metadata.SafeFileHandle); payloadState = LinuxRecoveryNative.Stat(payload.SafeFileHandle);
        }
        internal static Original Open(Guid id, LinuxRecoveryNative.Identity parent)
        {
            string path = LinuxRecoveryCaptureHost.OutputParent + "/" + id.ToString("N");
            var directory = LinuxRecoveryNative.OpenDirectory(path, false);
            FileStream? metadata = null, payload = null;
            try
            {
                if (!Private(LinuxRecoveryNative.Stat(directory)) || !SameDevice(parent, LinuxRecoveryNative.Stat(directory))) throw Refused();
                metadata = ReadFile(path + "/image.metadata"); payload = ReadFile(path + "/payload.raw");
                var first = LinuxRecoveryNative.Stat(metadata.SafeFileHandle); var second = LinuxRecoveryNative.Stat(payload.SafeFileHandle);
                if (!OriginalFile(first) || !OriginalFile(second) || !SameDevice(parent, first) || !SameDevice(parent, second) || first.Inode == second.Inode) throw Refused();
                return new(path, directory, metadata, payload);
            }
            catch { payload?.Dispose(); metadata?.Dispose(); directory.Dispose(); throw; }
        }
        internal void Recheck()
        {
            if (LinuxRecoveryNative.Stat(directory) != directoryState || LinuxRecoveryNative.StatPath(path) != directoryState ||
                LinuxRecoveryNative.Stat(Metadata.SafeFileHandle) != metadataState || LinuxRecoveryNative.StatPath(path + "/image.metadata") != metadataState ||
                LinuxRecoveryNative.Stat(Payload.SafeFileHandle) != payloadState || LinuxRecoveryNative.StatPath(path + "/payload.raw") != payloadState) throw Refused();
        }
        public void Dispose() { try { Metadata.Dispose(); } finally { try { Payload.Dispose(); } finally { directory.Dispose(); } } }
    }

    private static FileStream ReadFile(string path)
    {
        var handle = LinuxRecoveryNative.Open(path, 0x800 | 0x20000 | 0x80000);
        try { return new(handle, FileAccess.Read, 1, false); } catch { handle.Dispose(); throw; }
    }
    internal static bool OriginalFile(LinuxRecoveryNative.Identity value) => value.Mode == 0x8100 && value.Owner == 0 && value.Group == 0 && value.Links == 1;
    internal static bool SharedFile(LinuxRecoveryNative.Identity value) => value.Mode == 0x8120 && value.Owner == 0 && value.Group == ControllerGroup && value.Links == 1;
    internal static bool ExportRoot(LinuxRecoveryNative.Identity value) => value.Mode == 0x41e8 && value.Owner == 0 && value.Group == ControllerGroup;
    internal static bool SharedDirectory(LinuxRecoveryNative.Identity value) => value.Mode == 0x41c8 && value.Owner == 0 && value.Group == ControllerGroup;
    private static bool Private(LinuxRecoveryNative.Identity value) => value.Mode == 0x41c0 && value.Owner == 0 && value.Group == 0;
    private static bool SameDevice(LinuxRecoveryNative.Identity a, LinuxRecoveryNative.Identity b) => a.DeviceMajor == b.DeviceMajor && a.DeviceMinor == b.DeviceMinor;
    private static bool SameDirectory(LinuxRecoveryNative.Identity a, LinuxRecoveryNative.Identity b) => SameDevice(a, b) && a.Inode == b.Inode && a.Mode == b.Mode && a.Owner == b.Owner && a.Group == b.Group;
    private static string Device(LinuxRecoveryNative.Identity state) => state.DeviceMajor.ToString(CultureInfo.InvariantCulture) + ":" + state.DeviceMinor.ToString(CultureInfo.InvariantCulture);
    private static bool Space(SafeFileHandle handle, ulong required) => StatFs(handle, out var fs) == 0 && fs.BlockSize > 0 && fs.Available <= ulong.MaxValue / (ulong)fs.BlockSize && fs.Available * (ulong)fs.BlockSize >= required;
    private static IOException Refused() => new("recovery-capture.export-refused");
    [StructLayout(LayoutKind.Explicit, Size = 120)] private struct Filesystem { [FieldOffset(8)] public long BlockSize; [FieldOffset(32)] public ulong Available; }
    [DllImport("libc", EntryPoint = "fstatfs")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int StatFs(SafeFileHandle handle, out Filesystem fs);
    [DllImport("libc", EntryPoint = "renameat2")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Rename(SafeFileHandle source, string sourceName, SafeFileHandle target, string targetName, uint flags);
}
