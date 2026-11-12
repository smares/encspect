namespace Encspect.Formats.Opus;

// What the entropy pass of the SILK part of one Opus frame found, for the report.
internal struct SilkFacts
{
    public int Channels;
    public int Frames;
    public int SampleRate;

    // The frames of the part by signal type (0 no voice activity, 1 unvoiced, 2 voiced), over every channel; frames whose in-band redundancy (LBRR) data were read and skipped.
    public int Inactive;
    public int Unvoiced;
    public int Voiced;
    public int LbrrFrames;
}

// The syntax of the SILK layer (RFC 6716, section 4.2): the header bits, the stereo prediction weights, and for every frame and channel the quantization indices and
// the pulses, read with the code of dec_API.c, decode_indices.c, decode_pulses.c, shell_coder.c, code_signs.c, NLSF_unpack.c and stereo_decode_pred.c of libopus 1.5.2
// (BSD-3-Clause, notice in THIRD-PARTY-NOTICES.md). SILK's integers are exact in any port, so the decoder shares the tables and the macros (SilkMath) with the walk.
//
// The record of a SILK part (integers, in order): channels, sample rate in kHz (8, 12 or 16), subframes of a frame (2 or 4), frames; then for each frame: for two
// channels the prediction weights (two) and whether only the mid channel is coded, then for each coded channel (the second one only where the frame codes it) the
// frame's indices (SilkIndices.Size integers) and its pulses (the frame length, with their signs).
internal sealed class SilkSyntax
{
    public const int FrameLength10 = 80;

    private readonly Channel[] _channels = [new Channel(), new Channel()];
    private readonly short[] _pulses = new short[336];
    private int _channelsInternal;
    private int _previousDecodeOnlyMiddle;

    private sealed class Channel
    {
        public int EcPrevSignalType;
        public int EcPrevLagIndex;
        public int[] VadFlags = new int[3];
        public int LbrrFlag;
        public int[] LbrrFlags = new int[3];
        public int FramesDecoded;
    }

    // The state a decoder starts with (a gap, an empty frame, a stream start).
    public void Reset()
    {
        _channels[0] = new Channel();
        _channels[1] = new Channel();
        _channelsInternal = 0;
        _previousDecodeOnlyMiddle = 0;
    }

    // silk_ResetDecoder: the channels start fresh; how many there are stays (psDec->nChannelsInternal).
    public void ResetDecoder()
    {
        _channels[0] = new Channel();
        _channels[1] = new Channel();
        _previousDecodeOnlyMiddle = 0;
    }

    // Walks the SILK part of one Opus frame: audioMs is the duration the part decodes (10, 20, 40 or 60 ms).
    public SilkFacts Walk(ref RangeDecoder dec, IntWriter w, int channels, int fsKHz, int audioMs)
    {
        int payloadMs = Math.Max(10, audioMs);
        int framesPerPacket = payloadMs switch { 10 or 20 => 1, 40 => 2, _ => 3 };
        int nbSubfr = payloadMs == 10 ? 2 : 4;
        var facts = new SilkFacts { Channels = channels, Frames = framesPerPacket, SampleRate = fsKHz * 1000 };
        w.Add(channels);
        w.Add(fsKHz);
        w.Add(nbSubfr);
        w.Add(framesPerPacket);
        for (int call = 0; call < framesPerPacket; call++)
        {
            WalkCall(ref dec, w, channels, fsKHz, nbSubfr, framesPerPacket, call == 0, ref facts);
        }

        return facts;
    }

    // One call of silk_Decode (dec_API.c) with data and no loss.
    private void WalkCall(ref RangeDecoder dec, IntWriter w, int channelsInternal, int fsKHz, int nbSubfr, int framesPerPacket, bool isFirst, ref SilkFacts facts)
    {
        int frameLength = nbSubfr * 5 * fsKHz;
        if (isFirst)
        {
            for (int n = 0; n < channelsInternal; n++)
            {
                _channels[n].FramesDecoded = 0;
            }
        }

        if (channelsInternal > _channelsInternal)
        {
            _channels[1] = new Channel();
        }

        Span<int> msPred = stackalloc int[2];
        int decodeOnlyMiddle = 0;
        if (_channels[0].FramesDecoded == 0)
        {
            for (int n = 0; n < channelsInternal; n++)
            {
                for (int i = 0; i < framesPerPacket; i++)
                {
                    _channels[n].VadFlags[i] = dec.BitLogP(1);
                }

                _channels[n].LbrrFlag = dec.BitLogP(1);
            }

            // The flags of the frames of each channel come after the flags of every channel (RFC 6716, section 4.2.4; the second loop of silk_Decode).
            for (int n = 0; n < channelsInternal; n++)
            {
                _channels[n].LbrrFlags[0] = _channels[n].LbrrFlags[1] = _channels[n].LbrrFlags[2] = 0;
                if (_channels[n].LbrrFlag != 0)
                {
                    if (framesPerPacket == 1)
                    {
                        _channels[n].LbrrFlags[0] = 1;
                    }
                    else
                    {
                        int symbol = dec.ICdf(framesPerPacket == 2 ? SilkTables.LbrrFlags2ICdf : SilkTables.LbrrFlags3ICdf, 8) + 1;
                        for (int i = 0; i < framesPerPacket; i++)
                        {
                            _channels[n].LbrrFlags[i] = (symbol >> i) & 1;
                        }
                    }
                }
            }

            // The in-band redundancy is read and dropped, as a decoder that is not concealing a loss does.
            for (int i = 0; i < framesPerPacket; i++)
            {
                for (int n = 0; n < channelsInternal; n++)
                {
                    if (_channels[n].LbrrFlags[i] == 0)
                    {
                        continue;
                    }

                    if (channelsInternal == 2 && n == 0)
                    {
                        StereoDecodePred(ref dec, msPred);
                        if (_channels[1].LbrrFlags[i] == 0)
                        {
                            decodeOnlyMiddle = dec.ICdf(SilkTables.StereoOnlyCodeMidICdf, 8);
                        }
                    }

                    int condCoding = i > 0 && _channels[n].LbrrFlags[i - 1] != 0 ? SilkIndices.CodeConditionally : SilkIndices.CodeIndependently;
                    var scratch = new SilkIndices();
                    DecodeIndices(ref dec, _channels[n], fsKHz, nbSubfr, i, true, condCoding, ref scratch);
                    DecodePulses(ref dec, _pulses, scratch.SignalType, scratch.QuantOffsetType, frameLength);
                    facts.LbrrFrames++;
                }
            }
        }

        if (channelsInternal == 2)
        {
            StereoDecodePred(ref dec, msPred);
            if (_channels[1].VadFlags[_channels[0].FramesDecoded] == 0)
            {
                decodeOnlyMiddle = dec.ICdf(SilkTables.StereoOnlyCodeMidICdf, 8);
            }
            else
            {
                decodeOnlyMiddle = 0;
            }

            w.Add(msPred[0]);
            w.Add(msPred[1]);
            w.Add(decodeOnlyMiddle);
        }

        bool hasSide = decodeOnlyMiddle == 0;
        for (int n = 0; n < channelsInternal; n++)
        {
            if (n == 0 || hasSide)
            {
                Channel channel = _channels[n];
                int frameIndex = _channels[0].FramesDecoded - n;
                int condCoding;
                if (frameIndex <= 0)
                {
                    condCoding = SilkIndices.CodeIndependently;
                }
                else if (n > 0 && _previousDecodeOnlyMiddle != 0)
                {
                    condCoding = SilkIndices.CodeIndependentlyNoLtpScaling;
                }
                else
                {
                    condCoding = SilkIndices.CodeConditionally;
                }

                var indices = new SilkIndices();
                DecodeIndices(ref dec, channel, fsKHz, nbSubfr, channel.FramesDecoded, false, condCoding, ref indices);
                DecodePulses(ref dec, _pulses, indices.SignalType, indices.QuantOffsetType, frameLength);
                indices.Write(w, condCoding);
                for (int i = 0; i < frameLength; i++)
                {
                    w.Add(_pulses[i]);
                }

                if (indices.SignalType == 0)
                {
                    facts.Inactive++;
                }
                else if (indices.SignalType == 1)
                {
                    facts.Unvoiced++;
                }
                else
                {
                    facts.Voiced++;
                }
            }

            _channels[n].FramesDecoded++;
        }

        _channelsInternal = channelsInternal;
        _previousDecodeOnlyMiddle = decodeOnlyMiddle;
    }

    // silk_stereo_decode_pred (stereo_decode_pred.c).
    private static void StereoDecodePred(ref RangeDecoder dec, scoped Span<int> pred)
    {
        Span<int> ix = stackalloc int[6];
        int n = dec.ICdf(SilkTables.StereoPredJointICdf, 8);
        ix[2] = n / 5;
        ix[5] = n - (5 * ix[2]);
        for (n = 0; n < 2; n++)
        {
            ix[n * 3] = dec.ICdf(SilkTables.Uniform3ICdf, 8);
            ix[(n * 3) + 1] = dec.ICdf(SilkTables.Uniform5ICdf, 8);
        }

        for (n = 0; n < 2; n++)
        {
            ix[n * 3] += 3 * ix[(n * 3) + 2];
            int lowQ13 = SilkTables.StereoPredQuantQ13[ix[n * 3]];
            int stepQ13 = SilkMath.Smulwb(SilkTables.StereoPredQuantQ13[ix[n * 3] + 1] - lowQ13, 6554);
            pred[n] = SilkMath.Smlabb(lowQ13, stepQ13, (2 * ix[(n * 3) + 1]) + 1);
        }

        pred[0] -= pred[1];
    }

    // silk_decode_indices (decode_indices.c).
    private static void DecodeIndices(ref RangeDecoder dec, Channel channel, int fsKHz, int nbSubfr, int frameIndex, bool isLbrr, int condCoding, ref SilkIndices indices)
    {
        int ix;
        if (isLbrr || channel.VadFlags[frameIndex] != 0)
        {
            ix = dec.ICdf(SilkTables.TypeOffsetVadICdf, 8) + 2;
        }
        else
        {
            ix = dec.ICdf(SilkTables.TypeOffsetNoVadICdf, 8);
        }

        indices.SignalType = ix >> 1;
        indices.QuantOffsetType = ix & 1;
        if (condCoding == SilkIndices.CodeConditionally)
        {
            indices.Gains[0] = dec.ICdf(SilkTables.DeltaGainICdf, 8);
        }
        else
        {
            indices.Gains[0] = dec.ICdf(SilkTables.GainICdf.Slice(indices.SignalType * 8, 8), 8) << 3;
            indices.Gains[0] += dec.ICdf(SilkTables.Uniform8ICdf, 8);
        }

        for (int i = 1; i < nbSubfr; i++)
        {
            indices.Gains[i] = dec.ICdf(SilkTables.DeltaGainICdf, 8);
        }

        bool wide = fsKHz == 16;
        int order = wide ? 16 : 10;
        ReadOnlySpan<byte> cb1ICdf = wide ? SilkTables.NlsfCb1WbICdf : SilkTables.NlsfCb1NbMbICdf;
        indices.Nlsf[0] = dec.ICdf(cb1ICdf.Slice((indices.SignalType >> 1) * 32, 32), 8);
        Span<short> ecIx = stackalloc short[16];
        Span<byte> pred = stackalloc byte[16];
        NlsfUnpack(ecIx, pred, wide, indices.Nlsf[0]);
        ReadOnlySpan<byte> ecICdf = wide ? SilkTables.NlsfCb2ICdfWb : SilkTables.NlsfCb2ICdfNbMb;
        for (int i = 0; i < order; i++)
        {
            ix = dec.ICdf(ecICdf.Slice(ecIx[i]), 8);
            if (ix == 0)
            {
                ix -= dec.ICdf(SilkTables.NlsfExtICdf, 8);
            }
            else if (ix == 2 * 4)
            {
                ix += dec.ICdf(SilkTables.NlsfExtICdf, 8);
            }

            indices.Nlsf[i + 1] = ix - 4;
        }

        indices.NlsfInterpCoef = nbSubfr == 4 ? dec.ICdf(SilkTables.NlsfInterpolationFactorICdf, 8) : 4;
        if (indices.SignalType == 2)
        {
            bool absolute = true;
            if (condCoding == SilkIndices.CodeConditionally && channel.EcPrevSignalType == 2)
            {
                int delta = dec.ICdf(SilkTables.PitchDeltaICdf, 8);
                if (delta > 0)
                {
                    delta -= 9;
                    indices.LagIndex = (short)(channel.EcPrevLagIndex + delta);
                    absolute = false;
                }
            }

            if (absolute)
            {
                indices.LagIndex = (short)(dec.ICdf(SilkTables.PitchLagICdf, 8) * (fsKHz >> 1));
                ReadOnlySpan<byte> low = fsKHz == 16 ? SilkTables.Uniform8ICdf : fsKHz == 12 ? SilkTables.Uniform6ICdf : SilkTables.Uniform4ICdf;
                indices.LagIndex += (short)dec.ICdf(low, 8);
            }

            channel.EcPrevLagIndex = indices.LagIndex;
            ReadOnlySpan<byte> contour = fsKHz == 8
                ? (nbSubfr == 4 ? SilkTables.PitchContourNbICdf : SilkTables.PitchContour10MsNbICdf)
                : (nbSubfr == 4 ? SilkTables.PitchContourICdf : SilkTables.PitchContour10MsICdf);
            indices.ContourIndex = dec.ICdf(contour, 8);
            indices.PerIndex = dec.ICdf(SilkTables.LtpPerIndexICdf, 8);
            ReadOnlySpan<byte> ltpICdf = indices.PerIndex == 0 ? SilkTables.LtpGainICdf0 : indices.PerIndex == 1 ? SilkTables.LtpGainICdf1 : SilkTables.LtpGainICdf2;
            for (int k = 0; k < nbSubfr; k++)
            {
                indices.LtpIndex[k] = dec.ICdf(ltpICdf, 8);
            }

            indices.LtpScaleIndex = condCoding == SilkIndices.CodeIndependently ? dec.ICdf(SilkTables.LtpScaleICdf, 8) : 0;
        }

        channel.EcPrevSignalType = indices.SignalType;
        indices.Seed = dec.ICdf(SilkTables.Uniform4ICdf, 8);
    }

    // silk_NLSF_unpack (NLSF_unpack.c): the entropy coding context and the prediction weight of each coefficient of a first stage vector.
    internal static void NlsfUnpack(Span<short> ecIx, Span<byte> predQ8, bool wide, int cb1Index)
    {
        int order = wide ? 16 : 10;
        ReadOnlySpan<byte> sel = wide ? SilkTables.NlsfCb2SelectWb : SilkTables.NlsfCb2SelectNbMb;
        ReadOnlySpan<byte> pred = wide ? SilkTables.NlsfPredWbQ8 : SilkTables.NlsfPredNbMbQ8;
        int at = cb1Index * order / 2;
        for (int i = 0; i < order; i += 2)
        {
            byte entry = sel[at++];
            ecIx[i] = (short)(((entry >> 1) & 7) * ((2 * 4) + 1));
            predQ8[i] = pred[i + ((entry & 1) * (order - 1))];
            ecIx[i + 1] = (short)(((entry >> 5) & 7) * ((2 * 4) + 1));
            predQ8[i + 1] = pred[i + (((entry >> 4) & 1) * (order - 1)) + 1];
        }
    }

    // silk_decode_pulses (decode_pulses.c), with the shell decoder (shell_coder.c) and the signs (code_signs.c). The pulses come out in index order, signed.
    private static void DecodePulses(ref RangeDecoder dec, short[] pulses, int signalType, int quantOffsetType, int frameLength)
    {
        Span<int> sumPulses = stackalloc int[20];
        Span<int> lshifts = stackalloc int[20];
        int rateLevel = dec.ICdf(SilkTables.RateLevelsICdf.Slice((signalType >> 1) * 9, 9), 8);
        int iter = frameLength >> 4;
        if ((iter << 4) < frameLength)
        {
            iter++;
        }

        ReadOnlySpan<byte> cdf = SilkTables.PulsesPerBlockICdf.Slice(rateLevel * 18, 18);
        for (int i = 0; i < iter; i++)
        {
            lshifts[i] = 0;
            sumPulses[i] = dec.ICdf(cdf, 8);
            while (sumPulses[i] == 17)
            {
                lshifts[i]++;
                sumPulses[i] = dec.ICdf(SilkTables.PulsesPerBlockICdf.Slice((9 * 18) + (lshifts[i] == 10 ? 1 : 0)), 8);
            }
        }

        for (int i = 0; i < iter; i++)
        {
            Span<short> block = pulses.AsSpan(i * 16, 16);
            if (sumPulses[i] > 0)
            {
                ShellDecode(ref dec, block, sumPulses[i]);
            }
            else
            {
                block.Clear();
            }
        }

        for (int i = 0; i < iter; i++)
        {
            if (lshifts[i] > 0)
            {
                int nls = lshifts[i];
                for (int k = 0; k < 16; k++)
                {
                    int absQ = pulses[(i * 16) + k];
                    for (int j = 0; j < nls; j++)
                    {
                        absQ <<= 1;
                        absQ += dec.ICdf(SilkTables.LsbICdf, 8);
                    }

                    pulses[(i * 16) + k] = (short)absQ;
                }

                sumPulses[i] |= nls << 5;
            }
        }

        // silk_decode_signs
        Span<byte> icdf = stackalloc byte[2];
        icdf[1] = 0;
        int signAt = 7 * (quantOffsetType + (signalType << 1));
        int blocks = (frameLength + 8) >> 4;
        for (int i = 0; i < blocks; i++)
        {
            int p = sumPulses[i];
            if (p > 0)
            {
                icdf[0] = SilkTables.SignICdf[signAt + Math.Min(p & 0x1F, 6)];
                for (int j = 0; j < 16; j++)
                {
                    int q = pulses[(i * 16) + j];
                    if (q > 0)
                    {
                        pulses[(i * 16) + j] = (short)(q * ((dec.ICdf(icdf, 8) << 1) - 1));
                    }
                }
            }
        }
    }

    private static void ShellDecode(ref RangeDecoder dec, scoped Span<short> pulses0, int pulses4)
    {
        Span<short> p3 = stackalloc short[2];
        Span<short> p2 = stackalloc short[4];
        Span<short> p1 = stackalloc short[8];
        Split(ref dec, p3, 0, pulses4, SilkTables.ShellCodeTable3);
        Split(ref dec, p2, 0, p3[0], SilkTables.ShellCodeTable2);
        Split(ref dec, p1, 0, p2[0], SilkTables.ShellCodeTable1);
        Split(ref dec, pulses0, 0, p1[0], SilkTables.ShellCodeTable0);
        Split(ref dec, pulses0, 2, p1[1], SilkTables.ShellCodeTable0);
        Split(ref dec, p1, 2, p2[1], SilkTables.ShellCodeTable1);
        Split(ref dec, pulses0, 4, p1[2], SilkTables.ShellCodeTable0);
        Split(ref dec, pulses0, 6, p1[3], SilkTables.ShellCodeTable0);
        Split(ref dec, p2, 2, p3[1], SilkTables.ShellCodeTable2);
        Split(ref dec, p1, 4, p2[2], SilkTables.ShellCodeTable1);
        Split(ref dec, pulses0, 8, p1[4], SilkTables.ShellCodeTable0);
        Split(ref dec, pulses0, 10, p1[5], SilkTables.ShellCodeTable0);
        Split(ref dec, p1, 6, p2[3], SilkTables.ShellCodeTable1);
        Split(ref dec, pulses0, 12, p1[6], SilkTables.ShellCodeTable0);
        Split(ref dec, pulses0, 14, p1[7], SilkTables.ShellCodeTable0);
    }

    private static void Split(ref RangeDecoder dec, scoped Span<short> children, int at, int p, ReadOnlySpan<byte> table)
    {
        if (p > 0)
        {
            children[at] = (short)dec.ICdf(table.Slice(SilkTables.ShellCodeTableOffsets[p]), 8);
            children[at + 1] = (short)(p - children[at]);
        }
        else
        {
            children[at] = 0;
            children[at + 1] = 0;
        }
    }
}

// The quantization indices of one SILK frame of one channel (SideInfoIndices of structs.h).
internal struct SilkIndices
{
    public const int CodeIndependently = 0;
    public const int CodeIndependentlyNoLtpScaling = 1;
    public const int CodeConditionally = 2;
    public const int Size = 34;

    public int SignalType;
    public int QuantOffsetType;
    public int[] Gains;
    public int[] Nlsf;
    public int NlsfInterpCoef;
    public short LagIndex;
    public int ContourIndex;
    public int PerIndex;
    public int[] LtpIndex;
    public int LtpScaleIndex;
    public int Seed;

    public SilkIndices()
    {
        Gains = new int[4];
        Nlsf = new int[17];
        LtpIndex = new int[4];
    }

    public readonly void Write(IntWriter w, int condCoding)
    {
        w.Add(SignalType);
        w.Add(QuantOffsetType);
        for (int i = 0; i < 4; i++)
        {
            w.Add(Gains[i]);
        }

        for (int i = 0; i < 17; i++)
        {
            w.Add(Nlsf[i]);
        }

        w.Add(NlsfInterpCoef);
        w.Add(LagIndex);
        w.Add(ContourIndex);
        w.Add(PerIndex);
        for (int i = 0; i < 4; i++)
        {
            w.Add(LtpIndex[i]);
        }

        w.Add(LtpScaleIndex);
        w.Add(Seed);
        w.Add(condCoding);
    }

    public static SilkIndices Read(ref IntReader r, out int condCoding)
    {
        var s = new SilkIndices
        {
            SignalType = r.Next(),
            QuantOffsetType = r.Next(),
        };
        for (int i = 0; i < 4; i++)
        {
            s.Gains[i] = r.Next();
        }

        for (int i = 0; i < 17; i++)
        {
            s.Nlsf[i] = r.Next();
        }

        s.NlsfInterpCoef = r.Next();
        s.LagIndex = (short)r.Next();
        s.ContourIndex = r.Next();
        s.PerIndex = r.Next();
        for (int i = 0; i < 4; i++)
        {
            s.LtpIndex[i] = r.Next();
        }

        s.LtpScaleIndex = r.Next();
        s.Seed = r.Next();
        condCoding = r.Next();
        return s;
    }
}
