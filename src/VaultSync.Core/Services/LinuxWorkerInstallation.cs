using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VaultSync.Core.Services;

/// <summary>
/// Held local installation integrity, not publisher attestation, privilege or operation
/// authority. Root launch accepts only root-owned ancestry/files. No caller data chooses it.
/// </summary>
internal sealed class LinuxWorkerInstallation : IDisposable
{
    private readonly List<SafeFileHandle> directories = new();
    private readonly List<(FileStream Stream, FileIdentity Identity)> files = new();
    private FileIdentity executable;
    private FileStream? runningExecutable;
    private bool disposed;
    private readonly uint owner = User();
    internal static string FixedWorker => Path.Combine(AppContext.BaseDirectory, "DiskWorker", "VaultSync.DiskWorker");

    private LinuxWorkerInstallation() { }

    internal static async Task<LinuxWorkerInstallation?> OpenAsync(string worker, CancellationToken token)
    {
        if (!LinuxInventoryWorkerPeerChannel.Supported) return null;
        var lease = new LinuxWorkerInstallation();
        try
        {
            token.ThrowIfCancellationRequested();
            string? commit = DiskWorkerBuildIdentity.SourceCommit;
            if (commit is null || commit.Length != 40 || Path.GetFileName(worker) != "VaultSync.DiskWorker") return null;
            var directory = lease.OpenDirectory(Path.GetDirectoryName(worker)!);
            var manifest = lease.OpenFile(directory, DiskWorkerInstallationManifest.FileName,
                DiskWorkerInstallationManifest.MaximumBytes, false, out var manifestIdentity);
            lease.files.Add((manifest, manifestIdentity));
            byte[] bytes = new byte[checked((int)manifest.Length)];
            await manifest.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (manifest.ReadByte() != -1) return null;
            var decoded = DiskWorkerInstallationManifest.Decode(bytes, commit);
            var names = Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(worker)!)
                .Select(path => Path.GetFileName(path)).Take(DiskWorkerInstallationManifest.MaximumFiles + 2)
                .Order(StringComparer.Ordinal).ToArray();
            var declared = decoded.Files.Select(file => file.Name).Append(DiskWorkerInstallationManifest.FileName)
                .Order(StringComparer.Ordinal).ToArray();
            if (!names.SequenceEqual(declared, StringComparer.Ordinal)) return null;
            foreach (var file in decoded.Files)
            {
                token.ThrowIfCancellationRequested();
                var stream = lease.OpenFile(directory, file.Name, file.Size, file.Executable, out var identity);
                lease.files.Add((stream, identity));
                if (stream.Length != file.Size ||
                    Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != file.Sha256)
                    return null;
                if (file.Name == "VaultSync.DiskWorker") lease.executable = identity;
            }
            // The controller and child must carry the same Core bytes. Its directory
            // and files receive the same ownership/no-link rules as the child package.
            string core = typeof(LinuxWorkerInstallation).Assembly.Location;
            if (string.IsNullOrEmpty(core)) return null;
            var coreDirectory = lease.OpenDirectory(Path.GetDirectoryName(core)!);
            var current = lease.OpenFile(coreDirectory, Path.GetFileName(core), DiskWorkerInstallationManifest.MaximumFileBytes,
                false, out var currentIdentity);
            lease.files.Add((current, currentIdentity));
            string currentHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(current, token).ConfigureAwait(false));
            if (decoded.Files.Single(file => file.Name == "VaultSync.Core.dll").Sha256 != currentHash || !lease.Recheck()) return null;
            var result = lease; lease = null;
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Text.Json.JsonException or EntryPointNotFoundException or DllNotFoundException)
        { return null; }
        finally { lease?.Dispose(); }
    }

    // Call only around the already authenticated, pidfd-bound child, BEFORE sending
    // its request or allowing privilege dropping to make /proc/exe unreadable.
    internal bool BindProcess(int process, Action validatePeer)
    {
        if (process <= 1 || runningExecutable is not null || !Recheck()) return false;
        validatePeer();
        using var proc = Hold(OpenRoot(-100, "/proc", 0x10000 | 0x20000 | 0x80000));
        if (Statfs(proc, out var fs) != 0 || fs.Type != 0x9fa0) return false;
        var handle = Hold(OpenAt(proc, process.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/exe", 0x80000));
        try
        {
            if (!Identity(handle, out var actual) || actual != executable) return false;
            runningExecutable = new FileStream(handle, FileAccess.Read, 1, false);
            handle = null; validatePeer(); return Recheck();
        }
        finally { handle?.Dispose(); }
    }

    internal bool Recheck()
    {
        if (disposed || User() != owner) return false;
        foreach (var directory in directories)
            if (!SafeIdentity(directory, 0x4000, out _)) return false;
        foreach (var (stream, expected) in files)
            if (!SafeIdentity(stream.SafeFileHandle, 0x8000, out var fresh) || fresh != expected) return false;
        return runningExecutable is null || Identity(runningExecutable.SafeFileHandle, out var actual) && actual == executable;
    }

    private SafeFileHandle OpenDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length > 4096 || path.Contains('\0') ||
            path.Split('/').Any(component => component is "." or "..") ||
            path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length > 128) throw Refused();
        var directory = Hold(OpenRoot(-100, "/", 0x10000 | 0x20000 | 0x80000));
        directories.Add(directory);
        if (!SafeIdentity(directory, 0x4000, out _)) throw Refused();
        foreach (string component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            directory = Hold(OpenAt(directory, component, 0x10000 | 0x20000 | 0x80000));
            directories.Add(directory);
            if (!SafeIdentity(directory, 0x4000, out _)) throw Refused();
        }
        return directory;
    }

    private FileStream OpenFile(SafeFileHandle directory, string name, long maximumBytes, bool executableRequired, out FileIdentity identity)
    {
        if (!DiskWorkerInstallationManifest.ValidName(name)) throw Refused();
        var handle = Hold(OpenAt(directory, name, 0x800 | 0x20000 | 0x80000));
        try
        {
            if (!SafeIdentity(handle, 0x8000, out identity) || identity.Size is 0 || identity.Size > (ulong)maximumBytes ||
                executableRequired != ((identity.Mode & 0x49) != 0) || executableRequired && (identity.Mode & 0x40) == 0 ||
                (identity.Mode & 0xc00) != 0 ||
                Capability(handle, "security.capability", IntPtr.Zero, 0) != -1 || Marshal.GetLastPInvokeError() != 61)
                throw Refused();
            var stream = new FileStream(handle, FileAccess.Read, 1, false); handle = null;
            return stream;
        }
        finally { handle?.Dispose(); }
    }

    private bool SafeIdentity(SafeFileHandle handle, ushort kind, out FileIdentity identity) =>
        Identity(handle, out identity) && (identity.Mode & 0xf000) == kind && (identity.Mode & 0x12) == 0 &&
        (identity.User == 0 || identity.User == owner) && (kind != 0x8000 || identity.Links == 1);

    private static bool Identity(SafeFileHandle handle, out FileIdentity identity)
    {
        identity = default;
        if (Statx(handle, "", 0x1000 | 0x100, 0x7ff, out var status) != 0 || (status.Mask & 0x3cf) != 0x3cf) return false;
        identity = new(status.User, status.Mode, status.Links, status.Inode, status.Size, status.DeviceMajor, status.DeviceMinor,
            status.ModifiedSeconds, status.ModifiedNanos, status.ChangedSeconds, status.ChangedNanos);
        return true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; runningExecutable?.Dispose();
        foreach (var file in files) file.Stream.Dispose();
        foreach (var directory in directories) directory.Dispose();
    }
    private static IOException Refused() => new("worker-installation.refused");
    private static SafeFileHandle Hold(int descriptor) => descriptor >= 0 ? new((IntPtr)descriptor, true) : throw Refused();
    private readonly record struct FileIdentity(uint User, ushort Mode, uint Links, ulong Inode, ulong Size,
        uint Major, uint Minor, long ModifiedSeconds, uint ModifiedNanos, long ChangedSeconds, uint ChangedNanos);
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileStatus
    {
        [FieldOffset(0)] public uint Mask; [FieldOffset(16)] public uint Links; [FieldOffset(20)] public uint User;
        [FieldOffset(28)] public ushort Mode; [FieldOffset(32)] public ulong Inode; [FieldOffset(40)] public ulong Size;
        [FieldOffset(112)] public long ModifiedSeconds; [FieldOffset(120)] public uint ModifiedNanos;
        [FieldOffset(96)] public long ChangedSeconds; [FieldOffset(104)] public uint ChangedNanos;
        [FieldOffset(136)] public uint DeviceMajor; [FieldOffset(140)] public uint DeviceMinor;
    }
    [StructLayout(LayoutKind.Explicit, Size = 120)] private struct FileSystemStatus { [FieldOffset(0)] public long Type; }
    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenRoot(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(SafeFileHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out FileStatus status);
    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statfs(SafeFileHandle handle, out FileSystemStatus status);
    [DllImport("libc", EntryPoint = "fgetxattr", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint Capability(SafeFileHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr value, nuint size);
    [DllImport("libc", EntryPoint = "geteuid", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint User();
}
