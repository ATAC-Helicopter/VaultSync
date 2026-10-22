using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

internal enum LinuxRawRestoreOpenState { OpenedExclusive, Unsupported, Refused, Changed, InUse, AccessDenied, Unavailable, Failed }
internal sealed record LinuxRawRestoreOpenResult(LinuxRawRestoreOpenState State,
    RawImageDeviceRestoreService.ITarget? Target = null);

/// <summary>
/// Internal native acquisition prerequisite, not operation authorization. The future
/// trusted broker must establish consent, peer/build trust and controlled offline
/// execution BEFORE calling. Only disposable-VM fixtures currently call this opener.
/// No public route, elevation, image-selected path or automatic retry.
/// </summary>
internal static class LinuxRawRestoreTargetOpenService
{
    internal interface IDeviceAccess
    {
        SafeFileHandle? Open(string kernelName, out LinuxRawRestoreOpenState failure);
        bool Inspect(SafeFileHandle handle, out LinuxBlockReadService.HandleIdentity identity);
        RawImageDeviceRestoreService.ITarget? Adopt(SafeFileHandle handle, LinuxBlockObservation target);
    }

    internal static ValueTask<LinuxRawRestoreOpenResult> OpenAsync(LinuxRawImageStagingSession stage,
        RawImageDeviceRestoreRequest request, RawImageRestoreJournal journal, LinuxBlockInventory expected,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return ValueTask.FromResult(new LinuxRawRestoreOpenResult(LinuxRawRestoreOpenState.Unsupported));
        return OpenAsync(stage, request, journal, expected, LinuxBlockInventoryService.ObserveAsync, new Native(), token);
    }

    // Ordinary-file fault fixtures exercise ordering only, never native authority.
    internal static async ValueTask<LinuxRawRestoreOpenResult> OpenAsync(LinuxRawImageStagingSession stage,
        RawImageDeviceRestoreRequest request, RawImageRestoreJournal journal, LinuxBlockInventory expected,
        Func<CancellationToken, ValueTask<LinuxBlockInventory>> observe, IDeviceAccess access,
        CancellationToken token = default)
    {
        SafeFileHandle? handle = null;
        RawImageDeviceRestoreService.ITarget? target = null;
        try
        {
            token.ThrowIfCancellationRequested();
            // Reuse strict request validation before even the fixed /dev lookup.
            _ = RawImageRestoreJournalCodec.Header(request);
            if (!Matches(stage, request, expected, expected) ||
                !await journal.MatchesUnstartedAsync(request, expected, token).ConfigureAwait(false))
                return new(LinuxRawRestoreOpenState.Refused);
            var before = await observe(token).ConfigureAwait(false);
            if (!Matches(stage, request, expected, before) ||
                !await journal.MatchesUnstartedAsync(request, before, token).ConfigureAwait(false))
                return new(LinuxRawRestoreOpenState.Changed);
            token.ThrowIfCancellationRequested();
            handle = access.Open(request.Target.KernelName, out var failure);
            if (handle is null) return new(failure);
            if (handle.IsInvalid || handle.IsClosed || !access.Inspect(handle, out var first) || !first.Matches(request.Target))
                return new(LinuxRawRestoreOpenState.Changed);
            var after = await observe(token).ConfigureAwait(false);
            if (!Matches(stage, request, expected, after) ||
                !await journal.MatchesUnstartedAsync(request, after, token).ConfigureAwait(false) ||
                !access.Inspect(handle, out var second) || first != second)
                return new(LinuxRawRestoreOpenState.Changed);
            token.ThrowIfCancellationRequested();
            target = access.Adopt(handle, request.Target);
            if (target is null) return new(LinuxRawRestoreOpenState.Refused);
            handle = null; // Successful adoption transfers the descriptor, including failure cleanup.
            await target.RecheckAsync(token).ConfigureAwait(false);
            if (!stage.IsAlive || !await journal.MatchesUnstartedAsync(request, after, token).ConfigureAwait(false))
                return new(LinuxRawRestoreOpenState.Changed);
            token.ThrowIfCancellationRequested();
            var result = new LinuxRawRestoreOpenResult(LinuxRawRestoreOpenState.OpenedExclusive, target);
            target = null;
            return result;
        }
        catch (InvalidDataException) { return new(LinuxRawRestoreOpenState.Refused); }
        catch (UnauthorizedAccessException) { return new(LinuxRawRestoreOpenState.AccessDenied); }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        { return new(LinuxRawRestoreOpenState.Failed); }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        { return new(LinuxRawRestoreOpenState.Unsupported); }
        finally
        {
            handle?.Dispose();
            if (target is not null) await target.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool Matches(LinuxRawImageStagingSession stage, RawImageDeviceRestoreRequest request,
        LinuxBlockInventory initial, LinuxBlockInventory fresh)
    {
        var image = stage.Image;
        var device = request.Target;
        if (!stage.IsAlive || stage.Payload.CanWrite || stage.Metadata.CanWrite || !stage.RepositoryAncestryKnown ||
            image.State != DiskImageManifestVerificationState.ContentVerified || image.ImageId != request.ImageId ||
            image.CaptureId != request.CaptureId || image.Generation != request.Generation || image.ManifestRoot != request.ManifestRoot ||
            image.VerifiedBytes != device.Capacity || image.DeclaredOrigin is not { } origin ||
            origin.Layout.LogicalSectorBytes != device.LogicalSectorBytes || origin.PhysicalSectorBytes != device.PhysicalSectorBytes ||
            !RawImageRestoreJournal.Current(initial, device) || !RawImageRestoreJournal.Current(fresh, device) ||
            initial.BootId != fresh.BootId || initial.MountNamespace != fresh.MountNamespace)
            return false;
        foreach (string number in stage.RepositoryDeviceNumbers)
            if (!RawImageRestoreJournal.OutsideTarget(initial, device, number) ||
                !RawImageRestoreJournal.OutsideTarget(fresh, device, number) ||
                RawImageRestoreJournal.RepositoryDisk(initial, device, number) !=
                RawImageRestoreJournal.RepositoryDisk(fresh, device, number))
                return false;
        return true;
    }

    private sealed class Native : IDeviceAccess
    {
        public SafeFileHandle? Open(string kernelName, out LinuxRawRestoreOpenState failure)
        {
            // Linux x64 openat2: O_RDWR|O_EXCL|O_NOCTTY|O_NONBLOCK|O_NOFOLLOW|O_CLOEXEC.
            // NO_SYMLINKS includes magic links and every ancestor. Never fall back.
            var how = new OpenHow { Flags = 2 | 0x80 | 0x100 | 0x800 | 0x20000 | 0x80000, Mode = 0, Resolve = 4 };
            long descriptor = OpenAt2(437, -100, "/dev/" + kernelName, ref how, 24);
            if (descriptor >= 0)
            { failure = LinuxRawRestoreOpenState.OpenedExclusive; return new SafeFileHandle((IntPtr)descriptor, true); }
            failure = Marshal.GetLastPInvokeError() switch
            {
                1 or 13 => LinuxRawRestoreOpenState.AccessDenied,
                16 => LinuxRawRestoreOpenState.InUse,
                2 or 6 or 19 => LinuxRawRestoreOpenState.Unavailable,
                38 or 22 => LinuxRawRestoreOpenState.Unsupported,
                40 => LinuxRawRestoreOpenState.Refused,
                _ => LinuxRawRestoreOpenState.Failed
            };
            return null;
        }
        public bool Inspect(SafeFileHandle handle, out LinuxBlockReadService.HandleIdentity identity) =>
            new LinuxBlockReadNative().Inspect(handle, out identity);
        public RawImageDeviceRestoreService.ITarget? Adopt(SafeFileHandle handle, LinuxBlockObservation target) =>
            LinuxRawRestoreTarget.Adopt(handle, target);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow { public ulong Flags, Mode, Resolve; }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long OpenAt2(long number, int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref OpenHow how, nuint size);
}
