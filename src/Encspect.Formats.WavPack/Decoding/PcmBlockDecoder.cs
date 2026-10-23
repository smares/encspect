using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.WavPack.Decoding;

// What decoding one block gave: its samples are in PcmBlockDecoder.Samples.
internal readonly record struct BlockOutcome(bool IsMuted, bool IsCrcValid, bool HasExtra, bool IsExtraCrcValid);

// The decoding of one block of integer or floating point audio, lossless or hybrid without its correction file: libwavpack
// 5.6.0 unpack.c (unpack_samples, the decorrelation passes and fixup_samples) and unpack_floats.c. The entropy decoder
// supplies the residuals; the passes undo the decorrelation; the block's CRC is worked out on the way, as the encoder
// works it out over its input; the shift, the integer and float details and the clipping of a lossy block restore the
// samples the decoder returns. A block whose data ends early or whose samples pass libwavpack's limit for a corrupt
// sample is muted, as libwavpack mutes it, and its CRC then differs from the stored one.
internal sealed class PcmBlockDecoder
{
    private readonly WordDecoder _words = new();
    private int[] _samples = [];

    // Interleaved for a block of two channels, else one channel; valid until the next Decode.
    public ReadOnlySpan<int> Samples => _samples;

    public BlockOutcome Decode(ReadOnlySpan<byte> block, in BlockHeader header, BlockContents contents, int count, CancellationToken cancellation)
    {
        uint flags = header.Flags;
        bool isMonoData = (flags & (BlockHeader.MonoFlag | BlockHeader.FalseStereoFlag)) != 0;
        bool isHybrid = (flags & BlockHeader.HybridFlag) != 0;
        int width = isMonoData ? 1 : 2;
        int total = checked(count * width);
        if (_samples.Length < total)
        {
            _samples = new int[total];
        }

        Span<int> buffer = _samples.AsSpan(0, total);
        int muteLimit = unchecked((int)((1L << header.MaxMagnitude) + 2));
        if (isHybrid)
        {
            muteLimit = unchecked((muteLimit * 2) + 128);
        }

        uint crc = uint.MaxValue;
        var bits = new LsbBitCursor(block.Slice(contents.Bitstream.Start, contents.Bitstream.Length));
        _words.Start(contents, flags);
        bool isComplete = ReadWords(ref bits, buffer, count, width, cancellation);
        if (isComplete)
        {
            int passes = contents.Passes;
            for (int pass = 0; pass < passes; pass++)
            {
                if (isMonoData)
                {
                    MonoPass(buffer, contents, pass);
                }
                else
                {
                    StereoPass(buffer, contents, pass);
                }
            }

            int stoppedAt = count;
            if (isMonoData)
            {
                for (int i = 0; i < buffer.Length; i++)
                {
                    if (Exceeds(buffer[i], muteLimit))
                    {
                        stoppedAt = i;
                        break;
                    }

                    crc = unchecked((crc * 3) + (uint)buffer[i]);
                }
            }
            else
            {
                bool isJoint = (flags & BlockHeader.JointStereoFlag) != 0;
                for (int i = 0; i + 1 < buffer.Length; i += 2)
                {
                    if (isJoint)
                    {
                        buffer[i + 1] -= buffer[i] >> 1;
                        buffer[i] += buffer[i + 1];
                    }

                    crc = unchecked(crc + (crc << 3) + ((uint)buffer[i] << 1) + (uint)buffer[i] + (uint)buffer[i + 1]);
                }

                for (int i = 0; i < buffer.Length; i += 16)
                {
                    if (Exceeds(buffer[i], muteLimit) || Exceeds(buffer[i + 1], muteLimit))
                    {
                        stoppedAt = i / 2;
                        break;
                    }
                }
            }

            isComplete = stoppedAt == count;
        }

        if (!isComplete)
        {
            buffer.Clear();
        }

        bool hasExtra = contents.ExtraBitstream.Length > 0 && isComplete;
        uint extraCrc = uint.MaxValue;
        var extra = new LsbBitCursor(hasExtra ? block.Slice(contents.ExtraBitstream.Start, contents.ExtraBitstream.Length) : []);
        Fixup(buffer, header, contents, hasExtra, ref extra, ref extraCrc);
        bool isExtraValid = !hasExtra || (extraCrc == contents.ExtraCrc && !extra.IsOverrun);
        return new BlockOutcome(!isComplete, crc == header.Crc, hasExtra, isExtraValid);
    }

    // get_words_lossless and the loop of get_word: the residuals of every sample, the channels alternating.
    private bool ReadWords(ref LsbBitCursor bits, Span<int> buffer, int count, int width, CancellationToken cancellation)
    {
        for (int i = 0; i < count; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            for (int c = 0; c < width; c++)
            {
                if (!_words.TryGetWord(ref bits, c, out buffer[(i * width) + c]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool Exceeds(int value, int limit) => (value < 0 ? -(long)value : value) > limit;

    // The weight times the sample, as libwavpack's apply_weight does in 32-bit arithmetic: the form that cannot
    // overflow for a sample beyond 16 bits.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Apply(int weight, int sample) =>
        sample != (short)sample
            ? unchecked((((((sample & 0xFFFF) * weight) >> 9) + (((sample & ~0xFFFF) >> 9) * weight)) + 1) >> 1)
            : unchecked((weight * sample) + 512) >> 10;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Update(ref int weight, int delta, int source, int result)
    {
        if (source != 0 && result != 0)
        {
            int s = (source ^ result) >> 31;
            weight = unchecked((delta ^ s) + (weight - s));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void UpdateClip(ref int weight, int delta, int source, int result)
    {
        if (source != 0 && result != 0)
        {
            int s = (source ^ result) >> 31;
            weight = unchecked((weight ^ s) + (delta - s));
            if (weight > 1024)
            {
                weight = 1024;
            }

            weight = unchecked((weight ^ s) - s);
        }
    }

    // decorr_mono_pass.
    private static void MonoPass(Span<int> buffer, BlockContents c, int pass)
    {
        int term = c.Terms[pass];
        int delta = c.Deltas[pass];
        int weight = c.WeightsA[pass];
        Span<int> a = c.SamplesA.AsSpan(pass * BlockContents.MaxTerm, BlockContents.MaxTerm);
        switch (term)
        {
            case 17:
                for (int i = 0; i < buffer.Length; i++)
                {
                    int sam = unchecked((2 * a[0]) - a[1]);
                    a[1] = a[0];
                    a[0] = Apply(weight, sam) + buffer[i];
                    Update(ref weight, delta, sam, buffer[i]);
                    buffer[i] = a[0];
                }

                break;
            case 18:
                for (int i = 0; i < buffer.Length; i++)
                {
                    int sam = unchecked((3 * a[0]) - a[1]) >> 1;
                    a[1] = a[0];
                    a[0] = Apply(weight, sam) + buffer[i];
                    Update(ref weight, delta, sam, buffer[i]);
                    buffer[i] = a[0];
                }

                break;
            default:
                int m = 0;
                int k = term & (BlockContents.MaxTerm - 1);
                for (int i = 0; i < buffer.Length; i++)
                {
                    int sam = a[m];
                    a[k] = unchecked(Apply(weight, sam) + buffer[i]);
                    Update(ref weight, delta, sam, buffer[i]);
                    buffer[i] = a[k];
                    m = (m + 1) & (BlockContents.MaxTerm - 1);
                    k = (k + 1) & (BlockContents.MaxTerm - 1);
                }

                break;
        }

        c.WeightsA[pass] = weight;
    }

    // decorr_stereo_pass.
    private static void StereoPass(Span<int> buffer, BlockContents c, int pass)
    {
        int term = c.Terms[pass];
        int delta = c.Deltas[pass];
        int weightA = c.WeightsA[pass];
        int weightB = c.WeightsB[pass];
        Span<int> a = c.SamplesA.AsSpan(pass * BlockContents.MaxTerm, BlockContents.MaxTerm);
        Span<int> b = c.SamplesB.AsSpan(pass * BlockContents.MaxTerm, BlockContents.MaxTerm);
        switch (term)
        {
            case 17:
                for (int i = 0; i + 1 < buffer.Length; i += 2)
                {
                    int sam = unchecked((2 * a[0]) - a[1]);
                    a[1] = a[0];
                    int original = buffer[i];
                    buffer[i] = a[0] = unchecked(Apply(weightA, sam) + original);
                    Update(ref weightA, delta, sam, original);
                    sam = unchecked((2 * b[0]) - b[1]);
                    b[1] = b[0];
                    original = buffer[i + 1];
                    buffer[i + 1] = b[0] = unchecked(Apply(weightB, sam) + original);
                    Update(ref weightB, delta, sam, original);
                }

                break;
            case 18:
                for (int i = 0; i + 1 < buffer.Length; i += 2)
                {
                    int sam = unchecked(a[0] + ((a[0] - a[1]) >> 1));
                    a[1] = a[0];
                    int original = buffer[i];
                    buffer[i] = a[0] = unchecked(Apply(weightA, sam) + original);
                    Update(ref weightA, delta, sam, original);
                    sam = unchecked(b[0] + ((b[0] - b[1]) >> 1));
                    b[1] = b[0];
                    original = buffer[i + 1];
                    buffer[i + 1] = b[0] = unchecked(Apply(weightB, sam) + original);
                    Update(ref weightB, delta, sam, original);
                }

                break;
            case -1:
                for (int i = 0; i + 1 < buffer.Length; i += 2)
                {
                    int sam = unchecked(buffer[i] + Apply(weightA, a[0]));
                    UpdateClip(ref weightA, delta, a[0], buffer[i]);
                    buffer[i] = sam;
                    a[0] = unchecked(buffer[i + 1] + Apply(weightB, sam));
                    UpdateClip(ref weightB, delta, sam, buffer[i + 1]);
                    buffer[i + 1] = a[0];
                }

                break;
            case -2:
                for (int i = 0; i + 1 < buffer.Length; i += 2)
                {
                    int sam = unchecked(buffer[i + 1] + Apply(weightB, b[0]));
                    UpdateClip(ref weightB, delta, b[0], buffer[i + 1]);
                    buffer[i + 1] = sam;
                    b[0] = unchecked(buffer[i] + Apply(weightA, sam));
                    UpdateClip(ref weightA, delta, sam, buffer[i]);
                    buffer[i] = b[0];
                }

                break;
            case -3:
                for (int i = 0; i + 1 < buffer.Length; i += 2)
                {
                    int samA = unchecked(buffer[i] + Apply(weightA, a[0]));
                    UpdateClip(ref weightA, delta, a[0], buffer[i]);
                    int samB = unchecked(buffer[i + 1] + Apply(weightB, b[0]));
                    UpdateClip(ref weightB, delta, b[0], buffer[i + 1]);
                    buffer[i] = b[0] = samA;
                    buffer[i + 1] = a[0] = samB;
                }

                break;
            default:
                int m = 0;
                int k = term & (BlockContents.MaxTerm - 1);
                for (int i = 0; i + 1 < buffer.Length; i += 2)
                {
                    int sam = a[m];
                    a[k] = unchecked(Apply(weightA, sam) + buffer[i]);
                    Update(ref weightA, delta, sam, buffer[i]);
                    buffer[i] = a[k];
                    sam = b[m];
                    b[k] = unchecked(Apply(weightB, sam) + buffer[i + 1]);
                    Update(ref weightB, delta, sam, buffer[i + 1]);
                    buffer[i + 1] = b[k];
                    m = (m + 1) & (BlockContents.MaxTerm - 1);
                    k = (k + 1) & (BlockContents.MaxTerm - 1);
                }

                break;
        }

        c.WeightsA[pass] = weightA;
        c.WeightsB[pass] = weightB;
    }

    // fixup_samples: floats, the integer details, the shift and the clipping of a lossy block.
    private static void Fixup(Span<int> buffer, in BlockHeader header, BlockContents c, bool hasExtra, ref LsbBitCursor extra, ref uint extraCrc)
    {
        uint flags = header.Flags;
        bool isLossy = (flags & BlockHeader.HybridFlag) != 0;
        int shift = header.LeftShift;
        if ((flags & BlockHeader.FloatFlag) != 0)
        {
            RestoreFloats(buffer, c, hasExtra, ref extra, ref extraCrc);
            return;
        }

        if ((flags & BlockHeader.Int32Flag) != 0)
        {
            int sentBits = c.Int32SentBits & 0x1F;
            int zeros = c.Int32Zeros & 0x1F;
            int ones = c.Int32Ones & 0x1F;
            int dups = c.Int32Dups & 0x1F;
            uint mask = unchecked((1u << sentBits) - 1);
            if (hasExtra)
            {
                uint crc = extraCrc;
                for (int i = 0; i < buffer.Length; i++)
                {
                    uint data = extra.Read(sentBits);
                    buffer[i] = unchecked((int)(((uint)buffer[i] << sentBits) | (data & mask)));
                    if (zeros != 0)
                    {
                        buffer[i] = unchecked((int)((uint)buffer[i] << zeros));
                    }
                    else if (ones != 0)
                    {
                        buffer[i] = unchecked((int)((uint)(buffer[i] + 1) << ones) - 1);
                    }
                    else if (dups != 0)
                    {
                        buffer[i] = unchecked((int)((uint)(buffer[i] + (buffer[i] & 1)) << dups) - (buffer[i] & 1));
                    }

                    int v = buffer[i];
                    crc = unchecked((crc * 9) + ((uint)(v & 0xFFFF) * 3) + (uint)((v >> 16) & 0xFFFF));
                }

                extraCrc = crc;
            }
            else if (sentBits == 0 && (zeros + ones + dups) != 0)
            {
                while (isLossy && (flags & BlockHeader.BytesStoredMask) == 3 && shift < 8)
                {
                    if (zeros != 0)
                    {
                        zeros--;
                    }
                    else if (ones != 0)
                    {
                        ones--;
                    }
                    else if (dups != 0)
                    {
                        dups--;
                    }
                    else
                    {
                        break;
                    }

                    shift++;
                }

                for (int i = 0; i < buffer.Length; i++)
                {
                    if (zeros != 0)
                    {
                        buffer[i] = unchecked((int)((uint)buffer[i] << zeros));
                    }
                    else if (ones != 0)
                    {
                        buffer[i] = unchecked((int)((uint)(buffer[i] + 1) << ones) - 1);
                    }
                    else if (dups != 0)
                    {
                        buffer[i] = unchecked((int)((uint)(buffer[i] + (buffer[i] & 1)) << dups) - (buffer[i] & 1));
                    }
                }
            }
            else
            {
                shift += zeros + sentBits + ones + dups;
            }
        }

        shift &= 0x1F;
        if (isLossy)
        {
            int minValue;
            int maxValue;
            int minShifted;
            int maxShifted;
            switch (flags & BlockHeader.BytesStoredMask)
            {
                case 0:
                    minValue = -128 >> shift;
                    maxValue = 127 >> shift;
                    break;
                case 1:
                    minValue = -32768 >> shift;
                    maxValue = 32767 >> shift;
                    break;
                case 2:
                    minValue = -8388608 >> shift;
                    maxValue = 8388607 >> shift;
                    break;
                default:
                    minValue = int.MinValue >> shift;
                    maxValue = int.MaxValue >> shift;
                    break;
            }

            minShifted = unchecked((int)((uint)minValue << shift));
            maxShifted = unchecked(maxValue << shift);
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = buffer[i] < minValue ? minShifted : buffer[i] > maxValue ? maxShifted : unchecked((int)((uint)buffer[i] << shift));
            }
        }
        else if (shift != 0)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] = unchecked((int)((uint)buffer[i] << shift));
            }
        }
    }

    // unpack_floats.c float_values: the integers the passes returned become the bits of single precision floats, with
    // the extra bitstream's bits where there is one.
    private static void RestoreFloats(Span<int> values, BlockContents c, bool hasExtra, ref LsbBitCursor extra, ref uint extraCrc)
    {
        int shiftBits = c.FloatShift & 0x1F;
        int maxExponent = c.FloatMaxExponent;
        int floatFlags = c.FloatFlags;
        uint crc = extraCrc;
        for (int index = 0; index < values.Length; index++)
        {
            int shiftCount = 0;
            int exponent = maxExponent;
            uint output = 0;
            int value = values[index];
            if (!hasExtra)
            {
                if (value != 0)
                {
                    value = unchecked((int)((uint)value << shiftBits));
                    if (value < 0)
                    {
                        value = unchecked(-value);
                        output = SetSign(output, 1);
                    }

                    if (value >= 0x1000000)
                    {
                        while ((value & 0xF000000) != 0)
                        {
                            value >>= 1;
                            ++exponent;
                        }
                    }
                    else if (exponent != 0)
                    {
                        while ((value & 0x800000) == 0 && --exponent != 0)
                        {
                            shiftCount++;
                            value = unchecked((int)((uint)value << 1));
                        }

                        if ((shiftCount &= 0x1F) != 0 && (floatFlags & 1) != 0)
                        {
                            value |= unchecked((int)((1u << shiftCount) - 1));
                        }
                    }

                    output = SetMantissa(output, (uint)value);
                    output = SetExponent(output, (uint)exponent);
                }

                values[index] = unchecked((int)output);
                continue;
            }

            if (value == 0)
            {
                if ((floatFlags & 8) != 0)
                {
                    if (extra.ReadBit())
                    {
                        output = SetMantissa(output, extra.Read(23));
                        if (exponent >= 25)
                        {
                            output = SetExponent(output, extra.Read(8));
                        }

                        output = SetSign(output, extra.ReadBit() ? 1u : 0u);
                    }
                    else if ((floatFlags & 0x10) != 0)
                    {
                        output = SetSign(output, extra.ReadBit() ? 1u : 0u);
                    }
                }
            }
            else
            {
                value = unchecked((int)((uint)value << shiftBits));
                if (value < 0)
                {
                    value = unchecked(-value);
                    output = SetSign(output, 1);
                }

                if (value == 0x1000000)
                {
                    if (extra.ReadBit())
                    {
                        output = SetMantissa(output, extra.Read(23));
                    }

                    output = SetExponent(output, 255);
                }
                else
                {
                    if (exponent != 0)
                    {
                        while ((value & 0x800000) == 0 && --exponent != 0)
                        {
                            shiftCount++;
                            value = unchecked((int)((uint)value << 1));
                        }
                    }

                    if ((shiftCount &= 0x1F) != 0)
                    {
                        if ((floatFlags & 1) != 0 || ((floatFlags & 2) != 0 && extra.ReadBit()))
                        {
                            value |= unchecked((int)((1u << shiftCount) - 1));
                        }
                        else if ((floatFlags & 4) != 0)
                        {
                            uint temp = extra.Read(shiftCount);
                            value |= unchecked((int)(temp & ((1u << shiftCount) - 1)));
                        }
                    }

                    output = SetMantissa(output, (uint)value);
                    output = SetExponent(output, (uint)exponent);
                }
            }

            crc = unchecked((crc * 27) + ((output & 0x7FFFFF) * 9) + (((output >> 23) & 0xFF) * 3) + (output >> 31));
            values[index] = unchecked((int)output);
        }

        extraCrc = crc;
    }

    private static uint SetMantissa(uint f, uint v) => f ^ ((f ^ v) & 0x7FFFFF);

    private static uint SetExponent(uint f, uint v) => f ^ ((f ^ (v << 23)) & 0x7F800000);

    private static uint SetSign(uint f, uint v) => f ^ ((f ^ (v << 31)) & 0x80000000);
}
