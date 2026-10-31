using System.Buffers.Binary;
using System.Numerics;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// What the container's WAVEFORMATEX and the 18 bytes of codec data state about a Windows Media Audio Lossless stream, and the values derived
// from them (docs/design/wma-lossless-format.md, "Codec data").
internal sealed class LosslessParameters : IStreamParameters
{
    public const int DataSize = 18;
    public const int MaxChannels = 8;

    private LosslessParameters()
    {
    }

    public int SampleSize { get; private init; }

    public uint ChannelMask { get; private init; }

    public int Flags { get; private init; }

    public int Channels { get; private init; }

    public int FormatChannels { get; private init; }

    public int SampleRate { get; private init; }

    public int BlockAlignment { get; private init; }

    public int CountWidth { get; private init; }

    public int FrameLength { get; private init; }

    public int FrameLengthBits { get; private init; }

    public int MaxSubframes { get; private init; }

    public int MinSubframe { get; private init; }

    public bool HasLengthPrefix { get; private init; }

    public bool HasDynamicRange { get; private init; }

    public bool HasLpcFlags { get; private init; }

    public string? Unsupported { get; private init; }

    public int PacketBits => BlockAlignment * 8;

    public int HeaderBits => 6 + CountWidth;

    public int TrimBits => FrameLengthBits + 1;

    public int LengthCodeWidth => MaxSubframes < 2 ? 0 : BitOperations.Log2((uint)(MaxSubframes - 1)) + 1;

    public static LosslessParameters Create(AudioFormat format, ReadOnlySpan<byte> data)
    {
        int flags = data.Length >= DataSize ? BinaryPrimitives.ReadUInt16LittleEndian(data[14..]) : 0;
        int sampleSize = data.Length >= DataSize ? data[0] : 0;
        uint mask = data.Length >= DataSize ? BinaryPrimitives.ReadUInt32LittleEndian(data[2..]) : 0;
        int channels = mask != 0 ? BitOperations.PopCount(mask) : format.Channels;
        int blockAlignment = format.BlockAlign;
        int frameBits = format.SampleRate switch
        {
            <= 16000 => 9,
            <= 22050 => 10,
            <= 48000 => 11,
            <= 96000 => 12,
            _ => 13,
        };
        frameBits += ((flags >> 1) & 3) switch { 1 => 1, 2 => -1, 3 => -2, _ => 0 };
        int maxSubframes = 1 << ((flags >> 3) & 7);

        string? reason = null;
        if (blockAlignment <= 0)
        {
            reason = "the block alignment is 0";
        }
        else if (data.Length < DataSize)
        {
            reason = $"the codec-specific data is {Counts.Bytes(data.Length)}, and FFmpeg's decoder reads 18 or more";
        }
        else if (sampleSize is not (16 or 24))
        {
            reason = $"the sample size is {Counts.Bits(sampleSize)}, and only 16 and 24 are read";
        }
        else if (format.SampleRate <= 0)
        {
            reason = "no sample rate";
        }
        else if (channels == 0)
        {
            reason = "no channels";
        }
        else if (channels > MaxChannels)
        {
            reason = Counts.Of(channels, "channel", "channels");
        }
        else if (maxSubframes > 32)
        {
            reason = $"{Counts.Of(maxSubframes, "subframe", "subframes")} per frame (64 or 128)";
        }

        return new LosslessParameters
        {
            SampleSize = sampleSize,
            ChannelMask = mask,
            Flags = flags,
            Channels = channels,
            FormatChannels = format.Channels,
            SampleRate = format.SampleRate,
            BlockAlignment = blockAlignment,
            CountWidth = blockAlignment > 0 ? BitOperations.Log2((uint)blockAlignment) + 4 : 0,
            FrameLength = 1 << Math.Clamp(frameBits, 0, 30),
            FrameLengthBits = frameBits,
            MaxSubframes = maxSubframes,
            MinSubframe = (1 << Math.Clamp(frameBits, 0, 30)) / maxSubframes,
            HasLengthPrefix = (flags & 0x40) != 0,
            HasDynamicRange = (flags & 0x80) != 0,
            HasLpcFlags = (flags & 0x100) != 0,
            Unsupported = reason,
        };
    }
}
