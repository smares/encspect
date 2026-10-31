using Encspect.Core;

namespace Encspect.Formats.Aiff;

// The Common Chunk (Audio Interchange File Format AIFF-C, section 4.0) as far as it was read.
internal sealed record CommonChunk(int Channels, uint Frames, int SampleSize, double SampleRate, bool IsRateValid, string? CompressionType, string? CompressionName, Node Node)
{
    // The coding: NONE for the uncompressed samples of a FORM AIFF.
    public string Type => CompressionType ?? "NONE";

    // The bytes one sample of one channel takes in uncompressed sound data: the smallest whole number that holds the bits (section 5.0).
    public int BytesPerSample => (SampleSize + 7) / 8;
}

// The Sound Data Chunk: where the bytes lie, how many the chunk holds after the offset, and the offset and block size it states.
internal sealed record SoundChunk(Node Node, long Start, long Length, uint Offset, uint BlockSize, bool IsSizeUnknown);

// The 80-bit IEEE 754 extended number in which the Common Chunk stores the sample rate: a sign, a 15-bit exponent with bias 16383 and a 64-bit
// mantissa whose leading bit is explicit (Apple Numerics Manual; the AIFF-C specification calls it "extended").
internal static class Extended
{
    // The value of the ten bytes, or null for one that is no finite number (an exponent of 0x7FFF); a finite value past what a double holds is returned as infinity.
    // .
    public static double? Decode(ReadOnlySpan<byte> bytes)
    {
        bool isNegative = (bytes[0] & 0x80) != 0;
        int exponent = ((bytes[0] & 0x7F) << 8) | bytes[1];
        ulong mantissa = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes[2..]);
        if (exponent == 0x7FFF)
        {
            return null;
        }

        if (mantissa == 0)
        {
            return isNegative ? -0.0 : 0.0;
        }

        double value = Math.ScaleB((double)mantissa, (exponent == 0 ? 1 : exponent) - 16383 - 63);
        return isNegative ? -value : value;
    }
}
