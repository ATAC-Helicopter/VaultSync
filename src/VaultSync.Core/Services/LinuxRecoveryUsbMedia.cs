using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Writes a locally verified UEFI image to one explicitly reviewed USB disk.</summary>
public static class LinuxRecoveryUsbMedia
{
    private const int BufferBytes = 1024 * 1024;

    public static bool Supported => LinuxInventoryWorkerPeerChannel.Supported &&
        (File.Exists("/usr/bin/sgdisk") || File.Exists("/usr/sbin/sgdisk"));

    public static bool Eligible(LinuxBlockObservation disk) =>
        disk.Kind == LinuxBlockKind.WholeDisk && disk.ParentKernelName is null &&
        disk.Identity != LinuxBlockIdentity.Ambiguous && disk.DiskSequence is > 0 &&
        disk.LogicalSectorBytes == 512 && disk.Capacity is > 0 and <= LinuxBlockReadService.MaximumCapacity &&
        disk.KernelName.Length > 2 && disk.KernelName.StartsWith("sd", StringComparison.Ordinal) &&
        disk.KernelName.Skip(2).All(char.IsAsciiLetterLower) &&
        !disk.IsReadOnly && !disk.MountObserved && !disk.SwapObserved &&
        !disk.HasHolders && disk.Use == LinuxBlockUse.Unknown;

    private static bool OnUsbBus(string kernelName)
    {
        var target = Directory.ResolveLinkTarget("/sys/class/block/" + kernelName, returnFinalTarget: true);
        for (var directory = target is null ? null : new DirectoryInfo(target.FullName);
             directory is not null && directory.FullName.StartsWith("/sys/devices/", StringComparison.Ordinal);
             directory = directory.Parent)
        {
            if (Directory.Exists(directory.FullName + "/subsystem") &&
                Directory.ResolveLinkTarget(directory.FullName + "/subsystem", returnFinalTarget: true)
                    ?.FullName == "/sys/bus/usb") return true;
        }
        return false;
    }

    public static async Task WriteAsync(string image, string sha256, LinuxBlockObservation reviewed,
        Func<LinuxBlockObservation, CancellationToken, Task<SafeFileHandle>> openUsbDisk,
        IProgress<double> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(openUsbDisk);
        ArgumentNullException.ThrowIfNull(progress);
        if (!Supported || !Eligible(reviewed) ||
            sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
            throw new IOException("recovery-usb.invalid-input");

        var workspace = Directory.CreateTempSubdirectory("vaultsync-recovery-usb-");
        try
        {
            string staged = Path.Combine(workspace.FullName, "recovery.raw");
            byte[] digest = await StageAsync(image, staged, Convert.FromHexString(sha256), reviewed.Capacity, token)
                .ConfigureAwait(false);
            await using var source = File.OpenRead(staged);
            await RecheckAsync(reviewed, token).ConfigureAwait(false);
            using var target = await openUsbDisk(reviewed, token).ConfigureAwait(false);
            var native = new LinuxBlockReadNative();
            CheckTarget(target, native, reviewed);
            await RecheckAsync(reviewed, token).ConfigureAwait(false);
            CheckTarget(target, native, reviewed);

            await RecoveryUsbImageWriter.WriteAndVerifyAsync(source, target, digest, progress, token).ConfigureAwait(false);
            CheckTarget(target, native, reviewed);
        }
        finally { workspace.Delete(recursive: true); }
    }

    private static async Task<byte[]> StageAsync(string input, string staged, byte[] expected, ulong capacity,
        CancellationToken token)
    {
        string fullPath = Path.GetFullPath(input);
        var state = LinuxRecoveryNative.StatPath(fullPath);
        if ((state.Mode & 0xf000) != 0x8000 || state.Size < 34 * 512 || state.Size > capacity || state.Size % 512 != 0)
            throw new IOException("recovery-usb.image-size");
        using var sourceHandle = LinuxRecoveryNative.Open(fullPath, 0x800 | 0x20000 | 0x80000);
        if (LinuxRecoveryNative.Stat(sourceHandle) != state) throw new IOException("recovery-usb.image-changed");
        await using (var source = new FileStream(sourceHandle, FileAccess.Read))
        await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            var buffer = new byte[BufferBytes];
            for (ulong copied = 0; copied < state.Size;)
            {
                int count = (int)Math.Min((ulong)buffer.Length, state.Size - copied);
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                copied += (ulong)count;
            }
            if (LinuxRecoveryNative.Stat(sourceHandle) != state)
                throw new IOException("recovery-usb.image-changed");
            output.Position = 0;
            byte[] digest = await SHA256.HashDataAsync(output, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(digest, expected))
                throw new IOException("recovery-usb.image-digest");
            output.SetLength((long)capacity);
            output.Flush(flushToDisk: true);
        }
        // Apply the GPT adjustment to a private ordinary file before acquiring any device.
        if (state.Size != capacity)
            await SgdiskAsync("--move-second-header", staged, token).ConfigureAwait(false);
        string verification = await SgdiskAsync("--verify", staged, token).ConfigureAwait(false);
        if (!verification.Contains("No problems found.", StringComparison.Ordinal))
            throw new IOException("recovery-usb.gpt");
        string table = await SgdiskAsync("--print", staged, token).ConfigureAwait(false);
        if (!HasRecoveryPartitions(table))
            throw new IOException("recovery-usb.partitions");
        await using var finalized = File.OpenRead(staged);
        return await SHA256.HashDataAsync(finalized, token).ConfigureAwait(false);
    }

    internal static bool HasRecoveryPartitions(string table)
    {
        bool efi = false, root = false;
        foreach (string line in table.Split('\n'))
        {
            string[] columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 6 || !int.TryParse(columns[0], out _)) continue;
            efi |= columns[5] == "EF00";
            root |= columns[5] == "8300";
        }
        return efi && root;
    }

    private static async Task<string> SgdiskAsync(string option, string staged, CancellationToken token)
    {
        var info = new ProcessStartInfo(File.Exists("/usr/bin/sgdisk") ? "/usr/bin/sgdisk" : "/usr/sbin/sgdisk")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.Environment["LC_ALL"] = "C";
        info.ArgumentList.Add(option);
        info.ArgumentList.Add(staged);
        using var process = Process.Start(info) ?? throw new IOException("recovery-usb.gpt-start");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await errors.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException("recovery-usb.gpt-command");
        return await output.ConfigureAwait(false);
    }

    private static async Task RecheckAsync(LinuxBlockObservation reviewed, CancellationToken token)
    {
        var snapshot = await LinuxBlockInventoryService.ObserveAsync(token).ConfigureAwait(false);
        if (snapshot.State != LinuxBlockInventoryState.Observed || snapshot.HasUnresolvedSwaps ||
            !snapshot.Devices.Contains(reviewed) || !OnUsbBus(reviewed.KernelName))
            throw new IOException("recovery-usb.changed");
    }

    private static void CheckTarget(SafeFileHandle handle, LinuxBlockReadNative native,
        LinuxBlockObservation reviewed)
    {
        if (!native.Inspect(handle, out var identity) || !identity.Matches(reviewed))
            throw new IOException("recovery-usb.target-identity");
    }
}
