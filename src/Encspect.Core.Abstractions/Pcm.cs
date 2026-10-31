using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Encspect.Core;

public enum SampleFormat
{
    // Signed integers in the low BitsPerSample bits of an int, sign-extended (8-bit WAV's unsigned samples converted).
    Integer,

    // IEEE 754 single precision (WavPack, WAV and AIFF-C floats of 32 bits).
    Float32,

    // IEEE 754 double precision (WAV and AIFF-C floats of 64 bits).
    Float64,
}

public sealed record PcmFormat(int SampleRate, int Channels, int BitsPerSample, SampleFormat Format)
{
    // The speaker of each channel in the stream's channel order, which is the format's, as StreamInfo.ChannelLayout.
    public IReadOnlyList<Speaker>? ChannelLayout { get; init; }
}

// Samples of every channel for one stretch of the stream, planar: channel c holds the Length samples from c * Stride on,
// in the one span of the block's sample format. Valid only during IPcmSink.Write.
public readonly ref struct PcmBlock
{
    public PcmBlock(long position, int length, int channels, int stride, ReadOnlySpan<int> integers)
        : this(position, length, channels, stride, SampleFormat.Integer, integers.Length)
    {
        Integers = integers;
    }

    public PcmBlock(long position, int length, int channels, int stride, ReadOnlySpan<float> floats)
        : this(position, length, channels, stride, SampleFormat.Float32, floats.Length)
    {
        Floats = floats;
    }

    public PcmBlock(long position, int length, int channels, int stride, ReadOnlySpan<double> doubles)
        : this(position, length, channels, stride, SampleFormat.Float64, doubles.Length)
    {
        Doubles = doubles;
    }

    private PcmBlock(long position, int length, int channels, int stride, SampleFormat format, int spanLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, length);
        if (spanLength < (long)(channels - 1) * stride + length)
        {
            throw new ArgumentException("The samples do not reach to the end of the last channel.");
        }

        Position = position;
        Length = length;
        Channels = channels;
        Stride = stride;
        Format = format;
    }

    // The first sample's position in the stream in samples per channel, before any gapless trimming; where frames were
    // lost, the next block's position jumps.
    public long Position { get; }

    public int Length { get; }

    public int Channels { get; }

    public int Stride { get; }

    public SampleFormat Format { get; }

    public ReadOnlySpan<int> Integers { get; }

    public ReadOnlySpan<float> Floats { get; }

    public ReadOnlySpan<double> Doubles { get; }

    public ReadOnlySpan<int> IntegerChannel(int channel) => Integers.Slice(channel * Stride, Length);
}

public interface IPcmSink
{
    // On the analysis thread, in stream order; a sink copies what it keeps.
    void Write(PcmBlock block);

    // Called once by the host when the stream's decoding ends, with the samples per channel of the frames that were decoded. A
    // frame that failed midway (decode.frame-invalid) or that a stop interrupted does not count there, though the blocks it
    // had written reached Write, so a sink may hold more samples than End reports. A canceled analysis (its own token, which
    // leaves no report) does not end its sinks. A decoder never calls it: the sink a decoder gets from DeclareOutput ignores it.
    void End(long samples);
}

// The canonical byte layout of decoded audio: interleaved, signed, little-endian, ceil(bits / 8) bytes a sample (floats
// as their IEEE 754 bytes). FLAC's MD5 is computed over it, and so are the tests' hashing sink, the manifests'
// decoded_pcm_md5. Monkey's Audio and WavPack compute their checksums over their own layouts.
public static class CanonicalPcm
{
    public static int BytesPerSample(int bitsPerSample)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bitsPerSample, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitsPerSample, 64);

        return (bitsPerSample + 7) / 8;
    }

    // The bytes the block takes in the layout.
    public static long ByteCount(in PcmBlock block, int bitsPerSample) => (long)block.Length * block.Channels * ByteWidth(block.Format, bitsPerSample);

    // Writes the block, sample by sample across its channels, and returns the bytes written. For integer blocks the
    // low bytes of each sample are written, so a value beyond bitsPerSample bits is cut. For float blocks
    // bitsPerSample is ignored: 4 or 8 bytes a sample.
    public static int Write(in PcmBlock block, int bitsPerSample, Span<byte> destination)
    {
        int width = ByteWidth(block.Format, bitsPerSample);
        long total = ByteCount(block, bitsPerSample);
        if (destination.Length < total)
        {
            throw new ArgumentException($"The block takes {total} bytes; the destination holds {destination.Length}.", nameof(destination));
        }

        int length = block.Length;
        int channels = block.Channels;
        int stride = block.Stride;
        int at = 0;
        switch (block.Format)
        {
            case SampleFormat.Integer:
                ReadOnlySpan<int> integers = block.Integers;
                if (width is 2 or 3 or 4 && length > 0 && channels > 0 && ((long)(channels - 1) * stride) + length <= integers.Length)
                {
                    return WriteIntegers(integers, destination, length, channels, stride, width);
                }

                for (int i = 0; i < length; i++)
                {
                    for (int channel = 0; channel < channels; channel++)
                    {
                        int value = integers[(channel * stride) + i];
                        switch (width)
                        {
                            case 1:
                                destination[at] = (byte)value;
                                break;
                            case 2:
                                BinaryPrimitives.WriteInt16LittleEndian(destination[at..], (short)value);
                                break;
                            case 3:
                                destination[at] = (byte)value;
                                destination[at + 1] = (byte)(value >> 8);
                                destination[at + 2] = (byte)(value >> 16);
                                break;
                            default:
                                BinaryPrimitives.WriteInt32LittleEndian(destination[at..], value);
                                destination[(at + 4)..(at + width)].Fill((byte)(value < 0 ? 0xFF : 0));
                                break;
                        }

                        at += width;
                    }
                }

                break;
            case SampleFormat.Float32:
                for (int i = 0; i < length; i++)
                {
                    for (int channel = 0; channel < channels; channel++)
                    {
                        BinaryPrimitives.WriteSingleLittleEndian(destination[at..], block.Floats[(channel * stride) + i]);
                        at += width;
                    }
                }

                break;
            default:
                for (int i = 0; i < length; i++)
                {
                    for (int channel = 0; channel < channels; channel++)
                    {
                        BinaryPrimitives.WriteDoubleLittleEndian(destination[at..], block.Doubles[(channel * stride) + i]);
                        at += width;
                    }
                }

                break;
        }

        return at;
    }

    // The common widths without a bounds check per sample: the caller has checked that the destination holds length * channels * width bytes
    // and the source length samples in each of channels planes stride apart.
    // Compiled optimized at the first call and not tiered (docs/development.md, "Hot loops and tiering").
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int WriteIntegers(ReadOnlySpan<int> integers, Span<byte> destination, int length, int channels, int stride, int width)
    {
        ref int source = ref MemoryMarshal.GetReference(integers);
        ref byte target = ref MemoryMarshal.GetReference(destination);
        int at = 0;
        for (int i = 0; i < length; i++)
        {
            for (int channel = 0; channel < channels; channel++)
            {
                int value = Unsafe.Add(ref source, (channel * stride) + i);
                ref byte sample = ref Unsafe.Add(ref target, at);
                switch (width)
                {
                    case 2:
                        Unsafe.WriteUnaligned(ref sample, BitConverter.IsLittleEndian ? (short)value : BinaryPrimitives.ReverseEndianness((short)value));
                        break;
                    case 3:
                        sample = (byte)value;
                        Unsafe.Add(ref sample, 1) = (byte)(value >> 8);
                        Unsafe.Add(ref sample, 2) = (byte)(value >> 16);
                        break;
                    default:
                        Unsafe.WriteUnaligned(ref sample, BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value));
                        break;
                }

                at += width;
            }
        }

        return at;
    }

    private static int ByteWidth(SampleFormat format, int bitsPerSample) => format switch
    {
        SampleFormat.Float32 => 4,
        SampleFormat.Float64 => 8,
        _ => BytesPerSample(bitsPerSample),
    };
}
