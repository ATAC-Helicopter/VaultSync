using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

internal enum RawImageRestoreJournalState { Invalid, Incomplete, Failed, Interrupted, ReadBackVerified }
internal sealed record RawImageRestoreJournalAssessment(RawImageRestoreJournalState State,
    ulong AcknowledgedBytes = 0, ulong UncertainOffset = 0, ulong UncertainLength = 0);

// Provisional internal v1 journal; no operation authority or automatic resume.
internal static class RawImageRestoreJournalCodec
{
    internal const int HeaderBytes = 256;
    internal const int RecordBytes = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Header(RawImageDeviceRestoreRequest request)
    {
        var d = request.Target;
        if (request.OperationId == Guid.Empty || request.ReviewId == Guid.Empty || request.ImageId == Guid.Empty ||
            request.CaptureId == Guid.Empty || request.Generation == 0 || !Hash(request.ManifestRoot) ||
            d.Kind != LinuxBlockKind.WholeDisk || d.ParentKernelName is not null ||
            d.Identity != LinuxBlockIdentity.UniqueInSnapshot || d.DiskSequence is not > 0 ||
            d.IsReadOnly || d.MountObserved || d.SwapObserved || d.HasHolders || d.Use != LinuxBlockUse.Unknown ||
            !d.ProfileGeometrySupported || d.PhysicalSectorBytes < d.LogicalSectorBytes ||
            d.Capacity is 0 or > DiskImageRawProfileVerificationService.MaximumCapacity || d.Capacity % d.LogicalSectorBytes != 0 ||
            !Name(d.KernelName) || d.HardwareIdentityCandidate is not { Length: > 0 and <= 263 } ||
            d.HardwareIdentityCandidate.Any(char.IsControl) || !DeviceNumber(d.DeviceNumber, out uint major, out uint minor))
            throw new InvalidDataException("restore-journal.invalid-binding");
        byte[] bytes = new byte[HeaderBytes];
        "VSJRNL01"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        request.OperationId.TryWriteBytes(bytes.AsSpan(16, 16), true, out _);
        request.ReviewId.TryWriteBytes(bytes.AsSpan(32, 16), true, out _);
        request.ImageId.TryWriteBytes(bytes.AsSpan(48, 16), true, out _);
        request.CaptureId.TryWriteBytes(bytes.AsSpan(64, 16), true, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(80), request.Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(88), d.Capacity);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96), d.LogicalSectorBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100), d.PhysicalSectorBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(104), d.DiskSequence.Value);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(112), major);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(116), minor);
        Convert.FromHexString(request.ManifestRoot).CopyTo(bytes, 120);
        try
        {
            SHA256.HashData(StrictUtf8.GetBytes(d.KernelName + "\0" + d.DeviceNumber + "\0" + d.HardwareIdentityCandidate))
                .CopyTo(bytes, 152);
        }
        catch (EncoderFallbackException) { throw new InvalidDataException("restore-journal.invalid-encoding"); }
        SHA256.HashData(bytes.AsSpan(0, 224)).CopyTo(bytes, 224);
        return bytes;
    }

    internal static ulong MaximumRecords(ulong capacity) =>
        checked(2 * ((capacity + RawImageDeviceRestoreService.ChunkBytes - 1) / RawImageDeviceRestoreService.ChunkBytes) + 3);
    internal static ulong Reservation(ulong capacity) => checked(MaximumRecords(capacity) * RecordBytes);
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Name(string? value) => value is { Length: > 0 and <= 128 } && value[0] != '.' &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '!');
    private static bool DeviceNumber(string value, out uint major, out uint minor)
    {
        major = minor = 0;
        if (value is not { Length: >= 3 and <= 21 }) return false;
        string[] parts = value.Split(':');
        return parts.Length == 2 && uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major) &&
            uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor) &&
            value == major.ToString(CultureInfo.InvariantCulture) + ":" + minor.ToString(CultureInfo.InvariantCulture);
    }

    internal sealed class Cursor(RawImageDeviceRestoreRequest request, byte[] header)
    {
        private byte[] previous = header.AsSpan(224, 32).ToArray();
        private RawImageRestoreRecord? pending;
        private RawImageRestoreRecordKind? last;
        internal ulong Sequence { get; private set; }
        internal ulong Acknowledged { get; private set; }
        internal RawImageRestoreJournalAssessment Assessment => last switch
        {
            RawImageRestoreRecordKind.ReadBackVerified => new(RawImageRestoreJournalState.ReadBackVerified, Acknowledged),
            RawImageRestoreRecordKind.Failed => new(RawImageRestoreJournalState.Failed, Acknowledged,
                pending?.Offset ?? 0, pending?.Length ?? 0),
            RawImageRestoreRecordKind.Interrupted => new(RawImageRestoreJournalState.Interrupted, Acknowledged,
                pending?.Offset ?? 0, pending?.Length ?? 0),
            _ => new(RawImageRestoreJournalState.Incomplete, Acknowledged, Acknowledged, request.Target.Capacity - Acknowledged)
        };

        internal byte[] Encode(RawImageRestoreRecord record)
        {
            Validate(record);
            byte[] bytes = new byte[RecordBytes];
            "VSR1"u8.CopyTo(bytes);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), Sequence + 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)record.Kind);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), record.Offset);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), record.Length);
            if (record.Sha256 is not null) Convert.FromHexString(record.Sha256).CopyTo(bytes, 32);
            previous.CopyTo(bytes, 64);
            SHA256.HashData(bytes.AsSpan(0, 96)).CopyTo(bytes, 96);
            return bytes;
        }

        internal void Accept(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != RecordBytes || !bytes[..4].SequenceEqual("VSR1"u8) ||
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[4..]) != Sequence + 1 ||
                !bytes.Slice(64, 32).SequenceEqual(previous) || !SHA256.HashData(bytes[..96]).AsSpan().SequenceEqual(bytes[96..]))
                throw new InvalidDataException("restore-journal.invalid-chain");
            var kind = (RawImageRestoreRecordKind)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
            string? hash = kind is RawImageRestoreRecordKind.WriteIntent or RawImageRestoreRecordKind.RangeFlushed
                ? Convert.ToHexStringLower(bytes.Slice(32, 32)) : null;
            if (hash is null && bytes.Slice(32, 32).ContainsAnyExcept((byte)0))
                throw new InvalidDataException("restore-journal.unexpected-hash");
            var record = new RawImageRestoreRecord(kind, request,
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]), hash);
            Validate(record);
            Sequence++;
            previous = bytes[96..].ToArray();
            if (record.Kind == RawImageRestoreRecordKind.WriteIntent) pending = record;
            if (record.Kind == RawImageRestoreRecordKind.RangeFlushed) { Acknowledged += record.Length; pending = null; }
            last = record.Kind;
        }

        private void Validate(RawImageRestoreRecord r)
        {
            if (r.Request != request || Sequence >= MaximumRecords(request.Target.Capacity) ||
                last is RawImageRestoreRecordKind.Failed or RawImageRestoreRecordKind.Interrupted)
                throw new InvalidDataException("restore-journal.binding-or-terminal");
            bool valid = r.Kind switch
            {
                RawImageRestoreRecordKind.Started => Sequence == 0 && r.Offset == 0 && r.Length == 0 && r.Sha256 is null,
                RawImageRestoreRecordKind.WriteIntent => last is RawImageRestoreRecordKind.Started or RawImageRestoreRecordKind.RangeFlushed &&
                    pending is null && Acknowledged < request.Target.Capacity && r.Offset == Acknowledged &&
                    r.Length == Math.Min((ulong)RawImageDeviceRestoreService.ChunkBytes, request.Target.Capacity - Acknowledged) && Hash(r.Sha256),
                RawImageRestoreRecordKind.RangeFlushed => pending is not null && last == RawImageRestoreRecordKind.WriteIntent &&
                    r.Offset == pending.Offset && r.Length == pending.Length && r.Sha256 == pending.Sha256,
                RawImageRestoreRecordKind.ReadBackVerified => last == RawImageRestoreRecordKind.RangeFlushed &&
                    Acknowledged == request.Target.Capacity && pending is null && r.Offset == 0 && r.Length == Acknowledged && r.Sha256 is null,
                RawImageRestoreRecordKind.Failed or RawImageRestoreRecordKind.Interrupted => Sequence > 0 &&
                    (last != RawImageRestoreRecordKind.ReadBackVerified || r.Kind == RawImageRestoreRecordKind.Failed) &&
                    r.Offset == (pending?.Offset ?? 0) && r.Length == (pending?.Length ?? 0) && r.Sha256 is null,
                _ => false
            };
            if (!valid) throw new InvalidDataException("restore-journal.invalid-transition");
        }
    }

    internal static async ValueTask<RawImageRestoreJournalAssessment> InspectAsync(Stream metadata, Stream records,
        RawImageDeviceRestoreRequest expected, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!metadata.CanRead || !records.CanRead || ReferenceEquals(metadata, records) ||
            metadata.CanSeek && metadata.Position != 0 || records.CanSeek && records.Position != 0)
            return new(RawImageRestoreJournalState.Invalid);
        Cursor? cursor = null;
        try
        {
            byte[] header = new byte[HeaderBytes];
            await metadata.ReadExactlyAsync(header, token).ConfigureAwait(false);
            if (!header.AsSpan().SequenceEqual(Header(expected)) || await metadata.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                return new(RawImageRestoreJournalState.Invalid);
            cursor = new(expected, header);
            byte[] frame = new byte[RecordBytes];
            while (await records.ReadAsync(frame.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
            {
                await records.ReadExactlyAsync(frame.AsMemory(1), token).ConfigureAwait(false);
                cursor.Accept(frame);
            }
            return cursor.Assessment;
        }
        catch (EndOfStreamException) { return new(RawImageRestoreJournalState.Incomplete,
            cursor?.Acknowledged ?? 0, cursor?.Acknowledged ?? 0, expected.Target.Capacity - (cursor?.Acknowledged ?? 0)); }
        catch (InvalidDataException) { return new(RawImageRestoreJournalState.Invalid); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
        { return new(RawImageRestoreJournalState.Invalid); }
    }
}
