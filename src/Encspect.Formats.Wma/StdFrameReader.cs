namespace Encspect.Formats.Wma;

// Why a frame is invalid, by the rule it broke (docs/design/wma-standard-format.md, "Validity rules and damage").
internal enum FrameFault : byte
{
    None,
    BlockSize,
    FrameOverflow,
    GainOverread,
    ExponentRange,
    ExponentsMissing,
    CoefficientOverflow,
    OutOfBits,
}

// The block sizes and exponents the reader carries from one frame to the next, packed in an int so that a frame record keeps
// the state it started with.
internal struct FrameState
{
    public int Previous;
    public int Current;
    public int Next;
    public bool Reset;

    // Bit c is set once channel c received exponents.
    public int Received;

    public static FrameState Initial => new() { Reset = true };

    public readonly int Pack() => Previous | (Current << 4) | (Next << 8) | (Reset ? 1 << 12 : 0) | (Received << 13);

    public static FrameState Unpack(int packed) => new()
    {
        Previous = packed & 15,
        Current = (packed >> 4) & 15,
        Next = (packed >> 8) & 15,
        Reset = (packed & (1 << 12)) != 0,
        Received = (packed >> 13) & 3,
    };
}

// The figures over every block read, failed ones included.
internal sealed class StdTotals
{
    public long Blocks;
    public readonly long[] BySize = new long[8];
    public long MidSide;
    public long NoiseSubstituted;
    public long WithoutCodedChannels;
    public long ReusingExponents;
    public bool HasGain;
    public long SmallestGain;
    public long LargestGain;
    public long NonZero;
    public long Escapes;

    public long KnownSize => BySize.Sum();
}

internal enum FieldKind : byte
{
    PreviousSize,
    Size,
    NextSize,
    MidSide,
    Coded,
    Gain,
    NoiseFlags,
    NoiseLevels,
    ExponentsSent,
    Exponents,
    Coefficients,
}

// A field of a block: bits [Start, End) of the frame's buffer. A and B hold its values (a block size index or -1 where it was out
// of range, a flag, the gain, the flagged and all high bands, the non-zero coefficients and the position of the last).
internal readonly record struct TraceField(FieldKind Kind, int Channel, int Start, int End, int A, int B);

internal sealed class TraceBlock(int start)
{
    public int Start { get; } = start;

    public int End { get; set; }

    // The size index once known, else -1.
    public int SizeIndex { get; set; } = -1;

    public bool IsMidSide { get; set; }

    public bool HasCodedChannel { get; set; }

    public bool IsInvalid { get; set; }

    public List<TraceField> Fields { get; } = [];
}

// Reads the syntax of one frame, down to the end of its coefficients (docs/design/wma-standard-format.md, "Frames and blocks").
// No audio is rebuilt, so the values of the exponents, noise levels and coefficients are read and not kept.
internal static class StdFrameReader
{
    // Reads blocks from the position of bits until they add up to a frame, leaving the position at the frame's end or where it
    // failed. state is carried on; totals and trace, when given, collect the figures and the fields.
    public static FrameFault Read(StdParameters parameters, Bits bits, ref FrameState state, StdTotals? totals, List<TraceBlock>? trace, out int blocks)
    {
        int frameLength = parameters.FrameLength;
        int filled = 0;
        blocks = 0;
        while (filled < frameLength)
        {
            blocks++;
            var block = trace is null ? null : new TraceBlock(bits.Position);
            trace?.Add(block!);
            FrameFault fault = ReadBlock(parameters, bits, ref state, filled, totals, block, out int length);
            if (block is not null)
            {
                block.End = bits.Position;
                block.IsInvalid = fault != FrameFault.None;
            }

            if (fault == FrameFault.None && bits.IsOverrun)
            {
                fault = FrameFault.OutOfBits;
                if (block is not null)
                {
                    block.IsInvalid = true;
                }
            }

            if (fault != FrameFault.None)
            {
                return fault;
            }

            filled += length;
        }

        return FrameFault.None;
    }

    private static FrameFault ReadBlock(StdParameters p, Bits bits, ref FrameState state, int filled, StdTotals? totals, TraceBlock? block, out int length)
    {
        length = 0;
        List<TraceField>? fields = block?.Fields;
        if (totals is not null)
        {
            totals.Blocks++;
        }

        int channels = p.Channels;
        int frameLength = p.FrameLength;

        if (p.UsesVariableBlocks)
        {
            int width = p.SizeIndexBits;
            if (state.Reset)
            {
                state.Reset = false;
                int at = bits.Position;
                int value = (int)bits.Read(width);
                bool valid = value < p.SizeCount;
                fields?.Add(new TraceField(FieldKind.PreviousSize, 0, at, bits.Position, valid ? value : -1, 0));
                if (!valid)
                {
                    return FrameFault.BlockSize;
                }

                state.Previous = value;
                at = bits.Position;
                value = (int)bits.Read(width);
                valid = value < p.SizeCount;
                fields?.Add(new TraceField(FieldKind.Size, 0, at, bits.Position, valid ? value : -1, 0));
                if (!valid)
                {
                    return FrameFault.BlockSize;
                }

                state.Current = value;
            }
            else
            {
                state.Previous = state.Current;
                state.Current = state.Next;
            }

            Known(state.Current, totals, block);
            int nextAt = bits.Position;
            int next = (int)bits.Read(width);
            bool isNextValid = next < p.SizeCount;
            fields?.Add(new TraceField(FieldKind.NextSize, 0, nextAt, bits.Position, isNextValid ? next : -1, 0));
            if (!isNextValid)
            {
                return FrameFault.BlockSize;
            }

            state.Next = next;
        }
        else
        {
            Known(0, totals, block);
        }

        int size = state.Current;
        length = frameLength >> size;
        if (filled + length > frameLength)
        {
            return FrameFault.FrameOverflow;
        }

        bool isMidSide = false;
        if (channels == 2)
        {
            int at = bits.Position;
            isMidSide = bits.Read(1) != 0;
            fields?.Add(new TraceField(FieldKind.MidSide, 0, at, bits.Position, isMidSide ? 1 : 0, 0));
        }

        if (block is not null)
        {
            block.IsMidSide = isMidSide;
        }

        if (totals is not null && isMidSide)
        {
            totals.MidSide++;
        }

        Span<bool> coded = stackalloc bool[2];
        bool any = false;
        for (int c = 0; c < channels; c++)
        {
            int at = bits.Position;
            coded[c] = bits.Read(1) != 0;
            any |= coded[c];
            fields?.Add(new TraceField(FieldKind.Coded, c, at, bits.Position, coded[c] ? 1 : 0, 0));
        }

        if (!any)
        {
            if (totals is not null)
            {
                totals.WithoutCodedChannels++;
            }

            return FrameFault.None;
        }

        if (block is not null)
        {
            block.HasCodedChannel = true;
        }

        bool isExponentsSent = true;
        long gain = 0;
        FrameFault fault = ReadCodedBlock(p, bits, ref state, size, isMidSide, coded, totals, block, ref isExponentsSent, ref gain);
        if (totals is not null)
        {
            if (!totals.HasGain)
            {
                totals.HasGain = true;
                totals.SmallestGain = gain;
                totals.LargestGain = gain;
            }
            else
            {
                totals.SmallestGain = Math.Min(totals.SmallestGain, gain);
                totals.LargestGain = Math.Max(totals.LargestGain, gain);
            }

            if (!isExponentsSent)
            {
                totals.ReusingExponents++;
            }
        }

        return fault;
    }

    private static void Known(int size, StdTotals? totals, TraceBlock? block)
    {
        if (totals is not null)
        {
            totals.BySize[size]++;
        }

        if (block is not null)
        {
            block.SizeIndex = size;
        }
    }

    private static FrameFault ReadCodedBlock(StdParameters p, Bits bits, ref FrameState state, int size, bool isMidSide, ReadOnlySpan<bool> coded, StdTotals? totals,
        TraceBlock? block, ref bool isExponentsSent, ref long gain)
    {
        List<TraceField>? fields = block?.Fields;
        int channels = p.Channels;
        int frameLength = p.FrameLength;
        int length = frameLength >> size;

        // The total gain: 1 plus a sum of 7-bit values, each below 127 but the last.
        int gainAt = bits.Position;
        long sum = 1;
        while (true)
        {
            if (bits.Remaining < 7)
            {
                return FrameFault.GainOverread;
            }

            int value = (int)bits.Read(7);
            sum += value;
            if (value != 127)
            {
                break;
            }
        }

        gain = sum;
        fields?.Add(new TraceField(FieldKind.Gain, 0, gainAt, bits.Position, (int)Math.Min(sum, int.MaxValue), 0));
        int escapeWidth = sum < 15 ? 13 : sum < 32 ? 12 : sum < 40 ? 11 : sum < 45 ? 10 : 9;

        Span<int> flaggedWidth = stackalloc int[2];
        if (p.UsesNoiseSubstitution)
        {
            int[] high = p.HighBands[size];
            int flagsAt = bits.Position;
            int flagged = 0;
            int total = 0;
            Span<int> flaggedBands = stackalloc int[2];
            for (int c = 0; c < channels; c++)
            {
                if (!coded[c])
                {
                    continue;
                }

                for (int i = 0; i < high.Length; i++)
                {
                    total++;
                    if (bits.Read(1) != 0)
                    {
                        flagged++;
                        flaggedBands[c]++;
                        flaggedWidth[c] += high[i];
                    }
                }
            }

            fields?.Add(new TraceField(FieldKind.NoiseFlags, 0, flagsAt, bits.Position, flagged, total));
            if (flagged > 0)
            {
                if (totals is not null)
                {
                    totals.NoiseSubstituted++;
                }

                int levelsAt = bits.Position;
                for (int c = 0; c < channels; c++)
                {
                    if (!coded[c])
                    {
                        continue;
                    }

                    // Each channel's levels start from a 7-bit value.
                    bool first = true;

                    for (int i = 0; i < flaggedBands[c]; i++)
                    {
                        if (first)
                        {
                            bits.Read(7);
                            first = false;
                        }
                        else
                        {
                            StdCodebooks.NoiseLevel.Read(bits);
                        }
                    }
                }

                fields?.Add(new TraceField(FieldKind.NoiseLevels, 0, levelsAt, bits.Position, flagged, 0));
            }
        }

        if (length < frameLength)
        {
            int at = bits.Position;
            isExponentsSent = bits.Read(1) != 0;
            fields?.Add(new TraceField(FieldKind.ExponentsSent, 0, at, bits.Position, isExponentsSent ? 1 : 0, 0));
        }

        if (isExponentsSent)
        {
            for (int c = 0; c < channels; c++)
            {
                if (!coded[c])
                {
                    continue;
                }

                int at = bits.Position;
                FrameFault fault = p.UsesScaleFactorExponents ? ReadScaleFactors(p, bits, size) : ReadLineSpectralPairs(bits);
                if (fault != FrameFault.None)
                {
                    return fault;
                }

                fields?.Add(new TraceField(FieldKind.Exponents, c, at, bits.Position, 0, 0));
                state.Received |= 1 << c;
            }
        }

        for (int c = 0; c < channels; c++)
        {
            if (coded[c] && (state.Received & (1 << c)) == 0)
            {
                return FrameFault.ExponentsMissing;
            }
        }

        int pair = p.CodebookPair;
        for (int c = 0; c < channels; c++)
        {
            if (coded[c])
            {
                int book = (2 * pair) + (isMidSide && c == 1 ? 1 : 0);
                int at = bits.Position;
                int n = p.CoefficientEnds[size] - p.FirstCoefficient - flaggedWidth[c];
                FrameFault fault = ReadCoefficients(p, bits, book, n, escapeWidth, totals, out int nonZero, out int last);
                if (fault != FrameFault.None)
                {
                    return fault;
                }

                if (p.Version == 1 && channels == 2)
                {
                    bits.Align();
                }

                fields?.Add(new TraceField(FieldKind.Coefficients, c, at, bits.Position, nonZero, last));
            }
            else if (p.Version == 1 && channels == 2)
            {
                bits.Align();
            }
        }

        return FrameFault.None;
    }

    private static FrameFault ReadLineSpectralPairs(Bits bits)
    {
        bits.Skip(37);
        return bits.IsOverrun ? FrameFault.OutOfBits : FrameFault.None;
    }

    private static FrameFault ReadScaleFactors(StdParameters p, Bits bits, int size)
    {
        int length = p.FrameLength >> size;
        int[] bands = p.ExponentBands[size];
        int value = 36;
        if (p.Version == 1)
        {
            value = (int)bits.Read(5) + 10;
        }

        int covered = 0;
        for (int band = 0; covered < length; band++)
        {
            if (p.Version == 2 || band > 0)
            {
                int index = StdCodebooks.Exponent.Read(bits);
                value += index - 60;
                if (value is < -60 or > 95)
                {
                    return FrameFault.ExponentRange;
                }

                if (bits.IsOverrun)
                {
                    return FrameFault.OutOfBits;
                }
            }

            int width = band < bands.Length ? bands[band] : 0;
            covered += width > 0 ? width : 4;
        }

        return bits.IsOverrun ? FrameFault.OutOfBits : FrameFault.None;
    }

    private static FrameFault ReadCoefficients(StdParameters p, Bits bits, int book, int n, int escapeWidth, StdTotals? totals, out int nonZero, out int last)
    {
        Vlc vlc = StdCodebooks.Coefficients[book];
        ushort[] runs = StdCodebooks.Runs[book];
        int position = 0;
        nonZero = 0;
        last = 0;
        int escapes = 0;
        while (position < n)
        {
            int symbol = vlc.Read(bits);
            if (symbol >= 2)
            {
                position += runs[symbol];
                bits.Read(1);
                nonZero++;
                last = position;
                position++;
            }
            else if (symbol == 1)
            {
                break;
            }
            else
            {
                bits.Read(escapeWidth);
                position += (int)bits.Read(p.FrameBits);
                bits.Read(1);
                nonZero++;
                escapes++;
                last = position;
                position++;
            }

            if (bits.IsOverrun)
            {
                break;
            }
        }

        if (totals is not null)
        {
            totals.NonZero += nonZero;
            totals.Escapes += escapes;
        }

        if (position > n)
        {
            return FrameFault.CoefficientOverflow;
        }

        return bits.IsOverrun ? FrameFault.OutOfBits : FrameFault.None;
    }
}
