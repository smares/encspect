using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.MpegAudio.Decoding;

// The reconstruction of the MPEG-2 multichannel extension of Layer II (ISO/IEC 13818-3 2.5.3.2; docs/design/lossy-decoders.md, "MPEG-2 multichannel"): the additional
// transmission channels T2 to T4 from their allocation, scalefactors and sample codes (the parser reads the codes into the lent buffers), dynamic crosstalk, prediction,
// dematrixing and de-normalization in the subband domain, the LFE channel, and the polyphase synthesis of every output channel. The multilingual channels are not decoded.
internal sealed class MultichannelDecoder
{
    // A frame's subband samples of one channel: three parts of 32 subbands of 12 time slots.
    private const int FrameSamples = 3 * 32 * 12;

    // The last subband samples of T0 and T1 a predictor can reach: 7 samples of delay compensation and two of tap.
    private const int PredictionHistory = 9;

    // The standard gives the LFE channel no output scale; the reference output of test01 of ISO/IEC 13818-4 is twice the requantized and scaled samples.
    private const float LfeGain = 2f;

    private readonly MultichannelPlan _plan;
    private readonly string _configuration;
    private readonly float[][] _histories;
    private readonly float[] _lins = new float[Polyphase.ScratchLength];
    private readonly float[] _pairLines = new float[2 * 576];
    private readonly float[] _pairSamples = new float[2 * 384];
    private readonly float[][] _signals = [new float[FrameSamples], new float[FrameSamples], new float[FrameSamples], new float[FrameSamples], new float[FrameSamples]];
    private readonly float[][] _outputs;
    private readonly float[] _variables = new float[Dematrix.Variables * 12];
    private readonly float[] _predictionHistory = new float[2 * 8 * PredictionHistory];
    private readonly Layer12Scale _base = new();

    public MultichannelDecoder(MultichannelPlan plan)
    {
        _plan = plan;
        _configuration = $"{(plan.HasCenter ? 3 : 2)}{(plan.Surround is 1 or 2 ? plan.Surround : 0)}";
        _histories = new float[(plan.FullChannels + 1) / 2][];
        for (int i = 0; i < _histories.Length; i++)
        {
            _histories[i] = new float[Polyphase.HistoryLength];
        }

        _outputs = new float[plan.FullChannels][];
        for (int i = 0; i < _outputs.Length; i++)
        {
            _outputs[i] = new float[FrameSamples];
        }
    }

    public void Reset()
    {
        foreach (float[] history in _histories)
        {
            Array.Clear(history);
        }

        Array.Clear(_predictionHistory);
    }

    // Decodes one frame into planar output: stride samples to a channel, the full-bandwidth channels first and the LFE last, as the plan's layout has them. lent holds
    // the frame's sample codes by channel (0 and 1 the base channels, 2 to 4 the additional ones, 5 the LFE's twelve).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Decode(MpegHeader header, ReadOnlySpan<byte> frame, ReadOnlySpan<int> lent, int stride, Span<float> planar, int planarStride)
    {
        if (!_base.Read(header, frame))
        {
            throw new InvalidDataException("The allocation and scalefactors run past the frame");
        }

        byte[] bytes = frame.ToArray();
        var audio = Layer2AudioData.Read(bytes, header);
        var mc = Layer2Multichannel.Read(bytes, audio.End, bytes.Length * 8, audio.BaseAllocation(), header.SampleRate);
        if (!mc.HeaderFits || !mc.IsCrcValid || mc.ChannelKey != _plan.Key)
        {
            throw new InvalidDataException("The multichannel extension is not the stream's or fails its CRC");
        }

        mc.ReadData(bytes);
        if (!mc.DataFits)
        {
            throw new InvalidDataException("The multichannel samples run past the frame");
        }

        foreach (float[] signal in _signals)
        {
            Array.Clear(signal);
        }

        ScaleBase(lent, stride);
        ScaleAdditional(mc, lent, stride);
        Predict(mc);
        SaveHistory();
        ApplyMatrix(mc);
        Synthesize(planar, planarStride);
        if (_plan.HasLfe)
        {
            Lfe(mc, lent.Slice(5 * stride, 12), planar.Slice(_plan.FullChannels * planarStride, planarStride));
        }
    }

    private void ScaleBase(ReadOnlySpan<int> lent, int stride)
    {
        for (int ch = 0; ch < 2; ch++)
        {
            for (int band = 0; band < _base.TotalBands; band++)
            {
                int entry = (2 * band) + ch;
                int ba = _base.ScalefactorAllocation[entry];
                for (int part = 0; part < 3; part++)
                {
                    int index = _base.Scalefactor[(entry * 3) + part];
                    float scale = index == 0 ? 0 : Layer12Dequantizer.Scale(ba, index - 1);
                    ReadOnlySpan<int> codes = lent.Slice((ch * stride) + (((part * 32) + band) * 12), 12);
                    Span<float> destination = _signals[ch].AsSpan((part * 384) + (band * 12), 12);
                    for (int k = 0; k < 12; k++)
                    {
                        destination[k] = codes[k] * scale;
                    }
                }
            }
        }
    }

    // T2 to T4: the quantized samples of the channel they come from (their own, or the source of a dynamic crosstalk copy) with their own scalefactors.
    private void ScaleAdditional(Layer2Multichannel mc, ReadOnlySpan<int> lent, int stride)
    {
        for (int ch = 0; ch < mc.AdditionalChannels; ch++)
        {
            for (int sb = 0; sb < mc.Subbands; sb++)
            {
                int index = mc.AllocationIndex(2 + ch, sb);
                int source = mc.SampleSource(2 + ch, sb);
                if (index == 0 || source < 0)
                {
                    continue;
                }

                int quantizer = Layer2Multichannel.Quantizer(Layer2AudioData.Levels(mc.Table, sb)[index]);
                for (int part = 0; part < 3; part++)
                {
                    int factor = mc.Scalefactor[ch, sb, part];
                    if (factor == 0)
                    {
                        continue;
                    }

                    float scale = Layer12Dequantizer.Scale(quantizer, factor - 1);
                    ReadOnlySpan<int> codes = lent.Slice((source * stride) + (((part * 32) + sb) * 12), 12);
                    Span<float> destination = _signals[2 + ch].AsSpan((part * 384) + (sb * 12), 12);
                    for (int k = 0; k < 12; k++)
                    {
                        destination[k] = codes[k] * scale;
                    }
                }
            }
        }
    }

    // The prediction of 2.5.3.2.1.3: in each of the subband groups 0 to 7 that use it, a channel transmitted alone gets the sum of up to three taps of T0 and of T1, which
    // already carry their scalefactors, added to its prediction error; the predictors go to the channels in the order T2, T3, T4, two each (T0's, then T1's).
    private void Predict(Layer2Multichannel mc)
    {
        if (!mc.HasPrediction)
        {
            return;
        }

        for (int sb = 0; sb < 8; sb++)
        {
            if (!mc.HasPredictionInGroup[sb])
            {
                continue;
            }

            int predictor = 0;
            for (int ch = 0; ch < mc.AdditionalChannels; ch++)
            {
                if (!IsPredicted(mc, ch, sb))
                {
                    continue;
                }

                for (int n = 0; n < 36; n++)
                {
                    float sum = 0;
                    for (int from = 0; from < 2; from++)
                    {
                        int px = predictor + from;
                        float taps = 0;
                        for (int pci = 0; pci < 3; pci++)
                        {
                            taps += mc.PredictionCoefficient[sb, px, pci] * Earlier(from, sb, n - mc.PredictionDelay[sb, px] - pci);
                        }

                        sum += taps;
                    }

                    _signals[2 + ch][((n / 12) * 384) + (sb * 12) + (n % 12)] += sum;
                }

                predictor += 2;
            }
        }
    }

    // Whether channel ch of the subband is predicted: its samples are its own and no other channel copies them, and it is no channel of a second stereo program, which is
    // never predicted.
    private static bool IsPredicted(Layer2Multichannel mc, int ch, int sb)
    {
        if (mc.SampleSource(2 + ch, sb) != 2 + ch || (mc.Surround == 3 && (ch > 0 || mc.Center is not (1 or 3))))
        {
            return false;
        }

        for (int other = 0; other < mc.AdditionalChannels; other++)
        {
            if (other != ch && mc.SampleSource(2 + other, sb) == 2 + ch)
            {
                return false;
            }
        }

        return true;
    }

    // The scaled sample of T0 or T1 at time n of the frame in a subband, n counted from the frame's start, negative into the frames before.
    private float Earlier(int channel, int sb, int n) =>
        n >= 0 ? _signals[channel][((n / 12) * 384) + (sb * 12) + (n % 12)] : _predictionHistory[(((channel * 8) + sb) * PredictionHistory) + (-n - 1)];

    private void SaveHistory()
    {
        for (int channel = 0; channel < 2; channel++)
        {
            for (int sb = 0; sb < 8; sb++)
            {
                for (int j = 0; j < PredictionHistory; j++)
                {
                    int n = 35 - j;
                    _predictionHistory[(((channel * 8) + sb) * PredictionHistory) + j] = _signals[channel][((n / 12) * 384) + (sb * 12) + (n % 12)];
                }
            }
        }
    }

    // Dematrixing and de-normalization, subband by subband with the transmission channel allocation of its group; a second stereo program comes through as it is.
    private void ApplyMatrix(Layer2Multichannel mc)
    {
        float[] factors = Dematrix.Factors(mc.DematrixProcedure);
        bool dematrixes = mc.AdditionalChannels > 0 && !(mc.Surround == 3 && !_plan.HasCenter);
        int front = _plan.HasCenter ? 1 : 0;
        for (int part = 0; part < 3; part++)
        {
            for (int sb = 0; sb < 32; sb++)
            {
                int offset = (part * 384) + (sb * 12);
                int group = Layer2Multichannel.Groups[Math.Min(sb, 29)];
                for (int v = 0; v < 5; v++)
                {
                    _signals[v].AsSpan(offset, 12).CopyTo(_variables.AsSpan(v * 12, 12));
                }

                Dematrix.Program program = Dematrix.Get(_configuration, dematrixes || _plan.HasCenter ? mc.DematrixProcedure : 3, mc.ChannelAllocation[group]);
                Dematrix.Run(program, _variables);
                int output = 0;
                Emit(output++, Dematrix.L, factors[0], dematrixes, offset);
                Emit(output++, Dematrix.R, factors[1], dematrixes, offset);
                if (_plan.HasCenter)
                {
                    Emit(output++, Dematrix.C, factors[2], dematrixes, offset);
                }

                if (mc.Surround == 1)
                {
                    Emit(output++, Dematrix.LS, factors[3], dematrixes, offset);
                }
                else if (mc.Surround == 2)
                {
                    Emit(output++, Dematrix.LS, factors[3], dematrixes, offset);
                    Emit(output++, Dematrix.RS, factors[4], dematrixes, offset);
                }
                else if (mc.Surround == 3)
                {
                    _signals[2 + front].AsSpan(offset, 12).CopyTo(_outputs[output++].AsSpan(offset, 12));
                    _signals[3 + front].AsSpan(offset, 12).CopyTo(_outputs[output++].AsSpan(offset, 12));
                }
            }
        }
    }

    private void Emit(int output, int variable, float factor, bool isScaled, int offset)
    {
        Span<float> destination = _outputs[output].AsSpan(offset, 12);
        ReadOnlySpan<float> source = _variables.AsSpan(variable * 12, 12);
        for (int k = 0; k < 12; k++)
        {
            destination[k] = isScaled ? source[k] * factor : source[k];
        }
    }

    // The polyphase filter bank of every full-bandwidth channel, in pairs that share a call and a history.
    private void Synthesize(Span<float> planar, int planarStride)
    {
        for (int pair = 0; pair < _histories.Length; pair++)
        {
            int channels = Math.Min(2, _outputs.Length - (2 * pair));
            for (int part = 0; part < 3; part++)
            {
                Array.Clear(_pairLines);
                for (int i = 0; i < channels; i++)
                {
                    float[] output = _outputs[(2 * pair) + i];
                    for (int band = 0; band < 32; band++)
                    {
                        output.AsSpan((part * 384) + (band * 12), 12).CopyTo(_pairLines.AsSpan((576 * i) + (band * 18), 12));
                    }
                }

                Polyphase.Granule(_histories[pair], _pairLines, 12, channels, _pairSamples, _lins);
                for (int i = 0; i < channels; i++)
                {
                    Span<float> destination = planar.Slice(((2 * pair) + i) * planarStride, 1152);
                    for (int t = 0; t < 384; t++)
                    {
                        destination[(part * 384) + t] = _pairSamples[(t * channels) + i];
                    }
                }
            }
        }
    }

    // The LFE channel (2.5.3.2.4): twelve samples a frame, requantized as Layer I and scaled by lf_scalefactor, at 1/96 of the sample rate, each held for 96 samples.
    private static void Lfe(Layer2Multichannel mc, ReadOnlySpan<int> codes, Span<float> destination)
    {
        if (mc.LfeAllocation == 0)
        {
            destination[..1152].Clear();
            return;
        }

        float scale = Layer12Dequantizer.Scale(mc.LfeAllocation + 1, mc.LfeScalefactor) * LfeGain;
        for (int gr = 0; gr < 12; gr++)
        {
            destination.Slice(gr * 96, 96).Fill(codes[gr] * scale);
        }
    }
}
