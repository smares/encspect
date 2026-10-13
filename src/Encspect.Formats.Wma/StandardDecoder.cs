namespace Encspect.Formats.Wma;

// What stopped the reading of a frame.
internal enum FrameStatus : byte
{
    Ok,

    // A block size index outside the stream's sizes (wmadec.c: "block_len_bits out of range").
    BlockSizeRange,

    // Blocks that add up to more than a frame (wmadec.c: "frame_len overflow").
    FrameOverflow,

    // The total gain ran past the end of the data (wmadec.c: "total_gain overread").
    GainOverread,

    // An exponent outside -60 to 95 (wmadec.c: "Exponent out of range").
    ExponentRange,

    // A channel coded for a block that reuses exponents it never received.
    ExponentsMissing,

    // Bits that are no code of the coefficient codebook.
    CoefficientCode,

    // Coefficients past the end of the block (wma.c: "overflow in spectral RLE").
    CoefficientOverflow,

    // A frame that needs more bits than it was given.
    Overrun,
}

// The bits of one block, as positions in the frame's buffer, and what was read in it. Bits are counted
// from the start of the frame's buffer (the first reservoir byte of a frame that spans superframes).
internal struct BlockRecord
{
    public int Start;
    public int LengthFieldsEnd;
    public int StereoEnd;
    public int CodedEnd;
    public int GainEnd;
    public int NoiseFlagsEnd;
    public int NoiseGainsEnd;
    public int ReuseEnd;
    public int Exponents0End;
    public int Exponents1End;
    public int Coefficients0End;
    public int Coefficients1End;
    public int End;
    public int TotalGain;
    public ushort NonZero0;
    public ushort NonZero1;
    public ushort Escapes0;
    public ushort Escapes1;
    public ushort Last0;
    public ushort Last1;
    public byte BlockBits;
    public byte PreviousBits;
    public byte NextBits;
    public byte NoiseBands0;
    public byte NoiseBands1;
    public byte Flags;

    // Both the previous and the current block size were sent (the first block of a superframe), else
    // only the next.
    public const byte SentPreviousAndCurrent = 1;
    public const byte HasStereoBit = 2;
    public const byte MidSide = 4;
    public const byte Coded0 = 8;
    public const byte Coded1 = 16;
    public const byte ExponentsSent = 32;
    public const byte ExponentsFlagRead = 64;
    public const byte HasGain = 128;

    public readonly bool IsMidSide => (Flags & MidSide) != 0;

    public readonly bool IsCoded(int channel) => (Flags & (channel == 0 ? Coded0 : Coded1)) != 0;

    public readonly int CodedChannels => ((Flags & Coded0) != 0 ? 1 : 0) + ((Flags & Coded1) != 0 ? 1 : 0);
}

// Reads the syntax of a Windows Media Audio version 1 or 2 frame as FFmpeg's wmadec.c does
// (wma_decode_frame and wma_decode_block), keeping the bit positions of each part instead of the
// audio. One instance follows one stream: the block sizes of a block depend on the one before.
internal sealed class StandardDecoder(StandardParameters p)
{
    private static readonly Lazy<Vlc> ExponentCodebook = new(() => new Vlc(Tables.ExponentCodes, Tables.ExponentLengths));

    private static readonly Lazy<Vlc> HighBandGainCodebook = new(() =>
        Vlc.FromLengths(Tables.HighBandGainLengths, [.. Tables.HighBandGainSymbols.Select(s => s - 18)]));

    private static readonly Lazy<Vlc>[] CoefficientCodebooks =
        [.. Enumerable.Range(0, 6).Select(i => new Lazy<Vlc>(() => new Vlc(Tables.CoefficientCodes[i], Tables.CoefficientLengths[i])))];

    private static readonly Lazy<ushort[]>[] RunTables = [.. Enumerable.Range(0, 6).Select(i => new Lazy<ushort[]>(() => BuildRunTable(i)))];

    private bool _resetBlockLengths = true;
    private int _blockLengthBits = p.FrameLengthBits;
    private int _nextBlockLengthBits = p.FrameLengthBits;
    private readonly bool[] _exponentsInitialized = new bool[2];

    // wmadec.c: reset_block_lengths, set at the start of a stream and of the frames after the bit
    // offset in a superframe, which then send the previous and current block sizes.
    public void ResetBlockLengths() => _resetBlockLengths = true;

    public static Vlc ExponentVlc => ExponentCodebook.Value;

    public static Vlc HighBandGainVlc => HighBandGainCodebook.Value;

    public static Vlc CoefficientVlc(int index) => CoefficientCodebooks[index].Value;

    // wma.c init_coef_vlc: codes 0 and 1 are the escape and the end of the block; the codes after them
    // run in groups of the sizes the level table gives, a group per level, with runs 0, 1, 2 and so on.
    public static ushort[] RunTable(int index) => RunTables[index].Value;

    private static ushort[] BuildRunTable(int index)
    {
        int count = Tables.CoefficientLengths[index].Length;
        ushort[] levels = Tables.CoefficientLevels[index];
        var run = new ushort[count];
        int i = 2;
        int k = 0;
        while (i < count && k < levels.Length)
        {
            int length = levels[k++];
            for (int j = 0; j < length && i < count; j++)
            {
                run[i++] = (ushort)j;
            }
        }

        return run;
    }

    // Reads one frame from the bits' position; the blocks' records are added to blocks. The status says
    // what stopped the frame, if anything.
    public FrameStatus DecodeFrame(Bits bits, List<BlockRecord> blocks, out int blockCount)
    {
        blockCount = 0;
        int blockPosition = 0;
        while (true)
        {
            var record = new BlockRecord { Start = bits.Position };
            FrameStatus status = DecodeBlock(bits, ref record, ref blockPosition, out bool last);
            record.End = bits.Position;
            blocks.Add(record);
            blockCount++;
            if (status != FrameStatus.Ok)
            {
                return status;
            }

            if (last)
            {
                return bits.IsOverrun ? FrameStatus.Overrun : FrameStatus.Ok;
            }

            if (bits.IsOverrun)
            {
                return FrameStatus.Overrun;
            }
        }
    }

    private FrameStatus DecodeBlock(Bits bits, ref BlockRecord r, ref int blockPosition, out bool last)
    {
        last = false;
        int channels = p.Channels;
        if (p.UsesVariableBlockLength)
        {
            int n = StandardParameters.Log2(p.BlockSizeCount - 1) + 1;
            if (_resetBlockLengths)
            {
                _resetBlockLengths = false;
                r.Flags |= BlockRecord.SentPreviousAndCurrent;
                int v = (int)bits.Read(n);
                if (v >= p.BlockSizeCount)
                {
                    return FrameStatus.BlockSizeRange;
                }

                r.PreviousBits = (byte)(p.FrameLengthBits - v);
                v = (int)bits.Read(n);
                if (v >= p.BlockSizeCount)
                {
                    return FrameStatus.BlockSizeRange;
                }

                _blockLengthBits = p.FrameLengthBits - v;
            }
            else
            {
                r.PreviousBits = (byte)_blockLengthBits;
                _blockLengthBits = _nextBlockLengthBits;
            }

            int next = (int)bits.Read(n);
            if (next >= p.BlockSizeCount)
            {
                r.BlockBits = (byte)_blockLengthBits;
                return FrameStatus.BlockSizeRange;
            }

            _nextBlockLengthBits = p.FrameLengthBits - next;
        }
        else
        {
            _nextBlockLengthBits = p.FrameLengthBits;
            _blockLengthBits = p.FrameLengthBits;
            r.PreviousBits = (byte)p.FrameLengthBits;
        }

        r.BlockBits = (byte)_blockLengthBits;
        r.NextBits = (byte)_nextBlockLengthBits;
        r.LengthFieldsEnd = bits.Position;

        if (p.FrameLengthBits - _blockLengthBits >= p.BlockSizeCount)
        {
            return FrameStatus.BlockSizeRange;
        }

        int blockLength = 1 << _blockLengthBits;
        if (blockPosition + blockLength > p.FrameLength)
        {
            return FrameStatus.FrameOverflow;
        }

        if (channels == 2)
        {
            r.Flags |= BlockRecord.HasStereoBit;
            if (bits.ReadBit() != 0)
            {
                r.Flags |= BlockRecord.MidSide;
            }
        }

        r.StereoEnd = bits.Position;
        bool any = false;
        for (int ch = 0; ch < channels; ch++)
        {
            if (bits.ReadBit() != 0)
            {
                r.Flags |= ch == 0 ? BlockRecord.Coded0 : BlockRecord.Coded1;
                any = true;
            }
        }

        r.CodedEnd = bits.Position;
        int bsize = p.FrameLengthBits - _blockLengthBits;
        r.GainEnd = r.NoiseFlagsEnd = r.NoiseGainsEnd = r.ReuseEnd = r.Exponents0End = r.Exponents1End = r.Coefficients0End = r.Coefficients1End = r.CodedEnd;

        if (any)
        {
            int totalGain = 1;
            while (true)
            {
                if (bits.Remaining < 7)
                {
                    return FrameStatus.GainOverread;
                }

                int a = (int)bits.Read(7);
                totalGain += a;
                if (a != 127)
                {
                    break;
                }
            }

            r.TotalGain = totalGain;
            r.Flags |= BlockRecord.HasGain;
            r.GainEnd = r.NoiseFlagsEnd = r.NoiseGainsEnd = r.ReuseEnd = r.Exponents0End = r.Exponents1End = r.Coefficients0End = r.Coefficients1End = bits.Position;
            int coefficientBits = StandardParameters.TotalGainToBits(totalGain);

            int count = p.CoefsEnd[bsize] - p.CoefsStart;
            Span<int> coefficients = [count, count];
            Span<byte> noise = [0, 0];
            if (p.UsesNoiseCoding)
            {
                int bands = p.ExponentHighSizes[bsize];
                for (int ch = 0; ch < channels; ch++)
                {
                    if (!r.IsCoded(ch))
                    {
                        continue;
                    }

                    for (int i = 0; i < bands; i++)
                    {
                        if (bits.ReadBit() != 0)
                        {
                            coefficients[ch] -= p.ExponentHighBands[bsize][i];
                            noise[ch]++;
                        }
                    }
                }

                r.NoiseBands0 = noise[0];
                r.NoiseBands1 = noise[1];
                r.NoiseFlagsEnd = bits.Position;

                // The gains of the noise-coded bands: the first 7 bits, the rest coded as differences.
                // Reading the flags again would need them kept; the gains are one value per coded band.
                // (A second pass over the flags is not possible here, so the count read above sizes this.)
                for (int ch = 0; ch < channels; ch++)
                {
                    if (!r.IsCoded(ch))
                    {
                        continue;
                    }

                    bool first = true;
                    for (int i = 0; i < noise[ch]; i++)
                    {
                        if (first)
                        {
                            bits.Skip(7);
                            first = false;
                        }
                        else
                        {
                            HighBandGainVlc.Read(bits);
                        }
                    }
                }

                r.NoiseGainsEnd = bits.Position;
            }

            r.ReuseEnd = r.NoiseGainsEnd;
            if (_blockLengthBits == p.FrameLengthBits || bits.ReadBit() != 0)
            {
                r.Flags |= BlockRecord.ExponentsSent;
                if (_blockLengthBits != p.FrameLengthBits)
                {
                    r.Flags |= BlockRecord.ExponentsFlagRead;
                }

                r.ReuseEnd = bits.Position;
                int exponentEnd = r.ReuseEnd;
                for (int ch = 0; ch < channels; ch++)
                {
                    if (r.IsCoded(ch))
                    {
                        FrameStatus status = p.UsesExponentVlc ? DecodeExponentsVlc(bits, bsize) : DecodeExponentsLsp(bits);
                        if (status != FrameStatus.Ok)
                        {
                            return status;
                        }

                        _exponentsInitialized[ch] = true;
                    }

                    exponentEnd = bits.Position;
                    if (ch == 0)
                    {
                        r.Exponents0End = exponentEnd;
                    }
                    else
                    {
                        r.Exponents1End = exponentEnd;
                    }
                }

                if (channels == 1)
                {
                    r.Exponents1End = r.Exponents0End;
                }
            }
            else
            {
                r.ReuseEnd = bits.Position;
                r.Flags |= BlockRecord.ExponentsFlagRead;
                r.Exponents0End = r.Exponents1End = bits.Position;
            }

            for (int ch = 0; ch < channels; ch++)
            {
                if (r.IsCoded(ch) && !_exponentsInitialized[ch])
                {
                    return FrameStatus.ExponentsMissing;
                }
            }

            for (int ch = 0; ch < channels; ch++)
            {
                if (r.IsCoded(ch))
                {
                    int tableIndex = (p.CoefficientTable * 2) + (ch == 1 && r.IsMidSide ? 1 : 0);
                    FrameStatus status = DecodeCoefficients(bits, tableIndex, coefficients[ch], blockLength, coefficientBits, out ushort nonZero, out ushort escapes, out ushort lastIndex);
                    if (ch == 0)
                    {
                        r.NonZero0 = nonZero;
                        r.Escapes0 = escapes;
                        r.Last0 = lastIndex;
                    }
                    else
                    {
                        r.NonZero1 = nonZero;
                        r.Escapes1 = escapes;
                        r.Last1 = lastIndex;
                    }

                    if (status != FrameStatus.Ok)
                    {
                        return status;
                    }
                }

                // Version 1 starts each channel's coefficients on a byte (wmadec.c), even one that is not coded.
                if (p.Version == 1 && channels >= 2)
                {
                    bits.Align();
                }

                if (ch == 0)
                {
                    r.Coefficients0End = bits.Position;
                }
                else
                {
                    r.Coefficients1End = bits.Position;
                }
            }

            if (channels == 1)
            {
                r.Coefficients1End = r.Coefficients0End;
            }
        }

        blockPosition += blockLength;
        last = blockPosition >= p.FrameLength;
        return FrameStatus.Ok;
    }

    // wmadec.c decode_exp_lsp: ten line spectral pair indexes of 3 or 4 bits.
    private static FrameStatus DecodeExponentsLsp(Bits bits)
    {
        for (int i = 0; i < 10; i++)
        {
            bits.Skip(i == 0 || i >= 8 ? 3 : 4);
        }

        return FrameStatus.Ok;
    }

    // wmadec.c decode_exp_vlc: version 1 starts with a 5-bit exponent, then every band is a code, a
    // difference from the exponent before, which has to stay within the table of powers.
    private FrameStatus DecodeExponentsVlc(Bits bits, int bsize)
    {
        int last;
        int index = 0;
        int position = 0;
        int blockLength = 1 << _blockLengthBits;
        if (p.Version == 1)
        {
            last = (int)bits.Read(5) + 10;
            position += Advance(p.ExponentBands[bsize], ref index, p.ExponentSizes[bsize]);
        }
        else
        {
            last = 36;
        }

        while (position < blockLength)
        {
            int code = ExponentVlc.Read(bits);
            if (code < 0)
            {
                return FrameStatus.ExponentRange;
            }

            last += code - 60;
            if ((uint)(last + 60) >= 156)
            {
                return FrameStatus.ExponentRange;
            }

            position += Advance(p.ExponentBands[bsize], ref index, p.ExponentSizes[bsize]);
            if (bits.IsOverrun)
            {
                return FrameStatus.Overrun;
            }
        }

        return FrameStatus.Ok;
    }

    // The next band's size in coefficients; FFmpeg's unrolled loop writes four for a size of 0.
    private static int Advance(int[] bands, ref int index, int count)
    {
        int size = index < count ? bands[index] : 0;
        index++;
        return size == 0 ? 4 : size;
    }

    // wma.c ff_wma_run_level_decode, version 0: each code is a run and a level, a sign bit follows; code 1 ends
    // the block, code 0 is an escape followed by the level in coefficientBits bits and the run in
    // frame-length-bits bits.
    private FrameStatus DecodeCoefficients(Bits bits, int tableIndex, int numCoefficients, int blockLength, int coefficientBits,
        out ushort nonZero, out ushort escapes, out ushort lastIndex)
    {
        Vlc vlc = CoefficientVlc(tableIndex);
        ushort[] run = RunTable(tableIndex);
        int nonZeroCount = 0;
        int escapeCount = 0;
        int last = 0;
        int mask = blockLength - 1;
        int offset = 0;
        for (; offset < numCoefficients; offset++)
        {
            int code = vlc.Read(bits);
            if (code < 0)
            {
                nonZero = (ushort)Math.Min(nonZeroCount, ushort.MaxValue);
                escapes = (ushort)Math.Min(escapeCount, ushort.MaxValue);
                lastIndex = (ushort)last;
                return FrameStatus.CoefficientCode;
            }

            if (code > 1)
            {
                offset += run[code];
                bits.Skip(1);
                nonZeroCount++;
                last = offset & mask;
            }
            else if (code == 1)
            {
                break;
            }
            else
            {
                bits.Skip(coefficientBits);
                offset += (int)bits.Read(p.FrameLengthBits);
                bits.Skip(1);
                nonZeroCount++;
                escapeCount++;
                last = offset & mask;
            }

            if (bits.IsOverrun)
            {
                break;
            }
        }

        nonZero = (ushort)Math.Min(nonZeroCount, ushort.MaxValue);
        escapes = (ushort)Math.Min(escapeCount, ushort.MaxValue);
        lastIndex = (ushort)last;
        return offset > numCoefficients ? FrameStatus.CoefficientOverflow : bits.IsOverrun ? FrameStatus.Overrun : FrameStatus.Ok;
    }
}
