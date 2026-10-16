using Encspect.Core;

namespace Encspect.Formats.Matroska;

// Why an element header could not be read.
internal enum HeaderProblem
{
    None,

    // The data ends inside the header, or where it starts.
    Truncated,

    // A header begins with a byte of zero bits, which no variable-size integer of at most eight octets starts with (RFC 8794 section 4.1).
    NotAnInteger,

    // The Element ID is longer than EBMLMaxIDLength allows (RFC 8794 section 5), or is the reserved value 0xFF.
    InvalidId,
}

// An element's ID and Element Data Size (RFC 8794 section 3), with where they lie. Size is the length of the Element Data,
// or null when the size is the reserved value that says it is unknown (section 6.2).
internal readonly record struct ElementHeader(long Offset, ulong Id, int IdLength, int SizeLength, ulong? Size, bool IsLongSize, bool IsLongId)
{
    public int Length => IdLength + SizeLength;

    public long DataOffset => Offset + Length;

    public bool IsUnknownSize => Size is null;

    // Where the element ends by its own account, saturating, or null where its size is unknown.
    public long? End => Size is { } size ? (size > (ulong)(long.MaxValue - DataOffset) ? long.MaxValue : DataOffset + (long)size) : null;
}

internal static class Ebml
{
    // RFC 8794 section 4: the number of octets of a variable-size integer from its first octet, or 0 for a first octet of zero bits.
    public static int Width(byte first) => first == 0 ? 0 : System.Numerics.BitOperations.LeadingZeroCount((uint)first << 24) + 1;

    // Reads an Element ID (section 5) and Element Data Size (section 6) at position, not past limit. maxIdLength and
    // maxSizeLength are the EBML header's (section 11.2.4, 11.2.5). A size longer than maxSizeLength is read and flagged
    // (IsLongSize); an ID longer than maxIdLength is a problem.
    public static HeaderProblem TryReadHeader(ByteReader reader, long position, long limit, int maxIdLength, int maxSizeLength, out ElementHeader header)
    {
        header = default;
        long available = Math.Min(limit, reader.End) - position;
        if (available < 2 || position < reader.Start)
        {
            return HeaderProblem.Truncated;
        }

        Span<byte> bytes = stackalloc byte[16];
        int count = (int)Math.Min(available, bytes.Length);
        reader.Position = position;
        reader.Read(bytes[..count]);
        return TryParseHeader(bytes[..count], position, maxIdLength, maxSizeLength, out header);
    }

    public static HeaderProblem TryParseHeader(ReadOnlySpan<byte> bytes, long position, int maxIdLength, int maxSizeLength, out ElementHeader header)
    {
        header = default;
        int idLength = Width(bytes[0]);
        if (idLength == 0)
        {
            return HeaderProblem.NotAnInteger;
        }

        if (idLength > maxIdLength || idLength > 8)
        {
            return HeaderProblem.InvalidId;
        }

        if (bytes.Length < idLength + 1)
        {
            return HeaderProblem.Truncated;
        }

        ulong id = 0;
        for (int i = 0; i < idLength; i++)
        {
            id = (id << 8) | bytes[i];
        }

        // The VINT_DATA of an ID is not all ones (section 5); all zeros is legal for 0x80 (RFC 9559 section 4.2).
        ulong dataMask = (1UL << (7 * idLength)) - 1;
        if ((id & dataMask) == dataMask)
        {
            return HeaderProblem.InvalidId;
        }

        bool isLongId = idLength > 1 && (id & dataMask) < (1UL << (7 * (idLength - 1))) - 1;

        int sizeLength = Width(bytes[idLength]);
        if (sizeLength == 0)
        {
            return HeaderProblem.NotAnInteger;
        }

        if (bytes.Length < idLength + sizeLength)
        {
            return HeaderProblem.Truncated;
        }

        ulong size = bytes[idLength] & (0xFFUL >> sizeLength);
        for (int i = 1; i < sizeLength; i++)
        {
            size = (size << 8) | bytes[idLength + i];
        }

        ulong all = (1UL << (7 * sizeLength)) - 1;
        bool unknown = size == all;
        header = new ElementHeader(position, id, idLength, sizeLength, unknown ? null : size, sizeLength > maxSizeLength, isLongId);
        return HeaderProblem.None;
    }

    // The unsigned value of up to eight big-endian octets (RFC 8794 section 7.2).
    public static ulong Unsigned(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        foreach (byte b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    // Two's complement (section 7.1).
    public static long Signed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return 0;
        }

        long value = (sbyte)bytes[0];
        for (int i = 1; i < bytes.Length; i++)
        {
            value = (value << 8) | bytes[i];
        }

        return value;
    }
}
