using Encspect.Core;

namespace Encspect.Formats.Wma;

// What a Windows Media Audio version 1 or 2 stream's WAVEFORMATEX and codec-specific data fix for the whole stream:
// the frame length, the block sizes, the widths of the fields, the band lists and the codebook pair
// (docs/design/wma-standard-format.md, "Derived parameters").
internal sealed class StdParameters
{
    public const int MaxBitOffsetWidth = 25;

    private StdParameters()
    {
    }

    public int Version { get; private init; }

    public int SampleRate { get; private init; }

    public int Channels { get; private init; }

    public long BitsPerSecond { get; private init; }

    public int BlockAlign { get; private init; }

    public int Options { get; private init; }

    public bool UsesScaleFactorExponents => (Options & 1) != 0;

    public bool UsesBitReservoir => (Options & 2) != 0;

    // The setting the decoder works with: version 2 with the options 0x000D turns variable block lengths off.
    public bool UsesVariableBlocks { get; private init; }

    public bool UsesNoiseSubstitution { get; private init; }

    // F = 2^FrameBits samples per channel.
    public int FrameBits { get; private init; }

    public int FrameLength => 1 << FrameBits;

    public int SizeCount { get; private init; }

    // The width of a block size index.
    public int SizeIndexBits { get; private init; }

    public int BitOffsetBits { get; private init; }

    // The first coded coefficient: 3 for version 1, 0 for version 2.
    public int FirstCoefficient => Version == 1 ? 3 : 0;

    public int CodebookPair { get; private init; }

    // Per block size index: the coefficient end E_k, the band widths the exponents walk, and the widths of the high bands.
    public int[] CoefficientEnds { get; private init; } = [];

    public int[][] ExponentBands { get; private init; } = [];

    public int[][] HighBands { get; private init; } = [];

    // Why the stream is not decoded, or null.
    public string? Unsupported { get; private init; }

    public int BlockLength(int sizeIndex) => FrameLength >> sizeIndex;

    public static StdParameters Create(AudioFormat format, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(format);

        int version = format.FormatTag == 0x0160 ? 1 : 2;
        int channels = format.Channels;
        int rate = format.SampleRate;
        long bytesPerSecond = format.BytesPerSecond;
        long bitsPerSecond = bytesPerSecond is < 0 or > 268_435_455 ? int.MaxValue : bytesPerSecond * 8;
        int options = 0;
        if (version == 1 && data.Length >= 4)
        {
            options = data[2] | (data[3] << 8);
        }
        else if (version == 2 && data.Length >= 6)
        {
            options = data[4] | (data[5] << 8);
        }

        bool variable = (options & 4) != 0 && !(version == 2 && data.Length >= 8 && options == 0x000D);
        int frameBits = rate <= 16000 ? 9 : rate <= 22050 || (version == 1 && rate <= 32000) ? 10 : 11;
        int frameLength = 1 << frameBits;

        string? unsupported = null;
        if (format.BlockAlign == 0)
        {
            unsupported = "the block alignment is 0";
        }
        else if (rate <= 0 || rate > 50_000)
        {
            unsupported = $"the sampling rate is {rate} Hz, outside 1 to 50,000";
        }
        else if (channels is 0 or > 2)
        {
            unsupported = $"the stream has {channels} channels, and 1 or 2 are decoded";
        }
        else if (bytesPerSecond == 0)
        {
            unsupported = "the average bytes per second are 0";
        }

        if (unsupported is not null)
        {
            return new StdParameters
            {
                Version = version,
                SampleRate = rate,
                Channels = channels,
                BitsPerSecond = bitsPerSecond,
                BlockAlign = format.BlockAlign,
                Options = options,
                UsesVariableBlocks = variable,
                FrameBits = frameBits,
                Unsupported = unsupported,
            };
        }

        // The bits per sample and channel, in single precision as the stream's encoders compute it.
        float b = (float)bitsPerSecond / (float)(channels * rate);
        float bStereo = channels == 2 ? (float)((double)b * 1.6) : b;

        float offsetProduct = b * frameLength;
        double x = offsetProduct / 8.0;
        int rounded = (int)(x + 0.5);
        int bitOffsetBits = FloorLog2(rounded) + 2 + 3;
        if (bitOffsetBits > MaxBitOffsetWidth)
        {
            return new StdParameters
            {
                Version = version,
                SampleRate = rate,
                Channels = channels,
                BitsPerSecond = bitsPerSecond,
                BlockAlign = format.BlockAlign,
                Options = options,
                UsesVariableBlocks = variable,
                FrameBits = frameBits,
                BitOffsetBits = bitOffsetBits,
                Unsupported = $"the bit offset of a superframe would be {bitOffsetBits} bits wide, and at most {MaxBitOffsetWidth} are decoded",
            };
        }

        int sizeCount = 1;
        if (variable)
        {
            int n = ((options >> 3) & 3) + 1;
            if (bitsPerSecond / channels >= 32_000)
            {
                n += 2;
            }

            n = Math.Min(n, frameBits - 7);
            sizeCount = n + 1;
        }

        int sizeIndexBits = sizeCount > 1 ? FloorLog2(sizeCount - 1) + 1 : 0;

        float? highFrequency = HighBandFrequency(version, rate, b, bStereo);
        int[][] lists = new int[sizeCount][];
        for (int k = 0; k < sizeCount; k++)
        {
            lists[k] = BandList(version, rate, frameLength >> k);
        }

        int[][] exponentBands = lists;
        if (version == 1 && sizeCount > 1)
        {
            // One list is kept for version 1 and rewritten for each size in turn, so the exponents of the full-size block
            // read the smallest block's list and the other sizes have none.
            exponentBands = new int[sizeCount][];
            exponentBands[0] = lists[sizeCount - 1];
            for (int k = 1; k < sizeCount; k++)
            {
                exponentBands[k] = [];
            }
        }

        int[] ends = new int[sizeCount];
        int[][] highBands = new int[sizeCount][];
        int endBase = frameLength - (9 * frameLength / 100);
        for (int k = 0; k < sizeCount; k++)
        {
            ends[k] = endBase >> k;
            int[] source = version == 1 && k > 0 ? [] : lists[k];
            highBands[k] = highFrequency is { } h ? HighBandWidths(source, frameLength >> k, rate, h, ends[k]) : [];
        }

        int pair = 2;
        if (rate >= 32_000)
        {
            pair = bStereo < 0.72 ? 0 : bStereo < 1.16 ? 1 : 2;
        }

        return new StdParameters
        {
            Version = version,
            SampleRate = rate,
            Channels = channels,
            BitsPerSecond = bitsPerSecond,
            BlockAlign = format.BlockAlign,
            Options = options,
            UsesVariableBlocks = variable,
            UsesNoiseSubstitution = highFrequency is not null,
            FrameBits = frameBits,
            SizeCount = sizeCount,
            SizeIndexBits = sizeIndexBits,
            BitOffsetBits = bitOffsetBits,
            CodebookPair = pair,
            CoefficientEnds = ends,
            ExponentBands = exponentBands,
            HighBands = highBands,
        };
    }

    // floor(log2 x), and 0 for x <= 0.
    private static int FloorLog2(int x) => x <= 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)x);

    // The frequency from which noise substitution may replace bands, or null where the stream uses none.
    private static float? HighBandFrequency(int version, int rate, float b, float bStereo)
    {
        float h = (float)(rate * 0.5);
        int normalized = rate;
        if (version == 2)
        {
            normalized = rate >= 44_100 ? 44_100 : rate >= 22_050 ? 22_050 : rate >= 16_000 ? 16_000 : rate >= 11_025 ? 11_025 : rate >= 8_000 ? 8_000 : rate;
        }

        static float Scale(float value, double factor) => (float)(value * factor);

        switch (normalized)
        {
            case 44_100:
                return bStereo >= 0.61 ? null : Scale(h, 0.4);
            case 22_050:
                if (bStereo >= 1.16)
                {
                    return null;
                }

                return bStereo >= 0.72 ? Scale(h, 0.7) : Scale(h, 0.6);
            case 16_000:
                return b > 0.5 ? Scale(h, 0.5) : Scale(h, 0.3);
            case 11_025:
                return Scale(h, 0.7);
            case 8_000:
                if (b <= 0.625)
                {
                    return Scale(h, 0.5);
                }

                return b > 0.75 ? null : Scale(h, 0.65);
            default:
                return b >= 0.8 ? Scale(h, 0.75) : b >= 0.6 ? Scale(h, 0.6) : Scale(h, 0.5);
        }
    }

    private static int[] BandList(int version, int rate, int length)
    {
        int log = FloorLog2(length) - 7;
        if (version == 2 && log is >= 0 and <= 2 && rate >= 22_050)
        {
            byte[][] table = rate >= 44_100 ? Tables.ExponentBands44100 : rate >= 32_000 ? Tables.ExponentBands32000 : Tables.ExponentBands22050;
            byte[] row = table[log];
            int[] widths = new int[row[0]];
            for (int i = 0; i < widths.Length; i++)
            {
                widths[i] = row[i + 1];
            }

            return widths;
        }

        var list = new List<int>();
        int previous = 0;
        foreach (ushort frequency in Tables.CriticalFrequencies)
        {
            if (version == 2)
            {
                int p = Math.Min((((2 * length * frequency) + (2 * rate)) / (4 * rate)) * 4, length);
                if (p > previous)
                {
                    list.Add(p - previous);
                    previous = p;
                }

                if (p >= length)
                {
                    break;
                }
            }
            else
            {
                int p = Math.Min(((2 * length * frequency) + (rate / 2)) / rate, length);
                list.Add(p - previous);
                previous = p;
                if (p >= length)
                {
                    break;
                }
            }
        }

        return [.. list];
    }

    private static int[] HighBandWidths(int[] bands, int length, int rate, float h, int end)
    {
        float product = (float)(2 * length) * h;
        float quotient = product / (float)rate;
        int start = (int)((double)quotient + 0.5);
        var widths = new List<int>();
        int position = 0;
        foreach (int width in bands)
        {
            int from = Math.Max(position, start);
            int to = Math.Min(position + width, end);
            if (to > from)
            {
                widths.Add(to - from);
            }

            position += width;
        }

        return [.. widths];
    }
}
