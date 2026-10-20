using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

public enum DiskInventoryWorkerOutcome { Completed, Unavailable, Failed, TimedOut }

/// <summary>Transport completion is not device identity, offline proof or write authorization.</summary>
public sealed record DiskInventoryWorkerResult(DiskInventoryWorkerOutcome Outcome, LinuxBlockInventory? Inventory);

/// <summary>Unprivileged, one-shot consumer of the fixed adjacent inventory worker.</summary>
public static class DiskInventoryWorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        MaxDepth = 8,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static Task<DiskInventoryWorkerResult> ObserveAsync(CancellationToken cancellationToken = default)
    {
        string name = OperatingSystem.IsWindows() ? "VaultSync.DiskWorker.exe" : "VaultSync.DiskWorker";
        return ObserveAsync(Path.Combine(AppContext.BaseDirectory, name), TimeSpan.FromSeconds(35), cancellationToken);
    }

    // Only local integration tests may choose a worker. No protocol or UI input selects a program.
    internal static async Task<DiskInventoryWorkerResult> ObserveAsync(string workerPath, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(workerPath) || !File.Exists(workerPath) ||
            new FileInfo(workerPath).LinkTarget is not null)
            return new(DiskInventoryWorkerOutcome.Unavailable, null);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(workerPath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(workerPath)!
            }
        };
        bool started = false;
        try
        {
            started = process.Start();
            if (!started)
                return new(DiskInventoryWorkerOutcome.Failed, null);
            Guid operation = Guid.NewGuid();
            // Read output concurrently: a broken peer must not deadlock on a full pipe.
            Task<LinuxBlockInventory> response = ReadResponseAsync(process.StandardOutput.BaseStream, operation, deadline.Token);
            Task quiet = RequireEmptyDiagnosticsAsync(process.StandardError.BaseStream, deadline.Token);
            async Task SendRequestAsync()
            {
                await WriteRequestAsync(process.StandardInput.BaseStream, operation, deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close(); // EOF is mandatory, even for a valid 32-byte frame.
            }
            var pending = new List<Task> { response, quiet, SendRequestAsync(), process.WaitForExitAsync(deadline.Token) };
            // Observe every eventual fault, including pipe disposal after a timeout. React
            // to the first failing task instead of waiting for a blocked peer to exit.
            _ = Task.WhenAll(pending).ContinueWith(static completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            while (pending.Count != 0)
            {
                Task completed = await Task.WhenAny(pending).WaitAsync(deadline.Token).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            if (process.ExitCode != 0)
                return new(DiskInventoryWorkerOutcome.Failed, null);
            return new(DiskInventoryWorkerOutcome.Completed, await response.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(DiskInventoryWorkerOutcome.TimedOut, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   InvalidDataException or System.ComponentModel.Win32Exception)
        { return new(DiskInventoryWorkerOutcome.Failed, null); }
        finally
        {
            deadline.Cancel();
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                // Never wait indefinitely for a failed peer during cancellation/timeout.
                try { await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
        }
    }

    internal static async Task WriteRequestAsync(Stream output, Guid operation, CancellationToken token)
    {
        if (operation == Guid.Empty)
            throw new ArgumentException("An operation identifier is required.", nameof(operation));
        byte[] frame = new byte[DiskInventoryWorkerService.RequestBytes];
        "VSINVREQ"u8.CopyTo(frame);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), DiskInventoryWorkerService.ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), DiskInventoryWorkerService.InventoryOperation);
        operation.TryWriteBytes(frame.AsSpan(16), bigEndian: true, out _);
        await output.WriteAsync(frame, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<LinuxBlockInventory> ReadResponseAsync(Stream input, Guid operation, CancellationToken token)
    {
        byte[] header = new byte[DiskInventoryWorkerService.ResponseHeaderBytes];
        await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        if (operation == Guid.Empty || !header.AsSpan(0, 8).SequenceEqual("VSINVRSP"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != DiskInventoryWorkerService.ProtocolVersion ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) != DiskInventoryWorkerService.InventoryOperation ||
            new Guid(header.AsSpan(16, 16), bigEndian: true) != operation)
            throw new InvalidDataException("inventory.response-header");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32));
        if (length is 0 or > DiskInventoryWorkerService.MaximumResponseBytes)
            throw new InvalidDataException("inventory.response-limit");
        byte[] body = new byte[(int)length];
        await input.ReadExactlyAsync(body, token).ConfigureAwait(false);
        if (await input.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("inventory.response-trailing-data");
        LinuxBlockInventory inventory = JsonSerializer.Deserialize<LinuxBlockInventory>(body, JsonOptions)
            ?? throw new InvalidDataException("inventory.response-null");
        Validate(inventory);
        return inventory;
    }

    private static async Task RequireEmptyDiagnosticsAsync(Stream input, CancellationToken token)
    {
        // The worker contract has numeric exits only. Never retain or log arbitrary peer text.
        if (await input.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
            throw new InvalidDataException("inventory.unexpected-diagnostics");
    }

    private static void Validate(LinuxBlockInventory inventory)
    {
        if (!Enum.IsDefined(inventory.State) || !Enum.IsDefined(inventory.Reason) || inventory.Devices is null ||
            inventory.Devices.Count > LinuxBlockInventoryService.MaximumDevices || inventory.ObservedAtUtc == default)
            throw new InvalidDataException("inventory.response-schema");
        if (inventory.State != LinuxBlockInventoryState.Observed)
        {
            bool reasonValid = inventory.State switch
            {
                LinuxBlockInventoryState.UnsupportedPlatform => inventory.Reason == LinuxBlockInventoryReason.LinuxRequired,
                LinuxBlockInventoryState.ChangedDuringRead => inventory.Reason == LinuxBlockInventoryReason.MetadataChanged,
                _ => inventory.Reason is LinuxBlockInventoryReason.UnreadableMetadata or
                    LinuxBlockInventoryReason.InvalidMetadata or LinuxBlockInventoryReason.ResourceLimit
            };
            if (!reasonValid || inventory.Devices.Count != 0 || inventory.ObservationId != Guid.Empty ||
                inventory.BootId != Guid.Empty || inventory.MountNamespace is not null || inventory.HasUnresolvedSwaps)
                throw new InvalidDataException("inventory.response-failure-state");
            return;
        }
        if (inventory.Reason != LinuxBlockInventoryReason.NamespaceObservationOnly ||
            inventory.ObservationId == Guid.Empty || inventory.BootId == Guid.Empty ||
            inventory.MountNamespace is not { Length: >= 7 and <= 32 } ns ||
            !ns.StartsWith("mnt:[", StringComparison.Ordinal) || !ns.EndsWith(']') ||
            !ulong.TryParse(ns.AsSpan(5, ns.Length - 6), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            throw new InvalidDataException("inventory.response-observation");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        foreach (LinuxBlockObservation device in inventory.Devices)
        {
            if (device is null || !Name(device.KernelName) || !names.Add(device.KernelName) ||
                device.DeviceNumber is not { Length: >= 3 and <= 32 } number || !numbers.Add(number) ||
                !ValidNumber(number) || !Enum.IsDefined(device.Kind) || !Enum.IsDefined(device.Identity) ||
                !Enum.IsDefined(device.Use) || device.HardwareIdentityCandidate?.Length > 263 ||
                device.HardwareIdentityCandidate?.Any(char.IsControl) == true ||
                device.ParentKernelName is not null && !Name(device.ParentKernelName) ||
                device.LogicalSectorBytes is < 512 or > 1048576 ||
                (device.LogicalSectorBytes & (device.LogicalSectorBytes - 1)) != 0 ||
                device.PhysicalSectorBytes < device.LogicalSectorBytes || device.PhysicalSectorBytes > 1048576 ||
                (device.PhysicalSectorBytes & (device.PhysicalSectorBytes - 1)) != 0 ||
                device.Capacity % device.LogicalSectorBytes != 0 || device.DiskSequence == 0 ||
                (device.MountObserved || device.SwapObserved || device.HasHolders) && device.Use != LinuxBlockUse.ObservedInUse)
                throw new InvalidDataException("inventory.response-device");
        }
        foreach (LinuxBlockObservation device in inventory.Devices)
            if (device.ParentKernelName is not null &&
                (device.ParentKernelName == device.KernelName || !names.Contains(device.ParentKernelName)))
                throw new InvalidDataException("inventory.response-parent");
    }

    private static bool Name(string? value) => value is { Length: > 0 and <= 128 } && value[0] != '.' &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '!');

    private static bool ValidNumber(string value)
    {
        string[] parts = value.Split(':');
        return parts.Length == 2 && parts.All(p => uint.TryParse(p, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out uint n) &&
            p == n.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
