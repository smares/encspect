using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Ac3.Decoding;

// The decoding state of one E-AC-3 independent substream: the overlap of each channel's last block, the two noise generators (the dither of zero-bit mantissas and spectral
// extension's blend), the adaptive hybrid transform's six blocks of coefficients per channel, and the span of output a gap withholds.
internal sealed class Eac3FrameDecoder : IFrameDecoder
{
    private const int BlockSamples = 256;
    private const int Cpl = 5;
    private const int Lfe = 6;

    // The units a sample of a channel costs on top of the floor of the budget, measured with DecoderWorkTests (docs/design/decoders.md, "As built: the E-AC-3 decoder's work weights").
    private const int ExtraUnits = 22;

    // The seeds of the generators, at the start of the stream and after each gap (docs/design/lossy-decoders.md, "Random noise").
    private const uint NoiseSeed = 0x2545F491;
    private const uint SpxSeed = 0x6C8E9CF5;

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
    private readonly int[] _attenuation = new int[5];
    private readonly float[][][] _aht = [.. Enumerable.Range(0, 7).Select(_ => new float[][] { new float[256], new float[256], new float[256], new float[256], new float[256], new float[256] })];
    private readonly float[][] _ahtShift = [.. Enumerable.Range(0, 7).Select(_ => new float[256])];
    private float[] _planar = [];
    private int _stride;
    private IPcmSink? _sink;
    private uint _noise = NoiseSeed;
    private uint _spxNoise = SpxSeed;
    private long _withheldUntil;
    private bool _lostBefore;

    public Eac3FrameDecoder(DecodeContext context, SyncFrame header)
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
        _spxNoise = SpxSeed;
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
        try
        {
            for (int block = 0; block < blocks; block++)
            {
                _context.CancellationToken.ThrowIfCancellationRequested();
                DecodeBlock(ref r, block);
            }
        }
        catch (InvalidDataException)
        {
            // The frame is not decoded: the blocks it left half done must not be overlapped into the next one.
            _lostBefore = true;
            throw;
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

    // One audio block: the coefficients of every channel from the records, coupling, rematrixing and spectral extension, then each channel's inverse transform, window and overlap.
    private void DecodeBlock(ref IntReader r, int block)
    {
        if (block == 0)
        {
            for (int ch = 0; ch < _fullChannels; ch++)
            {
                _attenuation[ch] = r.Next() - 1;
            }
        }

        int blksw = r.Next();
        int dither = r.Next();

        bool spx = r.Next() != 0;
        int spxChannels = 0;
        int spxStart = 0;
        int spxBegin = 0;
        int spxEnd = 0;
        int spxBands = 0;
        Span<int> spxSizes = stackalloc int[17];
        Span<int> spxBlend = stackalloc int[5];
        Span<int> spxMaster = stackalloc int[5];
        Span<int> spxCoordinates = stackalloc int[5 * 17];
        if (spx)
        {
            spxChannels = r.Next();
            spxStart = r.Next();
            spxBegin = r.Next();
            spxEnd = r.Next();
            int joined = r.Next();
            spxBands = 1;
            spxSizes[0] = 12;
            for (int sub = spxBegin + 1; sub < spxEnd; sub++)
            {
                if (((joined >> sub) & 1) == 0)
                {
                    spxSizes[spxBands++] = 12;
                }
                else
                {
                    spxSizes[spxBands - 1] += 12;
                }
            }

            for (int ch = 0; ch < _fullChannels; ch++)
            {
                if (((spxChannels >> ch) & 1) != 0)
                {
                    spxBlend[ch] = r.Next();
                    spxMaster[ch] = r.Next();
                    for (int band = 0; band < spxBands; band++)
                    {
                        spxCoordinates[(ch * 17) + band] = r.Next();
                    }
                }
            }
        }

        int mode = r.Next();
        int cplbegf = 0;
        int subbands = 0;
        int chincpl = 0;
        int combined = 0;
        int phsflg = 0;
        int bands2 = 0;
        int cplStart = 0;
        Span<int> coordinates = stackalloc int[5 * 18];
        if (mode == 1)
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
            cplStart = 37 + (12 * cplbegf);
        }
        else if (mode == 2)
        {
            throw new InvalidDataException("Enhanced coupling is not decoded");
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
            ends[ch] = Channel(ref r, ch, _spectrum[ch], 0, ((dither >> ch) & 1) != 0, block, false);
            if (mode != 0 && ((chincpl >> ch) & 1) != 0 && !gotCoupling)
            {
                Channel(ref r, Cpl, _coupling, cplStart, false, block, true);
                gotCoupling = true;
            }
        }

        if (_hasLfe)
        {
            Channel(ref r, Lfe, _spectrum[_fullChannels], 0, false, block, false);
        }

        if (_acmod == 2 && rematBands > 0)
        {
            FrameDecoder.Rematrix(_spectrum[0], _spectrum[1], rematBands, rematFlags, mode == 1 ? cplbegf : -1, Math.Min(ends[0], ends[1]));
        }

        if (mode == 1)
        {
            for (int ch = 0; ch < _fullChannels; ch++)
            {
                if (((chincpl >> ch) & 1) != 0)
                {
                    FrameDecoder.ExpandCoupling(_spectrum[ch], _coupling, _couplingDither, ref _noise, coordinates.Slice(ch * 18, 18), cplbegf, subbands, combined, phsflg, bands2,
                        ch == 1 && _acmod == 2, ((dither >> ch) & 1) != 0);
                }
            }
        }

        if (spx)
        {
            for (int ch = 0; ch < _fullChannels; ch++)
            {
                if (((spxChannels >> ch) & 1) != 0)
                {
                    SpectralExtension(_spectrum[ch], 25 + (12 * spxStart), 25 + (12 * spxBegin), 25 + (12 * spxEnd), spxSizes[..spxBands], spxBlend[ch], spxMaster[ch],
                        spxCoordinates.Slice(ch * 17, 17), _attenuation[ch]);
                }
            }
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            bool isShort = ch < _fullChannels && ((blksw >> ch) & 1) != 0;
            Span<float> x = _x;
            float[] spectrum = _spectrum[ch];
            if (FrameDecoder.IsSilent(spectrum))
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

            FrameDecoder.Overlap(x, _delay[ch], _planar.AsSpan((ch * _stride) + (block * BlockSamples), BlockSamples));
        }
    }

    // One channel's coefficients (or the coupling channel's, in _coupling with the shifts of its zero-bit bins in _couplingDither) from its record: coded in this block (kind 0), the
    // adaptive hybrid transform's vectors and values with the inverse DCT across the six blocks (kind 1, in block 0), or taken from them (kind 2, in blocks 1 to 5). Returns the
    // number of bins.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int Channel(ref IntReader r, int slot, float[] target, int start, bool dither, int block, bool isCoupling)
    {
        int kind = r.Next();
        int count = r.Next();
        Array.Clear(target);
        if (isCoupling)
        {
            Array.Clear(_couplingDither);
        }

        if (kind == 0)
        {
            ReadOnlySpan<int> bins = r.Take(count);
            for (int i = 0; i < count; i++)
            {
                int packed = bins[i];
                int bap = (packed >> 5) & 15;
                if (isCoupling)
                {
                    if (bap == 0)
                    {
                        _couplingDither[start + i] = Quantizers.Shift[packed & 31];
                    }
                    else
                    {
                        target[start + i] = FrameDecoder.Dequantize(packed);
                    }
                }
                else if (bap == 0)
                {
                    target[start + i] = dither ? FrameDecoder.Noise(ref _noise) * Quantizers.Shift[packed & 31] : 0;
                }
                else
                {
                    target[start + i] = FrameDecoder.Dequantize(packed);
                }
            }

            return count;
        }

        if (kind == 1)
        {
            float[][] six = _aht[slot];
            float[] shift = _ahtShift[slot];
            Span<float> x = stackalloc float[6];
            for (int i = 0; i < count; i++)
            {
                int head = r.Next();
                int exponent = head & 31;
                int hebap = (head >> 5) & 31;
                int gain = (head >> 10) & 3;
                float scale = Quantizers.Shift[exponent];
                int bin = start + i;
                shift[bin] = hebap == 0 ? scale : 0;
                if (hebap == 0)
                {
                    for (int m = 0; m < 6; m++)
                    {
                        six[m][bin] = 0;
                    }

                    continue;
                }

                if (hebap < 8)
                {
                    int index = r.Next();
                    ReadOnlySpan<short> vector = AhtTables.Vectors[hebap].AsSpan(index * 6, 6);
                    for (int j = 0; j < 6; j++)
                    {
                        x[j] = vector[j] * (1.0f / 32768.0f);
                    }
                }
                else
                {
                    for (int j = 0; j < 6; j++)
                    {
                        x[j] = GainAdaptiveValue(hebap, gain, r.Next());
                    }
                }

                InverseDct(x, six, bin, scale);
            }
        }

        FillAht(slot, target, start, count, dither, block, isCoupling);
        return count;
    }

    // The block's coefficients of an adaptive hybrid transform channel, from the six blocks the first block's record decoded; the zero-bit bins take the channel's dither.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void FillAht(int slot, float[] target, int start, int count, bool dither, int block, bool isCoupling)
    {
        float[] values = _aht[slot][block];
        float[] shift = _ahtShift[slot];
        for (int bin = start; bin < start + count; bin++)
        {
            if (isCoupling)
            {
                target[bin] = values[bin];
                _couplingDither[bin] = shift[bin];
            }
            else if (shift[bin] != 0)
            {
                target[bin] = dither ? FrameDecoder.Noise(ref _noise) * shift[bin] : 0;
            }
            else
            {
                target[bin] = values[bin];
            }
        }
    }

    // The inverse DCT of A/52 Annex E 3.4.5 across the six blocks of one bin, C(m) = sqrt(2) sum R(j) X(j) cos(j (2m + 1) pi / 12) with R(0) = 1 / sqrt(2), scaled by the bin's exponent.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void InverseDct(ReadOnlySpan<float> x, float[][] six, int bin, float scale)
    {
        for (int m = 0; m < 6; m++)
        {
            float[] row = DctMatrix[m];
            float sum = 0;
            for (int j = 0; j < 6; j++)
            {
                sum += row[j] * x[j];
            }

            six[m][bin] = sum * scale;
        }
    }

    // sqrt(2) R(j) cos(j (2m + 1) pi / 12) by m (rows) and j.
    private static readonly float[][] DctMatrix = BuildDctMatrix();

    private static float[][] BuildDctMatrix()
    {
        var matrix = new float[6][];
        for (int m = 0; m < 6; m++)
        {
            matrix[m] = new float[6];
            for (int j = 0; j < 6; j++)
            {
                DetMath.SinCos(j * ((2 * m) + 1) % 24, 24, out _, out double cos);
                matrix[m][j] = (float)(Math.Sqrt(2.0) * (j == 0 ? 1.0 / Math.Sqrt(2.0) : 1.0) * cos);
            }
        }

        return matrix;
    }

    // A value of gain adaptive quantization (Annex E 3.4.4.2, Tables E.2.5 and E.2.6): a small mantissa of the gains 2 and 4 is its code times the step of Table E.2.5, which is
    // the output's, not the amplified value's; large ones and those of the gain 1 are the codeword as a two's complement fraction remapped as y = x + a x + b.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float GainAdaptiveValue(int hebap, int gain, int packed)
    {
        int code = packed >> 1;
        int m = Tables.GainAdaptiveBits[hebap - 8];
        short[] constants = AhtTables.Remap[hebap - 8];
        if (gain == 0)
        {
            float x = code * Quantizers.Shift[m - 1];
            return x + (x * (constants[0] * (1.0f / 32768.0f)));
        }

        if ((packed & 1) == 0)
        {
            return code * Quantizers.Shift[m - 1];
        }

        int bits = gain == 1 ? m - 1 : m;
        float large = code * Quantizers.Shift[bits - 1];
        float a = constants[gain == 1 ? 1 : 4] * (1.0f / 32768.0f);
        float b = constants[(gain == 1 ? 2 : 5) + (code < 0 ? 1 : 0)] * (1.0f / 32768.0f);
        return large + (a * large) + b;
    }

    // Spectral extension (Annex E 3.6.4): the coefficients from the copy start up to the begin bin are copied above it band by band, blended with noise scaled to each band's RMS
    // energy, and scaled by the band's coordinate. A notch filter around the begin bin and the bands whose copy wrapped attenuates the seams where the channel asks for it.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void SpectralExtension(float[] tc, int copyStart, int begin, int end, ReadOnlySpan<int> sizes, int blend, int master, ReadOnlySpan<int> coordinates, int attenuation)
    {
        Span<bool> wrapped = stackalloc bool[17];
        Span<float> rms = stackalloc float[17];
        int copy = copyStart;
        int insert = begin;
        for (int band = 0; band < sizes.Length; band++)
        {
            int size = sizes[band];
            if (copy + size > begin)
            {
                copy = copyStart;
                wrapped[band] = true;
            }

            for (int i = 0; i < size; i++)
            {
                if (copy == begin)
                {
                    copy = copyStart;
                }

                tc[insert++] = tc[copy++];
            }
        }

        int at = begin;
        for (int band = 0; band < sizes.Length; band++)
        {
            float accumulated = 0;
            for (int i = 0; i < sizes[band]; i++)
            {
                float value = tc[at + i];
                accumulated += value * value;
            }

            rms[band] = (float)Math.Sqrt(accumulated / sizes[band]);
            at += sizes[band];
        }

        if (attenuation >= 0)
        {
            float[] notch = AhtTables.Attenuation[attenuation];
            Notch(tc, begin, notch);
            at = begin;
            for (int band = 0; band < sizes.Length; band++)
            {
                if (band > 0 && wrapped[band])
                {
                    Notch(tc, at, notch);
                }

                at += sizes[band];
            }
        }

        double offset = blend / 32.0;
        at = begin;
        for (int band = 0; band < sizes.Length; band++)
        {
            int size = sizes[band];
            double ratio = (((double)at + (0.5 * size)) / end) - offset;
            ratio = ratio < 0.0 ? 0.0 : ratio > 1.0 ? 1.0 : ratio;
            float noiseScale = rms[band] * (float)Math.Sqrt(ratio);
            float signalScale = (float)Math.Sqrt(1.0 - ratio);
            int packed = coordinates[band];
            int exponent = packed & 15;
            int mantissa = (packed >> 4) & 3;
            float coordinate = exponent == 15 ? mantissa * 0.25f : (mantissa + 4) * 0.125f;
            float scale = coordinate * Quantizers.Shift[exponent + (3 * master)] * 32.0f;
            for (int i = 0; i < size; i++)
            {
                tc[at + i] = ((tc[at + i] * signalScale) + (UnitNoise(ref _spxNoise) * noiseScale)) * scale;
            }

            at += size;
        }
    }

    private static void Notch(float[] tc, int centre, float[] attenuation)
    {
        tc[centre - 2] *= attenuation[0];
        tc[centre - 1] *= attenuation[1];
        tc[centre] *= attenuation[2];
        tc[centre + 1] *= attenuation[1];
        tc[centre + 2] *= attenuation[0];
    }

    // A value of zero mean and unit variance, uniform between -sqrt(3) and sqrt(3), from a linear congruential generator whose top 24 bits are the integer part (Annex E 3.6.4.2).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float UnitNoise(ref uint state)
    {
        state = unchecked((state * 1664525u) + 1013904223u);
        return ((int)state >> 8) * (1.0f / 8388608.0f) * 1.7320508f;
    }
}
