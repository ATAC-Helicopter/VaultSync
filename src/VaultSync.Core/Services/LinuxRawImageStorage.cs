using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VaultSync.Core.Services;

/// <summary>
/// Private, create-only Linux x64/ext4 image transaction. Partial directories are
/// retained; no automatic cleanup, replacement, resume or arbitrary file names.
/// Same-user/admin/kernel compromise is outside this storage boundary.
/// </summary>
internal sealed class LinuxRawImageStorage : LinuxRawImageStagingService.IStorage
{
    private const int DirectoryFlags = 0x10000 | 0x20000 | 0x80000;
    private const int FileFlags = 2 | 0x40 | 0x80 | 0x20000 | 0x80000;
    private readonly SafeFileHandle parent;
    private readonly SafeFileHandle directory;
    private readonly string partialName;
    private readonly string finalName;
    private FileStream payload;
    private FileStream metadata;
    private bool sealedForStaging;
    public string? FilesystemDeviceNumber { get; }

    private LinuxRawImageStorage(SafeFileHandle parent, SafeFileHandle directory,
        string partialName, string finalName, FileStream payload, FileStream metadata)
    {
        this.parent = parent;
        this.directory = directory;
        this.partialName = partialName;
        this.finalName = finalName;
        this.payload = payload;
        this.metadata = metadata;
        FilesystemDeviceNumber = DeviceNumber(parent, 0x4000);
    }

    public Stream Payload => payload;
    public Stream Metadata => metadata;
    public bool Published { get; private set; }

    internal static string? TryFilesystemDeviceNumber(Stream stream) =>
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        stream is FileStream file && file.CanRead ? DeviceNumber(file.SafeFileHandle, 0x8000) : null;

    private static string? DeviceNumber(SafeFileHandle handle, ushort type) =>
        Statx(handle, "", 0x1000 | 0x100, 0x7ff, out var status) == 0 && (status.Mask & 1) != 0 &&
        (status.Mode & 0xf000) == type
            ? status.DeviceMajor.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
              status.DeviceMinor.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    internal static LinuxRawImageStorage Create(string outputParent, Guid imageId, ulong capacity)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new IOException("raw-storage.unsupported-platform");
        if (string.IsNullOrEmpty(outputParent) || outputParent.Length > 4096 || outputParent[0] != '/' ||
            outputParent.Contains('\0') || outputParent.Split('/').Any(p => p is "." or "..") ||
            imageId == Guid.Empty || capacity is 0 or > DiskImageRawProfileVerificationService.MaximumCapacity)
            throw new IOException("raw-storage.invalid-request");
        SafeFileHandle? parent = null, directory = null;
        FileStream? payload = null, metadata = null;
        try
        {
            // openat2 refuses symlinks/magic links in EVERY path component. No
            // fallback to weaker path checks on older kernels/filesystems.
            var how = new OpenHow { Flags = DirectoryFlags, Resolve = 4 };
            long descriptor = OpenDirectory(437, -100, outputParent, in how, 24);
            if (descriptor < 0 || descriptor > int.MaxValue) throw IoFailure();
            parent = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            if (!PrivateDirectory(parent) || Statfs(parent, out var fs) != 0 || fs.Type != 0xef53 ||
                fs.BlockSize <= 0 || fs.AvailableBlocks > ulong.MaxValue / (ulong)fs.BlockSize ||
                fs.AvailableBlocks * (ulong)fs.BlockSize < capacity + DiskImageRawProfileVerificationService.MaximumMetadataBytes)
                throw new IOException("raw-storage.private-ext4-space-required");
            string partial = ".vaultsync-incomplete-" + Guid.NewGuid().ToString("N");
            if (Mkdirat(parent, partial, 0x1c0) != 0) throw IoFailure(); // 0700
            directory = Hold(Openat(parent, partial, DirectoryFlags, 0));
            if (!PrivateDirectory(directory)) throw IoFailure();
            Sync(parent);
            payload = NewFile(directory, "payload.raw");
            metadata = NewFile(directory, "image.metadata");
            // Reserve actual ext4 blocks before reading the source; KEEP_SIZE
            // leaves logical EOF at zero until copied. ENOSPC is not a partial success.
            if (Fallocate(payload.SafeFileHandle, 1, 0, checked((long)capacity)) != 0) throw IoFailure();
            var storage = new LinuxRawImageStorage(parent, directory, partial, imageId.ToString("N"), payload, metadata);
            parent = null; directory = null; payload = null; metadata = null;
            return storage;
        }
        catch (EntryPointNotFoundException) { throw new IOException("raw-storage.kernel-operation-unavailable"); }
        catch (DllNotFoundException) { throw new IOException("raw-storage.native-library-unavailable"); }
        finally
        {
            metadata?.Dispose(); payload?.Dispose(); directory?.Dispose(); parent?.Dispose();
        }
    }

    public void FlushPayload() => payload.Flush(flushToDisk: true);
    public void FlushMetadata() => metadata.Flush(flushToDisk: true);
    public void FlushDirectories() { Sync(directory); Sync(parent); }

    public void Seal()
    {
        if (Published || sealedForStaging) throw new IOException("raw-storage.invalid-seal-state");
        if (Fchmod(payload.SafeFileHandle, 0x100) != 0 || Fchmod(metadata.SafeFileHandle, 0x100) != 0)
            throw IoFailure(); // 0400; persist permissions before dropping writable descriptors.
        payload.Flush(flushToDisk: true); metadata.Flush(flushToDisk: true);
        FileStream? readPayload = null, readMetadata = null;
        try
        {
            readPayload = ReopenReadOnly("payload.raw", payload);
            readMetadata = ReopenReadOnly("image.metadata", metadata);
            payload.Dispose(); metadata.Dispose();
            payload = readPayload; metadata = readMetadata;
            readPayload = null; readMetadata = null;
            sealedForStaging = true;
        }
        finally { readPayload?.Dispose(); readMetadata?.Dispose(); }
    }

    private FileStream ReopenReadOnly(string name, FileStream original)
    {
        var handle = Hold(Openat(directory, name, 0x800 | 0x20000 | 0x80000, 0));
        try
        {
            if (Statx(original.SafeFileHandle, "", 0x1000 | 0x100, 0x7ff, out var old) != 0 ||
                Statx(handle, "", 0x1000 | 0x100, 0x7ff, out var fresh) != 0 ||
                (old.Mask & 0x30f) != 0x30f || (fresh.Mask & 0x30f) != 0x30f ||
                fresh.Mode != 0x8100 || fresh.UserId != Geteuid() || fresh.Links != 1 ||
                old.Inode != fresh.Inode || old.Size != fresh.Size ||
                old.DeviceMajor != fresh.DeviceMajor || old.DeviceMinor != fresh.DeviceMinor)
                throw IoFailure();
            return new FileStream(handle, FileAccess.Read, 128 * 1024, isAsync: false);
        }
        catch { handle.Dispose(); throw; }
    }

    public void Publish()
    {
        if (Published) throw new IOException("raw-storage.already-published");
        // Linux renameat2(RENAME_NOREPLACE): atomic visibility, never overwrite a
        // file, link, empty directory or existing image with the chosen image ID.
        if (Renameat2(parent, partialName, parent, finalName, 1) != 0) throw IoFailure();
        Published = true;
        // Failure here is reported as PublicationUnconfirmed, never success or
        // safe cancellation. No destructive retry/rollback of a visible image.
        Sync(parent);
    }

    public async ValueTask DisposeAsync()
    {
        try { await metadata.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { await payload.DisposeAsync().ConfigureAwait(false); }
            finally { directory.Dispose(); parent.Dispose(); }
        }
    }

    private static FileStream NewFile(SafeFileHandle directory, string name)
    {
        var handle = Hold(Openat(directory, name, FileFlags, 0x180)); // 0600
        try
        {
            if (Statx(handle, "", 0x1000 | 0x100, 0x7ff, out var status) != 0 ||
                (status.Mask & 0xf) != 0xf || (status.Mode & 0xf000) != 0x8000 ||
                (status.Mode & 0x3f) != 0 || status.UserId != Geteuid() || status.Links != 1)
                throw IoFailure();
            return new FileStream(handle, FileAccess.ReadWrite, 128 * 1024, isAsync: false);
        }
        catch { handle.Dispose(); throw; }
    }

    private static bool PrivateDirectory(SafeFileHandle handle) =>
        Statx(handle, "", 0x1000 | 0x100, 0x7ff, out var status) == 0 &&
        (status.Mask & 0xb) == 0xb && (status.Mode & 0xf000) == 0x4000 &&
        (status.Mode & 0x3f) == 0 && status.UserId == Geteuid();

    private static SafeFileHandle Hold(int descriptor) => descriptor >= 0
        ? new((IntPtr)descriptor, ownsHandle: true) : throw IoFailure();
    private static void Sync(SafeFileHandle handle) { if (Fsync(handle) != 0) throw IoFailure(); }
    private static IOException IoFailure() => new("raw-storage.native-operation-failed");

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow { public ulong Flags; public ulong Mode; public ulong Resolve; }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct FileSystemStatus
    {
        [FieldOffset(0)] public long Type;
        [FieldOffset(8)] public long BlockSize;
        [FieldOffset(32)] public ulong AvailableBlocks;
    }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long OpenDirectory(long number, int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, in OpenHow how, nuint size);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Openat(SafeFileHandle dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, uint mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Mkdirat(SafeFileHandle dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);

    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statfs(SafeFileHandle handle, out FileSystemStatus status);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(SafeFileHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out FileStatus status);

    [DllImport("libc", EntryPoint = "geteuid", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint Geteuid();

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Fchmod(SafeFileHandle handle, uint mode);

    [DllImport("libc", EntryPoint = "fallocate", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Fallocate(SafeFileHandle handle, int mode, long offset, long length);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Fsync(SafeFileHandle handle);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Renameat2(SafeFileHandle oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldName,
        SafeFileHandle newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newName, uint flags);
}
