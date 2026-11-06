using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VaultSync.Core.Services;

// Linux x64 UAPI only. Fixed getters; no write, mount, discard or partition ioctl.
// ABI references: Linux v6.12 include/uapi/{linux/fs.h,linux/stat.h,asm-generic/fcntl.h}.
internal sealed class LinuxBlockReadNative : LinuxBlockReadService.IDeviceAccess
{
    private const int OpenFlags = 0x80 | 0x100 | 0x800 | 0x20000 | 0x80000;
    // O_RDONLY=0, O_EXCL, O_NOCTTY, O_NONBLOCK, O_NOFOLLOW, O_CLOEXEC.

    public SafeFileHandle? Open(string kernelName, out LinuxBlockReadState failure)
    {
        int descriptor = OpenDevice("/dev/" + kernelName, OpenFlags);
        if (descriptor >= 0)
        {
            failure = LinuxBlockReadState.OpenedReadOnly;
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }
        failure = Marshal.GetLastPInvokeError() switch
        {
            1 or 13 => LinuxBlockReadState.AccessDenied,
            16 => LinuxBlockReadState.InUse,
            2 or 6 or 19 => LinuxBlockReadState.Unavailable,
            22 or 25 => LinuxBlockReadState.UnsupportedDevice,
            40 => LinuxBlockReadState.Refused,
            _ => LinuxBlockReadState.Failed
        };
        return null;
    }

    public bool Inspect(SafeFileHandle handle, out LinuxBlockReadService.HandleIdentity identity)
    {
        identity = default;
        // AT_EMPTY_PATH queries the held descriptor, never another path lookup.
        if (Statx(handle, "", 0x1000 | 0x100, 1, out DeviceStatus status) != 0 ||
            (status.Mask & 1) == 0 || (status.Mode & 0xf000) != 0x6000 ||
            Ioctl64(handle, 0x80081272, out ulong capacity) != 0 ||
            Ioctl32(handle, 0x1268, out uint logical) != 0 ||
            Ioctl32(handle, 0x127b, out uint physical) != 0 ||
            Ioctl64(handle, 0x80081280, out ulong sequence) != 0 ||
            Ioctl32(handle, 0x125e, out uint readOnly) != 0 || readOnly > 1)
            return false;
        identity = new(true, status.Major, status.Minor, capacity, logical, physical, sequence, readOnly == 1);
        return true;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct DeviceStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(128)] public uint Major;
        [FieldOffset(132)] public uint Minor;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int OpenDevice([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Statx(SafeFileHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out DeviceStatus status);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Ioctl64(SafeFileHandle handle, nuint request, out ulong value);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Ioctl32(SafeFileHandle handle, nuint request, out uint value);
}
