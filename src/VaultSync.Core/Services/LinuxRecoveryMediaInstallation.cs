using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace VaultSync.Core.Services;

/// <summary>Immutable local role closure and boot facts, never publisher attestation or human authority.</summary>
internal sealed class LinuxRecoveryMediaInstallation : IDisposable
{
    internal enum Role { Host, Controller }
    internal const string Root = "/usr/lib/vaultsync-recovery";
    internal const string ManifestName = "recovery.installation.json";
    internal const string Systemd = "/usr/lib/systemd/systemd";
    internal const string Unit = "/usr/lib/systemd/system/vaultsync-recovery.service";
    internal const string ServiceGroup = "/system.slice/vaultsync-recovery.service";
    internal const string ControllerGroup = "/sys/fs/cgroup" + ServiceGroup + "/controller";
    internal const string SocketPath = "/run/vaultsync-recovery/controller.sock";
    internal const uint ControllerId = 60999;
    internal const int MaximumFiles = 2048, MaximumManifest = 1024 * 1024;
    internal const long MaximumFile = 64L * 1024 * 1024, MaximumTotal = 1024L * 1024 * 1024;
    internal sealed record Entry(string Path, long Size, string Sha256, bool Executable);
    internal sealed record Manifest(string Commit, string Culture, string SystemdHash, IReadOnlyList<Entry> Files);
    internal readonly record struct BootFacts(bool RootIdentity, int Parent, bool ReadOnlyRoot, bool SameMount,
        bool SameNetwork, bool InitialUser, bool SystemdMatches, bool NetworkDown, string Group);
    internal static void ValidateHostBoot(BootFacts value)
    {
        if (!value.RootIdentity || value.Parent != 1 || !value.ReadOnlyRoot || !value.SameMount || !value.SameNetwork ||
            !value.InitialUser || !value.SystemdMatches || !value.NetworkDown || value.Group != "0::" + ServiceGroup + "\n") throw Invalid();
    }
    private readonly List<(string Path, FileStream Stream, LinuxRecoveryNative.Identity Identity)> files = new();
    private readonly List<SafeFileHandle> directories = new();
    private readonly Dictionary<string, LinuxRecoveryNative.Identity> identities = new(StringComparer.Ordinal);
    private readonly HashSet<string> heldDirectories = new(StringComparer.Ordinal);
    private bool disposed;
    internal Manifest Description { get; private set; } = null!;

    internal static string RoleName(Role role) => role == Role.Host ? "offline-recovery-supervisor-v1" : "recovery-controller-v1";
    internal static string ProgramPath(Role role) => Root + (role == Role.Host
        ? "/host/VaultSync.RecoveryHost" : "/controller/VaultSync.RecoveryController");
    internal static byte[] BuildIdentity(Role role) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1, role = RoleName(role), runtimeIdentifier = "linux-x64",
        sourceCommit = DiskWorkerBuildIdentity.SourceCommit ?? throw Invalid()
    });

    internal static Manifest Decode(byte[] bytes, string commit)
    {
        if (bytes.Length is 0 or > MaximumManifest || !Hex(commit, 40)) throw Invalid();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 5 });
        var root = document.RootElement;
        Keys(root, "schemaVersion", "role", "runtimeIdentifier", "sourceCommit", "consoleCulture", "systemdSha256", "files");
        if (!root.GetProperty("schemaVersion").TryGetInt32(out int schema) || schema != 1 || Text(root, "role") != "offline-recovery-media-v1" ||
            Text(root, "runtimeIdentifier") != "linux-x64" || Text(root, "sourceCommit") != commit ||
            Text(root, "consoleCulture") is not ("en" or "it") || !Hex(Text(root, "systemdSha256"), 64)) throw Invalid();
        var list = root.GetProperty("files");
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > MaximumFiles) throw Invalid();
        var entries = new List<Entry>(); string? previous = null; long total = 0;
        foreach (var item in list.EnumerateArray())
        {
            Keys(item, "path", "size", "sha256", "executable");
            string path = Text(item, "path"), hash = Text(item, "sha256");
            if (!RelativePath(path) || previous is not null && string.CompareOrdinal(previous, path) >= 0 ||
                !item.GetProperty("size").TryGetInt64(out long size) || size is <= 0 or > MaximumFile ||
                !Hex(hash, 64) || total > MaximumTotal - size ||
                item.GetProperty("executable").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
            entries.Add(new(path, size, hash, item.GetProperty("executable").GetBoolean())); total += size; previous = path;
        }
        foreach (string role in new[] { "host", "controller" })
        {
            string name = role == "host" ? "VaultSync.RecoveryHost" : "VaultSync.RecoveryController";
            foreach (string file in new[] { name, name + ".dll", name + ".deps.json", name + ".runtimeconfig.json",
                "VaultSync.Core.dll", "libhostfxr.so", "libhostpolicy.so", "libcoreclr.so", "System.Private.CoreLib.dll" })
                if (!entries.Any(e => e.Path == role + "/" + file)) throw Invalid();
            if (!entries.Single(e => e.Path == role + "/" + name).Executable) throw Invalid();
        }
        foreach (string required in new[] { "launch/recovery-controller-launch", "boot/vaultsync-recovery.service",
            "host/DiskWorker/worker.installation.json", "host/DiskWorker/VaultSync.DiskWorker", "host/DiskWorker/VaultSync.Core.dll",
            "catalogs/strings." + Text(root, "consoleCulture") + ".json" })
            if (!entries.Any(e => e.Path == required)) throw Invalid();
        if (!entries.Single(e => e.Path == "launch/recovery-controller-launch").Executable ||
            entries.Single(e => e.Path == "host/VaultSync.Core.dll").Sha256 != entries.Single(e => e.Path == "controller/VaultSync.Core.dll").Sha256 ||
            entries.Single(e => e.Path == "host/VaultSync.Core.dll").Sha256 != entries.Single(e => e.Path == "host/DiskWorker/VaultSync.Core.dll").Sha256)
            throw Invalid();
        return new(commit, Text(root, "consoleCulture"), Text(root, "systemdSha256"), entries.AsReadOnly());
    }

    internal static async Task<LinuxRecoveryMediaInstallation> OpenAsync(Role role, CancellationToken token)
    {
        LinuxRecoveryNative.RequireSupported();
        var lease = new LinuxRecoveryMediaInstallation();
        try
        {
            string commit = DiskWorkerBuildIdentity.SourceCommit ?? throw Invalid();
            lease.directories.Add(LinuxRecoveryNative.OpenDirectory(Root, true));
            var manifest = lease.Hold(Root + "/" + ManifestName, MaximumManifest, false);
            byte[] bytes = new byte[checked((int)manifest.Length)]; await manifest.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            lease.Description = Decode(bytes, commit);
            var observed = EnumerateClosure();
            if (!observed.SequenceEqual(lease.Description.Files.Select(e => e.Path).Append(ManifestName).Order(StringComparer.Ordinal))) throw Invalid();
            foreach (var entry in lease.Description.Files)
            {
                token.ThrowIfCancellationRequested();
                var stream = lease.Hold(Root + "/" + entry.Path, MaximumFile, entry.Executable);
                if (stream.Length != entry.Size || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != entry.Sha256) throw Invalid();
            }
            var systemd = lease.Hold(Systemd, MaximumFile, true);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(systemd, token).ConfigureAwait(false)) != lease.Description.SystemdHash) throw Invalid();
            var unit = lease.Hold(Unit, MaximumFile, false);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(unit, token).ConfigureAwait(false)) !=
                lease.Description.Files.Single(e => e.Path == "boot/vaultsync-recovery.service").Sha256) throw Invalid();
            string expectedCore = Root + (role == Role.Host ? "/host/" : "/controller/") + "VaultSync.Core.dll";
            if (typeof(LinuxRecoveryMediaInstallation).Assembly.Location != expectedCore) throw Invalid();
            lease.RequireExecutable(Environment.ProcessId, role);
            lease.RecheckBoot(role);
            lease.Recheck();
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private FileStream Hold(string path, long maximum, bool executable)
    {
        string directory = Path.GetDirectoryName(path)!;
        if (heldDirectories.Add(directory)) directories.Add(LinuxRecoveryNative.OpenDirectory(directory, true));
        var stream = LinuxRecoveryNative.OpenFile(path, true);
        try
        {
            var identity = LinuxRecoveryNative.Stat(stream.SafeFileHandle);
            if (!LinuxRecoveryNative.TrustedFile(identity, executable) || !LinuxRecoveryNative.NoCapabilities(stream.SafeFileHandle) ||
                identity.Size is 0 || identity.Size > (ulong)maximum) throw Invalid();
            files.Add((path, stream, identity)); identities.Add(path, identity); return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static string[] EnumerateClosure()
    {
        var pending = new Queue<string>(); pending.Enqueue(Root);
        var result = new List<string>(); int directories = 0;
        while (pending.Count != 0)
        {
            string directory = pending.Dequeue();
            if (++directories > 128) throw Invalid();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).Take(MaximumFiles + 129))
            {
                var state = LinuxRecoveryNative.StatPath(path);
                if ((state.Mode & 0xf000) == 0x4000)
                {
                    using var held = LinuxRecoveryNative.OpenDirectory(path, true);
                    pending.Enqueue(path);
                }
                else if ((state.Mode & 0xf000) == 0x8000) result.Add(path[(Root.Length + 1)..]);
                else throw Invalid();
                if (result.Count + pending.Count > MaximumFiles + 128) throw Invalid();
            }
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    internal void RequireExecutable(int process, Role role)
    {
        using var executable = LinuxRecoveryNative.OpenProcExecutable(process);
        if (LinuxRecoveryNative.Stat(executable.SafeFileHandle) != identities[ProgramPath(role)]) throw Invalid();
    }

    internal void Recheck()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (var file in files)
        {
            if (LinuxRecoveryNative.Stat(file.Stream.SafeFileHandle) != file.Identity) throw Invalid();
            using var current = LinuxRecoveryNative.OpenFile(file.Path, true);
            if (LinuxRecoveryNative.Stat(current.SafeFileHandle) != file.Identity) throw Invalid();
        }
    }

    internal void RecheckBoot(Role role)
    {
        if (!LinuxRecoveryNative.ReadOnlyFilesystem("/")) throw Invalid();
        string group = LinuxRecoveryNative.ReadBounded("/proc/self/cgroup", 4096);
        if (role == Role.Host)
        {
            ValidateHostBoot(new(LinuxRecoveryNative.RootIdentity, LinuxRecoveryNative.ParentProcess(), true,
                LinuxRecoveryNative.SameMountNamespace(), LinuxRecoveryNative.SameNamespace("net"), LinuxRecoveryNative.InitialUserNamespace(),
                LinuxRecoveryNative.ExecutableIdentity(1) == identities[Systemd], LinuxRecoveryNative.NetworkDown(), group));
        }
        else
        {
            if (group != "0::" + ServiceGroup + "/controller\n") throw Invalid();
            LinuxRecoveryControllerLaunch.ValidateNonrootProcess(Environment.ProcessId);
            // The unprivileged child cannot read root /proc/exe or namespace links.
            // Parent authentication is kernel getppid + root peer pidfd on the
            // nonwritable fixed socket, not an inaccessible /proc assumption.
            if (LinuxRecoveryNative.ParentProcess() <= 1) throw Invalid();
        }
    }

    internal (Func<string, string?> Text, CultureInfo Culture) ConsoleText()
    {
        // The allowlist is packaging admission, NOT physical font/rendering qualification.
        string path = Root + "/catalogs/strings." + Description.Culture + ".json";
        using var input = LinuxRecoveryNative.OpenFile(path, true);
        if (input.Length > MaximumManifest) throw Invalid();
        using var json = JsonDocument.Parse(input, new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 3 });
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in json.RootElement.EnumerateObject())
            if (property.Name.StartsWith("Recovery.Console.", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.String)
                text.Add(property.Name, property.Value.GetString()!);
        return (key => text.GetValueOrDefault(key), CultureInfo.GetCultureInfo(Description.Culture));
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var file in files) file.Stream.Dispose();
        foreach (var directory in directories) directory.Dispose();
    }
    internal static bool RelativePath(string path) => path.Length is > 0 and <= 512 && path.Split('/') is { Length: >= 2 and <= 4 } parts &&
        parts[0] is "host" or "controller" or "launch" or "catalogs" or "boot" && parts.All(p => p.Length is > 0 and <= 128 &&
            p[0] != '.' && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'));
    private static bool Hex(string value, int size) => value.Length == size && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Text(JsonElement element, string key) => element.GetProperty(key).ValueKind == JsonValueKind.String
        ? element.GetProperty(key).GetString() ?? throw Invalid() : throw Invalid();
    private static void Keys(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(expected.Order(StringComparer.Ordinal))) throw Invalid();
    }
    internal static InvalidDataException Invalid() => new("recovery-installation.refused");
}

/// <summary>Linux x64 held-file primitives shared only by fixed recovery roles.</summary>
internal static class LinuxRecoveryNative
{
    internal readonly record struct Identity(ushort Mode, uint Owner, uint Group, uint Links, ulong Inode, ulong Size,
        uint DeviceMajor, uint DeviceMinor, long Modified, uint ModifiedNanos, long Changed, uint ChangedNanos);
    internal static bool RootIdentity => Uid() == 0 && Euid() == 0 && Gid() == 0 && Egid() == 0;
    internal static void RequireSupported()
    { if (!LinuxInventoryWorkerPeerChannel.Supported) throw LinuxRecoveryMediaInstallation.Invalid(); }
    internal static SafeFileHandle OpenDirectory(string path, bool readOnly)
    {
        var handle = Open(path, 0x10000 | 0x20000 | 0x80000);
        try
        {
            var state = Stat(handle);
            if ((state.Mode & 0xf000) != 0x4000 || state.Owner != 0 || (state.Mode & 0x12) != 0 || readOnly && !ReadOnly(handle)) throw LinuxRecoveryMediaInstallation.Invalid();
            for (string? ancestor = Path.GetDirectoryName(path); ancestor is not null; ancestor = ancestor == "/" ? null : Path.GetDirectoryName(ancestor))
            {
                using var parent = Open(ancestor, 0x10000 | 0x20000 | 0x80000);
                var parentState = Stat(parent);
                if ((parentState.Mode & 0xf000) != 0x4000 || parentState.Owner != 0 || (parentState.Mode & 0x12) != 0 || readOnly && !ReadOnly(parent))
                    throw LinuxRecoveryMediaInstallation.Invalid();
            }
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }
    internal static FileStream OpenFile(string path, bool readOnly)
    {
        var handle = Open(path, 0x20000 | 0x80000);
        try
        {
            if ((Stat(handle).Mode & 0xf000) != 0x8000 || readOnly && !ReadOnly(handle)) throw LinuxRecoveryMediaInstallation.Invalid();
            return new(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }
    internal static SafeFileHandle Open(string path, ulong flags, ulong mode = 0)
    {
        if (!Path.IsPathFullyQualified(path) || path.Split('/').Any(p => p is "." or "..")) throw LinuxRecoveryMediaInstallation.Invalid();
        var how = new OpenHow { Flags = flags, Mode = mode, Resolve = 4 };
        long fd = OpenAt2(437, -100, path, in how, 24);
        if (fd < 0 || fd > int.MaxValue) throw LinuxRecoveryMediaInstallation.Invalid();
        return new((IntPtr)fd, true);
    }
    internal static Identity Stat(SafeFileHandle handle)
    {
        if (StatHandle(handle, "", 0x1100, 0x7ff, out var value) != 0 || (value.Mask & 0x3df) != 0x3df) throw LinuxRecoveryMediaInstallation.Invalid();
        return IdentityOf(value);
    }
    internal static Identity StatPath(string path)
    {
        if (StatName(-100, path, 0x100, 0x7ff, out var value) != 0 || (value.Mask & 0x3df) != 0x3df) throw LinuxRecoveryMediaInstallation.Invalid();
        return IdentityOf(value);
    }
    private static Identity IdentityOf(FileStatus value) => new(value.Mode, value.Owner, value.Group, value.Links, value.Inode, value.Size,
        value.DeviceMajor, value.DeviceMinor, value.Modified, value.ModifiedNanos, value.Changed, value.ChangedNanos);
    internal static bool TrustedFile(Identity state, bool executable) => (state.Mode & 0xf000) == 0x8000 && state.Owner == 0 &&
        state.Links == 1 && (state.Mode & (0xc00 | 0x12)) == 0 && ((state.Mode & 0x49) != 0) == executable;
    internal static bool ReadOnlyFilesystem(string path)
    { using var held = OpenDirectory(path, false); return ReadOnly(held); }
    internal static bool ReadOnly(SafeFileHandle handle) => StatFs(handle, out var value) == 0 && (value.Flags & 1) != 0 &&
        value.Type is 0xef53 or 0x73717368 or 0x9660;
    internal static bool Ext4(SafeFileHandle handle) => StatFs(handle, out var value) == 0 && value.Type == 0xef53 && (value.Flags & 1) == 0;
    internal static bool Cgroup(SafeFileHandle handle) => StatFs(handle, out var value) == 0 && value.Type == 0x63677270;
    internal static bool SameMountNamespace() => SameNamespace("mnt");
    internal static bool SameNamespace(string name)
    {
        string? current = new FileInfo("/proc/self/ns/" + name).LinkTarget, initial = new FileInfo("/proc/1/ns/" + name).LinkTarget;
        return current is { Length: > 0 and < 64 } && current.StartsWith(name + ":[", StringComparison.Ordinal) && current == initial;
    }
    internal static bool InitialUserNamespace() => ReadBounded("/proc/self/uid_map", 4096)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).SequenceEqual(new[] { "0", "0", "4294967295" });
    internal static bool NetworkDown()
    {
        string[] names = Directory.GetDirectories("/sys/class/net").Take(65).ToArray();
        if (names.Length is 0 or > 64) return false;
        foreach (string path in names)
        {
            if (Path.GetFileName(path) == "lo") continue;
            string value = ReadBounded(path + "/flags", 32).Trim();
            if (!value.StartsWith("0x", StringComparison.Ordinal) || !uint.TryParse(value[2..], NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out uint flags) || (flags & 1) != 0) return false;
        }
        return names.Order(StringComparer.Ordinal).SequenceEqual(Directory.GetDirectories("/sys/class/net").Order(StringComparer.Ordinal));
    }
    internal static bool NoCapabilities(SafeFileHandle handle) => GetXattr(handle, "security.capability", IntPtr.Zero, 0) == -1 &&
        Marshal.GetLastPInvokeError() == 61; // ENODATA; unsupported checks refuse.
    internal static FileStream OpenProcExecutable(int process)
    {
        if (process <= 0) throw LinuxRecoveryMediaInstallation.Invalid();
        int fd = OpenProc("/proc/" + process.ToString(CultureInfo.InvariantCulture) + "/exe", 0x80000);
        if (fd < 0) throw LinuxRecoveryMediaInstallation.Invalid();
        return new(new SafeFileHandle((IntPtr)fd, true), FileAccess.Read);
    }
    internal static Identity ExecutableIdentity(int process)
    { using var executable = OpenProcExecutable(process); return Stat(executable.SafeFileHandle); }
    internal static string ReadBounded(string path, int maximum)
    {
        using var file = File.OpenRead(path); byte[] bytes = new byte[maximum + 1]; int count = 0, read;
        while (count < bytes.Length && (read = file.Read(bytes, count, bytes.Length - count)) != 0) count += read;
        if (count > maximum) throw LinuxRecoveryMediaInstallation.Invalid();
        return new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, count);
    }
    internal static void Sync(SafeFileHandle handle) { if (Fsync(handle) != 0) throw new IOException("recovery-storage.sync-failed"); }
    internal static void MakeDirectory(SafeFileHandle parent, string name, uint mode = 0x1c0)
    { if (Mkdir(parent, name, mode) != 0) throw new IOException("recovery-storage.create-refused"); }
    internal static void SetGroup(SafeFileHandle handle, uint group, uint mode)
    { if (Chown(handle, 0, group) != 0 || Chmod(handle, mode) != 0) throw new IOException("recovery-storage.permissions-refused"); }
    internal static void Remove(SafeFileHandle parent, string name, bool directory)
    { if (Unlink(parent, name, directory ? 0x200 : 0) != 0) throw new IOException("recovery-storage.unlink-refused"); }
    internal static void Lock(SafeFileHandle handle) { if (Flock(handle, 2 | 4) != 0) throw new IOException("recovery-host.already-owned"); }
    [StructLayout(LayoutKind.Sequential)] private struct OpenHow { public ulong Flags, Mode, Resolve; }
    [StructLayout(LayoutKind.Explicit, Size = 256)] private struct FileStatus
    {
        [FieldOffset(0)] public uint Mask; [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint Owner; [FieldOffset(24)] public uint Group; [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode; [FieldOffset(40)] public ulong Size;
        [FieldOffset(96)] public long Changed; [FieldOffset(104)] public uint ChangedNanos;
        [FieldOffset(112)] public long Modified; [FieldOffset(120)] public uint ModifiedNanos;
        [FieldOffset(136)] public uint DeviceMajor; [FieldOffset(140)] public uint DeviceMinor;
    }
    [StructLayout(LayoutKind.Explicit, Size = 120)] private struct Filesystem
    { [FieldOffset(0)] public long Type; [FieldOffset(80)] public ulong Flags; }
    [DllImport("libc", EntryPoint = "syscall")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long OpenAt2(long number, int parent, string path, in OpenHow how, nuint bytes);
    [DllImport("libc", EntryPoint = "statx")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int StatHandle(SafeFileHandle handle, string path, int flags, uint mask, out FileStatus value);
    [DllImport("libc", EntryPoint = "statx")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int StatName(int parent, string path, int flags, uint mask, out FileStatus value);
    [DllImport("libc", EntryPoint = "fstatfs")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int StatFs(SafeFileHandle handle, out Filesystem value);
    [DllImport("libc", EntryPoint = "open")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int OpenProc(string path, int flags);
    [DllImport("libc", EntryPoint = "getppid")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int ParentProcess();
    [DllImport("libc", EntryPoint = "getsid")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] internal static extern int Session(int process);
    [DllImport("libc", EntryPoint = "fgetxattr", SetLastError = true)][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern nint GetXattr(SafeFileHandle handle, string name, IntPtr bytes, nuint count);
    [DllImport("libc", EntryPoint = "getuid")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern uint Uid();
    [DllImport("libc", EntryPoint = "geteuid")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern uint Euid();
    [DllImport("libc", EntryPoint = "getgid")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern uint Gid();
    [DllImport("libc", EntryPoint = "getegid")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern uint Egid();
    [DllImport("libc", EntryPoint = "mkdirat")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Mkdir(SafeFileHandle parent, string name, uint mode);
    [DllImport("libc", EntryPoint = "fchmod")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Chmod(SafeFileHandle handle, uint mode);
    [DllImport("libc", EntryPoint = "fchown")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Chown(SafeFileHandle handle, uint owner, uint group);
    [DllImport("libc", EntryPoint = "unlinkat")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Unlink(SafeFileHandle parent, string name, int flags);
    [DllImport("libc", EntryPoint = "fsync")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Fsync(SafeFileHandle handle);
    [DllImport("libc", EntryPoint = "flock")][DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Flock(SafeFileHandle handle, int mode);
}
