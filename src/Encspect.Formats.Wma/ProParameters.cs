using System.Buffers.Binary;
using System.Numerics;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// What the container's WAVEFORMATEX and the 18 bytes of codec data state about a Windows Media Audio Professional stream, and the
// values derived from them (docs/design/wma-pro-format.md, "Codec data").
internal sealed class ProParameters : IStreamParameters
{
    public const int DataSize = 18;
    public const int MaxChannels = 8;
    public const int MaxRecordedBlockBits = 25;

    private ProParameters()
    {
    }

    public int SampleSize { get; private init; }

    public uint ChannelMask { get; private init; }

    public int Flags { get; private init; }

    public int Channels { get; private init; }

    public int SampleRate { get; private init; }

    public int BlockAlignment { get; private init; }

    // The width of the count of bits that finish the frame before and of the length prefix.
    public int CountWidth { get; private init; }

    public int FrameLength { get; private init; }

    public int FrameLengthBits { get; private init; }

    public int SubframeCountBits { get; private init; }

    public int MaxSubframes { get; private init; }

    public int MinSubframe { get; private init; }

    public bool HasLengthPrefix { get; private init; }

    public bool HasDynamicRange { get; private init; }

    // The channel the mask names as low-frequency, from 0, or -1.
    public int LowFrequencyChannel { get; private init; }

    // The scale factor bands of each subframe size index: the band edges, the last of which is the subframe's length.
    public int[][] BandEdges { get; private init; } = [];

    // Why the stream is not read; null for a stream whose checks pass.
    public string? Unsupported { get; private init; }

    public int PacketBits => BlockAlignment * 8;

    public int HeaderBits => 6 + CountWidth;

    // The width of a trim value.
    public int TrimBits => FrameLengthBits + 1;

    // The width of a subframe length code (the number of bits it takes in the tile header for M above 1).
    public int LengthCodeWidth => SubframeCountBits == 0 ? 0 : BitOperations.Log2((uint)SubframeCountBits) + 1;

    public static ProParameters Create(AudioFormat format, ReadOnlySpan<byte> data)
    {
        string? Fail(string reason) => reason;

        int flags = data.Length >= DataSize ? BinaryPrimitives.ReadUInt16LittleEndian(data[14..]) : 0;
        int sampleSize = data.Length >= DataSize ? BinaryPrimitives.ReadUInt16LittleEndian(data) : 0;
        uint mask = data.Length >= DataSize ? BinaryPrimitives.ReadUInt32LittleEndian(data[2..]) : 0;
        int channels = mask != 0 ? BitOperations.PopCount(mask) : format.Channels;
        int blockAlignment = format.BlockAlign;
        int countWidth = blockAlignment > 0 ? BitOperations.Log2((uint)blockAlignment) + 4 : 0;
        int subframeBits = (flags >> 3) & 7;
        int adjustment = (flags >> 1) & 3;
        int frameBits = format.SampleRate switch
        {
            <= 16000 => 9,
            <= 22050 => 10,
            <= 48000 => 11,
            <= 96000 => 12,
            _ => 13,
        };
        frameBits += adjustment switch { 1 => 1, 2 => -1, 3 => -2, _ => 0 };
        int frameLength = 1 << Math.Clamp(frameBits, 0, 30);
        int maxSubframes = 1 << subframeBits;

        string? reason = null;
        if (blockAlignment <= 0)
        {
            reason = Fail("the block alignment is 0");
        }
        else if (data.Length < DataSize)
        {
            reason = Fail($"the codec-specific data is {Counts.Bytes(data.Length)}, and FFmpeg's decoder reads 18 or more");
        }
        else if (sampleSize is < 1 or > 32)
        {
            reason = Fail($"the sample size is {Counts.Bits(sampleSize)} (0, or above 32)");
        }
        else if (countWidth > MaxRecordedBlockBits)
        {
            reason = Fail("the block alignment is too large");
        }
        else if (frameBits > 13)
        {
            reason = Fail("a frame of more than 8,192 samples");
        }
        else if (maxSubframes > 32)
        {
            reason = Fail($"{Counts.Of(maxSubframes, "subframe", "subframes")} per frame (64 or 128)");
        }
        else if (frameLength / maxSubframes < 64)
        {
            reason = Fail($"subframes of {Counts.Of(frameLength / maxSubframes, "sample", "samples")} (below 64)");
        }
        else if (format.SampleRate <= 0)
        {
            reason = Fail("no sample rate");
        }
        else if (channels == 0)
        {
            reason = Fail("no channels");
        }
        else if (channels > MaxChannels)
        {
            reason = Fail($"{Counts.Of(channels, "channel", "channels")}");
        }
        else if (channels > format.Channels)
        {
            reason = Fail($"{Counts.Of(channels, "channel", "channels")} in the channel mask against {format.Channels} in the format");
        }

        int[][] bands = [];
        if (reason is null)
        {
            bands = new int[subframeBits + 1][];
            for (int k = 0; k <= subframeBits; k++)
            {
                bands[k] = EdgesOf(frameLength >> k, format.SampleRate);
                if (bands[k].Length < 2)
                {
                    reason = Fail("a subframe size without scale factor bands");
                    break;
                }
            }
        }

        return new ProParameters
        {
            SampleSize = sampleSize,
            ChannelMask = mask,
            Flags = flags,
            Channels = channels,
            SampleRate = format.SampleRate,
            BlockAlignment = blockAlignment,
            CountWidth = countWidth,
            FrameLength = frameLength,
            FrameLengthBits = frameBits,
            SubframeCountBits = subframeBits,
            MaxSubframes = maxSubframes,
            MinSubframe = frameLength / maxSubframes,
            HasLengthPrefix = (flags & 0x40) != 0,
            HasDynamicRange = (flags & 0x80) != 0,
            LowFrequencyChannel = (mask & 8) != 0 ? BitOperations.PopCount(mask & 0xF) - 1 : -1,
            BandEdges = bands,
            Unsupported = reason,
        };
    }

    // The band edges of a subframe of length samples at rate hertz: the critical frequencies of ProTables, in multiples of 4.
    private static int[] EdgesOf(int length, int rate)
    {
        var edges = new List<int>(30) { 0 };
        foreach (ushort frequency in ProTables.CriticalFrequencies)
        {
            if (edges[^1] >= length)
            {
                break;
            }

            int edge = (int)(((long)length * 2 * frequency / rate) + 2) & ~3;
            if (edge > edges[^1])
            {
                edges.Add(edge);
            }

            if (edge >= length)
            {
                break;
            }
        }

        edges[^1] = length;
        return [.. edges];
    }
}
