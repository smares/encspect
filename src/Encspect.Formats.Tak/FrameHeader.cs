using Encspect.Core;

namespace Encspect.Formats.Tak;

internal enum HeaderFault : byte
{
    None,

    // The sync word is not there.
    Sync,

    // The sync word is there, but the fields do not decode as takStream.pas defines them (metadata, an undecodable stream info, a reserved or padding bit that is set).
    Structure,

    // The data ends inside the header.
    Cut,

    // The fields decode and the header's length is known, but its CRC-24 differs.
    Crc,
}

// A frame header as tak_str_DecodeFrameHeader reads it (takStream.pas, "Frame-Info" and "Frame-Header"): the sync word 0xA0FF, the last frame, info frame and metadata flags, the 21-bit
// frame number, for a last frame its sample count less 1 (14 bits) and 2 padding bits, for an info frame the stream info, the previous-distance flag, 5 reserved bits and the 25-bit
// distance, padded to a byte, and the CRC-24 over all of it.
internal readonly record struct FrameHeader
{
    // The longest header: 5 bytes of frame info, 2 for a last frame, 23 for the longest stream info, 4 for the flag and the distance, and the CRC-24.
    public const int MaxLength = 5 + 2 + 23 + 4 + 3;

    // The shortest: the frame info and the CRC-24.
    public const int MinLength = 5 + Layout.CrcLength;

    public HeaderFault Fault { get; init; }

    public bool IsLast { get; init; }

    public bool IsInfo { get; init; }

    public int Number { get; init; }

    // The samples of a last frame; 0 for any other.
    public int Samples { get; init; }

    // The length with the CRC-24, where Fault is None or Crc; 0 otherwise.
    public int Length { get; init; }

    public uint StoredCrc { get; init; }

    public uint ComputedCrc { get; init; }

    public StreamInfoFields? Info { get; init; }

    public bool HasDistance { get; init; }

    public int Distance { get; init; }

    // The byte of the header where the stream info of an info frame starts.
    public int InfoOffset => IsLast ? 7 : 5;

    public bool HasLength => Fault is HeaderFault.None or HeaderFault.Crc;

    // bytes are those at the position, as many as the data holds up to MaxLength.
    public static FrameHeader Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0xFF || bytes[1] != 0xA0)
        {
            return new FrameHeader { Fault = bytes.Length < 2 ? HeaderFault.Cut : HeaderFault.Sync };
        }

        HeaderFault overflow = bytes.Length < MaxLength ? HeaderFault.Cut : HeaderFault.Structure;
        var bits = new Layout.LsbBits(bytes);
        bits.Get(16);
        bool isLast = bits.Get(1) != 0;
        bool isInfo = bits.Get(1) != 0;
        bool hasMetadata = bits.Get(1) != 0;
        int number = bits.Get(21);
        int samples = 0;
        if (isLast)
        {
            samples = bits.Get(14) + 1;
            // The 2 padding bits are read and ignored, as tak_str_DecodeFrameInfo does; the CRC-24 still covers them.
            bits.Get(2);
        }

        if (bits.IsOverflow)
        {
            return new FrameHeader { Fault = overflow };
        }

        if (hasMetadata)
        {
            return new FrameHeader { Fault = HeaderFault.Structure };
        }

        StreamInfoFields? info = null;
        bool hasDistance = false;
        int distance = 0;
        if (isInfo)
        {
            info = StreamInfoFields.Read(ref bits);
            if (info is null)
            {
                return new FrameHeader { Fault = overflow };
            }

            hasDistance = bits.Get(1) != 0;
            bool isReserved = bits.Get(5) != 0;
            if (hasDistance)
            {
                distance = bits.Get(25);
            }

            if (bits.IsOverflow)
            {
                return new FrameHeader { Fault = overflow };
            }

            if (isReserved || bits.Align() != 0)
            {
                return new FrameHeader { Fault = HeaderFault.Structure };
            }
        }

        int length = (bits.Position + 7) / 8;
        if (bytes.Length < length + Layout.CrcLength)
        {
            return new FrameHeader { Fault = HeaderFault.Cut };
        }

        uint stored = (uint)(bytes[length] | (bytes[length + 1] << 8) | (bytes[length + 2] << 16));
        uint computed = Layout.Crc24(bytes[..length]);
        return new FrameHeader
        {
            Fault = stored == computed ? HeaderFault.None : HeaderFault.Crc,
            IsLast = isLast,
            IsInfo = isInfo,
            Number = number,
            Samples = samples,
            Length = length + Layout.CrcLength,
            StoredCrc = stored,
            ComputedCrc = computed,
            Info = info,
            HasDistance = hasDistance,
            Distance = distance,
        };
    }
}
