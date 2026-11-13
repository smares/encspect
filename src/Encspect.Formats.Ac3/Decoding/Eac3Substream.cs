using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Ac3.Decoding;

// The decoding state of one E-AC-3 substream (an independent one or a dependent one): the overlap of each channel's last block, the two noise generators (the dither of zero-bit
// mantissas and spectral extension's blend) and the adaptive hybrid transform's six blocks of coefficients per channel. The blocks it finishes go to the program's decoder
// (Eac3FrameDecoder), which merges the substreams' channels, withholds after a gap and writes the samples.
internal sealed class Eac3Substream
{
    private const int BlockSamples = 256;
    private const int Cpl = 5;
    private const int Lfe = 6;

    // The units a sample of a channel costs on top of the floor of the budget, measured with DecoderWorkTests (docs/design/decoders.md, "As built: the E-AC-3 decoder's work weights").
    private const int ExtraUnits = 22;

    // Charged on top of the floor where a block uses a tool that costs more than the dequantization and transform of a sample: an adaptive hybrid transform channel's inverse DCT and
    // table lookups (units a bin, over its six blocks), and enhanced coupling's transform, complex transform and the sines and cosines of each coupled channel's bins (a fixed part
    // for the block and units a bin of a coupled channel), measured with DecoderWorkTests.
    private const int AhtUnitsPerBin = 40;
    private const int EnhancedBlockUnits = 8192;
    private const int EnhancedUnitsPerBin = 192;

    // The seeds of the generators, at the start of the stream and after each gap (docs/design/lossy-decoders.md, "Random noise").
    private const uint NoiseSeed = 0x2545F491;
    private const uint SpxSeed = 0x6C8E9CF5;

    private readonly DecodeContext _context;
    private readonly int _channels;
    private readonly int _acmod;
    private readonly bool _hasLfe;
    private readonly int _fullChannels;
    private readonly InverseTransform _transform = new();
    private readonly float[][] _delay;
    private readonly float[] _coupling = new float[256];
    private readonly float[] _couplingDither = new float[256];
    private readonly float[] _x = new float[512];
    private readonly int[] _attenuation = new int[5];
    private readonly BlockState[] _states = [.. Enumerable.Range(0, 6).Select(_ => new BlockState())];
    private readonly EnhancedCoupling _enhanced = new();
    private readonly float[] _previousX = new float[512];
    private bool _hasPreviousX;

    // The last block of a frame that has enhanced coupling needs the next block's coupling channel, which is the next frame's first block: it waits here, with its own output, until
    // that frame is read (or a gap, or the end of the stream, says there is none). Its samples are written then, at their own position.
    private BlockState? _pending;
    private long _pendingFirst;
    private readonly float[] _pendingPcm = new float[6 * BlockSamples];
    private readonly float[] _pendingPreviousX = new float[512];
    private bool _hasPendingPreviousX;
    private readonly float[][][] _aht = [.. Enumerable.Range(0, 7).Select(_ => new float[][] { new float[256], new float[256], new float[256], new float[256], new float[256], new float[256] })];
    private readonly float[][] _ahtShift = [.. Enumerable.Range(0, 7).Select(_ => new float[256])];
    private float[] _planar = [];
    private int _stride;
    private readonly Action<long, float[], int, int> _deliver;
    private uint _noise = NoiseSeed;
    private uint _spxNoise = SpxSeed;
    private long _frameFirst;

    // deliver(first sample, planar samples, stride, offset) is called with each block of 256 samples the substream finishes, its channels in coded order.
    public Eac3Substream(DecodeContext context, int acmod, bool hasLfe, Action<long, float[], int, int> deliver)
    {
        _context = context;
        _deliver = deliver;
        _acmod = acmod;
        _hasLfe = hasLfe;
        _fullChannels = DecodeFeed.ChannelOf(acmod);
        _channels = _fullChannels + (hasLfe ? 1 : 0);
        _delay = [.. Enumerable.Range(0, _channels).Select(_ => new float[BlockSamples])];
    }

    public int Channels => _channels;

    public int Acmod => _acmod;

    public bool HasLfe => _hasLfe;

    public void Reset()
    {
        foreach (float[] delay in _delay)
        {
            Array.Clear(delay);
        }

        _noise = NoiseSeed;
        _spxNoise = SpxSeed;
        _hasPreviousX = false;
        _enhanced.Reset();
    }

    // Finishes the block that waited for its next block, which is next (the first block of the frame just read) or absent, and delivers its 256 samples.
    public void FlushPending(BlockState? next = null)
    {
        if (_pending is not { } state)
        {
            return;
        }

        _pending = null;
        Span<float> previous = _hasPendingPreviousX ? _pendingPreviousX : default;
        FinishState(state, previous, next is { Mode: 2 } ? CouplingTransform(next) : default, _pendingPcm, BlockSamples, 0);
        // The block's output is the previous frame's last 256 samples; the next frame's first block continues from it.
        _hasPreviousX = true;
        CouplingTransform(state).CopyTo(_previousX);
        _deliver(_pendingFirst, _pendingPcm, BlockSamples, 0);
    }

    // Decodes the substream's frame whose record header (acmod, lfeon, the number of blocks) the caller has read, and delivers its blocks.
    public void DecodeUnit(ref IntReader r, long firstSample, int blocks)
    {
        Transients.Clear();
        int samples = blocks * BlockSamples;
        _frameFirst = firstSample;
        EnsureBuffers(samples);
        _context.AddWork(DecodeWork.ForSample(4) * samples * _channels);
        _context.AddWork((long)ExtraUnits * samples * _channels);
        for (int block = 0; block < blocks; block++)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            ParseBlock(ref r, block, _states[block]);
        }

        FlushPending(_states[0]);
        for (int block = 0; block < blocks; block++)
        {
            FinishBlock(block, blocks);
        }

        // A deferred last block is delivered when it is finished.
        int ready = _pending is null ? blocks : blocks - 1;
        for (int block = 0; block < ready; block++)
        {
            _deliver(firstSample + ((long)block * BlockSamples), _planar, _stride, block * BlockSamples);
        }
    }

    // The transient pre-noise processing data of the frame last decoded: channel, location from the frame's first sample, and length (3.7.2).
    public List<(int Channel, int Location, int Length)> Transients { get; } = [];

    private void EnsureBuffers(int samples)
    {
        if (samples <= _stride)
        {
            return;
        }

        _stride = samples;
        _planar = new float[_channels * _stride];
    }

    // What one audio block's record states and its coefficients, up to the steps that need the blocks around it.
    internal sealed class BlockState
    {
        public int Blksw;
        public int Dither;
        public bool Spx;
        public int SpxChannels;
        public int SpxStart;
        public int SpxBegin;
        public int SpxEnd;
        public int SpxBands;
        public readonly int[] SpxSizes = new int[17];
        public readonly int[] SpxBlend = new int[5];
        public readonly int[] SpxMaster = new int[5];
        public readonly int[] SpxCoordinates = new int[5 * 17];
        public int Mode;
        public int EcplBegin;
        public int EcplEnd;
        public int EcplJoined;
        public int EcplChannels;
        public bool EcplInterpolates;
        public int EcplBands;
        public readonly int[] EcplParameters = new int[5 * 22];
        public readonly bool[] EcplTransient = new bool[5];
        public readonly float[] Coupling = new float[256];
        public readonly float[] CouplingX = new float[512];
        public readonly float[] EcplDither = new float[5 * 256];
        public bool HasCouplingX;
        public readonly int[] Attenuation = new int[5];
        public readonly float[][] Spectrum = [.. Enumerable.Range(0, 6).Select(_ => new float[256])];
    }

    // One audio block's record read into the block's state: the coefficients of every channel, rematrixing and standard coupling. Enhanced coupling, spectral extension and the
    // inverse transforms wait for FinishBlock, which has the blocks before and after.
    private void ParseBlock(ref IntReader r, int block, BlockState state)
    {
        if (block == 0)
        {
            for (int ch = 0; ch < _fullChannels; ch++)
            {
                _attenuation[ch] = r.Next() - 1;
            }

            for (int ch = 0; ch < _fullChannels; ch++)
            {
                int location = r.Next() - 1;
                if (location >= 0)
                {
                    Transients.Add((ch, location * 4, r.Next()));
                }
            }
        }

        state.Blksw = r.Next();
        int dither = state.Dither = r.Next();
        state.Spx = r.Next() != 0;
        if (state.Spx)
        {
            state.SpxChannels = r.Next();
            state.SpxStart = r.Next();
            state.SpxBegin = r.Next();
            state.SpxEnd = r.Next();
            int joined = r.Next();
            state.SpxBands = 1;
            state.SpxSizes[0] = 12;
            for (int sub = state.SpxBegin + 1; sub < state.SpxEnd; sub++)
            {
                if (((joined >> sub) & 1) == 0)
                {
                    state.SpxSizes[state.SpxBands++] = 12;
                }
                else
                {
                    state.SpxSizes[state.SpxBands - 1] += 12;
                }
            }

            for (int ch = 0; ch < _fullChannels; ch++)
            {
                if (((state.SpxChannels >> ch) & 1) != 0)
                {
                    state.SpxBlend[ch] = r.Next();
                    state.SpxMaster[ch] = r.Next();
                    for (int band = 0; band < state.SpxBands; band++)
                    {
                        state.SpxCoordinates[(ch * 17) + band] = r.Next();
                    }
                }
            }
        }

        int mode = state.Mode = r.Next();
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
            state.EcplBegin = r.Next();
            state.EcplEnd = r.Next();
            chincpl = state.EcplChannels = r.Next();
            state.EcplInterpolates = r.Next() != 0;
            state.EcplJoined = r.Next();
            int bands = state.EcplEnd - state.EcplBegin;
            for (int sub = state.EcplBegin + 1; sub < state.EcplEnd; sub++)
            {
                bands -= (state.EcplJoined >> sub) & 1;
            }

            state.EcplBands = bands;
            for (int ch = 0; ch < _fullChannels; ch++)
            {
                if (((chincpl >> ch) & 1) != 0)
                {
                    for (int band = 0; band < bands; band++)
                    {
                        state.EcplParameters[(ch * 22) + band] = r.Next();
                    }

                    state.EcplTransient[ch] = r.Next() != 0;
                }
            }

            cplStart = Tables.EnhancedCouplingSubBandStart[state.EcplBegin];
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
            ends[ch] = Channel(ref r, ch, state.Spectrum[ch], 0, ((dither >> ch) & 1) != 0, block, false);
            if (mode != 0 && ((chincpl >> ch) & 1) != 0 && !gotCoupling)
            {
                Channel(ref r, Cpl, _coupling, cplStart, false, block, true);
                gotCoupling = true;
            }
        }

        if (_hasLfe)
        {
            Channel(ref r, Lfe, state.Spectrum[_fullChannels], 0, false, block, false);
        }

        if (_acmod == 2 && rematBands > 0)
        {
            FrameDecoder.Rematrix(state.Spectrum[0], state.Spectrum[1], rematBands, rematFlags, mode == 1 ? cplbegf : -1, Math.Min(ends[0], ends[1]));
        }

        if (mode == 1)
        {
            for (int ch = 0; ch < _fullChannels; ch++)
            {
                if (((chincpl >> ch) & 1) != 0)
                {
                    FrameDecoder.ExpandCoupling(state.Spectrum[ch], _coupling, _couplingDither, ref _noise, coordinates.Slice(ch * 18, 18), cplbegf, subbands, combined, phsflg, bands2,
                        ch == 1 && _acmod == 2, ((dither >> ch) & 1) != 0);
                }
            }
        }
        else if (mode == 2)
        {
            Array.Copy(_coupling, state.Coupling, 256);
            // The zero-bit bins of the coupling channel take dither in each coupled channel that has dithflag set (A/52 7.3.4), drawn here in block order and added after the channel
            // is extracted from the coupling channel (FinishState), scaled by the band's amplitude as standard coupling scales it by the coordinate.
            Array.Clear(state.EcplDither);
            int ecplStart = Tables.EnhancedCouplingSubBandStart[state.EcplBegin];
            int ecplStop = Tables.EnhancedCouplingSubBandStart[state.EcplEnd];
            for (int ch = 0; ch < _fullChannels; ch++)
            {
                if (((state.EcplChannels >> ch) & 1) != 0 && ((dither >> ch) & 1) != 0)
                {
                    for (int bin = ecplStart; bin < ecplStop; bin++)
                    {
                        if (_couplingDither[bin] != 0)
                        {
                            state.EcplDither[(ch * 256) + bin] = FrameDecoder.Noise(ref _noise) * _couplingDither[bin];
                        }
                    }
                }
            }
        }

        state.HasCouplingX = false;
        Array.Copy(_attenuation, state.Attenuation, 5);
    }

    // The steps of a block that need its neighbors or all of its channels' coefficients: enhanced coupling (the coupling channel of the blocks before and after, the next frame's
    // first block, which waits for it), spectral extension, and each channel's inverse transform, window and overlap.
    private void FinishBlock(int block, int blocks)
    {
        BlockState state = _states[block];
        Span<float> previous = block > 0 ? (_states[block - 1].Mode == 2 ? CouplingTransform(_states[block - 1]) : default) : _hasPreviousX ? _previousX : default;
        if (block == blocks - 1 && state.Mode == 2)
        {
            // Wait for the next frame's first block: keep this block's state (the array slot gets the object that is free), its previous block's transform and the position of its output.
            _hasPendingPreviousX = !previous.IsEmpty;
            previous.CopyTo(_pendingPreviousX);
            CouplingTransform(state);
            _pending = state;
            _states[block] = new BlockState();
            _pendingFirst = _frameFirst + ((long)block * BlockSamples);
            return;
        }

        Span<float> next = block + 1 < blocks && _states[block + 1].Mode == 2 ? CouplingTransform(_states[block + 1]) : default;
        FinishState(state, previous, next, _planar, _stride, block * BlockSamples);
        if (block == blocks - 1)
        {
            // The next frame's first block sees this one as its previous block.
            _hasPreviousX = false;
        }
    }

    // The steps of a block that need its neighbors or all of its channels' coefficients: enhanced coupling (the coupling channel of the blocks before and after), spectral extension, and
    // each channel's inverse transform, window and overlap into dest (channel c at c * stride + offset).
    private void FinishState(BlockState state, Span<float> previous, Span<float> next, float[] dest, int stride, int offset)
    {
        if (state.Mode == 2)
        {
            Span<float> current = CouplingTransform(state);
            int coupled = System.Numerics.BitOperations.PopCount((uint)state.EcplChannels);
            int bins = Tables.EnhancedCouplingSubBandStart[state.EcplEnd] - Tables.EnhancedCouplingSubBandStart[state.EcplBegin];
            _context.AddWork(EnhancedBlockUnits + ((long)EnhancedUnitsPerBin * bins * coupled));
            _enhanced.Analyze(previous, current, next);
            int first = System.Numerics.BitOperations.TrailingZeroCount(state.EcplChannels);
            for (int ch = 0; ch < _fullChannels; ch++)
            {
                if (((state.EcplChannels >> ch) & 1) != 0)
                {
                    _enhanced.Channel(state.Spectrum[ch], state.EcplParameters.AsSpan(ch * 22, 22), ch, ch == first, state.EcplTransient[ch], state.EcplInterpolates, state.EcplBegin, state.EcplEnd, state.EcplJoined, state.EcplDither.AsSpan(ch * 256, 256));
                }
            }
        }

        if (state.Spx)
        {
            for (int ch = 0; ch < _fullChannels; ch++)
            {
                if (((state.SpxChannels >> ch) & 1) != 0)
                {
                    SpectralExtension(state.Spectrum[ch], 25 + (12 * state.SpxStart), 25 + (12 * state.SpxBegin), 25 + (12 * state.SpxEnd), state.SpxSizes.AsSpan(0, state.SpxBands),
                        state.SpxBlend[ch], state.SpxMaster[ch], state.SpxCoordinates.AsSpan(ch * 17, 17), state.Attenuation[ch]);
                }
            }
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            bool isShort = ch < _fullChannels && ((state.Blksw >> ch) & 1) != 0;
            Span<float> x = _x;
            float[] spectrum = state.Spectrum[ch];
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

            FrameDecoder.Overlap(x, _delay[ch], dest.AsSpan((ch * stride) + offset, BlockSamples));
        }
    }

    // The windowed output of the inverse transform of an enhanced coupling block's coupling channel (7.9.4.1, steps 1 to 5), made once for the block.
    private Span<float> CouplingTransform(BlockState state)
    {
        if (!state.HasCouplingX)
        {
            _transform.Long(state.Coupling, state.CouplingX);
            state.HasCouplingX = true;
        }

        return state.CouplingX;
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
            _context.AddWork((long)AhtUnitsPerBin * count);
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

    // A value of gain adaptive quantization (Annex E 3.4.4.2, A/52 Tables E3.5 and E3.6): a small mantissa of the gains 2 and 4 is its code times the step of Table E.2.5, which is
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

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Notch(float[] tc, int center, float[] attenuation)
    {
        tc[center - 2] *= attenuation[0];
        tc[center - 1] *= attenuation[1];
        tc[center] *= attenuation[2];
        tc[center + 1] *= attenuation[1];
        tc[center + 2] *= attenuation[0];
    }

    // A value of zero mean and unit variance, uniform between -sqrt(3) and sqrt(3), from a linear congruential generator whose top 24 bits are the integer part (Annex E 3.6.4.2).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float UnitNoise(ref uint state)
    {
        state = unchecked((state * 1664525u) + 1013904223u);
        return ((int)state >> 8) * (1.0f / 8388608.0f) * 1.7320508f;
    }
}
