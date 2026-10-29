using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>
/// Single-use independent local-console interaction. No stdin, supplied descriptor
/// or caller-selected path is accepted by production. Trusted media must qualify
/// the selected catalog/culture and physical VT font/rendering; translations alone
/// do not prove CJK/RTL console rendering. The UI must not own this VT/session.
/// </summary>
internal sealed class LinuxRecoveryLocalConsole : LinuxRecoverySupervisor.IConfirmation
{
    internal interface IAccess : IDisposable
    {
        void ValidateContext();
        void FlushInput();
        void Write(byte[] bytes, CancellationToken token);
        int ReadByte(CancellationToken token);
        bool HasPendingInput();
    }
    internal readonly record struct DeviceIdentity(uint Mask, ushort Mode, uint Owner, uint Links,
        uint SpecialMajor, uint SpecialMinor, ulong Inode, uint DeviceMajor, uint DeviceMinor);
    internal readonly record struct Context(uint User, uint EffectiveUser, uint Group, uint EffectiveGroup,
        DeviceIdentity Held, DeviceIdentity Path, bool IsTerminal, int TerminalSession, int ProcessSession,
        int ForegroundGroup, int ProcessGroup, ushort ActiveVt, bool Canonical, bool InjectionDisabled);

    internal static void ValidateContext(Context value)
    {
        var held = value.Held; var path = value.Path;
        if (value.User != 0 || value.EffectiveUser != 0 || value.Group != 0 || value.EffectiveGroup != 0 ||
            (held.Mask & 0x11f) != 0x11f || (path.Mask & 0x11f) != 0x11f ||
            held.Mode != 0x2180 || path.Mode != held.Mode || held.Owner != 0 || path.Owner != 0 ||
            held.Links != 1 || path.Links != 1 || held.SpecialMajor != 4 || held.SpecialMinor != 1 ||
            path.SpecialMajor != 4 || path.SpecialMinor != 1 || held.Inode != path.Inode ||
            held.DeviceMajor != path.DeviceMajor || held.DeviceMinor != path.DeviceMinor || !value.IsTerminal ||
            value.ProcessSession <= 0 || value.TerminalSession != value.ProcessSession ||
            value.ProcessGroup <= 0 || value.ForegroundGroup != value.ProcessGroup || value.ActiveVt != 1 ||
            !value.Canonical || !value.InjectionDisabled) throw Invalid();
    }

    private readonly Func<string, string?> text;
    private readonly CultureInfo culture;
    private readonly Func<IAccess> open;
    private int consumed;

    internal LinuxRecoveryLocalConsole(Func<string, string?> trustedText, CultureInfo culture)
        : this(trustedText, culture, NativeAccess.Open) { }

    // Fixture access only, never selected by controller/image input.
    internal LinuxRecoveryLocalConsole(Func<string, string?> trustedText, CultureInfo culture, Func<IAccess> open)
    {
        text = trustedText ?? throw new ArgumentNullException(nameof(trustedText));
        this.culture = CultureInfo.ReadOnly((CultureInfo)(culture ?? throw new ArgumentNullException(nameof(culture))).Clone());
        this.open = open ?? throw new ArgumentNullException(nameof(open));
    }

    public Task ConfirmAsync(LinuxDiskRestoreOverwriteReview review, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0) throw Invalid();
        byte[] prompt = Prompt(review);
        string expected = Challenge(review);
        // Scheduling cannot skip ownership cleanup when cancellation races entry.
        // The native reader polls with cancellation; no background ReadLine remains.
        return Task.Run(() =>
        {
            var age = DateTimeOffset.UtcNow - review.ObservedAtUtc;
            if (age < TimeSpan.Zero || age >= TimeSpan.FromSeconds(30)) throw Invalid();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30) - age);
            using var input = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var cancellation = input.Token;
            cancellation.ThrowIfCancellationRequested();
            using var access = open();
            access.ValidateContext();
            access.FlushInput();
            access.ValidateContext();
            access.Write(prompt, cancellation);
            var entered = new StringBuilder(128);
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                int next = access.ReadByte(cancellation);
                if (next == '\n') break;
                if (next is < 32 or > 126 || entered.Length == 128) throw Invalid();
                entered.Append((char)next);
            }
            cancellation.ThrowIfCancellationRequested();
            access.ValidateContext();
            if (access.HasPendingInput() || !string.Equals(entered.ToString(), expected, StringComparison.Ordinal)) throw Invalid();
            access.ValidateContext();
            cancellation.ThrowIfCancellationRequested();
        }, CancellationToken.None);
    }

    internal static string Challenge(LinuxDiskRestoreOverwriteReview review)
    {
        if (review is null || review.OperationId == Guid.Empty || review.ReviewId == Guid.Empty ||
            review.Target.KernelName is not { Length: > 0 and <= 63 } name ||
            !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) throw Invalid();
        return name + " " + review.ReviewId.ToString("D", CultureInfo.InvariantCulture);
    }

    private byte[] Prompt(LinuxDiskRestoreOverwriteReview review)
    {
        _ = Challenge(review);
        var target = review.Target;
        var image = review.Image;
        if (review.OverwriteOffset != 0 || review.OverwriteLength == 0 || review.OverwriteLength != target.Capacity ||
            target.Kind != LinuxBlockKind.WholeDisk || target.DiskSequence is not > 0 ||
            image.State != DiskImageManifestVerificationState.ContentVerified || image.ImageId == Guid.Empty ||
            image.CaptureId == Guid.Empty || image.Generation == 0 || image.ManifestRoot is not { Length: 64 } root ||
            !root.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw Invalid();
        var builder = new StringBuilder();
        Line("Title"); Line("Warning");
        Line("Target", Escape(target.KernelName), Escape(target.DeviceNumber), Number(target.DiskSequence.Value));
        Line("Capacity", Number(review.OverwriteLength), Number(target.LogicalSectorBytes), Number(target.PhysicalSectorBytes));
        Line("Image", image.ImageId.ToString("D", CultureInfo.InvariantCulture), image.CaptureId.ToString("D", CultureInfo.InvariantCulture), Number(image.Generation));
        Line("Manifest", Escape(root)); Line("Hardware", Escape(target.HardwareIdentityCandidate ?? ""));
        Line("Deadline"); Line("Challenge", Challenge(review));
        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        if (bytes.Length > 16384) throw Invalid();
        return bytes;

        string Number(ulong value) => value.ToString("N0", culture);
        void Line(string key, params string[] values)
        {
            string? template = text("Recovery.Console." + key);
            if (template is not { Length: > 0 and <= 1024 } || template.Any(char.IsControl)) throw Invalid();
            // Exact simple placeholder signatures; missing fields cannot disappear
            // through a damaged/mismatched catalog. Values are already formatted.
            var seen = new HashSet<int>();
            for (int i = 0; i < template.Length; i++)
            {
                if (template[i] == '}') throw Invalid();
                if (template[i] != '{') continue;
                if (i + 2 >= template.Length || template[i + 2] != '}' || template[i + 1] < '0' ||
                    template[i + 1] > '9') throw Invalid();
                int index = template[i + 1] - '0';
                if (index >= values.Length) throw Invalid();
                seen.Add(index); i += 2;
            }
            if (seen.Count != values.Length) throw Invalid();
            builder.AppendFormat(culture, template, values.Cast<object>().ToArray()).Append('\n');
        }
    }

    internal static string Escape(string value)
    {
        if (value.Length > 1024) throw Invalid();
        var output = new StringBuilder();
        foreach (char c in value)
        {
            if (c is >= ' ' and <= '~' && c != '\\') output.Append(c);
            else output.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
        }
        return output.ToString();
    }

    private static InvalidDataException Invalid() => new("recovery-console.refused");

    private sealed class NativeAccess : IAccess
    {
        private const string Pathname = "/dev/tty1";
        private readonly Descriptor handle;
        // Original adopted descriptor, not extracted from SafeHandle. This access
        // belongs exclusively to the confirming task and closes only after polling.
        private readonly int descriptor;

        private NativeAccess(int descriptor) { this.descriptor = descriptor; handle = new(descriptor); }
        internal static IAccess Open()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) throw Invalid();
            int descriptor = OpenDevice(Pathname, 2 | 0x100 | 0x800 | 0x20000 | 0x80000); // RDWR, NOCTTY, NONBLOCK, NOFOLLOW, CLOEXEC
            if (descriptor < 3)
            {
                if (descriptor >= 0) _ = Close(descriptor);
                throw Invalid();
            }
            return new NativeAccess(descriptor);
        }

        public void ValidateContext()
        {
            if (StatHandle(handle, "", 0x1100, 0x7ff, out var held) != 0 ||
                StatPath(-100, Pathname, 0x100, 0x7ff, out var path) != 0 ||
                GetVt(handle, 0x5603, out var vt) != 0 || GetAttributes(handle, out var attributes) != 0) throw Invalid();
            LinuxRecoveryLocalConsole.ValidateContext(new(GetUid(), GetEuid(), GetGid(), GetEgid(),
                Identity(held), Identity(path), IsTerminal(handle) == 1, SessionOfTerminal(handle), GetSession(0),
                ForegroundGroup(handle), GetProcessGroup(), vt.Active, (attributes.LocalFlags & 2) != 0, InjectionDisabled()));
        }

        private static DeviceIdentity Identity(FileStatus value) => new(value.Mask, value.Mode, value.Owner, value.Links,
            value.SpecialMajor, value.SpecialMinor, value.Inode, value.DeviceMajor, value.DeviceMinor);

        private static bool InjectionDisabled()
        {
            using var file = File.OpenRead("/proc/sys/dev/tty/legacy_tiocsti");
            Span<byte> bytes = stackalloc byte[16];
            int count = file.Read(bytes);
            return file.ReadByte() == -1 && (bytes[..count].SequenceEqual("0\n"u8) || bytes[..count].SequenceEqual("0"u8));
        }

        public void FlushInput() { if (Flush(handle, 0) != 0) throw Invalid(); } // TCIFLUSH
        public void Write(byte[] bytes, CancellationToken token)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                Wait(4, token);
                byte[] remaining = bytes[offset..];
                nint count = WriteNative(handle, remaining, (nuint)remaining.Length);
                if (count < 0 && Retry()) continue;
                if (count <= 0 || count > remaining.Length) throw Invalid();
                offset += (int)count;
            }
        }
        public int ReadByte(CancellationToken token)
        {
            byte[] bytes = new byte[1];
            while (true)
            {
                Wait(1, token);
                nint count = ReadNative(handle, bytes, 1);
                if (count < 0 && Retry()) continue;
                if (count < 0 || count > 1) throw Invalid();
                return count == 0 ? -1 : bytes[0];
            }
        }
        public bool HasPendingInput()
        {
            var entry = new PollEntry { Descriptor = descriptor, Events = 1 };
            int result = Poll(ref entry, 1, 0);
            if (result < 0 || (entry.Returned & ~1) != 0) throw Invalid();
            return result != 0;
        }
        private void Wait(short events, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var entry = new PollEntry { Descriptor = descriptor, Events = events };
                int result = Poll(ref entry, 1, 100);
                if (result < 0 && Retry()) continue;
                if (result < 0 || (entry.Returned & ~events) != 0) throw Invalid();
                if (result > 0) { token.ThrowIfCancellationRequested(); return; }
            }
        }
        private static bool Retry() => Marshal.GetLastPInvokeError() is 4 or 11; // EINTR/EAGAIN
        public void Dispose() => handle.Dispose();

        private sealed class Descriptor : SafeHandle
        {
            internal Descriptor(int number) : base((IntPtr)(-1), true) => SetHandle((IntPtr)number);
            public override bool IsInvalid => handle.ToInt64() < 0;
            protected override bool ReleaseHandle() => NativeAccess.Close(handle.ToInt32()) == 0;
        }
        [StructLayout(LayoutKind.Sequential)] private struct PollEntry { public int Descriptor; public short Events, Returned; }
        [StructLayout(LayoutKind.Sequential)] private struct VtState { public ushort Active, Signal, State; }
        [StructLayout(LayoutKind.Explicit, Size = 60)] private struct Attributes { [FieldOffset(12)] public uint LocalFlags; }
        [StructLayout(LayoutKind.Explicit, Size = 256)] private struct FileStatus
        {
            [FieldOffset(0)] public uint Mask;
            [FieldOffset(16)] public uint Links;
            [FieldOffset(20)] public uint Owner;
            [FieldOffset(28)] public ushort Mode;
            [FieldOffset(32)] public ulong Inode;
            [FieldOffset(128)] public uint SpecialMajor;
            [FieldOffset(132)] public uint SpecialMinor;
            [FieldOffset(136)] public uint DeviceMajor;
            [FieldOffset(140)] public uint DeviceMinor;
        }
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int OpenDevice([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
        [DllImport("libc", EntryPoint = "close")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Close(int descriptor);
        [DllImport("libc", EntryPoint = "statx")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int StatHandle(Descriptor descriptor, string path, int flags, uint mask, out FileStatus value);
        [DllImport("libc", EntryPoint = "statx")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int StatPath(int directory, string path, int flags, uint mask, out FileStatus value);
        [DllImport("libc", EntryPoint = "isatty")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int IsTerminal(Descriptor descriptor);
        [DllImport("libc", EntryPoint = "tcgetsid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int SessionOfTerminal(Descriptor descriptor);
        [DllImport("libc", EntryPoint = "getsid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int GetSession(int process);
        [DllImport("libc", EntryPoint = "tcgetpgrp")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int ForegroundGroup(Descriptor descriptor);
        [DllImport("libc", EntryPoint = "getpgrp")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int GetProcessGroup();
        [DllImport("libc", EntryPoint = "ioctl")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int GetVt(Descriptor descriptor, uint request, out VtState value);
        [DllImport("libc", EntryPoint = "tcgetattr")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int GetAttributes(Descriptor descriptor, out Attributes value);
        [DllImport("libc", EntryPoint = "tcflush")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Flush(Descriptor descriptor, int queue);
        [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern int Poll(ref PollEntry entry, nuint count, int timeout);
        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern nint ReadNative(Descriptor descriptor, byte[] bytes, nuint count);
        [DllImport("libc", EntryPoint = "write", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern nint WriteNative(Descriptor descriptor, byte[] bytes, nuint count);
        [DllImport("libc", EntryPoint = "getuid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern uint GetUid();
        [DllImport("libc", EntryPoint = "geteuid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern uint GetEuid();
        [DllImport("libc", EntryPoint = "getgid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern uint GetGid();
        [DllImport("libc", EntryPoint = "getegid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)] private static extern uint GetEgid();
    }
}
