using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>
/// Development worker bodies: display/correlation data only, never native inventory,
/// freshness/offline proof, consent, or a writable capability. The dispatcher must
/// separately enforce operation state (in particular, never replace a post-write
/// Outcome with Refused). Framing and channel failure ownership remain external.
/// </summary>
internal static class LinuxRecoveryResponses
{
    internal const int InventoryHeaderBytes = 32, DeviceBytes = 168, ReviewBytes = 304;
    internal const int MaximumDevices = 256;
    internal const ulong MaximumRestoreBytes = 8UL * 1024 * 1024 * 1024 * 1024;
    internal sealed record Device(string KernelName, string? ParentKernelName, uint Major, uint Minor,
        ulong Capacity, ulong? DiskSequence, uint LogicalSectorBytes, uint PhysicalSectorBytes,
        LinuxBlockKind Kind, LinuxBlockIdentity Identity, LinuxBlockUse Use,
        bool IsReadOnly, bool MountObserved, bool SwapObserved, bool HasHolders);
    internal sealed record Inventory(Guid ObservationId, DateTimeOffset ObservedAtUtc,
        LinuxBlockInventoryState State, LinuxBlockInventoryReason Reason, bool HasUnresolvedSwaps,
        IReadOnlyList<Device> Devices);
    internal sealed record Review(Guid OperationId, Guid ReviewId, Guid ImageId, Guid CaptureId,
        ulong Generation, string ManifestRoot, DateTimeOffset ObservedAtUtc, ulong OverwriteLength, Device Target, Guid ObservationId);
    internal readonly record struct Progress(Guid ReviewId, ulong AcknowledgedBytes);
    internal sealed record Outcome(Guid ReviewId, LinuxRawRestoreExecution Execution);

    internal static Inventory ProjectInventory(LinuxBlockInventory value)
    {
        if (value is null || value.Devices is null || value.Devices.Count > MaximumDevices) throw Invalid();
        int count = value.Devices.Count;
        var devices = new Device[count];
        for (int i = 0; i < count; i++) devices[i] = ProjectDevice(value.Devices[i]);
        var result = new Inventory(value.ObservationId, value.ObservedAtUtc, value.State, value.Reason,
            value.HasUnresolvedSwaps, Array.AsReadOnly(devices));
        ValidateInventory(result); return result;
    }

    private static Device ProjectDevice(LinuxBlockObservation value)
    {
        if (value is null || value.DeviceNumber is null || value.DeviceNumber.Length > 21) throw Invalid();
        string[] parts = value.DeviceNumber.Split(':');
        if (parts.Length != 2 || !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint major) ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint minor) ||
            value.DeviceNumber != FormattableString.Invariant($"{major}:{minor}")) throw Invalid();
        return new(value.KernelName, value.ParentKernelName, major, minor, value.Capacity, value.DiskSequence,
            value.LogicalSectorBytes, value.PhysicalSectorBytes, value.Kind, value.Identity, value.Use,
            value.IsReadOnly, value.MountObserved, value.SwapObserved, value.HasHolders);
    }

    internal static byte[] EncodeInventory(Inventory value)
    {
        ValidateInventory(value);
        byte[] bytes = new byte[InventoryHeaderBytes + value.Devices.Count * DeviceBytes];
        PutGuid(bytes, value.ObservationId); PutTime(bytes.AsSpan(16), value.ObservedAtUtc);
        bytes[24] = (byte)value.State; bytes[25] = (byte)value.Reason; bytes[26] = value.HasUnresolvedSwaps ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)value.Devices.Count);
        for (int i = 0; i < value.Devices.Count; i++) PutDevice(bytes.AsSpan(32 + i * DeviceBytes), value.Devices[i]);
        return bytes;
    }

    internal static Inventory DecodeInventory(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < InventoryHeaderBytes || bytes[27] != 0 || bytes[26] > 1) throw Invalid();
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
        if (count > MaximumDevices || bytes.Length != InventoryHeaderBytes + count * DeviceBytes) throw Invalid();
        var devices = new Device[(int)count];
        for (int i = 0; i < devices.Length; i++) devices[i] = GetDevice(bytes.Slice(32 + i * DeviceBytes, DeviceBytes));
        var value = new Inventory(GetGuid(bytes), GetTime(bytes[16..]), (LinuxBlockInventoryState)bytes[24],
            (LinuxBlockInventoryReason)bytes[25], bytes[26] == 1, Array.AsReadOnly(devices));
        ValidateInventory(value); return value;
    }

    private static void ValidateInventory(Inventory value)
    {
        if (value is null || value.Devices is null || value.Devices.Count > MaximumDevices) throw Invalid();
        ValidateTime(value.ObservedAtUtc);
        bool observed = value.State == LinuxBlockInventoryState.Observed && value.Reason == LinuxBlockInventoryReason.NamespaceObservationOnly;
        bool failure = (value.State, value.Reason) is
            (LinuxBlockInventoryState.UnsupportedPlatform, LinuxBlockInventoryReason.LinuxRequired) or
            (LinuxBlockInventoryState.Incomplete, LinuxBlockInventoryReason.UnreadableMetadata or LinuxBlockInventoryReason.InvalidMetadata or LinuxBlockInventoryReason.ResourceLimit) or
            (LinuxBlockInventoryState.ChangedDuringRead, LinuxBlockInventoryReason.MetadataChanged);
        if (!observed && !failure || observed && value.ObservationId == Guid.Empty ||
            failure && (value.ObservationId != Guid.Empty || value.Devices.Count != 0 || value.HasUnresolvedSwaps)) throw Invalid();
        var names = new Dictionary<string, Device>(StringComparer.Ordinal);
        var numbers = new HashSet<(uint, uint)>();
        foreach (var device in value.Devices)
        {
            ValidateDevice(device);
            if (!names.TryAdd(device.KernelName, device) || !numbers.Add((device.Major, device.Minor))) throw Invalid();
        }
        foreach (var device in value.Devices)
            if (device.ParentKernelName is { } parent &&
                (!names.TryGetValue(parent, out var disk) || disk.Kind == LinuxBlockKind.Partition ||
                device.Capacity > disk.Capacity || device.LogicalSectorBytes != disk.LogicalSectorBytes ||
                device.PhysicalSectorBytes != disk.PhysicalSectorBytes || device.DiskSequence != disk.DiskSequence ||
                device.Use != disk.Use)) throw Invalid();
    }

    internal static Review ProjectReview(LinuxDiskRestoreOverwriteReview value, Inventory inventory)
    {
        if (value is null || inventory is null || value.Image.State != DiskImageManifestVerificationState.ContentVerified || value.OverwriteOffset != 0) throw Invalid();
        var result = new Review(value.OperationId, value.ReviewId, value.Image.ImageId, value.Image.CaptureId,
            value.Image.Generation, value.Image.ManifestRoot!, value.ObservedAtUtc, value.OverwriteLength, ProjectDevice(value.Target), inventory.ObservationId);
        ValidateReview(result); BindReview(result, result.OperationId, inventory, inventory.ObservationId, result.Target.KernelName);
        return result;
    }

    internal static byte[] EncodeReview(Review value)
    {
        ValidateReview(value); byte[] bytes = new byte[ReviewBytes];
        PutGuid(bytes, value.OperationId); PutGuid(bytes.AsSpan(16), value.ReviewId);
        PutGuid(bytes.AsSpan(32), value.ImageId); PutGuid(bytes.AsSpan(48), value.CaptureId);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(64), value.Generation);
        Convert.FromHexString(value.ManifestRoot).CopyTo(bytes, 72); PutTime(bytes.AsSpan(104), value.ObservedAtUtc);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(112), value.OverwriteLength);
        PutDevice(bytes.AsSpan(120), value.Target); PutGuid(bytes.AsSpan(288), value.ObservationId); return bytes;
    }

    internal static Review DecodeReview(ReadOnlySpan<byte> bytes, Guid expectedOperation, Inventory inventory,
        Guid expectedObservation, string selectedKernelName)
    {
        if (bytes.Length != ReviewBytes) throw Invalid();
        var value = new Review(GetGuid(bytes), GetGuid(bytes[16..]), GetGuid(bytes[32..]), GetGuid(bytes[48..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[64..]), Convert.ToHexStringLower(bytes.Slice(72, 32)),
            GetTime(bytes[104..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[112..]), GetDevice(bytes.Slice(120, DeviceBytes)), GetGuid(bytes[288..]));
        ValidateReview(value); BindReview(value, expectedOperation, inventory, expectedObservation, selectedKernelName); return value;
    }

    private static void BindReview(Review value, Guid operation, Inventory inventory, Guid observation, string selection)
    {
        ValidateInventory(inventory);
        if (operation == Guid.Empty || value.OperationId != operation || observation == Guid.Empty ||
            value.ObservationId != observation || inventory.ObservationId != observation || inventory.State != LinuxBlockInventoryState.Observed ||
            inventory.HasUnresolvedSwaps || value.Target.KernelName != selection ||
            !inventory.Devices.Contains(value.Target)) throw Invalid();
    }

    private static void ValidateReview(Review value)
    {
        if (value is null) throw Invalid();
        ValidateTime(value.ObservedAtUtc); ValidateDevice(value.Target);
        var target = value.Target;
        if (value.OperationId == Guid.Empty || value.ReviewId == Guid.Empty || value.ImageId == Guid.Empty ||
            value.CaptureId == Guid.Empty || value.ObservationId == Guid.Empty || value.Generation == 0 || value.ManifestRoot is not { Length: 64 } ||
            !value.ManifestRoot.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            value.OverwriteLength == 0 || value.OverwriteLength > MaximumRestoreBytes || value.OverwriteLength != target.Capacity ||
            target.Kind != LinuxBlockKind.WholeDisk || target.Identity != LinuxBlockIdentity.UniqueInSnapshot ||
            target.DiskSequence is not > 0 || target.IsReadOnly || target.Use != LinuxBlockUse.Unknown ||
            target.MountObserved || target.SwapObserved || target.HasHolders ||
            target.LogicalSectorBytes is not (512 or 4096) || target.PhysicalSectorBytes is not (512 or 4096)) throw Invalid();
    }

    internal static byte[] EncodeProgress(Progress value, Review review, ulong previousAcknowledged = 0)
    {
        ValidateProgress(value, review, previousAcknowledged); byte[] bytes = new byte[24];
        PutGuid(bytes, value.ReviewId); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), value.AcknowledgedBytes); return bytes;
    }

    internal static Progress DecodeProgress(ReadOnlySpan<byte> bytes, Review review, ulong previousAcknowledged = 0)
    {
        if (bytes.Length != 24) throw Invalid();
        var value = new Progress(GetGuid(bytes), BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]));
        ValidateProgress(value, review, previousAcknowledged); return value;
    }

    private static void ValidateProgress(Progress value, Review review, ulong previous)
    {
        ValidateReview(review);
        if (value.ReviewId != review.ReviewId || !Durable(value.AcknowledgedBytes, review) ||
            !Durable(previous, review) || value.AcknowledgedBytes < previous) throw Invalid();
    }

    internal static byte[] EncodeOutcome(Outcome value, Review review, ulong previousAcknowledged = 0)
    {
        ValidateOutcome(value, review, previousAcknowledged); byte[] bytes = new byte[64]; var execution = value.Execution;
        PutGuid(bytes, value.ReviewId); bytes[16] = (byte)execution.State; bytes[17] = (byte)execution.Reason;
        bytes[18] = execution.WriteResult is { } written ? (byte)written.State : (byte)0;
        bytes[19] = (byte)((execution.JournalAcquired ? 1 : 0) | (execution.CleanupConfirmed ? 2 : 0) |
            (execution.WriteResult is not null ? 4 : 0) | (execution.WriteResult?.TerminalRecordWritten == true ? 8 : 0));
        if (execution.WriteResult is { } result)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), result.AcknowledgedBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), result.UncertainOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), result.UncertainLength);
        }
        return bytes;
    }

    internal static Outcome DecodeOutcome(ReadOnlySpan<byte> bytes, Review review, ulong previousAcknowledged = 0)
    {
        if (bytes.Length != 64 || (bytes[19] & ~15) != 0 || bytes.Slice(20, 4).ContainsAnyExcept((byte)0) ||
            bytes[48..].ContainsAnyExcept((byte)0)) throw Invalid();
        bool hasResult = (bytes[19] & 4) != 0;
        ulong ack = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]), offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]),
            length = BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]);
        if (!hasResult && (bytes[18] != 0 || (bytes[19] & 8) != 0 || ack != 0 || offset != 0 || length != 0)) throw Invalid();
        var result = hasResult ? new RawImageDeviceRestoreResult((RawImageDeviceRestoreState)bytes[18], ack, offset, length, (bytes[19] & 8) != 0) : null;
        var value = new Outcome(GetGuid(bytes), new((RawImageDeviceRestoreState)bytes[16], (LinuxRawRestoreOperationReason)bytes[17],
            result, (bytes[19] & 1) != 0, (bytes[19] & 2) != 0));
        ValidateOutcome(value, review, previousAcknowledged); return value;
    }

    private static void ValidateOutcome(Outcome value, Review review, ulong previous)
    {
        ValidateReview(review);
        if (value is null || value.Execution is null || value.ReviewId != review.ReviewId || !Durable(previous, review)) throw Invalid();
        var execution = value.Execution;
        bool cleanupFailure = execution.State == RawImageDeviceRestoreState.Failed && execution.Reason == LinuxRawRestoreOperationReason.CleanupUnconfirmed;
        bool normal = (execution.State, execution.Reason) is
            (RawImageDeviceRestoreState.Refused, LinuxRawRestoreOperationReason.ReviewRefused or LinuxRawRestoreOperationReason.ImageChanged or
                LinuxRawRestoreOperationReason.SelectionChanged or LinuxRawRestoreOperationReason.ExecutionRefused or
                LinuxRawRestoreOperationReason.TargetUnavailable or LinuxRawRestoreOperationReason.AlreadyConsumed) or
            (RawImageDeviceRestoreState.Interrupted, LinuxRawRestoreOperationReason.Interrupted or LinuxRawRestoreOperationReason.TimedOut) or
            (RawImageDeviceRestoreState.Failed, LinuxRawRestoreOperationReason.Failed) or
            (RawImageDeviceRestoreState.WrittenAndReadBack, LinuxRawRestoreOperationReason.BytesVerified);
        if (!normal && !cleanupFailure || cleanupFailure && execution.CleanupConfirmed ||
            !cleanupFailure && !execution.CleanupConfirmed && execution.Reason != LinuxRawRestoreOperationReason.AlreadyConsumed) throw Invalid();
        if (execution.Reason == LinuxRawRestoreOperationReason.AlreadyConsumed &&
            (execution.JournalAcquired || execution.WriteResult is not null)) throw Invalid();
        if (execution.WriteResult is not { } result)
        {
            if (execution.State == RawImageDeviceRestoreState.WrittenAndReadBack || previous != 0 ||
                execution.Reason == LinuxRawRestoreOperationReason.ExecutionRefused ||
                execution.State == RawImageDeviceRestoreState.Refused && execution.Reason != LinuxRawRestoreOperationReason.AlreadyConsumed &&
                execution.JournalAcquired != (execution.Reason == LinuxRawRestoreOperationReason.TargetUnavailable)) throw Invalid();
            return;
        }
        if (!execution.JournalAcquired || !WriterState(result.State) || !Durable(result.AcknowledgedBytes, review) ||
            result.AcknowledgedBytes < previous || (!cleanupFailure && execution.State != result.State)) throw Invalid();
        bool uncertain = result.UncertainLength != 0;
        if (uncertain ? result.UncertainOffset != result.AcknowledgedBytes || result.AcknowledgedBytes == review.OverwriteLength ||
                result.UncertainLength != Math.Min((ulong)RawImageDeviceRestoreService.ChunkBytes, review.OverwriteLength - result.AcknowledgedBytes)
            : result.UncertainOffset != 0) throw Invalid();
        if (result.State == RawImageDeviceRestoreState.WrittenAndReadBack &&
            (result.AcknowledgedBytes != review.OverwriteLength || uncertain || !result.TerminalRecordWritten)) throw Invalid();
        if (result.State == RawImageDeviceRestoreState.Refused &&
            (result.AcknowledgedBytes != 0 || uncertain || result.TerminalRecordWritten)) throw Invalid();
        if (!cleanupFailure && execution.State == RawImageDeviceRestoreState.Refused &&
            execution.Reason != LinuxRawRestoreOperationReason.ExecutionRefused) throw Invalid();
    }

    private static bool WriterState(RawImageDeviceRestoreState state) => state is RawImageDeviceRestoreState.Refused or
        RawImageDeviceRestoreState.Interrupted or RawImageDeviceRestoreState.Failed or RawImageDeviceRestoreState.WrittenAndReadBack;
    private static bool Durable(ulong bytes, Review review) => bytes <= review.OverwriteLength &&
        (bytes == review.OverwriteLength || bytes % RawImageDeviceRestoreService.ChunkBytes == 0);

    // Only pre-execution refusals. A stateful dispatcher must disallow this after execution begins.
    internal static byte[] EncodeRefused(LinuxRawRestoreOperationReason reason)
    {
        if (!RefusalReason(reason)) throw Invalid(); byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)reason); return bytes;
    }
    internal static LinuxRawRestoreOperationReason DecodeRefused(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 4) throw Invalid(); uint raw = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (raw > int.MaxValue || !RefusalReason((LinuxRawRestoreOperationReason)raw)) throw Invalid();
        return (LinuxRawRestoreOperationReason)raw;
    }
    private static bool RefusalReason(LinuxRawRestoreOperationReason reason) => reason is
        LinuxRawRestoreOperationReason.ReviewRefused or LinuxRawRestoreOperationReason.ImageChanged or
        LinuxRawRestoreOperationReason.SelectionChanged or LinuxRawRestoreOperationReason.JournalUnavailable or
        LinuxRawRestoreOperationReason.TargetUnavailable or LinuxRawRestoreOperationReason.AlreadyConsumed;

    private static void ValidateDevice(Device value)
    {
        if (value is null || !Selector(value.KernelName) || value.ParentKernelName is { } parent && !Selector(parent) ||
            value.Kind is not (LinuxBlockKind.WholeDisk or LinuxBlockKind.Partition or LinuxBlockKind.VirtualOrStacked or LinuxBlockKind.Other) ||
            value.Identity is not (LinuxBlockIdentity.Unknown or LinuxBlockIdentity.UniqueInSnapshot or LinuxBlockIdentity.Ambiguous) ||
            value.Use is not (LinuxBlockUse.Unknown or LinuxBlockUse.ObservedInUse) ||
            !Sector(value.LogicalSectorBytes) || !Sector(value.PhysicalSectorBytes) || value.PhysicalSectorBytes < value.LogicalSectorBytes ||
            value.Capacity % value.LogicalSectorBytes != 0 || value.DiskSequence == 0 ||
            (value.Kind == LinuxBlockKind.Partition) != (value.ParentKernelName is not null) || value.KernelName == value.ParentKernelName ||
            value.Kind != LinuxBlockKind.WholeDisk && value.Identity != LinuxBlockIdentity.Unknown ||
            (value.MountObserved || value.SwapObserved || value.HasHolders) && value.Use != LinuxBlockUse.ObservedInUse) throw Invalid();
    }
    private static bool Sector(uint value) => value is >= 512 and <= 1048576 && (value & (value - 1)) == 0;
    private static bool Selector(string value) => value is { Length: > 0 and <= 63 } &&
        value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');
    private static void PutDevice(Span<byte> bytes, Device value)
    {
        ValidateDevice(value); PutSelector(bytes[..64], value.KernelName); PutSelector(bytes.Slice(64, 64), value.ParentKernelName);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[128..], value.Major); BinaryPrimitives.WriteUInt32LittleEndian(bytes[132..], value.Minor);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[136..], value.Capacity); BinaryPrimitives.WriteUInt64LittleEndian(bytes[144..], value.DiskSequence ?? 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[152..], value.LogicalSectorBytes); BinaryPrimitives.WriteUInt32LittleEndian(bytes[156..], value.PhysicalSectorBytes);
        bytes[160] = (byte)value.Kind; bytes[161] = (byte)value.Identity; bytes[162] = (byte)value.Use;
        bytes[163] = (byte)((value.IsReadOnly ? 1 : 0) | (value.MountObserved ? 2 : 0) | (value.SwapObserved ? 4 : 0) | (value.HasHolders ? 8 : 0));
    }
    private static Device GetDevice(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != DeviceBytes || (bytes[163] & ~15) != 0 || bytes[164..].ContainsAnyExcept((byte)0)) throw Invalid();
        ulong sequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes[144..]);
        var value = new Device(GetSelector(bytes[..64], false)!, GetSelector(bytes.Slice(64, 64), true),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[128..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[132..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[136..]), sequence == 0 ? null : sequence,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[152..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[156..]),
            (LinuxBlockKind)bytes[160], (LinuxBlockIdentity)bytes[161], (LinuxBlockUse)bytes[162],
            (bytes[163] & 1) != 0, (bytes[163] & 2) != 0, (bytes[163] & 4) != 0, (bytes[163] & 8) != 0);
        ValidateDevice(value); return value;
    }
    private static void PutSelector(Span<byte> bytes, string? value)
    {
        bytes.Clear(); if (value is not null) { if (!Selector(value)) throw Invalid(); Encoding.ASCII.GetBytes(value, bytes); }
    }
    private static string? GetSelector(ReadOnlySpan<byte> bytes, bool optional)
    {
        int end = bytes.IndexOf((byte)0);
        if (end < 0 || bytes[end..].ContainsAnyExcept((byte)0) || end == 0 && !optional) throw Invalid();
        if (end == 0) return null;
        foreach (byte b in bytes[..end]) if (b > 127) throw Invalid();
        string result = Encoding.ASCII.GetString(bytes[..end]); if (!Selector(result)) throw Invalid(); return result;
    }
    private static void ValidateTime(DateTimeOffset time)
    { if (time.Offset != TimeSpan.Zero || time < DateTimeOffset.UnixEpoch) throw Invalid(); }
    private static void PutTime(Span<byte> bytes, DateTimeOffset time)
    { ValidateTime(time); BinaryPrimitives.WriteInt64LittleEndian(bytes, time.Ticks); }
    private static DateTimeOffset GetTime(ReadOnlySpan<byte> bytes)
    {
        long ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        if (ticks < DateTimeOffset.UnixEpoch.Ticks || ticks > DateTimeOffset.MaxValue.Ticks) throw Invalid();
        return new(ticks, TimeSpan.Zero);
    }
    private static void PutGuid(Span<byte> bytes, Guid value) => value.TryWriteBytes(bytes, bigEndian: true, out _);
    private static Guid GetGuid(ReadOnlySpan<byte> bytes) => new(bytes[..16], bigEndian: true);
    private static InvalidDataException Invalid() => new("recovery-protocol.invalid-response");
}
