namespace Encspect.Formats.Wma;

// What a Windows Media Audio version 1 or 2 stream is configured with, and what follows from it,
// worked out as FFmpeg's decoder does (wmadec.c wma_decode_init and wma.c ff_wma_init, FFmpeg 6.1.1):
// the frame length, the block sizes, the exponent bands, the noise coding, the codebooks.
internal sealed class StandardParameters
{
    // Blocks are 2^7 to 2^11 samples (wma.h: BLOCK_MIN_BITS and BLOCK_MAX_BITS).
    public const int BlockMinBits = 7;
    public const int BlockMaxBits = 11;
    public const int BlockSizes = BlockMaxBits - BlockMinBits + 1;
    public const int HighBandMax = 25;

    public int Version { get; private init; }

    public int Channels { get; private init; }

    public int SampleRate { get; private init; }

    public int BitRate { get; private init; }

    public int BlockAlign { get; private init; }

    // The encode options word of the codec-specific data (wmadec.c: flags2).
    public int Options { get; private init; }

    public bool UsesExponentVlc => (Options & 1) != 0;

    public bool UsesBitReservoir => (Options & 2) != 0;

    public bool UsesVariableBlockLength { get; private init; }

    public bool UsesNoiseCoding { get; private set; }

    public int FrameLengthBits { get; private set; }

    public int FrameLength => 1 << FrameLengthBits;

    public int BlockSizeCount { get; private set; }

    // The bits of the bit offset field of a superframe header (wma.c: byte_offset_bits + 3 bits).
    public int ByteOffsetBits { get; private set; }

    public int CoefsStart { get; private set; }

    public int[] CoefsEnd { get; } = new int[BlockSizes];

    public int[] HighBandStart { get; } = new int[BlockSizes];

    public int[] ExponentSizes { get; } = new int[BlockSizes];

    public int[][] ExponentBands { get; } = Enumerable.Range(0, BlockSizes).Select(_ => new int[25]).ToArray();

    public int[] ExponentHighSizes { get; } = new int[BlockSizes];

    public int[][] ExponentHighBands { get; } = Enumerable.Range(0, BlockSizes).Select(_ => new int[HighBandMax]).ToArray();

    // Which pair of run-level codebooks the stream uses (0 to 2), and the value the bits-per-sample
    // ratio of the stream gives (wma.c: bps and bps1), for the report.
    public int CoefficientTable { get; private set; }

    public float BitsPerSample { get; private set; }

    public float HighFrequency { get; private set; }

    // Why FFmpeg's decoder refuses the configuration, or null (ff_wma_init returns -1 for a sample
    // rate outside 1 to 50,000 Hz, no or more than two channels and no bitrate).
    public string? Error { get; private set; }

    public static StandardParameters Create(int version, int channels, int sampleRate, int bytesPerSecond, int blockAlign, int options, int dataLength)
    {
        var p = new StandardParameters
        {
            Version = version,
            Channels = channels,
            SampleRate = sampleRate,
            BitRate = bytesPerSecond > int.MaxValue / 8 ? int.MaxValue : bytesPerSecond * 8,
            BlockAlign = blockAlign,
            Options = options,
            UsesVariableBlockLength = (options & 4) != 0 && !(version == 2 && dataLength >= 8 && options == 0xD),
        };

        if (blockAlign <= 0)
        {
            p.Error = "the block alignment is 0, so a superframe has no size (wmadec.c: \"block_align is not set\")";
            return p;
        }

        if (sampleRate <= 0 || sampleRate > 50000 || channels <= 0 || channels > 2 || p.BitRate <= 0)
        {
            p.Error = $"FFmpeg's decoder takes 1 to 50,000 Hz, 1 or 2 channels and a bitrate above 0 (wma.c: ff_wma_init); the stream has {sampleRate} Hz, {channels} channels and {p.BitRate} bps";
            return p;
        }

        p.Initialize();
        return p;
    }

    private void Initialize()
    {
        FrameLengthBits = FrameLengthBitsFor(SampleRate, Version);
        int frameLength = FrameLength;
        if (UsesVariableBlockLength)
        {
            int count = ((Options >> 3) & 3) + 1;
            if (BitRate / Channels >= 32000)
            {
                count += 2;
            }

            int max = FrameLengthBits - BlockMinBits;
            if (count > max)
            {
                count = max;
            }

            BlockSizeCount = count + 1;
        }
        else
        {
            BlockSizeCount = 1;
        }

        UsesNoiseCoding = true;
        float highFrequency = (float)(SampleRate * 0.5);
        int rate1 = SampleRate;
        if (Version == 2)
        {
            rate1 = rate1 >= 44100 ? 44100 : rate1 >= 22050 ? 22050 : rate1 >= 16000 ? 16000 : rate1 >= 11025 ? 11025 : rate1 >= 8000 ? 8000 : rate1;
        }

        float bps = BitRate / (float)(Channels * SampleRate);
        ByteOffsetBits = Log2((int)((bps * frameLength / 8.0) + 0.5)) + 2;
        float bps1 = bps;
        if (Channels == 2)
        {
            bps1 = (float)(bps * 1.6);
        }

        if (rate1 == 44100)
        {
            if (bps1 >= 0.61)
            {
                UsesNoiseCoding = false;
            }
            else
            {
                highFrequency = (float)(highFrequency * 0.4);
            }
        }
        else if (rate1 == 22050)
        {
            if (bps1 >= 1.16)
            {
                UsesNoiseCoding = false;
            }
            else if (bps1 >= 0.72)
            {
                highFrequency = (float)(highFrequency * 0.7);
            }
            else
            {
                highFrequency = (float)(highFrequency * 0.6);
            }
        }
        else if (rate1 == 16000)
        {
            highFrequency = bps > 0.5 ? (float)(highFrequency * 0.5) : (float)(highFrequency * 0.3);
        }
        else if (rate1 == 11025)
        {
            highFrequency = (float)(highFrequency * 0.7);
        }
        else if (rate1 == 8000)
        {
            if (bps <= 0.625)
            {
                highFrequency = (float)(highFrequency * 0.5);
            }
            else if (bps > 0.75)
            {
                UsesNoiseCoding = false;
            }
            else
            {
                highFrequency = (float)(highFrequency * 0.65);
            }
        }
        else if (bps >= 0.8)
        {
            highFrequency = (float)(highFrequency * 0.75);
        }
        else if (bps >= 0.6)
        {
            highFrequency = (float)(highFrequency * 0.6);
        }
        else
        {
            highFrequency = (float)(highFrequency * 0.5);
        }

        BitsPerSample = bps;
        HighFrequency = highFrequency;

        CoefsStart = Version == 1 ? 3 : 0;
        for (int k = 0; k < BlockSizeCount; k++)
        {
            int blockLength = frameLength >> k;
            if (Version == 1)
            {
                int lastPosition = 0;
                int i;
                for (i = 0; i < 25; i++)
                {
                    int a = Tables.CriticalFrequencies[i];
                    int position = ((blockLength * 2 * a) + (SampleRate >> 1)) / SampleRate;
                    if (position > blockLength)
                    {
                        position = blockLength;
                    }

                    ExponentBands[0][i] = position - lastPosition;
                    if (position >= blockLength)
                    {
                        i++;
                        break;
                    }

                    lastPosition = position;
                }

                ExponentSizes[0] = i;
            }
            else
            {
                byte[]? table = null;
                int a = FrameLengthBits - BlockMinBits - k;
                if (a is >= 0 and < 3)
                {
                    if (SampleRate >= 44100)
                    {
                        table = Tables.ExponentBands44100[a];
                    }
                    else if (SampleRate >= 32000)
                    {
                        table = Tables.ExponentBands32000[a];
                    }
                    else if (SampleRate >= 22050)
                    {
                        table = Tables.ExponentBands22050[a];
                    }
                }

                if (table is not null)
                {
                    int n = table[0];
                    for (int i = 0; i < n; i++)
                    {
                        ExponentBands[k][i] = table[1 + i];
                    }

                    ExponentSizes[k] = n;
                }
                else
                {
                    int j = 0;
                    int lastPosition = 0;
                    for (int i = 0; i < 25; i++)
                    {
                        int critical = Tables.CriticalFrequencies[i];
                        int position = ((blockLength * 2 * critical) + (SampleRate << 1)) / (4 * SampleRate);
                        position <<= 2;
                        if (position > blockLength)
                        {
                            position = blockLength;
                        }

                        if (position > lastPosition)
                        {
                            ExponentBands[k][j++] = position - lastPosition;
                        }

                        if (position >= blockLength)
                        {
                            break;
                        }

                        lastPosition = position;
                    }

                    ExponentSizes[k] = j;
                }
            }

            CoefsEnd[k] = (frameLength - (frameLength * 9 / 100)) >> k;
            HighBandStart[k] = (int)(((blockLength * 2 * highFrequency) / SampleRate) + 0.5);
            int bands = ExponentSizes[k];
            int high = 0;
            int pos = 0;
            for (int i = 0; i < bands; i++)
            {
                int start = pos;
                pos += ExponentBands[k][i];
                int end = pos;
                if (start < HighBandStart[k])
                {
                    start = HighBandStart[k];
                }

                if (end > CoefsEnd[k])
                {
                    end = CoefsEnd[k];
                }

                if (end > start && high < HighBandMax)
                {
                    ExponentHighBands[k][high++] = end - start;
                }
            }

            ExponentHighSizes[k] = high;
        }

        CoefficientTable = 2;
        if (SampleRate >= 32000)
        {
            if (bps1 < 0.72)
            {
                CoefficientTable = 0;
            }
            else if (bps1 < 1.16)
            {
                CoefficientTable = 1;
            }
        }

        if (ByteOffsetBits + 3 > 25)
        {
            Error = $"the bit offset of a superframe would take {ByteOffsetBits + 3} bits, more than FFmpeg's reader caches (wma.c: byte_offset_bits)";
        }
    }

    // The block length (in samples) of the bsize-th smaller block size.
    public int BlockLength(int blockLengthBits) => 1 << blockLengthBits;

    // wma_common.c: ff_wma_get_frame_len_bits, for versions 1 and 2.
    private static int FrameLengthBitsFor(int sampleRate, int version) =>
        sampleRate <= 16000 ? 9
        : sampleRate <= 22050 || (sampleRate <= 32000 && version == 1) ? 10
        : 11;

    // av_log2: the position of the highest set bit, 0 for 0.
    internal static int Log2(int value) => value <= 0 ? 0 : 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)value);

    // wma.c: ff_wma_total_gain_to_bits.
    internal static int TotalGainToBits(int totalGain) => totalGain < 15 ? 13 : totalGain < 32 ? 12 : totalGain < 40 ? 11 : totalGain < 45 ? 10 : 9;
}
