using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Dts.Decoding;

internal sealed class FrameDecoder : IFrameDecoder
{
    // Per output sample of each channel, besides the fixed DecodeWork.ForSample: the synthesis filter bank's 32 multiplications and as many additions a sample (C.3.6, 1,024 per slot
    // of 32), charged for the primary channels; 2 for the LFE channel's interpolation (512 taps for 64 or 128 samples, so 8 or 4); and per subband sample of each channel 4 for the
    // inverse quantization and prediction (the four taps of the predictor, C.3.3) and 1 for joint intensity and sum/difference decoding.
    internal const long SynthesisUnits = 32;
    internal const long LfeUnits = 8;
    internal const long SubbandSampleUnits = 5;

    // After a gap the output that depends on the lost frames is withheld: the filter bank's history of 15 vectors of 32 (480 samples), and the subband prediction, which converges within
    // it (docs/design/lossy-decoders.md, "Gaps, withheld output and priming").
    internal const int WithheldAfterGap = 480;

    private const int MaxBlocks = 128;
    private const int History = 4;

    // The specification states no PCM scale (C.3.6 leaves rScale open). The decoded values are those of FFmpeg's decoder times 2^15 times the square root of 2 (a correlation of
    // 0.9999999999996 and that exact ratio on FFmpeg's own encoder files, scripts/fixtures/crosscheck_dts_pcm.py), so full scale 1.0 is 2^15 * sqrt(2) of a decoded value.
    private const float OutputScale = 1f / 46340.95f;

    // The LFE channel's samples are in the units of a 24-bit PCM sample (5.6 rounds them to integers: full scale 2^23), which FFmpeg's decoder agrees with to the same bound; the
    // filter bank's gain makes the primary channels' unit 2^7 times the square root of 2 larger, as the cross-check shows.
    private const float LfeOutputScale = 1f / 8388608f;

    private static readonly float[] Adjustment = [1f, 1.125f, 1.25f, 1.4375f];

    private readonly DecodeContext _context;
    private readonly DecodeShared _shared;
    private readonly int _channels;
    private readonly SubbandSynthesis[] _synthesis;
    private readonly float[][] _subbands;
    private readonly float[][] _history;
    private readonly float[] _lfeHistory = new float[8];
    private readonly float[] _planar;
    private readonly float[] _slot = new float[MaxBlocks * 32];
    private bool _isFresh = true;
    private int _withheldLeft;
    private IPcmSink? _sink;

    public FrameDecoder(DecodeContext context, DecodeShared shared)
    {
        _context = context;
        _shared = shared;
        _channels = context.Setup.Channels;
        int primary = _channels - (shared.Positions[^1] == Position.Lfe ? 1 : 0);
        _synthesis = [.. Enumerable.Range(0, primary).Select(_ => new SubbandSynthesis())];
        _subbands = [.. Enumerable.Range(0, primary).Select(_ => new float[32 * MaxBlocks])];
        _history = [.. Enumerable.Range(0, primary).Select(_ => new float[32 * History])];
        _planar = new float[_channels * MaxBlocks * 32];
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];

    public int Decode(in CodedFrame frame)
    {
        SampleBuffers lent = _context.LentBuffers;
        if (lent.IsEmpty || lent.Channels != _channels || lent.Stride < LentLayout.HeaderInts)
        {
            throw new InvalidDataException("The frame was not walked into the lent buffers");
        }

        _context.CancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<int> header = lent.Channel(0)[..LentLayout.HeaderInts];
        int primary = header[0];
        int subframes = header[1];
        bool hasLfe = header[14] != 0;
        if (primary + (hasLfe ? 1 : 0) != _channels || subframes < 1 || lent.Stride < LentLayout.Stride(subframes))
        {
            throw new InvalidDataException("The frame's channels are not the stream's");
        }

        int blocks = 0;
        for (int sf = 0; sf < subframes; sf++)
        {
            blocks += lent.Channel(0)[LentLayout.Subframe(sf)] * 8;
        }

        if (blocks is < 1 or > MaxBlocks || blocks != frame.Samples / 32)
        {
            throw new InvalidDataException("The subframes do not hold the frame's samples");
        }

        int amode = header[8];
        if (amode > 9)
        {
            throw new InvalidDataException($"The channel arrangement {amode} is not decoded");
        }

        if (frame.FollowsGap && !_isFresh)
        {
            Reset();
            _withheldLeft = WithheldAfterGap;
        }

        _isFresh = false;
        int samples = blocks * 32;
        _context.AddWork(checked(((DecodeWork.ForSample(4) + SynthesisUnits) * samples * primary) + (hasLfe ? (DecodeWork.ForSample(4) + LfeUnits) * samples : 0) + (SubbandSampleUnits * blocks * 32 * primary)));
        if (!VectorTables.IsAvailable && NeedsVectorTables(lent, primary, subframes))
        {
            throw new InvalidDataException("The frame uses subband prediction or high-frequency vector quantization, whose tables (Annex D.10) are not available");
        }

        for (int channel = 0; channel < primary; channel++)
        {
            Reconstruct(lent, channel, subframes, header[10] != 0);
        }

        JointIntensity(lent, primary, subframes);
        SumDifference(header, primary, blocks);
        for (int channel = 0; channel < primary; channel++)
        {
            int active = lent.Channel(channel)[2];
            int source = lent.Channel(channel)[13];
            _synthesis[channel].Run(_subbands[channel], MaxBlocks, blocks, Math.Max(active, source), header[5] != 0, _planar.AsSpan(channel * MaxBlocks * 32, samples));
        }

        for (int channel = 0; channel < primary; channel++)
        {
            Span<float> plane = _planar.AsSpan(channel * MaxBlocks * 32, samples);
            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] *= OutputScale;
            }
        }

        if (hasLfe)
        {
            Lfe(lent.Channel(primary), header[9], subframes, _planar.AsSpan(primary * MaxBlocks * 32, samples));
        }

        return Write(frame, samples);
    }

    private void Reset()
    {
        foreach (SubbandSynthesis synthesis in _synthesis)
        {
            synthesis.Reset();
        }

        foreach (float[] history in _history)
        {
            Array.Clear(history);
        }

        Array.Clear(_lfeHistory);
    }

    private static bool NeedsVectorTables(SampleBuffers lent, int primary, int subframes)
    {
        for (int channel = 0; channel < primary; channel++)
        {
            ReadOnlySpan<int> plane = lent.Channel(channel);
            for (int sf = 0; sf < subframes; sf++)
            {
                for (int n = 0; n < 32; n++)
                {
                    int meta = LentLayout.MetaAt(sf, n);
                    if (plane[meta] == LentLayout.Vq || plane[meta + 1] != 0)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // The subband samples of one channel: the indexes times the step size of their quantizer, the scale factor of their part of the subframe and ADJ (5.6), then the prediction (C.3.3).
    // The vectors of high-frequency subbands come from Annex D.10.2 (not yet available).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Reconstruct(SampleBuffers lent, int channel, int subframes, bool hasHistory)
    {
        ReadOnlySpan<int> plane = lent.Channel(channel);
        float[] subbands = _subbands[channel];
        float[] history = _history[channel];
        if (!hasHistory)
        {
            Array.Clear(history);
        }

        Span<float> coefficients = stackalloc float[History];
        ReadOnlySpan<int> steps = lent.Channel(0)[12] == 31 ? SubframeTables.StepSizeLossless : SubframeTables.StepSizeLossy;
        int start = 0;
        for (int sf = 0; sf < subframes; sf++)
        {
            int ssc = plane[LentLayout.Subframe(sf)];
            int count = ssc * 8;
            for (int n = 0; n < 32; n++)
            {
                int meta = LentLayout.MetaAt(sf, n);
                int abits = plane[meta];
                Span<float> samples = subbands.AsSpan((n * MaxBlocks) + start, count);
                if (abits <= 0)
                {
                    samples.Clear();
                }
                else
                {
                    ReadOnlySpan<int> values = plane.Slice(LentLayout.SamplesAt(sf, n), count);
                    float step = (float)(steps[Math.Min(abits, 31)] / 4194304.0);
                    int transient = plane[meta + 3] == 0 ? ssc : plane[meta + 3];
                    float adjustment = Adjustment[plane[meta + 7] & 3];
                    float first = step * plane[meta + 4] * adjustment;
                    float second = step * plane[meta + 5] * adjustment;
                    for (int m = 0; m < count; m++)
                    {
                        samples[m] = values[m] * (m / 8 < transient ? first : second);
                    }
                }

                // The history of the subband: its last samples before this subframe, newest first.
                Span<float> past = history.AsSpan(n * History, History);
                if (abits > 0 && plane[meta + 1] != 0)
                {
                    VectorTables.Coefficients(plane[meta + 2], coefficients);
                    for (int m = 0; m < count; m++)
                    {
                        float prediction = 0f;
                        for (int k = 0; k < History; k++)
                        {
                            float previous = m - k - 1 >= 0 ? samples[m - k - 1] : past[k - m];
                            prediction += coefficients[k] * previous;
                        }

                        samples[m] += prediction;
                    }
                }

                for (int k = 0; k < History; k++)
                {
                    past[k] = count - 1 - k >= 0 ? samples[count - 1 - k] : past[k - count];
                }
            }

            start += count;
        }
    }

    // C.3.4: the subbands above a channel's own that it copies from its source channel, scaled.
    private void JointIntensity(SampleBuffers lent, int primary, int subframes)
    {
        for (int channel = 0; channel < primary; channel++)
        {
            ReadOnlySpan<int> plane = lent.Channel(channel);
            int source = plane[4] - 1;
            if (source < 0 || source >= channel)
            {
                continue;
            }

            int start = 0;
            for (int sf = 0; sf < subframes; sf++)
            {
                int count = plane[LentLayout.Subframe(sf)] * 8;
                for (int n = 0; n < 32; n++)
                {
                    int meta = LentLayout.MetaAt(sf, n);
                    if (plane[meta] != LentLayout.Joint)
                    {
                        continue;
                    }

                    float scale = BitConverter.Int32BitsToSingle(plane[meta + 6]);
                    for (int m = 0; m < count; m++)
                    {
                        _subbands[channel][(n * MaxBlocks) + start + m] = scale * _subbands[source][(n * MaxBlocks) + start + m];
                    }
                }

                start += count;
            }
        }
    }

    // C.3.5: the channels of the front pair and of the surround pair that are coded as sum and difference become left and right.
    private void SumDifference(ReadOnlySpan<int> header, int primary, int blocks)
    {
        int amode = header[8];
        (int front, int surround) = amode switch
        {
            2 or 6 or 8 => (0, amode == 8 ? 2 : -1),
            3 or 4 => (0, -1),
            5 or 7 => (1, -1),
            9 => (1, 3),
            _ => (-1, -1),
        };
        if (front >= 0 && (amode == 3 || header[6] != 0))
        {
            Mix(front, front + 1, primary, blocks);
        }

        if (surround >= 0 && header[7] != 0)
        {
            Mix(surround, surround + 1, primary, blocks);
        }
    }

    private void Mix(int first, int second, int primary, int blocks)
    {
        if (second >= primary)
        {
            return;
        }

        for (int n = 0; n < 32; n++)
        {
            for (int t = 0; t < blocks; t++)
            {
                int at = (n * MaxBlocks) + t;
                float left = _subbands[first][at];
                float right = _subbands[second][at];
                _subbands[first][at] = left + right;
                _subbands[second][at] = left - right;
            }
        }
    }

    // 5.6 and C.3.7: the decimated samples of each subframe times the step 0.035 and the scale factor, interpolated by the filter of the decimation factor, with the previous samples as history.
    private void Lfe(ReadOnlySpan<int> plane, int lff, int subframes, Span<float> output)
    {
        int factor = lff == 1 ? 128 : 64;
        ReadOnlySpan<double> filter = lff == 1 ? FilterTables.Lfe128 : FilterTables.Lfe64;
        int taps = 512 / factor;
        int written = 0;
        for (int sf = 0; sf < subframes; sf++)
        {
            int ssc = plane[LentLayout.Subframe(sf)];
            int count = 2 * lff * ssc;
            int scaleIndex = Math.Min(plane[LentLayout.Subframe(sf) + 3], SubframeTables.ScaleFactor7.Length - 4);
            float scale = (float)(SubframeTables.ScaleFactor7[scaleIndex] * 0.035) * LfeOutputScale;
            for (int d = 0; d < count; d++)
            {
                // The newest decimated sample goes to the front of the history.
                for (int i = taps - 1; i > 0; i--)
                {
                    _lfeHistory[i] = _lfeHistory[i - 1];
                }

                _lfeHistory[0] = plane[LentLayout.LfeAt(sf) + d] * scale;
                for (int k = 0; k < factor; k++)
                {
                    float acc = 0f;
                    for (int j = 0; j < taps; j++)
                    {
                        acc += _lfeHistory[j] * (float)filter[k + (j * factor)];
                    }

                    output[written++] = acc;
                }
            }
        }
    }

    private int Write(in CodedFrame frame, int samples)
    {
        int withheld = Math.Min(_withheldLeft, samples);
        if (withheld > 0)
        {
            _context.Withhold(frame.FirstSample, withheld, WithholdReason.AfterGap);
            _withheldLeft -= withheld;
        }

        int count = samples - withheld;
        if (count == 0)
        {
            return 0;
        }

        _sink ??= _context.DeclareOutput(new PcmFormat(_shared.SampleRate, _channels, 32, SampleFormat.Float32)
        {
            ChannelLayout = Layouts.Speakers(_shared.Positions),
        });
        int stride = MaxBlocks * 32;
        if (withheld > 0)
        {
            for (int channel = 0; channel < _channels; channel++)
            {
                _planar.AsSpan((channel * stride) + withheld, count).CopyTo(_planar.AsSpan(channel * stride, count));
            }
        }

        _sink.Write(new PcmBlock(frame.FirstSample + withheld, count, _channels, stride, _planar));
        return count;
    }
}
