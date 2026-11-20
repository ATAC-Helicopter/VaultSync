using System.Buffers.Binary;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

// Bounded layout observations only. No device identity, path, name or authority.
internal static class DiskGptInspectionProtocol
{
    internal const int HeaderBytes = 128, PartitionBytes = 64;
    internal static byte[] Request(Guid operation, long capacity, uint sector)
    {
        if (operation == Guid.Empty || capacity <= 0 ||
            (ulong)capacity > DiskImageRawProfileVerificationService.MaximumCapacity ||
            sector is not (512 or 4096) || (ulong)capacity % sector != 0)
            throw new InvalidDataException("gpt-inspection.request");
        byte[] bytes = new byte[DiskImageInspectionProtocol.RequestBytes];
        "VSGPREQ1"u8.CopyTo(bytes);
        Put32(bytes, 8, 1); Put32(bytes, 12, 2);
        operation.TryWriteBytes(bytes.AsSpan(16, 16), true, out _);
        Put32(bytes, 32, sector); Put64(bytes, 40, (ulong)capacity);
        return bytes;
    }

    internal static (Guid Operation, long Capacity, uint Sector) DecodeRequest(byte[] bytes)
    {
        if (bytes.Length != DiskImageInspectionProtocol.RequestBytes)
            throw new InvalidDataException("gpt-inspection.request-length");
        Guid operation = new(bytes.AsSpan(16, 16), true);
        long capacity = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(40));
        uint sector = U32(bytes, 32);
        if (!Request(operation, capacity, sector).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("gpt-inspection.request-frame");
        return (operation, capacity, sector);
    }

    internal static byte[] Result(Guid operation, DiskImageGptInspectionAssessment assessment)
    {
        if (operation == Guid.Empty || !Enum.IsDefined(assessment.State) || !Enum.IsDefined(assessment.Reason))
            throw new InvalidDataException("gpt-inspection.result-state");
        bool inspected = assessment.State == DiskImageGptInspectionState.LayoutInspected;
        if (inspected ? assessment.Reason != DiskImageGptInspectionReason.LayoutOnly || !Valid(assessment.Layout)
            : assessment.Layout is not null || assessment.Reason == DiskImageGptInspectionReason.LayoutOnly)
            throw new InvalidDataException("gpt-inspection.result-facts");
        int count = inspected ? assessment.Layout!.Partitions.Count : 0;
        byte[] bytes = new byte[HeaderBytes + PartitionBytes * count];
        "VSGPRSL1"u8.CopyTo(bytes); operation.TryWriteBytes(bytes.AsSpan(8, 16), true, out _);
        Put32(bytes, 24, (uint)assessment.State); Put32(bytes, 28, (uint)assessment.Reason);
        Put32(bytes, 32, 1); // Independent kernel-policy checks remain mandatory.
        if (!inspected) return bytes;
        var layout = assessment.Layout!;
        Put64(bytes, 48, layout.Capacity); Put32(bytes, 56, layout.LogicalSectorBytes); Put32(bytes, 60, (uint)count);
        layout.DiskId.TryWriteBytes(bytes.AsSpan(64, 16), true, out _);
        Put64(bytes, 80, layout.FirstUsableLba); Put64(bytes, 88, layout.LastUsableLba);
        Convert.FromHexString(layout.MetadataSha256).CopyTo(bytes, 96);
        for (int index = 0; index < count; index++)
        {
            var partition = layout.Partitions[index]; int offset = HeaderBytes + index * PartitionBytes;
            Put32(bytes, offset, (uint)partition.EntryNumber);
            partition.TypeId.TryWriteBytes(bytes.AsSpan(offset + 4, 16), true, out _);
            partition.PartitionId.TryWriteBytes(bytes.AsSpan(offset + 20, 16), true, out _);
            Put64(bytes, offset + 36, partition.Offset); Put64(bytes, offset + 44, partition.Length);
            Put64(bytes, offset + 52, partition.Attributes);
        }
        return bytes;
    }

    internal static int ResultLength(byte[] header)
    {
        if (header.Length != HeaderBytes || U32(header, 60) > DiskImageRawProfileVerificationService.MaximumGptEntries)
            throw new InvalidDataException("gpt-inspection.result-bound");
        return HeaderBytes + PartitionBytes * (int)U32(header, 60);
    }

    internal static DiskImageGptInspectionAssessment DecodeResult(byte[] bytes, Guid operation, long capacity, uint sector)
    {
        if (bytes.Length < HeaderBytes || bytes.Length != ResultLength(bytes[..HeaderBytes]) ||
            new Guid(bytes.AsSpan(8, 16), true) != operation)
            throw new InvalidDataException("gpt-inspection.result-length-operation");
        var state = (DiskImageGptInspectionState)U32(bytes, 24);
        var reason = (DiskImageGptInspectionReason)U32(bytes, 28);
        DiskImageGptLayout? layout = null;
        if (state == DiskImageGptInspectionState.LayoutInspected)
        {
            if (U64(bytes, 48) != (ulong)capacity || U32(bytes, 56) != sector)
                throw new InvalidDataException("gpt-inspection.result-geometry");
            var partitions = new List<DiskImageGptPartition>();
            for (int offset = HeaderBytes; offset < bytes.Length; offset += PartitionBytes)
            {
                if (U32(bytes, offset) > int.MaxValue) throw new InvalidDataException("gpt-inspection.partition-number");
                partitions.Add(new((int)U32(bytes, offset), new(bytes.AsSpan(offset + 4, 16), true),
                    new(bytes.AsSpan(offset + 20, 16), true), U64(bytes, offset + 36), U64(bytes, offset + 44), U64(bytes, offset + 52)));
            }
            layout = new(new(bytes.AsSpan(64, 16), true), U64(bytes, 48), U32(bytes, 56),
                U64(bytes, 80), U64(bytes, 88), partitions.AsReadOnly(), Convert.ToHexStringLower(bytes.AsSpan(96, 32)));
        }
        var result = new DiskImageGptInspectionAssessment(state, reason, layout);
        if (!Result(operation, result).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("gpt-inspection.result-frame");
        return result;
    }

    private static bool Valid(DiskImageGptLayout? layout) => layout is not null && layout.Partitions is not null &&
        layout.Capacity <= DiskImageRawProfileVerificationService.MaximumCapacity &&
        layout.Partitions.Count <= DiskImageRawProfileVerificationService.MaximumGptEntries &&
        layout.Partitions.All(partition => partition is not null && partition.EntryNumber <= DiskImageRawProfileVerificationService.MaximumGptEntries) &&
        DiskImageOriginRecordCodec.Valid(new(Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"), layout.LogicalSectorBytes,
            DateTimeOffset.UnixEpoch, "inspection", new string('0', 40), layout));

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Put64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
}
