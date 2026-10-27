using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Wav.Decoding;

// The unpacking loops: sample frames in, planar samples out (channel c at c * count, count samples each). The bytes hold count whole sample frames.
internal static class PcmUnpack
{
    // The 16-bit sample of a companded byte: the A-law and mu-law expansions as the segment formulas of G.711 give them (a sign, a 3-bit segment and a
    // 4-bit step), in the scale of 16-bit samples that FFmpeg's decoders output. Format data built from the formulas, no table copied; the expansion of all
    // 256 codes of each is checked against FFmpeg's decoded MD5 (built-alaw-all-codes.wav, built-mulaw-all-codes.wav, docs/development.md).
    private static readonly short[] ALawTable = BuildTable(ALawValue);

    private static readonly short[] MuLawTable = BuildTable(MuLawValue);

    private static short[] BuildTable(Func<int, int> value)
    {
        var table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = (short)value(i);
        }

        return table;
    }

    // A-law: the even bits are inverted (XOR 0x55), then a sign, a 3-bit segment and a 4-bit step.
    private static int ALawValue(int code)
    {
        int a = code ^ 0x55;
        int step = (a & 0x0F) << 4;
        int segment = (a & 0x70) >> 4;
        int magnitude = segment switch
        {
            0 => step + 8,
            1 => step + 0x108,
            _ => (step + 0x108) << (segment - 1),
        };
        return (a & 0x80) != 0 ? magnitude : -magnitude;
    }

    // mu-law: all bits are inverted, then a sign, a 3-bit segment and a 4-bit step, with a bias of 33 in 14 bits (0x84 in 16).
    private static int MuLawValue(int code)
    {
        int u = ~code & 0xFF;
        int magnitude = (((u & 0x0F) << 3) + 0x84) << ((u & 0x70) >> 4);
        return (u & 0x80) != 0 ? 0x84 - magnitude : magnitude - 0x84;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Integers(ReadOnlySpan<byte> bytes, Span<int> output, int count, int channels, PcmCoding coding)
    {
        switch (coding.Encoding)
        {
            case PcmEncoding.ALaw:
                Companded(bytes, output, count, channels, ALawTable);
                return;
            case PcmEncoding.MuLaw:
                Companded(bytes, output, count, channels, MuLawTable);
                return;
        }

        // Unsigned samples are signed ones with the top bit of the container flipped: the sample minus 2^(bits - 1).
        uint flip = coding.Encoding == PcmEncoding.UnsignedInteger ? 1u << (coding.ContainerBits - 1) : 0;
        switch (coding.ContainerBits, coding.IsBigEndian)
        {
            case (8, _):
                Unpack<One>(bytes, output, count, channels, flip);
                break;
            case (16, false):
                Unpack<Little16>(bytes, output, count, channels, flip);
                break;
            case (16, true):
                Unpack<Big16>(bytes, output, count, channels, flip);
                break;
            case (24, false):
                Unpack<Little24>(bytes, output, count, channels, flip);
                break;
            case (24, true):
                Unpack<Big24>(bytes, output, count, channels, flip);
                break;
            case (32, false):
                Unpack<Little32>(bytes, output, count, channels, flip);
                break;
            default:
                Unpack<Big32>(bytes, output, count, channels, flip);
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Unpack<TReader>(ReadOnlySpan<byte> bytes, Span<int> output, int count, int channels, uint flip)
        where TReader : struct, IReader
    {
        int shift = 32 - (8 * TReader.Width);
        int frameBytes = TReader.Width * channels;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> frame = bytes.Slice(i * frameBytes, frameBytes);
            for (int channel = 0; channel < channels; channel++)
            {
                uint raw = TReader.Read(frame.Slice(channel * TReader.Width, TReader.Width));
                output[(channel * count) + i] = (int)((raw ^ flip) << shift) >> shift;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Companded(ReadOnlySpan<byte> bytes, Span<int> output, int count, int channels, short[] table)
    {
        for (int i = 0; i < count; i++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                output[(channel * count) + i] = table[bytes[(i * channels) + channel]];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Floats(ReadOnlySpan<byte> bytes, Span<float> output, int count, int channels, bool isBigEndian)
    {
        int frameBytes = 4 * channels;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> frame = bytes.Slice(i * frameBytes, frameBytes);
            for (int channel = 0; channel < channels; channel++)
            {
                ReadOnlySpan<byte> sample = frame.Slice(channel * 4, 4);
                output[(channel * count) + i] = isBigEndian ? BinaryPrimitives.ReadSingleBigEndian(sample) : BinaryPrimitives.ReadSingleLittleEndian(sample);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Doubles(ReadOnlySpan<byte> bytes, Span<double> output, int count, int channels, bool isBigEndian)
    {
        int frameBytes = 8 * channels;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> frame = bytes.Slice(i * frameBytes, frameBytes);
            for (int channel = 0; channel < channels; channel++)
            {
                ReadOnlySpan<byte> sample = frame.Slice(channel * 8, 8);
                output[(channel * count) + i] = isBigEndian ? BinaryPrimitives.ReadDoubleBigEndian(sample) : BinaryPrimitives.ReadDoubleLittleEndian(sample);
            }
        }
    }

    // One sample of a width and byte order, as the unsigned number of its bytes.
    private interface IReader
    {
        static abstract int Width { get; }

        static abstract uint Read(ReadOnlySpan<byte> sample);
    }

    private readonly struct One : IReader
    {
        public static int Width => 1;

        public static uint Read(ReadOnlySpan<byte> sample) => sample[0];
    }

    private readonly struct Little16 : IReader
    {
        public static int Width => 2;

        public static uint Read(ReadOnlySpan<byte> sample) => BinaryPrimitives.ReadUInt16LittleEndian(sample);
    }

    private readonly struct Big16 : IReader
    {
        public static int Width => 2;

        public static uint Read(ReadOnlySpan<byte> sample) => BinaryPrimitives.ReadUInt16BigEndian(sample);
    }

    private readonly struct Little24 : IReader
    {
        public static int Width => 3;

        public static uint Read(ReadOnlySpan<byte> sample) => (uint)(sample[0] | (sample[1] << 8) | (sample[2] << 16));
    }

    private readonly struct Big24 : IReader
    {
        public static int Width => 3;

        public static uint Read(ReadOnlySpan<byte> sample) => (uint)(sample[2] | (sample[1] << 8) | (sample[0] << 16));
    }

    private readonly struct Little32 : IReader
    {
        public static int Width => 4;

        public static uint Read(ReadOnlySpan<byte> sample) => BinaryPrimitives.ReadUInt32LittleEndian(sample);
    }

    private readonly struct Big32 : IReader
    {
        public static int Width => 4;

        public static uint Read(ReadOnlySpan<byte> sample) => BinaryPrimitives.ReadUInt32BigEndian(sample);
    }
}
