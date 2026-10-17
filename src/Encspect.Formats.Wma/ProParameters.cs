using System.Buffers.Binary;
using System.Numerics;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// What a Windows Media Audio Professional stream is configured with and what follows from it, worked out
// as FFmpeg's decoder does (wmaprodec.c decode_init, FFmpeg 6.1.1): the 18 bytes of codec-specific
// data, the frame length, the subframe sizes and the scale factor bands of each.
internal sealed class ProParameters
{
    public const int MaxChannels = 8;
    public const int MaxSubframes = 32;
    public const int MaxBands = 29;
    public const int BlockMinBits = 6;
    public const int BlockMaxBits = 13;
    public const int ExtraDataSize = 18;

    public int Channels { get; private init; }

    public int SampleRate { get; private init; }

    public int BlockAlign { get; private init; }

    // The sample size the stream was encoded from (extradata bytes 0 to 1); it scales the quantization step.
    public int BitsPerSample { get; private init; }

    // Extradata bytes 2 to 5: the WAVE channel mask, or 0.
    public uint ChannelMask { get; private init; }

    // Extradata bytes 14 to 15.
    public int DecodeFlags { get; private init; }

    public bool HasLengthPrefix => (DecodeFlags & 0x40) != 0;

    public bool HasDynamicRangeCompression => (DecodeFlags & 0x80) != 0;

    public int Log2FrameSize { get; private init; }

    public int SamplesPerFrame { get; private init; }

    public int MaxSubframeCount { get; private init; }

    // Whether the first bit of a subframe length says it is of maximum length (a maximum of 4 or 16 subframes).
    public bool HasMaxSubframeLengthBit { get; private init; }

    public int SubframeLengthBits { get; private init; }

    public int MinSamplesPerSubframe { get; private init; }

    // How many subframe sizes the stream can use (the frame length divided by powers of two).
    public int SubframeSizes { get; private init; }

    // The channel that carries low frequencies only, or -1.
    public int LfeChannel { get; private init; }

    // The number of scale factor bands of each subframe size.
    public int[] BandCounts { get; private init; } = [];

    public string? Error { get; private init; }

    public static ProParameters Create(AudioFormat format, ReadOnlySpan<byte> data)
    {
        if (format.BlockAlign == 0)
        {
            return Failed("the block alignment is 0");
        }

        if (data.Length < ExtraDataSize)
        {
            return Failed($"the codec-specific data is {Counts.Of(data.Length, "byte", "bytes")}, and FFmpeg's decoder reads 18 or more");
        }

        int bits = BinaryPrimitives.ReadUInt16LittleEndian(data);
        uint mask = BinaryPrimitives.ReadUInt32LittleEndian(data[2..]);
        int flags = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        int channels = mask != 0 ? BitOperations.PopCount(mask) : format.Channels;
        if (bits is > 32 or < 1)
        {
            return Failed($"the sample size is {Counts.Of(bits, "bit", "bits")}");
        }

        int log2FrameSize = Log2(format.BlockAlign) + 4;
        if (log2FrameSize > 25)
        {
            return Failed("the block alignment is too large");
        }

        int frameBits = FrameLengthBits(format.SampleRate, flags);
        if (frameBits > BlockMaxBits)
        {
            return Failed("a frame of more than 8,192 samples");
        }

        int samplesPerFrame = 1 << frameBits;
        int log2Max = (flags & 0x38) >> 3;
        int maxSubframes = 1 << log2Max;
        if (maxSubframes > MaxSubframes)
        {
            return Failed($"{Counts.Of(maxSubframes, "subframe", "subframes")} per frame");
        }

        int minSamples = samplesPerFrame / maxSubframes;
        if (minSamples < (1 << BlockMinBits))
        {
            return Failed($"subframes of {Counts.Of(minSamples, "sample", "samples")}");
        }

        if (format.SampleRate <= 0)
        {
            return Failed("no sample rate");
        }

        if (channels <= 0)
        {
            return Failed("no channels");
        }

        if (channels > MaxChannels || channels > format.Channels)
        {
            return Failed(channels > MaxChannels ? $"{Counts.Of(channels, "channel", "channels")}" : $"{Counts.Of(channels, "channel", "channels")} in the channel mask against {format.Channels} in the format");
        }

        int lfe = -1;
        if ((mask & 8) != 0)
        {
            for (uint bit = 1; bit < 16; bit <<= 1)
            {
                if ((mask & bit) != 0)
                {
                    lfe++;
                }
            }
        }

        int sizes = log2Max + 1;
        var bandCounts = new int[sizes];
        for (int i = 0; i < sizes; i++)
        {
            int length = samplesPerFrame >> i;
            int previous = 0;
            int bands = 1;
            for (int x = 0; x < MaxBands - 1 && previous < length; x++)
            {
                int offset = ((length * 2 * ProTables.CriticalFrequencies[x]) / format.SampleRate) + 2;
                offset &= ~3;
                if (offset > previous)
                {
                    previous = offset;
                    bands++;
                }

                if (offset >= length)
                {
                    break;
                }
            }

            bandCounts[i] = bands - 1;
            if (bandCounts[i] <= 0)
            {
                return Failed("a subframe size without scale factor bands");
            }
        }

        return new ProParameters
        {
            Channels = channels,
            SampleRate = format.SampleRate,
            BlockAlign = format.BlockAlign,
            BitsPerSample = bits,
            ChannelMask = mask,
            DecodeFlags = flags,
            Log2FrameSize = log2FrameSize,
            SamplesPerFrame = samplesPerFrame,
            MaxSubframeCount = maxSubframes,
            HasMaxSubframeLengthBit = maxSubframes is 16 or 4,
            SubframeLengthBits = Log2(log2Max) + 1,
            MinSamplesPerSubframe = minSamples,
            SubframeSizes = sizes,
            LfeChannel = lfe,
            BandCounts = bandCounts,
        };
    }

    private static ProParameters Failed(string error) => new() { Error = error };

    // wma_common.c: ff_wma_get_frame_len_bits for version 3.
    internal static int FrameLengthBits(int sampleRate, int flags)
    {
        int bits = sampleRate <= 16000 ? 9
            : sampleRate <= 22050 ? 10
            : sampleRate <= 48000 ? 11
            : sampleRate <= 96000 ? 12
            : 13;
        return (flags & 0x6) switch
        {
            0x2 => bits + 1,
            0x4 => bits - 1,
            0x6 => bits - 2,
            _ => bits,
        };
    }

    internal static int Log2(int value) => value <= 0 ? 0 : 31 - BitOperations.LeadingZeroCount((uint)value);
}
