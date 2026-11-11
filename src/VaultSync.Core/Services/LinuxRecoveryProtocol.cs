using System.Buffers.Binary;
using System.Text;

namespace VaultSync.Core.Services;

internal enum LinuxRecoveryRole { Controller, Worker }
internal enum LinuxRecoveryMessage : uint
{
    OfferImages = 1, InventoryRequest, Prepare, Confirm, Cancel, Heartbeat,
    Ready = 257, Staging, Inventory, Review, Executing, Progress, Outcome, Refused
}

/// <summary>Bounded framing, not operation authorization. Consumers must validate typed payloads.</summary>
internal static class LinuxRecoveryProtocol
{
    internal const int HeaderBytes = 40, MaximumBody = 65536;
    internal readonly record struct Header(LinuxRecoveryMessage Kind, Guid Operation, uint Sequence, int BodyBytes);

    internal static byte[] EncodeHeader(LinuxRecoveryRole sender, LinuxRecoveryMessage kind, Guid operation,
        uint sequence, int bodyBytes)
    {
        if (!Valid(sender, kind, bodyBytes) || operation == Guid.Empty || sequence == 0) throw Refused();
        byte[] bytes = new byte[HeaderBytes];
        "VSRPC001"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)kind);
        operation.TryWriteBytes(bytes.AsSpan(16, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), sequence);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), bodyBytes);
        return bytes;
    }

    internal static Header DecodeHeader(ReadOnlySpan<byte> bytes, LinuxRecoveryRole sender,
        Guid expectedOperation, uint expectedSequence)
    {
        if (bytes.Length != HeaderBytes || !bytes[..8].SequenceEqual("VSRPC001"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != 1) throw Refused();
        var kind = (LinuxRecoveryMessage)BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var operation = new Guid(bytes.Slice(16, 16), bigEndian: true);
        uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]);
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[36..]);
        if (operation == Guid.Empty || operation != expectedOperation || sequence == 0 || sequence != expectedSequence ||
            !Valid(sender, kind, length)) throw Refused();
        return new(kind, operation, sequence, length);
    }

    internal static byte[] Selection(Guid observation, string kernelName)
    {
        if (observation == Guid.Empty || !KernelName(kernelName)) throw Refused();
        byte[] bytes = new byte[80]; observation.TryWriteBytes(bytes.AsSpan(0, 16), bigEndian: true, out _);
        Encoding.ASCII.GetBytes(kernelName, bytes.AsSpan(16)); return bytes;
    }

    internal static (Guid Observation, string KernelName) DecodeSelection(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 80) throw Refused();
        var observation = new Guid(bytes[..16], bigEndian: true);
        int end = bytes[16..].IndexOf((byte)0);
        if (observation == Guid.Empty || end <= 0 || end > 63 || bytes[(16 + end)..].ContainsAnyExcept((byte)0)) throw Refused();
        // Validate bytes before ASCII conversion; replacement characters must not
        // normalize an invalid selector into a different device name.
        foreach (byte value in bytes.Slice(16, end)) if (value > 127) throw Refused();
        string name = Encoding.ASCII.GetString(bytes.Slice(16, end));
        if (!KernelName(name)) throw Refused();
        return (observation, name);
    }

    internal static byte[] ReviewAcknowledgment(Guid review)
    {
        if (review == Guid.Empty) throw Refused();
        byte[] bytes = new byte[16]; review.TryWriteBytes(bytes, bigEndian: true, out _); return bytes;
    }
    internal static Guid DecodeReviewAcknowledgment(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16) throw Refused();
        var review = new Guid(bytes, bigEndian: true); return review != Guid.Empty ? review : throw Refused();
    }

    private static bool KernelName(string name) => name is { Length: > 0 and <= 63 } &&
        name.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');

    private static bool Valid(LinuxRecoveryRole sender, LinuxRecoveryMessage kind, int length)
    {
        if (length < 0 || length > MaximumBody) return false;
        if (sender == LinuxRecoveryRole.Controller)
            return kind switch
            {
                LinuxRecoveryMessage.OfferImages or LinuxRecoveryMessage.InventoryRequest or LinuxRecoveryMessage.Cancel or
                    LinuxRecoveryMessage.Heartbeat => length == 0,
                LinuxRecoveryMessage.Prepare => length == 80,
                LinuxRecoveryMessage.Confirm => length == 16,
                _ => false
            };
        if (sender != LinuxRecoveryRole.Worker) return false;
        return kind switch
        {
            LinuxRecoveryMessage.Ready or LinuxRecoveryMessage.Staging or LinuxRecoveryMessage.Executing => length == 0,
            LinuxRecoveryMessage.Inventory => length is > 0 and <= MaximumBody,
            LinuxRecoveryMessage.Review => length is > 0 and <= 4096,
            LinuxRecoveryMessage.Progress => length == 24,
            LinuxRecoveryMessage.Outcome => length == 64,
            LinuxRecoveryMessage.Refused => length == 4,
            _ => false
        };
    }
    private static InvalidDataException Refused() => new("recovery-protocol.invalid-frame");
}
