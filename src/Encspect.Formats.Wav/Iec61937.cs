using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Wav;

// How the length code of a burst counts its payload (IEC 61937-2, Table 2).
internal enum BurstUnit
{
    Bits,
    Bytes,
    EightBytes,
}

// A data type of IEC 61937-2 Table 2 with a fixed repetition period: its name, the period in IEC 60958 frames, the unit of the length code, and the ID
// of the codec parser that reads the payload, or null where the analyzer has none for it.
internal sealed record BurstType(string Name, int Repetition, BurstUnit Unit, string? Codec);

// A chain found at the start of the data chunk: where its first burst of any kind starts (a pause or null burst may come before the audio), and the
// data type (bits 0 to 6 of Pc) and bitstream number of the audio bursts.
internal sealed record BurstChain(long First, int DataType, int Bitstream, BurstType Type);

// A burst preamble as read: the words Pa to Pd, with Pc taken apart.
internal readonly record struct BurstHeader(int Pc, int Pd)
{
    public int DataType => Pc & 0x7F;

    public bool HasError => (Pc & 0x80) != 0;

    public int Info => (Pc >> 8) & 0x1F;

    public int Bitstream => Pc >> 13;
}

// Data bursts of IEC 61937 in the samples of a 16-bit stereo PCM file (S/PDIF passthrough captured or rendered to WAVE): each burst is a preamble of
// four 16-bit words, Pa and Pb the sync words F872h and 4E1Fh, Pc the burst-info and Pd the length code, then the payload, which is an encoded audio
// frame, and stuffing up to the repetition period of the data type (IEC 61937-1 6.1.6 and 6.1.7, IEC 61937-2 Table 2). The words are the 16-bit
// samples, so the file holds them little-endian and the payload's bytes in pairs swapped. The analyzer works from the freely available previews of
// IEC 61937-1:2021, -2:2021 and -3:2017 and from Microsoft's description of the link rates; docs/formats/wav.md says what could not be verified.
internal static class Iec61937
{
    public const int PreambleLength = 8;

    // The bytes of one IEC 60958 frame: a 16-bit sample of each of two channels. A burst starts at a frame.
    public const int FrameLength = 4;

    // The consecutive bursts of one data type at the repetition period a chain needs before the samples are taken for a stream (the sync rule of
    // CLAUDE.md: a sync word alone is no stream).
    public const int ChainLength = 3;

    // How far into the data zero frames and pause or null bursts are skipped before the first audio burst, so that a recording that starts in silence
    // or in a gap of the stream is still found; a longer lead-in is PCM.
    private const int MaxSilence = 1 << 20;

    // The bursts listed as nodes; the rest are counted.
    public const int MaxListed = 1024;

    private static readonly byte[] Sync = [0x72, 0xF8, 0x1F, 0x4E];

    // The data types of IEC 61937-2:2021 Table 2 whose repetition period is a number, by the value of bits 0 to 6 of Pc (the conventional data type
    // in bits 0 to 4, the sub-data type in bits 5 and 6).
    private static readonly Dictionary<int, BurstType> Types = new()
    {
        [1] = new("AC-3", 1536, BurstUnit.Bits, "ac-3"),
        [4] = new("MPEG-1 Layer I", 384, BurstUnit.Bits, null),
        [5] = new("MPEG-1 Layer II or III", 1152, BurstUnit.Bits, null),
        [6] = new("MPEG-2 audio with extension", 1152, BurstUnit.Bits, null),
        [7] = new("MPEG-2 AAC", 1024, BurstUnit.Bits, "aac-adts"),
        [8] = new("MPEG-2 Layer I", 768, BurstUnit.Bits, null),
        [9] = new("MPEG-2 Layer II", 2304, BurstUnit.Bits, null),
        [10] = new("MPEG-2 Layer III", 1152, BurstUnit.Bits, null),
        [11] = new("DTS type I", 512, BurstUnit.Bits, "dts"),
        [12] = new("DTS type II", 1024, BurstUnit.Bits, "dts"),
        [13] = new("DTS type III", 2048, BurstUnit.Bits, "dts"),
        [14] = new("ATRAC", 512, BurstUnit.Bits, null),
        [15] = new("ATRAC 2/3", 1024, BurstUnit.Bits, null),
        [16] = new("ATRAC-X", 2048, BurstUnit.Bits, null),
        [48] = new("ATRAC-X low latency", 512, BurstUnit.Bits, null),
        [80] = new("ATRAC-X low latency", 256, BurstUnit.Bits, null),
        [112] = new("ATRAC-X low latency", 128, BurstUnit.Bits, null),
        [18] = new("WMA Professional type I", 2048, BurstUnit.Bits, null),
        [50] = new("WMA Professional type II", 2048, BurstUnit.Bits, null),
        [82] = new("WMA Professional type III", 1024, BurstUnit.Bits, null),
        [114] = new("WMA Professional type IV", 512, BurstUnit.Bits, null),
        [19] = new("MPEG-2 AAC low sampling frequency", 2048, BurstUnit.Bits, "aac-adts"),
        [51] = new("MPEG-2 AAC low sampling frequency", 4096, BurstUnit.Bits, "aac-adts"),
        [20] = new("MPEG-4 AAC", 1024, BurstUnit.Bits, "aac-adts"),
        [52] = new("MPEG-4 AAC", 2048, BurstUnit.Bits, "aac-adts"),
        [84] = new("MPEG-4 AAC", 4096, BurstUnit.Bits, "aac-adts"),
        [116] = new("MPEG-4 AAC", 512, BurstUnit.Bits, "aac-adts"),
        [21] = new("E-AC-3", 6144, BurstUnit.Bytes, "e-ac-3"),
        [22] = new("MAT", 15360, BurstUnit.Bytes, null),
    };

    public static BurstType? TypeOf(int dataType) => Types.GetValueOrDefault(dataType);

    // The data types that carry no audio: null data and the pause burst (IEC 61937-1 7.2 and 7.4).
    public static bool IsPauseOrNull(int dataType) => dataType is 0 or 3;

    // The name of a data type for a node's value: the audio types of the table, null data, pause, or a value the table does not give a period.
    public static string NameOf(int dataType) => dataType switch
    {
        0 => "null data",
        3 => "pause",
        _ => TypeOf(dataType)?.Name ?? "a data type without a fixed repetition period",
    };

    public static int LengthBytes(BurstType type, int pd) => type.Unit switch
    {
        BurstUnit.Bits => (pd + 7) / 8,
        BurstUnit.Bytes => pd,
        _ => pd * 8,
    };

    // The burst preamble at position if the sync words are there and the data type is one the analyzer knows (an audio type with a repetition
    // period, null data or pause); null otherwise. Looks at 8 bytes.
    public static BurstHeader? HeaderAt(BurstWindow data, long position)
    {
        ReadOnlySpan<byte> bytes = data.At(position, PreambleLength);
        if (bytes.Length < PreambleLength || !bytes[..4].SequenceEqual(Sync))
        {
            return null;
        }

        var header = new BurstHeader(BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]));
        return IsPauseOrNull(header.DataType) || TypeOf(header.DataType) is not null ? header : null;
    }

    // Where the frame after a pause or null burst at position lies: the preamble and the payload that the length code states in bits (IEC 61937-1
    // 7.2 and 7.4), up to a whole frame.
    public static long EndOfPause(long position, BurstHeader header) =>
        position + (((PreambleLength + ((header.Pd + 7) / 8) + FrameLength - 1) / FrameLength) * FrameLength);

    // A chain of bursts at the start of the data, after any silence and any pause or null bursts (a capture that starts in a gap of the stream):
    // ChainLength bursts of one audio data type and bitstream number, each at the repetition period of the type after the one before, each with a
    // payload that fits its period and the data. First is where the first burst of any kind starts. Null for anything else, a lone sync word
    // included.
    public static BurstChain? Recognize(ByteReader data)
    {
        var window = new BurstWindow(data);
        long limit = Math.Min(data.End, data.Start + MaxSilence);
        long first = SkipZeroFrames(window, data.Start, limit);
        long position = first;
        BurstHeader audio;
        while (true)
        {
            if (position >= limit || HeaderAt(window, position) is not { } header)
            {
                return null;
            }

            if (!IsPauseOrNull(header.DataType))
            {
                audio = header;
                break;
            }

            position = SkipZeroFrames(window, EndOfPause(position, header), limit);
        }

        if (TypeOf(audio.DataType) is not { } type)
        {
            return null;
        }

        long period = (long)type.Repetition * FrameLength;
        for (int i = 0; i < ChainLength; i++)
        {
            long at = position + (i * period);
            if (HeaderAt(window, at) is not { } header || header.DataType != audio.DataType || header.Bitstream != audio.Bitstream)
            {
                return null;
            }

            long payload = LengthBytes(type, header.Pd);
            if (payload == 0 || payload > period - PreambleLength || at + PreambleLength + payload > data.End)
            {
                return null;
            }
        }

        return new BurstChain(first, audio.DataType, audio.Bitstream, type);
    }

    // The first frame from position on that is not zero, as a position, or limit when the frames up to it are all zero.
    private static long SkipZeroFrames(BurstWindow data, long position, long limit)
    {
        const int Block = 1 << 14;
        while (position < limit)
        {
            ReadOnlySpan<byte> block = data.At(position, (int)Math.Min(Block, limit - position));
            if (block.Length == 0)
            {
                break;
            }

            int first = block.IndexOfAnyExcept((byte)0);
            if (first >= 0)
            {
                return position + (first / FrameLength * FrameLength);
            }

            position += block.Length;
        }

        return limit;
    }

    // The payload as the coding has it: the words are 16-bit samples, so a pair of bytes is swapped. A payload of an odd number of bytes ends in the
    // first byte of a word whose second byte is the zero stuffing, which the swap moves in front of it.
    public static byte[] Payload(ReadOnlySpan<byte> raw, int length)
    {
        byte[] result = new byte[length];
        for (int i = 0; i + 1 < raw.Length && i < length; i += 2)
        {
            result[i] = raw[i + 1];
            if (i + 1 < length)
            {
                result[i + 1] = raw[i];
            }
        }

        return result;
    }
}
