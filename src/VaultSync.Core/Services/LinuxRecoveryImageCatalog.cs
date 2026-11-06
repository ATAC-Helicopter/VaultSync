namespace VaultSync.Core.Services;

/// <summary>Unprivileged discovery hints only. Selection still requires independent image verification.</summary>
internal static class LinuxRecoveryImageCatalog
{
    internal enum State { Available, Unavailable, Failed }
    internal sealed record Image(string Name, ulong CandidateBytes)
    {
        internal string Folder => LinuxRecoveryCaptureExport.Root + "/" + Name;
    }
    internal sealed record Result(State Status, IReadOnlyList<Image> Images);
    internal interface ISource : IDisposable
    {
        IEnumerable<string> Names { get; }
        Image? Inspect(string name);
        void Recheck();
    }

    internal static Task<Result> ReadAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (!LinuxInventoryWorkerPeerChannel.Supported) return new Result(State.Unavailable, []);
        try { using var source = new NativeSource(); return Read(source, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new Result(State.Unavailable, []); }
    }, token);

    internal static Result Read(ISource source, CancellationToken token)
    {
        try
        {
            List<Image> images = [];
            int count = 0;
            source.Recheck();
            foreach (string name in source.Names)
            {
                token.ThrowIfCancellationRequested();
                if (++count > 16) return new(State.Failed, []);
                if (!CanonicalName(name)) continue;
                if (source.Inspect(name) is { } image) images.Add(image);
            }
            token.ThrowIfCancellationRequested();
            source.Recheck();
            return new(State.Available, images.OrderBy(image => image.Name, StringComparer.Ordinal).ToArray());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(State.Failed, []); }
    }

    internal static bool CanonicalName(string name) => Guid.TryParseExact(name, "N", out var id) &&
        id != Guid.Empty && name == id.ToString("N");
    internal static bool Admits(LinuxRecoveryNative.Identity directory, LinuxRecoveryNative.Identity metadata,
        LinuxRecoveryNative.Identity payload) => LinuxRecoveryCaptureExport.SharedDirectory(directory) &&
        LinuxRecoveryCaptureExport.SharedFile(metadata) && LinuxRecoveryCaptureExport.SharedFile(payload) &&
        metadata.Size is > 0 and <= DiskImageRawProfileVerificationService.MaximumMetadataBytes &&
        payload.Size is > 0 and <= DiskImageRawProfileVerificationService.MaximumCapacity &&
        metadata.Inode != payload.Inode && SameDevice(directory, metadata) && SameDevice(directory, payload);
    private static bool SameDevice(LinuxRecoveryNative.Identity a, LinuxRecoveryNative.Identity b) =>
        a.DeviceMajor == b.DeviceMajor && a.DeviceMinor == b.DeviceMinor;

    private sealed class NativeSource : ISource
    {
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle root;
        private readonly LinuxRecoveryNative.Identity identity;
        internal NativeSource()
        {
            root = LinuxRecoveryNative.OpenDirectory(LinuxRecoveryCaptureExport.Root, false);
            try
            {
                identity = LinuxRecoveryNative.Stat(root);
                if (!LinuxRecoveryCaptureExport.ExportRoot(identity) || !LinuxRecoveryNative.Ext4(root)) throw new IOException("recovery-catalog.root");
                Recheck();
            }
            catch { root.Dispose(); throw; }
        }
        public IEnumerable<string> Names => Directory.EnumerateFileSystemEntries(LinuxRecoveryCaptureExport.Root).Select(Path.GetFileName)!;
        public Image? Inspect(string name)
        {
            string path = LinuxRecoveryCaptureExport.Root + "/" + name;
            try
            {
                // O_PATH allows traversal-only children without granting directory enumeration.
                using var directory = LinuxRecoveryNative.Open(path, 0x200000 | 0x10000 | 0x20000 | 0x80000);
                using var metadata = LinuxRecoveryNative.Open(path + "/image.metadata", 0x800 | 0x20000 | 0x80000);
                using var payload = LinuxRecoveryNative.Open(path + "/payload.raw", 0x800 | 0x20000 | 0x80000);
                var d = LinuxRecoveryNative.Stat(directory); var m = LinuxRecoveryNative.Stat(metadata); var p = LinuxRecoveryNative.Stat(payload);
                if (!Admits(d, m, p) || !SameDevice(identity, d) ||
                    LinuxRecoveryNative.StatPath(path) != d || LinuxRecoveryNative.StatPath(path + "/image.metadata") != m ||
                    LinuxRecoveryNative.StatPath(path + "/payload.raw") != p) return null;
                return new(name, p.Size);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }
        public void Recheck()
        {
            if (LinuxRecoveryNative.Stat(root) != identity || LinuxRecoveryNative.StatPath(LinuxRecoveryCaptureExport.Root) != identity)
                throw new IOException("recovery-catalog.changed");
        }
        public void Dispose() => root.Dispose();
    }
}
