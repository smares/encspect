using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Ac3.Decoding;

// The decoding state of one AC-3 stream: the overlap of each channel's last block, the dither generator and the span of output a gap withholds.
internal sealed class FrameDecoder : IFrameDecoder
{
    private const int BlockSamples = 256;

    // The units a sample of a channel costs on top of the floor of the budget (22): the dequantization of 2 a coefficient, the inverse transform of 2 per level of its 512 points
    // for 256 samples (18 / 2 per sample after the pairing of its halves, which is about 9 plus its twiddles), and the window and overlap of 2, measured with DecoderWorkTests
    // (docs/design/decoders.md, "As built: the AC-3 decoder's work weights").
    private const int ExtraUnits = 22;

    private readonly DecodeContext _context;
    private readonly int _channels;
    private readonly int _acmod;
    private readonly bool _hasLfe;
    private readonly int _fullChannels;
    private readonly int _rate;
    private readonly InverseTransform _transform = new();
    private readonly float[][] _delay;
    private readonly float[][] _spectrum;
    private readonly float[] _coupling = new float[256];
    private readonly float[] _couplingDither = new float[256];
    private readonly float[] _x = new float[512];
    private float[] _planar = [];
    private int _stride;
    private IPcmSink? _sink;
    private uint _noise = NoiseSeed;
    private long _withheldUntil;
    private bool _lostBefore;

    // The seed of the dither generator, at the start of the stream and after each gap (docs/design/lossy-decoders.md, "Random noise").
    private const uint NoiseSeed = 0x2545F491;

    public FrameDecoder(DecodeContext context, SyncFrame header)
    {
        _context = context;
        _acmod = header.Acmod;
        _rate = header.SampleRate;
        _hasLfe = header.Lfeon;
        _fullChannels = DecodeFeed.ChannelCount(header) - (_hasLfe ? 1 : 0);
        _channels = DecodeFeed.ChannelCount(header);
        _delay = [.. Enumerable.Range(0, _channels).Select(_ => new float[BlockSamples])];
        _spectrum = [.. Enumerable.Range(0, _channels).Select(_ => new float[256])];
    }

    private void Reset()
    {
        foreach (float[] delay in _delay)
        {
            Array.Clear(delay);
        }

        _noise = NoiseSeed;
    }

    public int Decode(in CodedFrame frame)
    {
        SampleBuffers lent = _context.LentBuffers;
        if (!frame.IsWalked || lent.IsEmpty)
        {
            _context.AddFinding(Severity.Info, "decode.frame-too-large", $"The frame at 0x{frame.Bytes.FileStart:X} could hold more values than the host lends buffers for, so it is not decoded.");
            _lostBefore = true;
            return 0;
        }

        _context.CancellationToken.ThrowIfCancellationRequested();
        var r = new IntReader(lent.Narrow);
        int acmod = r.Next();
        bool lfe = r.Next() != 0;
        int blocks = r.Next();
        if (acmod != _acmod || lfe != _hasLfe || blocks * BlockSamples != frame.Samples)
        {
            throw new InvalidDataException("The frame's configuration differs from the stream's");
        }

        if (frame.FollowsGap || _lostBefore)
        {
            _lostBefore = false;
            Reset();
            _context.Withhold(frame.FirstSample, BlockSamples, WithholdReason.AfterGap);
            _withheldUntil = Math.Max(_withheldUntil, frame.FirstSample + BlockSamples);
        }

        int samples = frame.Samples;
        EnsureBuffers(samples);
        _context.AddWork(DecodeWork.ForSample(4) * samples * _channels);
        _context.AddWork((long)ExtraUnits * samples * _channels);
        for (int block = 0; block < blocks; block++)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            DecodeBlock(ref r, block);
        }

        long first = Math.Max(frame.FirstSample, _withheldUntil);
        int skip = (int)Math.Min(samples, first - frame.FirstSample);
        int count = samples - skip;
        if (count <= 0)
        {
            return 0;
        }

        _sink ??= _context.DeclareOutput(new PcmFormat(_rate, _channels, 32, SampleFormat.Float32) { ChannelLayout = Program.LayoutOf(_acmod, _hasLfe) });
        _sink.Write(new PcmBlock(first, count, _channels, _stride, _planar.AsSpan(skip)));
        return count;
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];

    private void EnsureBuffers(int samples)
    {
        if (samples <= _stride)
        {
            return;
        }

        _stride = samples;
        _planar = new float[_channels * _stride];
    }

    // One audio block: the coefficients of every channel from the records, coupling and rematrixing, then each channel's inverse transform, window and overlap.
    private void DecodeBlock(ref IntReader r, int block)
    {
        int blksw = r.Next();
        int dither = r.Next();
        bool coupled = r.Next() != 0;
        int cplbegf = 0;
        int subbands = 0;
        int chincpl = 0;
        int combined = 0;
        int phsflg = 0;
        int bands2 = 0;
        Span<int> coordinates = stackalloc int[5 * 18];
        if (coupled)
        {
            cplbegf = r.Next();
            int cplendf = r.Next();
            chincpl = r.Next();
            combined = r.Next();
            phsflg = r.Next();
            subbands = 3 + cplendf - cplbegf;
            int bands = subbands;
            for (int sub = 1; sub < subbands; sub++)
            {
                bands -= (combined >> sub) & 1;
            }

            for (int ch = 0; ch < _fullChannels; ch++)
            {
                if (((chincpl >> ch) & 1) != 0)
                {
                    for (int band = 0; band < bands; band++)
                    {
                        coordinates[(ch * 18) + band] = r.Next();
                    }
                }
            }

            bands2 = bands;
        }

        int rematBands = 0;
        int rematFlags = 0;
        if (_acmod == 2)
        {
            rematBands = r.Next();
            rematFlags = r.Next();
        }

        bool gotCoupling = false;
        Span<int> ends = stackalloc int[5];
        for (int ch = 0; ch < _fullChannels; ch++)
        {
            ends[ch] = Channel(ref r, _spectrum[ch], 0, ((dither >> ch) & 1) != 0);
            if (coupled && ((chincpl >> ch) & 1) != 0 && !gotCoupling)
            {
                CouplingChannel(ref r, 37 + (12 * cplbegf));
                gotCoupling = true;
            }
        }

        if (_hasLfe)
        {
            Channel(ref r, _spectrum[_fullChannels], 0, false);
        }

        if (_acmod == 2 && rematBands > 0)
        {
            Rematrix(_spectrum[0], _spectrum[1], rematBands, rematFlags, coupled ? cplbegf : -1, Math.Min(ends[0], ends[1]));
        }

        if (coupled)
        {
            for (int ch = 0; ch < _fullChannels; ch++)
            {
                if (((chincpl >> ch) & 1) != 0)
                {
                    ExpandCoupling(_spectrum[ch], _coupling, _couplingDither, ref _noise, coordinates.Slice(ch * 18, 18), cplbegf, subbands, combined, phsflg, bands2, ch == 1 && _acmod == 2, ((dither >> ch) & 1) != 0);
                }
            }
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            bool isShort = ch < _fullChannels && ((blksw >> ch) & 1) != 0;
            Span<float> x = _x;
            float[] spectrum = _spectrum[ch];
            if (IsSilent(spectrum))
            {
                x.Clear();
            }
            else if (isShort)
            {
                _transform.Short(spectrum, x);
            }
            else
            {
                _transform.Long(spectrum, x);
            }

            Overlap(x, _delay[ch], _planar.AsSpan((ch * _stride) + (block * BlockSamples), BlockSamples));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool IsSilent(float[] spectrum)
    {
        foreach (float value in spectrum)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    // pcm[n] = 2 * (x[n] + delay[n]); delay[n] = x[256 + n] (7.9.4.1, step 6).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Overlap(ReadOnlySpan<float> x, float[] delay, Span<float> pcm)
    {
        for (int n = 0; n < BlockSamples; n++)
        {
            pcm[n] = 2.0f * (x[n] + delay[n]);
            delay[n] = x[BlockSamples + n];
        }
    }

    // The coefficients of a channel's own bins, dequantized (7.3), with the dither of its zero-bit mantissas where the channel's flag is set.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int Channel(ref IntReader r, float[] spectrum, int start, bool dither)
    {
        int count = r.Next();
        ReadOnlySpan<int> bins = r.Take(count);
        Array.Clear(spectrum);
        for (int i = 0; i < count; i++)
        {
            int packed = bins[i];
            int bap = (packed >> 5) & 15;
            if (bap == 0)
            {
                spectrum[start + i] = dither ? Noise(ref _noise) * Quantizers.Shift[packed & 31] : 0;
            }
            else
            {
                spectrum[start + i] = Dequantize(packed);
            }
        }

        return count;
    }

    // The coupling channel's bins from the first bin it codes: the values of the coded ones, and for each zero-bit one the shift of its exponent, since every coupled channel
    // dithers those with noise of its own once it has been scaled by its coordinates (7.4.3, 7.3.4).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void CouplingChannel(ref IntReader r, int start)
    {
        int count = r.Next();
        ReadOnlySpan<int> bins = r.Take(count);
        Array.Clear(_coupling);
        Array.Clear(_couplingDither);
        for (int i = 0; i < count; i++)
        {
            int packed = bins[i];
            if (((packed >> 5) & 15) == 0)
            {
                _couplingDither[start + i] = Quantizers.Shift[packed & 31];
            }
            else
            {
                _coupling[start + i] = Dequantize(packed);
            }
        }
    }

    // The value of a coded mantissa shifted by its exponent: the quantizer's value for bap 1 to 5 (Tables 7.19 to 7.23), the two's complement fraction for bap 6 to 15.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static float Dequantize(int packed)
    {
        int exponent = packed & 31;
        int bap = (packed >> 5) & 15;
        int code = packed >> 9;
        if (bap <= 5)
        {
            return Quantizers.Symmetric[bap][code] * Quantizers.Shift[exponent];
        }

        return code * Quantizers.Shift[Quantizers.AsymmetricBits[bap] - 1 + exponent];
    }

    // A dither value: uniform between -0.707 and 0.707 (7.3.4, "the optimum scaling"), from a linear congruential generator whose top 24 bits are the integer part.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static float Noise(ref uint state)
    {
        state = unchecked((state * 1664525u) + 1013904223u);
        return ((int)state >> 8) * (1.0f / 8388608.0f) * 0.707f;
    }

    // 7.5.4: the bands the flags name hold sum and difference, from the table of the coupling state (Tables 7.25 to 7.28).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Rematrix(float[] left, float[] right, int bands, int flags, int cplbegf, int limit)
    {
        for (int band = 0; band < bands; band++)
        {
            if (((flags >> (bands - 1 - band)) & 1) == 0)
            {
                continue;
            }

            int low = band switch { 0 => 13, 1 => 25, 2 => 37, _ => 61 };
            int high = band switch { 0 => 24, 1 => 36, 2 => cplbegf is 1 or 2 ? 36 + (cplbegf * 12) : 60, _ => cplbegf > 2 ? 36 + (cplbegf * 12) : 252 };
            // Without coupling the channels may have different bandwidths, and rematrixing is only applied up to the lower one (A/52 7.5.4).
            for (int bin = low; bin <= Math.Min(high, limit - 1); bin++)
            {
                float l = left[bin];
                float rr = right[bin];
                left[bin] = l + rr;
                right[bin] = l - rr;
            }
        }
    }

    // 7.4.3: a coupled channel's bins above its own bandwidth are the coupling channel's times the channel's coordinate of the band, times 8; where the coupling channel has no
    // bits for a bin the channel's dither stands in for it, scaled the same way. With phase flags the right channel is negated in the bands that set them.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void ExpandCoupling(float[] spectrum, float[] coupling, float[] couplingDither, ref uint noise, ReadOnlySpan<int> coordinates, int cplbegf, int subbands, int combined, int phsflg, int bands, bool isRight, bool dither)
    {
        int band = -1;
        for (int sub = 0; sub < subbands; sub++)
        {
            if (sub == 0 || ((combined >> sub) & 1) == 0)
            {
                band++;
            }

            int packed = coordinates[band];
            int exponent = packed & 15;
            int mantissa = (packed >> 4) & 15;
            int master = (packed >> 8) & 3;
            float coordinate = exponent == 15 ? mantissa * (1.0f / 16.0f) : (mantissa + 16) * (1.0f / 32.0f);
            coordinate *= Quantizers.Shift[exponent + (3 * master)];
            float scale = coordinate * 8.0f;
            bool negate = isRight && ((phsflg >> (bands - 1 - band)) & 1) != 0;
            int first = 37 + (12 * (cplbegf + sub));
            for (int bin = first; bin < first + 12; bin++)
            {
                float value;
                float shift = couplingDither[bin];
                if (shift != 0)
                {
                    value = dither ? Noise(ref noise) * shift * scale : 0;
                }
                else
                {
                    value = coupling[bin] * scale;
                }

                spectrum[bin] = negate ? -value : value;
            }
        }
    }
}
