using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>
/// Adopts a caller-owned, already-authorized/exclusive Linux block descriptor.
/// No path opener or claim that inspection proves authority/exclusivity/offline.
/// Not reachable from a public restore API, protocol, desktop or media launcher.
/// </summary>
internal sealed class LinuxRawRestoreTarget : RawImageDeviceRestoreService.ITarget
{
    private readonly SafeFileHandle handle;
    private readonly LinuxBlockReadSession reader;
    public LinuxBlockObservation Observation { get; }
    public Stream ReadBack => reader;

    private LinuxRawRestoreTarget(SafeFileHandle handle, LinuxBlockReadSession reader, LinuxBlockObservation observation)
    { this.handle = handle; this.reader = reader; Observation = observation; }

    // Ownership transfers only on successful adoption. Exclusive opening and exact
    // operation consent are obligations of the future trusted broker, NOT proved here.
    internal static LinuxRawRestoreTarget? Adopt(SafeFileHandle handle, LinuxBlockObservation expected)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            handle.IsInvalid || handle.IsClosed || expected.IsReadOnly || expected.Capacity == 0 ||
            expected.Capacity > DiskImageRawProfileVerificationService.MaximumCapacity ||
            !expected.ProfileGeometrySupported || expected.PhysicalSectorBytes < expected.LogicalSectorBytes ||
            expected.Kind != LinuxBlockKind.WholeDisk || expected.ParentKernelName is not null ||
            expected.MountObserved || expected.SwapObserved || expected.HasHolders ||
            expected.Use != LinuxBlockUse.Unknown || expected.Identity != LinuxBlockIdentity.UniqueInSnapshot)
            return null;
        int flags = Flags(handle, 3); // F_GETFL; O_ACCMODE must be O_RDWR, never O_WRONLY.
        var access = new LinuxBlockReadNative();
        int descriptorFlags = Flags(handle, 1); // F_GETFD / FD_CLOEXEC.
        if (flags < 0 || (flags & 3) != 2 || (flags & (0x400 | 0x4000)) != 0 ||
            descriptorFlags < 0 || (descriptorFlags & 1) == 0 ||
            !access.Inspect(handle, out var identity) || !identity.Matches(expected))
            return null;
        var reader = new LinuxBlockReadSession(handle, identity, access);
        return new(handle, reader, expected);
    }

    public ValueTask RecheckAsync(CancellationToken token) => reader.ValidateBindingAsync(token);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, ulong offset, CancellationToken token)
    {
        if (bytes.Length == 0 || bytes.Length > RawImageDeviceRestoreService.ChunkBytes ||
            offset > Observation.Capacity || (ulong)bytes.Length > Observation.Capacity - offset ||
            offset % Observation.LogicalSectorBytes != 0 || (uint)bytes.Length % Observation.LogicalSectorBytes != 0)
            throw new IOException("raw-restore.write-range");
        await RecheckAsync(token).ConfigureAwait(false);
        await RandomAccess.WriteAsync(handle, bytes, checked((long)offset), token).ConfigureAwait(false);
        await RecheckAsync(token).ConfigureAwait(false);
    }

    public void Flush()
    {
        RecheckAsync(CancellationToken.None).GetAwaiter().GetResult();
        RandomAccess.FlushToDisk(handle);
        RecheckAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync() => reader.DisposeAsync();

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Flags(SafeFileHandle handle, int command);
}
