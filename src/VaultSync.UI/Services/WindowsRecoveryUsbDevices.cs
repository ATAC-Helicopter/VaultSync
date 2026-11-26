using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using VaultSync.Core.Services;

namespace VaultSync.UI.Services;

[SupportedOSPlatform("windows")]
internal static class WindowsRecoveryUsbDevices
{
    private const string InventoryError = "recovery-usb.inventory";
    private static readonly JsonSerializerOptions InventoryJson = new() { RespectRequiredConstructorParameters = true };

    internal sealed record Target(int Number, string FriendlyName, string SerialNumber, long Size,
        string UniqueId, int NumberOfPartitions)
    {
        public string KernelPath => @"\\.\PhysicalDrive" + Number.ToString(CultureInfo.InvariantCulture);
        public override string ToString() => $"{FriendlyName} · {SerialNumber} · {KernelPath} · {Size / 1073741824d:N2} GiB";
    }

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static async Task<IReadOnlyList<Target>> ListAsync(CancellationToken token)
    {
        if (!IsElevated()) return [];
        const string script = """
            $ErrorActionPreference = 'Stop'
            $items = @(Get-Disk | Where-Object {
              $_.BusType -eq 'USB' -and -not $_.IsBoot -and -not $_.IsSystem -and
              -not $_.IsReadOnly -and -not $_.IsOffline -and -not $_.IsClustered -and
              $_.LogicalSectorSize -eq 512
            } | Select-Object Number,FriendlyName,SerialNumber,Size,UniqueId,NumberOfPartitions)
            ConvertTo-Json -InputObject $items -Compress
            """;
        var disks = await QueryAsync<Target[]>(script, token).ConfigureAwait(false);
        return disks.Where(d => d.Number >= 0 && d.Size is > 0 and <= (long)LinuxBlockReadService.MaximumCapacity &&
                !string.IsNullOrWhiteSpace(d.FriendlyName) && !string.IsNullOrWhiteSpace(d.SerialNumber) &&
                d.NumberOfPartitions >= 0 && !string.IsNullOrWhiteSpace(d.UniqueId) && !d.SerialNumber.Any(char.IsControl) &&
                !d.FriendlyName.Any(char.IsControl))
            .GroupBy(d => d.SerialNumber.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
    }

    internal static async Task WriteAsync(Target reviewed, string image, string sha256, IProgress<double> progress, CancellationToken token)
    {
        if (!IsElevated() || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
            throw new IOException("recovery-usb.authorization");

        var staging = Directory.CreateTempSubdirectory("vaultsync-recovery-usb-");
        try
        {
            using var file = File.Open(Path.Combine(staging.FullName, "recovery.raw"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using (var source = File.Open(image, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length <= 0 || source.Length > reviewed.Size)
                    throw new IOException("recovery-usb.image-size");
                await source.CopyToAsync(file, token).ConfigureAwait(false);
            }
            file.Position = 0;
            if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, token).ConfigureAwait(false),
                    Convert.FromHexString(sha256))) throw new IOException("recovery-usb.image-digest");
            await DiskImageGptInspectionService.ResizeRecoveryUsbImageAsync(file, reviewed.Size, token).ConfigureAwait(false);
            file.Position = 0;
            var digest = await SHA256.HashDataAsync(file, token).ConfigureAwait(false);

            await RecheckAsync(reviewed, token).ConfigureAwait(false);
            using var disk = Open(reviewed.KernelPath, writeThrough: true);
            CheckHeldDisk(disk, reviewed);
            string[] volumes = await ReadVolumesAsync(reviewed, token).ConfigureAwait(false);
            List<SafeFileHandle> locked = [];
            try
            {
                foreach (string volume in volumes)
                {
                    token.ThrowIfCancellationRequested();
                    var handle = Open(volume.TrimEnd('\\'), writeThrough: false);
                    locked.Add(handle);
                    uint bytes = Control(handle, 0x00560000, new byte[1024], out var extents);
                    if (bytes < 32 || U32(extents, 0) != 1 || U32(extents, 8) != (uint)reviewed.Number)
                        throw new IOException("recovery-usb.volume-owner");
                    Control(handle, 0x00090018, []);
                }
                await RecheckAsync(reviewed, token).ConfigureAwait(false);
                string[] currentVolumes = await ReadVolumesAsync(reviewed, token).ConfigureAwait(false);
                if (!volumes.SequenceEqual(currentVolumes))
                    throw new IOException("recovery-usb.volumes-changed");
                CheckHeldDisk(disk, reviewed);
                token.ThrowIfCancellationRequested();
                foreach (var volume in locked) Control(volume, 0x00090020, []);
                await RecoveryUsbImageWriter.WriteAndVerifyAsync(file, disk, digest, progress, token).ConfigureAwait(false);
                CheckHeldDisk(disk, reviewed);
            }
            finally { foreach (var handle in locked) handle.Dispose(); }
        }
        finally { staging.Delete(recursive: true); }
    }

    private static async Task RecheckAsync(Target reviewed, CancellationToken token)
    {
        if (!(await ListAsync(token).ConfigureAwait(false)).Contains(reviewed))
            throw new IOException("recovery-usb.changed");
    }

    private sealed record Partition(string[]? AccessPaths);
    private static async Task<string[]> ReadVolumesAsync(Target disk, CancellationToken token)
    {
        if (disk.NumberOfPartitions == 0) return [];
        string number = disk.Number.ToString(CultureInfo.InvariantCulture);
        var partitions = await QueryAsync<Partition[]>(
            "$ErrorActionPreference = 'Stop'; $items = @(Get-Partition -DiskNumber " + number +
            " | Select-Object AccessPaths); ConvertTo-Json -InputObject $items -Compress", token).ConfigureAwait(false);
        if (partitions.Length != disk.NumberOfPartitions)
            throw new IOException("recovery-usb.partition-count");
        return partitions.Select(VolumePath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string VolumePath(Partition partition)
    {
        const string prefix = @"\\?\Volume{";
        var paths = (partition.AccessPaths ?? []).Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            p.Length == prefix.Length + 38 && p.EndsWith("}\\", StringComparison.Ordinal) &&
            Guid.TryParseExact(p.AsSpan(prefix.Length, 36), "D", out _)).ToArray();
        return paths.Length == 1 ? paths[0] : throw new IOException("recovery-usb.unresolved-volume");
    }

    private static void CheckHeldDisk(SafeFileHandle disk, Target reviewed)
    {
        if (Control(disk, 0x002d1080, new byte[12], out var device) < 12 ||
            U32(device, 0) != 7 || U32(device, 4) != (uint)reviewed.Number ||
            Control(disk, 0x000700a0, new byte[256], out var geometry) < 32 || U32(geometry, 20) != 512 ||
            BinaryPrimitives.ReadInt64LittleEndian(geometry.AsSpan(24)) != reviewed.Size)
            throw new IOException("recovery-usb.target-identity");
        uint descriptorBytes = Control(disk, 0x002d1400, new byte[4096], out var descriptor, new byte[12]);
        if (descriptorBytes < 36 || U32(descriptor, 28) != 7)
            throw new IOException("recovery-usb.target-bus");
        uint serial = U32(descriptor, 24);
        if (serial == 0 || serial >= descriptorBytes) throw new IOException("recovery-usb.target-serial");
        int end = Array.IndexOf(descriptor, (byte)0, (int)serial, (int)(descriptorBytes - serial));
        if (end < 0 || !string.Equals(Encoding.ASCII.GetString(descriptor, (int)serial, end - (int)serial).Trim(),
                reviewed.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new IOException("recovery-usb.target-serial");
    }

    private static async Task<T> QueryAsync<T>(string script, CancellationToken token)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        info.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script)) })
            info.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var process = Process.Start(info) ?? throw new IOException(InventoryError);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await errors.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException(InventoryError);
        try
        {
            return JsonSerializer.Deserialize<T>((await output.ConfigureAwait(false)).TrimStart('\uFEFF'), InventoryJson)
                ?? throw new IOException(InventoryError);
        }
        catch (JsonException ex) { throw new IOException(InventoryError, ex); }
    }

    private static SafeFileHandle Open(string path, bool writeThrough)
    {
        var handle = CreateFile(path, 0xc0000000, 3, IntPtr.Zero, 3, writeThrough ? 0x80000000u : 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private static uint Control(SafeFileHandle handle, uint code, byte[] output, out byte[] result, byte[]? input = null)
    {
        if (!DeviceIoControl(handle, code, input, input?.Length ?? 0, output, output.Length, out uint bytes, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        result = output;
        return bytes;
    }

    private static void Control(SafeFileHandle handle, uint code, byte[] output) =>
        Control(handle, code, output, out _);
    private static uint U32(byte[] bytes, int index) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index));

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input,
        int inputLength, [Out] byte[] output, int outputLength, out uint resultLength, IntPtr overlapped);
}
