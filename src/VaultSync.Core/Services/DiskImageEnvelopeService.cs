using System.Buffers.Binary;
using System.Text;
using VaultSync.Core.Models;

namespace VaultSync.Core.Services;

/// <summary>Reads only a bounded draft image header; never opens a path or trusts completion.</summary>
public static class DiskImageEnvelopeService
{
    public const int HeaderLength = 24;
    public const uint MaximumManifestLength = 16 * 1024 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VSIMAGE\0");

    public static async ValueTask<DiskImageEnvelopeAssessment> InspectAsync(
        Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The image stream must be readable.", nameof(source));

        byte[] header = new byte[HeaderLength];
        int read = 0;
        while (read < HeaderLength)
        {
            int count = await source.ReadAsync(header.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            read += count;
        }

        if (read < Magic.Length)
            return new(DiskImageEnvelopeState.Incomplete, "image.incomplete-envelope");
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return new(DiskImageEnvelopeState.NotImage, "image.unrecognized-family");
        if (read < HeaderLength)
            return new(DiskImageEnvelopeState.Incomplete, "image.incomplete-envelope");

        ReadOnlySpan<byte> fields = header.AsSpan(Magic.Length);
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(fields);
        ushort minor = BinaryPrimitives.ReadUInt16LittleEndian(fields[2..]);
        uint requiredFeatures = BinaryPrimitives.ReadUInt32LittleEndian(fields[4..]);
        uint manifestLength = BinaryPrimitives.ReadUInt32LittleEndian(fields[8..]);
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(fields[12..]);

        if (major != 1 || minor != 0 || requiredFeatures != 0)
            return new(DiskImageEnvelopeState.Unsupported, "image.unsupported-envelope", major, minor);
        if (reserved != 0 || manifestLength is 0 or > MaximumManifestLength)
            return new(DiskImageEnvelopeState.Invalid, "image.invalid-envelope", major, minor);
        return new(DiskImageEnvelopeState.SupportedEnvelope, "image.envelope-only",
            major, minor, manifestLength);
    }
}
