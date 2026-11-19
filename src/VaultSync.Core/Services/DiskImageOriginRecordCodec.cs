using System.Buffers.Binary;
using System.Text;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

// Manifest schema 2 source claims. Neither kind proves offline consistency or producer trust.
internal static class DiskImageOriginRecordCodec
{
    internal const int HeaderLength = 216;
    private const int PartitionLength = 64;

    internal static bool Valid(DiskImageCaptureOrigin origin)
    {
        DiskImageGptLayout layout = origin.Layout;
        if (origin.SourceKind is not (DiskImageSourceKind.RegularFileFixture or DiskImageSourceKind.OfflineBlockDevice) ||
            origin.SourceId == Guid.Empty || origin.ObservationId == Guid.Empty || origin.CapturedAtUtc.Offset != TimeSpan.Zero ||
            origin.CapturedAtUtc.ToUnixTimeMilliseconds() < 0 || origin.PhysicalSectorBytes is not (512 or 4096) ||
            layout is null || layout.LogicalSectorBytes is not (512 or 4096) || origin.PhysicalSectorBytes < layout.LogicalSectorBytes ||
            layout.Capacity == 0 || layout.Capacity > long.MaxValue || layout.Capacity % layout.LogicalSectorBytes != 0 ||
            layout.DiskId == Guid.Empty || !Digest(layout.MetadataSha256) || layout.Partitions is null ||
            layout.Partitions.Count > DiskImageGptInspectionService.MaximumEntries ||
            layout.FirstUsableLba < 2 || layout.FirstUsableLba > layout.LastUsableLba ||
            layout.LastUsableLba >= layout.Capacity / layout.LogicalSectorBytes - 1 ||
            !ValidTool(origin.ToolVersion, origin.ToolSourceCommit))
            return false;
        int lastEntry = 0;
        var identities = new HashSet<Guid>();
        foreach (DiskImageGptPartition partition in layout.Partitions)
        {
            if (partition is null || partition.EntryNumber <= lastEntry || partition.EntryNumber > DiskImageGptInspectionService.MaximumEntries ||
                partition.TypeId == Guid.Empty || partition.PartitionId == Guid.Empty || !identities.Add(partition.PartitionId) ||
                partition.Length == 0 || partition.Offset % layout.LogicalSectorBytes != 0 || partition.Length % layout.LogicalSectorBytes != 0 ||
                partition.Offset / layout.LogicalSectorBytes < layout.FirstUsableLba || partition.Offset >= layout.Capacity ||
                partition.Length > layout.Capacity - partition.Offset ||
                (partition.Offset + partition.Length) / layout.LogicalSectorBytes - 1 > layout.LastUsableLba ||
                (partition.Attributes & 0x0000fffffffffff8UL) != 0)
                return false;
            lastEntry = partition.EntryNumber;
        }
        var ordered = layout.Partitions.OrderBy(p => p.Offset).ToArray();
        return !ordered.Skip(1).Where((p, i) => ordered[i].Offset + ordered[i].Length > p.Offset).Any();
    }

    internal static bool ValidTool(string? version, string? commit) =>
        !string.IsNullOrEmpty(version) && version.Length <= 32 &&
        version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-') &&
        commit is { Length: 40 or 64 } && Hex(commit);

    internal static byte[] Encode(DiskImageCaptureOrigin origin)
    {
        if (!Valid(origin)) throw new ArgumentException("Invalid fixture origin", nameof(origin));
        byte[] bytes = new byte[HeaderLength + PartitionLength * origin.Layout.Partitions.Count];
        Put32(bytes, 0, 1); // origin schema
        Put32(bytes, 4, (uint)origin.SourceKind);
        PutGuid(bytes, 8, origin.SourceId);
        PutGuid(bytes, 24, origin.ObservationId);
        Put32(bytes, 40, origin.PhysicalSectorBytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(44), origin.CapturedAtUtc.ToUnixTimeMilliseconds());
        PutGuid(bytes, 52, origin.Layout.DiskId);
        Convert.FromHexString(origin.Layout.MetadataSha256).CopyTo(bytes, 68);
        Put64(bytes, 100, origin.Layout.FirstUsableLba);
        Put64(bytes, 108, origin.Layout.LastUsableLba);
        Put32(bytes, 116, (uint)origin.Layout.Partitions.Count);
        Encoding.ASCII.GetBytes(origin.ToolVersion).CopyTo(bytes, 120);
        Encoding.ASCII.GetBytes(origin.ToolSourceCommit).CopyTo(bytes, 152);
        for (int index = 0; index < origin.Layout.Partitions.Count; index++)
        {
            DiskImageGptPartition p = origin.Layout.Partitions[index];
            int offset = HeaderLength + index * PartitionLength;
            Put32(bytes, offset, (uint)p.EntryNumber);
            PutGuid(bytes, offset + 4, p.TypeId);
            PutGuid(bytes, offset + 20, p.PartitionId);
            Put64(bytes, offset + 36, p.Offset);
            Put64(bytes, offset + 44, p.Length);
            Put64(bytes, offset + 52, p.Attributes);
        }
        return bytes;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, ulong capacity, uint sector,
        out DiskImageCaptureOrigin? origin, out int consumed, out bool unsupported)
    {
        origin = null;
        consumed = 0;
        unsupported = false;
        if (bytes.Length < HeaderLength) return false;
        if (U32(bytes, 0) != 1 || U32(bytes, 4) is not (1 or 2)) { unsupported = true; return false; }
        uint count = U32(bytes, 116);
        if (count > DiskImageGptInspectionService.MaximumEntries || (ulong)HeaderLength + count * PartitionLength > (ulong)bytes.Length)
            return false;
        long milliseconds = BinaryPrimitives.ReadInt64LittleEndian(bytes[44..]);
        if (milliseconds < 0 || milliseconds > 253402300799999L) return false;
        string? version = Text(bytes.Slice(120, 32)), commit = Text(bytes.Slice(152, 64));
        if (version is null || commit is null) return false;
        var partitions = new List<DiskImageGptPartition>((int)count);
        for (int index = 0; index < count; index++)
        {
            int offset = HeaderLength + index * PartitionLength;
            uint entry = U32(bytes, offset);
            if (entry > int.MaxValue || U32(bytes, offset + 60) != 0) return false;
            partitions.Add(new((int)entry, new(bytes.Slice(offset + 4, 16), bigEndian: true),
                new(bytes.Slice(offset + 20, 16), bigEndian: true), U64(bytes, offset + 36), U64(bytes, offset + 44), U64(bytes, offset + 52)));
        }
        var layout = new DiskImageGptLayout(new(bytes.Slice(52, 16), bigEndian: true), capacity, sector,
            U64(bytes, 100), U64(bytes, 108), partitions.AsReadOnly(), Convert.ToHexStringLower(bytes.Slice(68, 32)));
        var decoded = new DiskImageCaptureOrigin(new(bytes.Slice(8, 16), bigEndian: true), new(bytes.Slice(24, 16), bigEndian: true),
            U32(bytes, 40), DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), version, commit, layout)
        { SourceKind = (DiskImageSourceKind)U32(bytes, 4) };
        if (!Valid(decoded)) return false;
        consumed = HeaderLength + (int)count * PartitionLength;
        origin = decoded;
        return true;
    }

    private static string? Text(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end < 0) end = bytes.Length;
        if (bytes[end..].ContainsAnyExcept((byte)0) || bytes[..end].ContainsAnyExceptInRange((byte)33, (byte)126)) return null;
        return Encoding.ASCII.GetString(bytes[..end]);
    }
    private static bool Digest(string? value) => value is { Length: 64 } && Hex(value);
    private static bool Hex(string value) => value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static uint U32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b[o..]);
    private static ulong U64(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
    private static void Put32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    private static void Put64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v);
    private static void PutGuid(byte[] b, int o, Guid v) => v.TryWriteBytes(b.AsSpan(o, 16), bigEndian: true, out _);
}
