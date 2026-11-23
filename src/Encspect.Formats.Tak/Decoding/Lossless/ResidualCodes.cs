using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Tak.Decoding.Lossless;

// The residual coding of docs/design/tak-format.md, section 3: a block of values, split into partitions whose modes follow one another, each value one code from a mode's
// row of the table (the bits of the code, the thresholds and the steps of its escapes).
internal static class ResidualCodes
{
    public const int MaxMode = 50;

    // Mode m uses row m of the table of section 10, a format table taken from the description: the bits of the first read (k), the threshold below which the code is that
    // value (t), the step of the unary escape (s), the width a first escape stays below (a) and the base of the long escape (b). The row of mode 0 is unused.
    private static readonly (int K, uint T, uint S, uint A, uint B)[] Rows =
    [
        (0, 0u, 0u, 0u, 0u),
        (1, 1u, 1u, 3u, 8u),
        (2, 3u, 1u, 7u, 6u),
        (3, 5u, 2u, 14u, 13u),
        (3, 3u, 3u, 13u, 24u),
        (4, 11u, 4u, 28u, 25u),
        (4, 6u, 6u, 26u, 48u),
        (5, 22u, 8u, 56u, 50u),
        (5, 12u, 12u, 52u, 96u),
        (6, 44u, 16u, 112u, 100u),
        (6, 24u, 24u, 104u, 192u),
        (7, 88u, 32u, 224u, 200u),
        (7, 48u, 48u, 208u, 384u),
        (8, 176u, 64u, 448u, 400u),
        (8, 96u, 96u, 416u, 768u),
        (9, 352u, 128u, 896u, 800u),
        (9, 192u, 192u, 832u, 1536u),
        (10, 704u, 256u, 1792u, 1600u),
        (10, 384u, 384u, 1664u, 3072u),
        (11, 1408u, 512u, 3584u, 3200u),
        (11, 768u, 768u, 3328u, 6144u),
        (12, 2816u, 1024u, 7168u, 6400u),
        (12, 1536u, 1536u, 6656u, 12288u),
        (13, 5632u, 2048u, 14336u, 12800u),
        (13, 3072u, 3072u, 13312u, 24576u),
        (14, 11264u, 4096u, 28672u, 25600u),
        (14, 6144u, 6144u, 26624u, 49152u),
        (15, 22528u, 8192u, 57344u, 51200u),
        (15, 12288u, 12288u, 53248u, 98304u),
        (16, 45056u, 16384u, 114688u, 102400u),
        (16, 24576u, 24576u, 106496u, 196608u),
        (17, 90112u, 32768u, 229376u, 204800u),
        (17, 49152u, 49152u, 212992u, 393216u),
        (18, 180224u, 65536u, 458752u, 409600u),
        (18, 98304u, 98304u, 425984u, 786432u),
        (19, 360448u, 131072u, 917504u, 819200u),
        (19, 196608u, 196608u, 851968u, 1572864u),
        (20, 720896u, 262144u, 1835008u, 1638400u),
        (20, 393216u, 393216u, 1703936u, 3145728u),
        (21, 1441792u, 524288u, 3670016u, 3276800u),
        (21, 786432u, 786432u, 3407872u, 6291456u),
        (22, 2883584u, 1048576u, 7340032u, 6553600u),
        (22, 1572864u, 1572864u, 6815744u, 12582912u),
        (23, 5767168u, 2097152u, 14680064u, 13107200u),
        (23, 3145728u, 3145728u, 13631488u, 25165824u),
        (24, 11534336u, 4194304u, 29360128u, 26214400u),
        (24, 6291456u, 6291456u, 27262976u, 50331648u),
        (25, 23068672u, 8388608u, 58720256u, 52428800u),
        (25, 12582912u, 12582912u, 54525952u, 100663296u),
        (26, 46137344u, 16777216u, 117440512u, 104857600u),
        (26, 25165824u, 25165824u, 109051904u, 201326592u),
    ];

    public static (int K, uint T, uint S, uint A, uint B) Row(int mode) => Rows[mode];

    // The length of a partition for a sample rate (section 1.1): G is the rate over 512 rounded up to a multiple of 4, times 8 below 11,025 Hz, 4 below 22,050, 2 below 44,100
    // and 1 from there.
    public static int PartitionLength(int sampleRate)
    {
        int g = ((sampleRate + 511) >> 9) + 3 & ~3;
        return sampleRate < 11025 ? g * 8 : sampleRate < 22050 ? g * 4 : sampleRate < 44100 ? g * 2 : g;
    }

    // Reads a residual block of values.Length values (which may be none).
    public static void Read(ref LsbBitCursor bits, Span<int> values, int partitionLength)
    {
        if (!bits.ReadBit())
        {
            ReadRun(ref bits, ReadMode(ref bits), values);
            return;
        }

        int length = values.Length;
        int quotient = length / partitionLength;
        int rest = length - (quotient * partitionLength);
        int count;
        int last;
        if (rest < partitionLength / 2)
        {
            count = quotient;
            last = rest + partitionLength;
        }
        else
        {
            count = quotient + 1;
            last = rest;
        }

        if (count is < 2 or > 128)
        {
            throw new InvalidDataException($"A residual block of {length} values is split into {count} partitions, and 2 to 128 are allowed");
        }

        Span<int> modes = stackalloc int[128];
        int mode = ReadMode(ref bits);
        modes[0] = mode;
        for (int i = 1; i < count; i++)
        {
            int change = bits.CountZeros(6);
            switch (change)
            {
                case 0:
                    break;
                case 1:
                    mode--;
                    break;
                case 2:
                    mode++;
                    break;
                case 6:
                    mode = (int)bits.Read(6);
                    break;
                default:
                    int step = change - 1;
                    mode += bits.ReadBit() ? -step : step;
                    break;
            }

            if (mode is < 0 or > MaxMode)
            {
                throw new InvalidDataException($"A partition's residual mode is {mode}, and 0 to {MaxMode} are defined");
            }

            modes[i] = mode;
        }

        int at = 0;
        for (int i = 0; i < count; i++)
        {
            int run = i == count - 1 ? last : partitionLength;
            ReadRun(ref bits, modes[i], values.Slice(at, run));
            at += run;
        }
    }

    private static int ReadMode(ref LsbBitCursor bits)
    {
        int mode = (int)bits.Read(6);
        if (mode > MaxMode)
        {
            throw new InvalidDataException($"A residual mode is {mode}, and 0 to {MaxMode} are defined");
        }

        return mode;
    }

    // The values of one partition, all of one mode. The codes are unsigned 32-bit with wraparound (section 3).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void ReadRun(ref LsbBitCursor bits, int mode, Span<int> values)
    {
        if (mode == 0)
        {
            values.Clear();
            return;
        }

        (int k, uint t, uint s, uint a, uint b) = Rows[mode];
        unchecked
        {
            for (int i = 0; i < values.Length; i++)
            {
                uint v = bits.Read(k);
                uint code;
                if (v < t || !bits.ReadBit())
                {
                    code = v;
                }
                else
                {
                    uint w = v + (1u << k);
                    if (w < a)
                    {
                        code = w - t;
                    }
                    else
                    {
                        int d = bits.CountZeros(9);
                        if (d < 9)
                        {
                            code = w + (s * (uint)d) - t;
                        }
                        else
                        {
                            int e = (int)bits.Read(3);
                            if (e == 7)
                            {
                                e += (int)bits.Read(5);
                            }

                            if (e > 29)
                            {
                                throw new InvalidDataException($"A residual code has an escape width of {e} bits, and 29 are allowed");
                            }

                            code = e == 0 ? w + b : w + b + (s * (bits.Read(e) + 1));
                        }
                    }
                }

                values[i] = (int)(code >> 1) ^ -(int)(code & 1);
            }
        }

        if (bits.IsOverrun)
        {
            throw new EndOfStreamException();
        }
    }
}
