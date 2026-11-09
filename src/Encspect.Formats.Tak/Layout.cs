using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Tak;

// The layout of a TAK file's container (TAK 2.3.3, Sdk/Source/takStream.pas): the stream ID "tBaK", metadata objects, the audio frames, then an APEv2 tag.
//
// The reading of the container follows takStream.pas, "Hopefully anything needed to understand, how TAK's container works" (version 2.2.0),
// Copyright 2006 by Thomas Becker, D-49080 Osnabrueck, Germany. All rights reserved. Its license: "Do with it, whatever you want. But please: Don't distribute
// this module without the SDK containing it. If for some reason you want to publish a modified version, please state clearly, that it isn't an official release."
// THIS IS NOT AN OFFICIAL RELEASE OF TAK: Encspect's reader is written from that module (not copied from it, and the module is not committed) and from the files
// Takc.exe 2.3.3 wrote, and it departs from the module where the files do (docs/formats/tak.md, THIRD-PARTY-NOTICES.md). Nothing is taken from any other source.
internal static class Layout
{
    public const int SignatureLength = 4;

    public const int ObjectHeaderLength = 4;

    public const int CrcLength = 3;

    public const int MaxObjectSize = (1 << 24) - 1;

    // The IDs of the metadata objects (tak_str_MetaDataId_*); the header's 7 bits allow 127.
    public const int End = 0;
    public const int StreamInfo = 1;
    public const int SeekTable = 2;
    public const int WaveData = 3;
    public const int EncoderInfo = 4;
    public const int Padding = 5;
    public const int Md5 = 6;
    public const int LastFrameInfo = 7;

    // The sizes of the objects with a fixed one, with their CRC-24 (the size counts it): the last frame info is 5 bytes of position and 3 of size, the encoder info 4 bytes,
    // the MD5 16.
    public const int LastFrameInfoSize = 8 + CrcLength;
    public const int EncoderInfoSize = 4 + CrcLength;
    public const int Md5Size = 16 + CrcLength;

    // The stream info's bits: 10 of encoder info, 39 of size info and 31 of audio format at the least (tak_str_StreamInfoBitsMin), 102 more with the extension.
    public const int StreamInfoSizeMin = (80 + 7) / 8 + CrcLength;
    public const int StreamInfoSizeMax = (182 + 7) / 8 + CrcLength;

    // The wave data's object holds a 3-byte header size, a 3-byte footer size, the bytes and the CRC-24 (tak_str_SimpleWaveDataSizeMax is 1 MiB).
    public const int WaveDataFixed = 6 + CrcLength;
    public const int MaxWaveData = 1 << 20;

    public const int SampleRateMin = 6000;
    public const int SampleRateMax = 262144;
    public const int MaxChannels = 16;
    public const int MaxFrameSamples = 16384;
    public const int FrameSizeTypes = 10;

    public const uint Crc24Initial = 0xB704CE;
    private const uint Crc24Polynomial = 0x864CFB;

    private static readonly uint[] Crc24Table = CreateCrc24Table();

    private static readonly int[] FrameSizeQuants = [3, 4, 6, 8, 4096, 8192, 16384, 512, 1024, 2048];

    public static ReadOnlySpan<byte> Signature => "tBaK"u8;

    public static string ObjectName(int id) => id switch
    {
        End => "End",
        StreamInfo => "Stream Info",
        SeekTable => "Seek Table",
        WaveData => "Wave Data",
        EncoderInfo => "Encoder Info",
        Padding => "Padding",
        Md5 => "MD5",
        LastFrameInfo => "Last Frame Info",
        _ => "Unknown Object",
    };

    // Whether the object carries a CRC-24 as its last 3 bytes (the stream info, seek table, wave data, encoder info, MD5 and last frame info; not the End object,
    // and not the padding, which pipe encoding fills with zeros: the departures in docs/formats/tak.md).
    public static bool HasCrc(int id) => id is StreamInfo or SeekTable or WaveData or EncoderInfo or Md5 or LastFrameInfo;

    // CRC-24 of RFC 2440 (OpenPGP): initial value 0xB704CE, polynomial 0x864CFB, most significant bit first, no final inversion.
    public static uint Crc24(ReadOnlySpan<byte> data) => Crc24Update(Crc24Initial, data);

    public static uint Crc24Update(uint crc, ReadOnlySpan<byte> data)
    {
        uint[] table = Crc24Table;
        foreach (byte b in data)
        {
            crc = ((crc << 8) ^ table[((crc >> 16) ^ b) & 0xFF]) & 0xFFFFFF;
        }

        return crc;
    }

    private static uint[] CreateCrc24Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i << 16;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 0x800000) != 0 ? ((value << 1) ^ Crc24Polynomial) & 0xFFFFFF : (value << 1) & 0xFFFFFF;
            }

            table[i] = value;
        }

        return table;
    }

    // The samples of a frame by its size type and the sample rate (tak_str_CalcFrameSizeFromType): a time type (0 to 3) is the rate times 3, 4, 6 or 8 over 32 seconds
    // (94, 125, 188 and 250 ms), a size type (4 to 9) a number of samples. Zero for a type that is none.
    public static int FrameSamples(int sizeType, int sampleRate) => sizeType is >= 0 and < FrameSizeTypes
        ? sizeType <= 3 ? (int)(((long)sampleRate * FrameSizeQuants[sizeType]) >> 5) : FrameSizeQuants[sizeType]
        : 0;

    // Whether the frame size is one the decoder accepts: a time type of at most 16,384 samples, a size type of at most 250 ms.
    public static bool IsFrameSizeValid(int sizeType, int sampleRate) => sizeType is >= 0 and < FrameSizeTypes
        && (sizeType <= 3 ? FrameSamples(sizeType, sampleRate) <= MaxFrameSamples : FrameSizeQuants[sizeType] <= (int)(((long)sampleRate * FrameSizeQuants[3]) >> 5));

    public static string FrameSizeTypeName(int sizeType) => sizeType switch
    {
        0 => "94 ms",
        1 => "125 ms",
        2 => "188 ms",
        3 => "250 ms",
        4 => "4096 samples",
        5 => "8192 samples",
        6 => "16384 samples",
        7 => "512 samples",
        8 => "1024 samples",
        9 => "2048 samples",
        15 => "size follows (not implemented)",
        _ => $"reserved ({sizeType})",
    };

    // The codecs as Takc's readme and tak_GetCodecName name them.
    public static string? CodecName(int codec) => codec switch
    {
        0 => "Integer 24 bit (TAK 1.0)",
        1 => "Experimental!",
        2 => "Integer 24 bit (TAK 2.0)",
        3 => "LossyWav (TAK 2.1 Beta)",
        4 => "Integer 24 bit MC (TAK 2.2)",
        _ => null,
    };

    // The preset as Takc prints it: "-p" and the level, then "e" for the evaluation Extra or "m" for Max.
    public static string PresetName(int preset, int evaluation) => $"-p{preset}{(evaluation switch { 1 => "e", 2 => "m", _ => string.Empty })}";

    public static string? EvaluationName(int evaluation) => evaluation switch { 0 => "Standard", 1 => "Extra", 2 => "Max", _ => null };

    public static string? SpeakerName(int speaker) => speaker switch
    {
        0 => "none",
        1 => "front left",
        2 => "front right",
        3 => "front center",
        4 => "low frequency",
        5 => "back left",
        6 => "back right",
        7 => "front left of center",
        8 => "front right of center",
        9 => "back center",
        10 => "side left",
        11 => "side right",
        12 => "top center",
        13 => "top front left",
        14 => "top front center",
        15 => "top front right",
        16 => "top back left",
        17 => "top back center",
        18 => "top back right",
        _ => null,
    };

    // The speakers of a TAK assignment as the project's layout words: the front left to top back right values 1 to 18 are the bits of a WAVEFORMATEXTENSIBLE mask.
    public static uint? SpeakerMask(IReadOnlyList<int> speakers)
    {
        uint mask = 0;
        foreach (int speaker in speakers)
        {
            if (speaker == 0)
            {
                continue;
            }

            if (speaker > 18 || (mask & (1u << (speaker - 1))) != 0)
            {
                return null;
            }

            mask |= 1u << (speaker - 1);
        }

        return mask;
    }

    public static void ReadObjectHeader(uint header, out int id, out bool isReserved, out int size)
    {
        id = (int)(header & 127);
        isReserved = (header & 128) != 0;
        size = (int)(header >> 8);
    }

    // The bit structures of takStream.pas: items start at the least significant bits of each byte, bytes in order.
    public ref struct LsbBits(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; }

        public bool IsOverflow { get; private set; }

        public int Get(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int at = Position + i;
                if (at / 8 >= _data.Length)
                {
                    IsOverflow = true;
                    continue;
                }

                value |= ((_data[at / 8] >> (at % 8)) & 1) << i;
            }

            Position += count;
            return value;
        }

        public long GetLong(int count)
        {
            long value = 0;
            for (int i = 0; i < count; i += 24)
            {
                value |= (long)Get(Math.Min(24, count - i)) << i;
            }

            return value;
        }

        public int Align() => Get((8 - (Position % 8)) % 8);
    }

    // The frame header at a position (tak_str_DecodeFrameHeader): its length with the CRC-24, or 0 where the sync word is not there, the header does not decode (it states
    // metadata, which is not supported), its reserved and padding bits are not zero, or its CRC-24 differs. The header is 5, 7 (a last frame) or more bytes (an info frame
    // holds the stream info) and a CRC-24.
    public static int FrameHeaderLength(ByteReader reader, long position)
    {
        const int MaxHeader = 5 + 2 + 23 + 4 + 3;
        long available = reader.End - position;
        if (available < 5 + CrcLength || !reader.MatchesAt(position, [0xFF, 0xA0]))
        {
            return 0;
        }

        Span<byte> bytes = stackalloc byte[(int)Math.Min(available, MaxHeader)];
        reader.Position = position;
        reader.Read(bytes);
        var bits = new LsbBits(bytes);
        bits.Get(16);
        bool isLast = bits.Get(1) != 0;
        bool isInfo = bits.Get(1) != 0;
        bool hasMetadata = bits.Get(1) != 0;
        bits.Get(21);
        if (isLast)
        {
            bits.Get(14);
            if (bits.Get(2) != 0)
            {
                return 0;
            }
        }

        if (hasMetadata)
        {
            return 0;
        }

        if (isInfo)
        {
            if (StreamInfoFields.Read(ref bits) is null)
            {
                return 0;
            }

            bool hasDistance = bits.Get(1) != 0;
            if (bits.Get(5) != 0)
            {
                return 0;
            }

            if (hasDistance)
            {
                bits.Get(25);
            }

            if (bits.Align() != 0)
            {
                return 0;
            }
        }

        int length = (bits.Position + 7) / 8;
        if (bits.IsOverflow || bytes.Length < length + CrcLength)
        {
            return 0;
        }

        uint stored = (uint)(bytes[length] | (bytes[length + 1] << 8) | (bytes[length + 2] << 16));
        return Crc24(bytes[..length]) == stored ? length + CrcLength : 0;
    }
}

// The stream info (tak_str_Ttak_str_StreamInfo): the encoder info (codec and profile), the size info (frame size type and sample count) and the audio format.
internal sealed record StreamInfoFields
{
    public int Codec { get; init; }

    public int Profile { get; init; }

    public int FrameSizeType { get; init; }

    public long Samples { get; init; }

    public int DataType { get; init; }

    public int SampleRate { get; init; }

    public int BitsPerSampleCode { get; init; }

    public int BitsPerSample => BitsPerSampleCode + 8;

    public int Channels { get; init; }

    public bool HasExtension { get; init; }

    public int ValidBitsPerSample { get; init; }

    public bool HasSpeakerAssignment { get; init; }

    public int[] Speakers { get; init; } = [];

    // Where each field lies, as the bit position in the stream info's data.
    public int BitLength { get; init; }

    // The fields from a bit reader positioned at the codec, or null where the data ends first.
    public static StreamInfoFields? Read(ref Layout.LsbBits bits)
    {
        int codec = bits.Get(6);
        int profile = bits.Get(4);
        int sizeType = bits.Get(4);
        long samples = bits.GetLong(35);
        int dataType = bits.Get(3);
        int rate = bits.Get(18) + Layout.SampleRateMin;
        int bitsCode = bits.Get(5);
        int channels = bits.Get(4) + 1;
        bool hasExtension = bits.Get(1) != 0;
        int validBits = 0;
        bool hasSpeakers = false;
        int[] speakers = [];
        if (hasExtension)
        {
            validBits = bits.Get(5) + 1;
            hasSpeakers = bits.Get(1) != 0;
            if (hasSpeakers)
            {
                speakers = new int[channels];
                for (int i = 0; i < channels; i++)
                {
                    speakers[i] = bits.Get(6);
                }
            }
        }

        if (bits.IsOverflow)
        {
            return null;
        }

        return new StreamInfoFields
        {
            Codec = codec,
            Profile = profile,
            FrameSizeType = sizeType,
            Samples = samples,
            DataType = dataType,
            SampleRate = rate,
            BitsPerSampleCode = bitsCode,
            Channels = channels,
            HasExtension = hasExtension,
            ValidBitsPerSample = validBits,
            HasSpeakerAssignment = hasSpeakers,
            Speakers = speakers,
            BitLength = bits.Position,
        };
    }
}
