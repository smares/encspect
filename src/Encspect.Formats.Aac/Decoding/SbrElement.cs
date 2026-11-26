using Encspect.Core;

namespace Encspect.Formats.Aac.Decoding;

// The SBR decoding of one channel element (a single channel element or a channel pair): the header and its frequency tables, the envelope and noise floor values (as coded: start values and
// differences across frequency or time, dequantized with the balance of a coupled pair), and the reconstruction of each channel's high band (SbrChannel). Without a usable payload in a
// frame the element only upsamples: the low band goes through the analysis and the synthesis alone, which libxaac does for a frame without SBR data too.
internal sealed class SbrElement
{
    private const int FrameSamples = 1024;
    private const float InputScale = 32768f;

    // The synthesis bank's gain for this analysis bank (measured by SbrQmfTests: the chain's gain on a sinusoid in a low band).
    internal const float OutputScale = 1f / (32768f * 64f);

    private readonly int _channels;
    private readonly int _rate;
    private readonly SbrChannel[] _channel;
    private readonly float[][] _re;
    private readonly float[][] _im;
    private readonly float[][] _energy = [new float[5 * 64], new float[5 * 64]];
    private readonly float[][] _noise = [new float[2 * 8], new float[2 * 8]];
    private readonly int[] _header = new int[16];
    private readonly float[] _chirp = new float[8];
    private readonly float[] _scaled = new float[FrameSamples];
    private SbrLayout? _layout;
    private bool _hasHeader;
    private int _previousKx = 32;

    public SbrElement(int channels, int rate)
    {
        _channels = channels;
        _rate = rate;
        _channel = new SbrChannel[channels];
        _re = new float[channels][];
        _im = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            _channel[c] = new SbrChannel();
            _re[c] = new float[SbrQmf.Slots * 64];
            _im[c] = new float[SbrQmf.Slots * 64];
        }
    }

    // The QMF domain of the frame (32 slots of 64 bands) of a channel, before the synthesis: parametric stereo works on it.
    public ReadOnlySpan<float> Real(int channel) => _re[channel];

    public ReadOnlySpan<float> Imaginary(int channel) => _im[channel];

    public SbrChannel Channel(int channel) => _channel[channel];

    public bool IsActive { get; private set; }

    public int Kx => _layout?.Kx ?? 32;

    public int K2 => _layout?.K2 ?? 64;

    // Resets what crosses frames, at a loss.
    public void Reset()
    {
        foreach (SbrChannel channel in _channel)
        {
            channel.Reset();
        }

        _hasHeader = false;
        _layout = null;
        _previousKx = 32;
    }

    // One frame: the core's samples of each channel (the AAC LC decoder's output, scaled to full scale 1, the channels in planes of stride samples) and the frame's SBR slot, or none. Fills the QMF domain; Synthesize turns it into samples.
    public void Process(float[] core, int firstChannel, int stride, ReadOnlySpan<int> slot)
    {
        bool valid = !slot.IsEmpty && slot[SbrRecord.Valid] == 1 && (slot[SbrRecord.Pair] == 1) == (_channels == 2);
        bool reset = false;
        if (valid)
        {
            reset = UpdateHeader(slot);
            valid = _layout is not null;
        }

        for (int c = 0; c < _channels; c++)
        {
            for (int i = 0; i < FrameSamples; i++)
            {
                _scaled[i] = core[((firstChannel + c) * stride) + i] * InputScale;
            }

            if (reset)
            {
                _channel[c].ResetState();
            }

            _channel[c].Analyze(_scaled);
        }

        IsActive = valid;
        if (valid)
        {
            DecodeValues(slot);
            for (int c = 0; c < _channels; c++)
            {
                ReadOnlySpan<int> frame = slot.Slice(SbrRecord.FirstChannel + (c * SbrRecord.ChannelStride), SbrRecord.ChannelStride);
                _channel[c].Generate(_layout!, frame, _chirp);
                _channel[c].Adjust(_layout!, frame, slot, _energy[c], _noise[c], reset);
                Assemble(c, frame[SbrRecord.Borders] * 2);
            }

            _previousKx = _layout!.Kx;
        }
        else
        {
            for (int c = 0; c < _channels; c++)
            {
                Assemble(c, 0);
            }
        }

        foreach (SbrChannel channel in _channel)
        {
            channel.Carry();
        }
    }

    // The columns of the frame, as the synthesis takes them: the low band below the crossover, the generated and adjusted band above it (the crossover of the frame before, for the columns
    // before this frame's first envelope).
    private void Assemble(int channel, int stop)
    {
        SbrChannel source = _channel[channel];
        int kx = IsActive ? _layout!.Kx : 32;
        for (int l = 0; l < SbrQmf.Slots; l++)
        {
            int cross = l < stop ? _previousKx : kx;
            int row = (l + SbrChannel.Offset) * 64;
            int target = l * 64;
            for (int k = 0; k < 64; k++)
            {
                if (k < cross)
                {
                    _re[channel][target + k] = source.LowRe[row + k];
                    _im[channel][target + k] = source.LowIm[row + k];
                }
                else if (IsActive || l < stop)
                {
                    _re[channel][target + k] = source.HighRe[row + k];
                    _im[channel][target + k] = source.HighIm[row + k];
                }
                else
                {
                    _re[channel][target + k] = 0;
                    _im[channel][target + k] = 0;
                }
            }
        }
    }

    // The 64-band synthesis of a channel's QMF domain (or of a domain parametric stereo made) into 2,048 samples.
    public void Synthesize(int channel, ReadOnlySpan<float> re, ReadOnlySpan<float> im, Span<float> output)
    {
        Span<float> block = stackalloc float[64];
        for (int l = 0; l < SbrQmf.Slots; l++)
        {
            _channel[channel].Synthesize(re.Slice(l * 64, 64), im.Slice(l * 64, 64), block, 64);
            for (int k = 0; k < 64; k++)
            {
                output[(l * 64) + k] = block[k] * OutputScale;
            }
        }
    }

    // The header's fields of the slot; true where the frequency tables changed (the standard's reset).
    private bool UpdateHeader(ReadOnlySpan<int> slot)
    {
        bool same = _hasHeader;
        for (int i = SbrRecord.AmpRes; i <= SbrRecord.SmoothingMode && same; i++)
        {
            same &= _header[i] == slot[i];
        }

        bool tablesSame = same || (_hasHeader && _header[SbrRecord.StartFrequency] == slot[SbrRecord.StartFrequency] && _header[SbrRecord.StopFrequency] == slot[SbrRecord.StopFrequency]
            && _header[SbrRecord.XoverBand] == slot[SbrRecord.XoverBand] && _header[SbrRecord.FrequencyScale] == slot[SbrRecord.FrequencyScale]
            && _header[SbrRecord.AlterScale] == slot[SbrRecord.AlterScale] && _header[SbrRecord.NoiseBands] == slot[SbrRecord.NoiseBands]);
        for (int i = SbrRecord.AmpRes; i <= SbrRecord.SmoothingMode; i++)
        {
            _header[i] = slot[i];
        }

        if (tablesSame && _layout is not null)
        {
            return false;
        }

        var header = new SbrHeader
        {
            AmpRes = slot[SbrRecord.AmpRes],
            StartFrequency = slot[SbrRecord.StartFrequency],
            StopFrequency = slot[SbrRecord.StopFrequency],
            XoverBand = slot[SbrRecord.XoverBand],
            FrequencyScale = slot[SbrRecord.FrequencyScale],
            AlterScale = slot[SbrRecord.AlterScale],
            NoiseBands = slot[SbrRecord.NoiseBands],
        };
        (SbrTables? tables, _) = Sbr.Tables(header, _rate);
        _hasHeader = true;
        _layout = tables is null ? null : new SbrLayout(tables, _rate);
        return true;
    }

    // The envelope and noise floor values of each channel: the sums of what is coded, across frequency from the start value or across time from the frame before, and then their energies
    // (ISO/IEC 14496-3 4.6.18.3.5 and 4.6.18.3.6; ixheaacd_process_del_cod_env_data, ixheaacd_calc_noise_floor and ixheaacd_sbr_env_dequant_coup).
    private void DecodeValues(ReadOnlySpan<int> slot)
    {
        SbrLayout layout = _layout!;
        bool coupled = slot[SbrRecord.Coupling] != 0;
        Span<int> level = stackalloc int[5 * 64];
        Span<int> noise = stackalloc int[2 * 8];
        Span<int> second = stackalloc int[5 * 64];
        Span<int> secondNoise = stackalloc int[2 * 8];
        for (int c = 0; c < _channels; c++)
        {
            ReadOnlySpan<int> frame = slot.Slice(SbrRecord.FirstChannel + (c * SbrRecord.ChannelStride), SbrRecord.ChannelStride);
            SbrChannel state = _channel[c];

            // A coupled pair's second channel codes the balance in steps of two (libxaac's env_data_tbl_comp_factor).
            int step = coupled && c == 1 ? 2 : 1;
            Span<int> values = c == 0 ? level : second;
            Span<int> noises = c == 0 ? noise : secondNoise;
            int envelopes = frame[SbrRecord.EnvelopeCount];
            int[] previous = state.PreviousEnvelope;
            int limit = 70 >> (frame[SbrRecord.ChannelAmpRes] & 1);
            int at = 0;
            for (int e = 0; e < envelopes; e++)
            {
                bool high = frame[SbrRecord.FrequencyResolution + e] != 0;
                int count = high ? layout.High : layout.Low;
                ReadOnlySpan<int> coded = frame.Slice(SbrRecord.Envelope + (e * SbrRecord.EnvelopeStride), SbrRecord.EnvelopeStride);
                bool time = frame[SbrRecord.EnvelopeTime + e] != 0;
                int sum = 0;
                for (int k = 0; k < count; k++)
                {
                    int value;
                    if (time)
                    {
                        value = previous[high ? k : LowToHigh(layout, k)] + (coded[k] * step);
                    }
                    else
                    {
                        value = k == 0 ? coded[0] * step : sum + (coded[k] * step);
                        sum = value;
                    }

                    values[at + k] = value;
                }

                // The frame's later envelopes continue from this one, on the high resolution bands.
                for (int k = 0; k < count; k++)
                {
                    if (high)
                    {
                        previous[k] = values[at + k];
                    }
                    else
                    {
                        int first = LowToHigh(layout, k);
                        int span = k == 0 && (layout.High & 1) != 0 ? 1 : 2;
                        for (int s = 0; s < span; s++)
                        {
                            previous[first + s] = values[at + k];
                        }
                    }
                }

                at += count;
            }

            for (int i = 0; i < at; i++)
            {
                values[i] = Math.Clamp(values[i], 0, limit);
            }

            for (int i = 0; i < layout.High; i++)
            {
                previous[i] = Math.Clamp(previous[i], 0, limit);
            }

            int noiseCount = frame[SbrRecord.NoiseCount];
            int bands = layout.Noise;
            for (int n = 0; n < noiseCount; n++)
            {
                ReadOnlySpan<int> coded = frame.Slice(SbrRecord.Noise + (n * SbrRecord.NoiseStride), SbrRecord.NoiseStride);
                bool time = frame[SbrRecord.NoiseTime + n] != 0;
                int sum = 0;
                for (int k = 0; k < bands; k++)
                {
                    int value;
                    if (time)
                    {
                        value = (n == 0 ? state.PreviousNoise[k] : noises[((n - 1) * 8) + k]) + (coded[k] * step);
                    }
                    else
                    {
                        value = k == 0 ? coded[0] * step : sum + (coded[k] * step);
                        sum = value;
                    }

                    noises[(n * 8) + k] = value;
                }
            }

            for (int n = 0; n < noiseCount; n++)
            {
                for (int k = 0; k < bands; k++)
                {
                    noises[(n * 8) + k] = Math.Clamp(noises[(n * 8) + k], 0, 35);
                }
            }

            for (int k = 0; k < bands; k++)
            {
                state.PreviousNoise[k] = noises[((noiseCount - 1) * 8) + k];
                state.PreviousInverseFilter[k] = frame[SbrRecord.InverseFilterMode + k];
            }
        }

        int[] counts = new int[2];
        for (int c = 0; c < _channels; c++)
        {
            ReadOnlySpan<int> frame = slot.Slice(SbrRecord.FirstChannel + (c * SbrRecord.ChannelStride), SbrRecord.ChannelStride);
            int total = 0;
            for (int e = 0; e < frame[SbrRecord.EnvelopeCount]; e++)
            {
                total += frame[SbrRecord.FrequencyResolution + e] != 0 ? layout.High : layout.Low;
            }

            counts[c] = total;
        }

        ReadOnlySpan<int> first0 = slot.Slice(SbrRecord.FirstChannel, SbrRecord.ChannelStride);
        int ampRes = first0[SbrRecord.ChannelAmpRes] & 1;
        double a = ampRes == 0 ? 0.5 : 1.0;
        if (!coupled)
        {
            for (int c = 0; c < _channels; c++)
            {
                ReadOnlySpan<int> frame = slot.Slice(SbrRecord.FirstChannel + (c * SbrRecord.ChannelStride), SbrRecord.ChannelStride);
                double ca = (frame[SbrRecord.ChannelAmpRes] & 1) == 0 ? 0.5 : 1.0;
                Span<int> values = c == 0 ? level : second;
                Span<int> noises = c == 0 ? noise : secondNoise;
                for (int i = 0; i < counts[c]; i++)
                {
                    _energy[c][i] = (float)(64 * DetMath.Pow(2.0, values[i] * ca));
                }

                for (int n = 0; n < frame[SbrRecord.NoiseCount]; n++)
                {
                    for (int k = 0; k < layout.Noise; k++)
                    {
                        _noise[c][(n * layout.Noise) + k] = (float)DetMath.PowerOfTwo(6 - noises[(n * 8) + k]);
                    }
                }
            }
        }
        else
        {
            double pan = ampRes == 0 ? 24.0 : 12.0;
            for (int i = 0; i < counts[0]; i++)
            {
                double tl = level[i];
                double tr = second[i];
                _energy[0][i] = (float)(64 * (DetMath.Pow(2.0, (tl * a) + 1) / (1 + DetMath.Pow(2.0, (pan - tr) * a))));
                _energy[1][i] = (float)(64 * (DetMath.Pow(2.0, (tl * a) + 1) / (1 + DetMath.Pow(2.0, (tr - pan) * a))));
            }

            for (int n = 0; n < first0[SbrRecord.NoiseCount]; n++)
            {
                for (int k = 0; k < layout.Noise; k++)
                {
                    double tl = noise[(n * 8) + k];
                    double tr = secondNoise[(n * 8) + k];
                    _noise[0][(n * layout.Noise) + k] = (float)(DetMath.PowerOfTwo(6 - (int)tl + 1) / (1 + DetMath.PowerOfTwo(12 - (int)tr)));
                    _noise[1][(n * layout.Noise) + k] = (float)(DetMath.PowerOfTwo(6 - (int)tl + 1) / (1 + DetMath.PowerOfTwo((int)tr - 12)));
                }
            }
        }
    }

    // The high resolution band a low resolution band starts at.
    private static int LowToHigh(SbrLayout layout, int band) => (layout.High & 1) != 0 ? (band == 0 ? 0 : (2 * band) - 1) : 2 * band;
}
