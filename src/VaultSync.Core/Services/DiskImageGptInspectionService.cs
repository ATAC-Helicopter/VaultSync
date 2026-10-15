using System.Buffers.Binary;
using System.Security.Cryptography;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>
/// Bounded read-only GPT inspection on a caller-owned seekable stream. Opens no paths,
/// writes nothing and preserves position. Does not verify filesystems, boot or device identity.
/// I/O errors and cancellation propagate; truncated metadata has an explicit assessment.
/// </summary>
public static class DiskImageGptInspectionService
{
    public const int MaximumEntries = 1024;
    private const int EntryBytes = 128;
    private const int HeaderBytes = 92;
    private sealed record Header(ulong Current, ulong Alternate, ulong First, ulong Last,
        Guid DiskId, ulong EntriesLba, uint Count, uint EntriesCrc);

    public static async ValueTask<DiskImageGptInspectionAssessment> InspectAsync(
        Stream source, ulong capacity, uint logicalSectorBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (logicalSectorBytes is not (512 or 4096) || capacity > long.MaxValue ||
            capacity % logicalSectorBytes != 0 || capacity / logicalSectorBytes < 2 * (16384UL / logicalSectorBytes) + 4)
            return Invalid(DiskImageGptInspectionReason.InvalidGeometry);
        if (!source.CanRead || !source.CanSeek)
            return Unsupported(DiskImageGptInspectionReason.SeekableSourceRequired);
        if ((ulong)source.Length != capacity)
            return new(source.Length < (long)capacity ? DiskImageGptInspectionState.Incomplete : DiskImageGptInspectionState.Invalid,
                DiskImageGptInspectionReason.SourceLengthMismatch);
        long originalPosition = source.Position;
        try
        {
            ulong lastLba = capacity / logicalSectorBytes - 1;
            byte[] mbr = await ReadAtAsync(source, 0, (int)logicalSectorBytes, cancellationToken).ConfigureAwait(false);
            if (mbr[510] != 0x55 || mbr[511] != 0xaa || mbr[450] != 0xee)
                return Unsupported(DiskImageGptInspectionReason.ProtectiveMbrRequired);
            if (mbr.AsSpan(462, 48).ContainsAnyExcept((byte)0))
                return Unsupported(DiskImageGptInspectionReason.HybridMbrUnsupported);
            if (mbr[446] != 0 || U32(mbr, 454) != 1 || U32(mbr, 458) != Math.Min(lastLba, uint.MaxValue))
                return Invalid(DiskImageGptInspectionReason.InvalidProtectiveMbr);

            byte[] primaryBytes = await ReadAtAsync(source, logicalSectorBytes, (int)logicalSectorBytes, cancellationToken).ConfigureAwait(false);
            var primary = ParseHeader(primaryBytes, out DiskImageGptInspectionAssessment? failure);
            if (failure is not null) return failure;
            byte[] backupBytes = await ReadAtAsync(source, lastLba * logicalSectorBytes, (int)logicalSectorBytes, cancellationToken).ConfigureAwait(false);
            var backup = ParseHeader(backupBytes, out failure);
            if (failure is not null) return failure;
            Header first = primary!, second = backup!;
            if (first.Current != 1 || first.Alternate != lastLba || second.Current != lastLba || second.Alternate != 1 ||
                first.DiskId != second.DiskId || first.First != second.First || first.Last != second.Last ||
                first.Count != second.Count || first.EntriesCrc != second.EntriesCrc)
                return Invalid(DiskImageGptInspectionReason.HeaderCopiesDisagree);
            ulong sectors = ((ulong)first.Count * EntryBytes + logicalSectorBytes - 1) / logicalSectorBytes;
            if (first.First < sectors + 2 || first.First > first.Last || first.Last >= lastLba ||
                first.EntriesLba < 2 || first.EntriesLba > first.First - sectors ||
                second.EntriesLba <= first.Last || sectors > lastLba || second.EntriesLba > lastLba - sectors)
                return Invalid(DiskImageGptInspectionReason.MetadataOverlap);

            byte[] entries = await ReadAtAsync(source, first.EntriesLba * logicalSectorBytes,
                (int)first.Count * EntryBytes, cancellationToken).ConfigureAwait(false);
            byte[] backupEntries = await ReadAtAsync(source, second.EntriesLba * logicalSectorBytes,
                entries.Length, cancellationToken).ConfigureAwait(false);
            if (Crc32(entries) != first.EntriesCrc || Crc32(backupEntries) != second.EntriesCrc)
                return Invalid(DiskImageGptInspectionReason.EntryChecksumMismatch);
            if (!entries.AsSpan().SequenceEqual(backupEntries))
                return Invalid(DiskImageGptInspectionReason.EntryCopiesDisagree);
            var partitions = new List<DiskImageGptPartition>();
            var identities = new HashSet<Guid>();
            for (int index = 0; index < first.Count; index++)
            {
                int offset = index * EntryBytes;
                Guid type = new(entries.AsSpan(offset, 16));
                if (type == Guid.Empty) continue;
                Guid id = new(entries.AsSpan(offset + 16, 16));
                ulong start = U64(entries, offset + 32), end = U64(entries, offset + 40);
                ulong attributes = U64(entries, offset + 48);
                if (id == Guid.Empty || !identities.Add(id) || start < first.First || end > first.Last || start > end ||
                    (attributes & 0x0000fffffffffff8UL) != 0)
                    return Invalid(DiskImageGptInspectionReason.InvalidPartition);
                partitions.Add(new(index + 1, type, id, start * logicalSectorBytes,
                    (end - start + 1) * logicalSectorBytes, attributes));
            }
            var ordered = partitions.OrderBy(partition => partition.Offset).ToArray();
            for (int index = 1; index < ordered.Length; index++)
                if (ordered[index - 1].Offset + ordered[index - 1].Length > ordered[index].Offset)
                    return Invalid(DiskImageGptInspectionReason.OverlappingPartitions);

            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            digest.AppendData("VaultSync.GptLayout.v1\0"u8);
            byte[] geometry = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(geometry, logicalSectorBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(geometry.AsSpan(4), capacity);
            digest.AppendData(geometry);
            foreach (byte[] region in new[] { mbr, primaryBytes, entries, backupEntries, backupBytes })
                digest.AppendData(region);
            return new(DiskImageGptInspectionState.LayoutInspected, DiskImageGptInspectionReason.LayoutOnly,
                new(first.DiskId, capacity, logicalSectorBytes, first.First, first.Last,
                    partitions.AsReadOnly(), Convert.ToHexStringLower(digest.GetHashAndReset())));
        }
        catch (EndOfStreamException)
        {
            return new(DiskImageGptInspectionState.Incomplete, DiskImageGptInspectionReason.TruncatedMetadata);
        }
        finally { source.Position = originalPosition; }
    }

    private static Header? ParseHeader(byte[] bytes, out DiskImageGptInspectionAssessment? failure)
    {
        failure = null;
        if (!bytes.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
            failure = Invalid(DiskImageGptInspectionReason.InvalidHeader);
        else if (U32(bytes, 8) != 0x00010000 || U32(bytes, 12) != HeaderBytes)
            failure = Unsupported(DiskImageGptInspectionReason.UnsupportedHeader);
        else if (U32(bytes, 20) != 0 || bytes.AsSpan(HeaderBytes).ContainsAnyExcept((byte)0) || new Guid(bytes.AsSpan(56, 16)) == Guid.Empty)
            failure = Invalid(DiskImageGptInspectionReason.InvalidHeader);
        else
        {
            byte[] checkedHeader = bytes[..HeaderBytes];
            checkedHeader.AsSpan(16, 4).Clear();
            if (Crc32(checkedHeader) != U32(bytes, 16))
                failure = Invalid(DiskImageGptInspectionReason.HeaderChecksumMismatch);
            else if (U32(bytes, 84) != EntryBytes || U32(bytes, 80) > MaximumEntries)
                failure = Unsupported(DiskImageGptInspectionReason.UnsupportedEntryFormat);
            else if (U32(bytes, 80) < 128)
                failure = Invalid(DiskImageGptInspectionReason.InvalidHeader);
        }
        return failure is not null ? null : new(U64(bytes, 24), U64(bytes, 32), U64(bytes, 40), U64(bytes, 48),
            new(bytes.AsSpan(56, 16)), U64(bytes, 72), U32(bytes, 80), U32(bytes, 88));
    }

    private static async ValueTask<byte[]> ReadAtAsync(Stream source, ulong offset, int count, CancellationToken token)
    {
        source.Position = (long)offset;
        byte[] bytes = new byte[count];
        await source.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static DiskImageGptInspectionAssessment Invalid(DiskImageGptInspectionReason reason) => new(DiskImageGptInspectionState.Invalid, reason);
    private static DiskImageGptInspectionAssessment Unsupported(DiskImageGptInspectionReason reason) => new(DiskImageGptInspectionState.Unsupported, reason);

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte item in bytes)
        {
            value ^= item;
            for (int bit = 0; bit < 8; bit++)
                value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320U : 0);
        }
        return ~value;
    }
}
