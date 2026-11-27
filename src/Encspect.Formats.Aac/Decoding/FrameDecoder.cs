using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Aac.Decoding;

// Follows libxaac (ixheaacd_channel.c ixheaacd_channel_pair_process, ixheaacd_stereo.c, ixheaacd_pns_js_thumb.c, ixheaacd_imdct.c; Apache License 2.0, Copyright (C) 2018 The
// Android Open Source Project; the notice is in THIRD-PARTY-NOTICES.md): the noise generator and its seed, intensity stereo over the second channel's own layout, and the ranges of
// the TNS filters. The order of the tools is the standard's (Figure 4.2, 4.1.1.2): mid/side stereo, noise substitution, intensity stereo, temporal noise shaping, the filter bank.
// The decoding state of one AAC LC stream: per channel the second half of the previous window's output (the overlap) and its window shape, and the noise generator's state.
internal sealed class FrameDecoder : IFrameDecoder
{
    private const int Lines = 1024;
    private const int ShortLines = 128;
    // TNS_MAX_ORDER of the object types but AAC Main (4.6.9.3, Table 4.156): a filter of a higher order takes its first 12 coefficients.
    private const int MaxTnsOrder = 12;
    // The shape of the window before the first block, which has the same shape on both halves (4.6.11.3.2).
    private const int FirstShape = -1;
    private const float SpectrumLimit = 1e30f;

    // The units each stage costs (docs/design/lossy-decoders.md, "Work units per format"): inverse quantization 2 for each coded line, mid/side and intensity stereo 1 for each line
    // of a pair, the inverse transform 2 per bit of its length for each output sample, the window and overlap 2, the TNS filter its order for each line it covers, noise substitution 3.
    private const int DequantizeUnits = 2;
    private const int StereoUnits = 1;
    private const int OverlapUnits = 2;
    private const int NoiseUnits = 3;
    private const int LongTransformUnits = 22;
    // Per output sample of the SBR stage (the 64-band analysis and synthesis, the high band's generation and adjustment, and parametric stereo's hybrid bank, decorrelator and mixing
    // for the second channel): measured with DecoderWorkTests (the HE-AAC cases), where the stage takes about 4 times what the core's stages charge in time per unit at 60.
    private const int SbrUnits = 220;
    private const int ShortTransformUnits = 16;

    private readonly DecodeContext _context;
    private readonly DecodeConfig _config;
    private readonly int _channels;
    private readonly int _outputChannels;
    private readonly bool _isSbr;
    private readonly bool _isPs;
    private PsDecoder? _ps;
    private readonly int[] _layout;
    private readonly SbrElement?[] _elements;
    private readonly float[] _sbrPlanar;
    private readonly ushort[] _longOffsets;
    private readonly ushort[] _shortOffsets;
    private readonly int _longBands;
    private readonly int _shortBands;
    private readonly int _rateIndex;
    private readonly InverseMdct _longTransform = new(2048, 1f / (1024f * 32768f));
    private readonly InverseMdct _shortTransform = new(256, 1f / (128f * 32768f));
    private readonly float[][] _spectrum;
    private readonly float[][] _overlap;
    private readonly int[] _previousShape;
    private readonly bool[] _isSilent;
    private readonly float[] _planar;
    private readonly float[] _raw = new float[2 * Lines];
    private readonly float[] _window = new float[2 * Lines];
    private readonly float[] _partial = new float[2 * ShortLines];
    private readonly int[] _quantized = new int[Lines];
    private readonly int[] _noiseSeeds = new int[8 * 64];
    private readonly bool[] _hasNoiseSeed = new bool[8 * 64];
    private readonly float[] _lpc = new float[BlockRecord.TnsMaxCoefficients + 1];
    private readonly float[] _parcor = new float[BlockRecord.TnsMaxCoefficients + 1];
    private readonly float[] _history = new float[BlockRecord.TnsMaxCoefficients + 1];
    private IPcmSink? _sink;
    private int _seed;
    private bool _lostBefore;

    // The block before this one failed its checksum: the decoder kept its overlap, so the samples this block returns depend on it and are withheld from the measurements
    // (docs/design/damaged-units.md).
    private bool _followsFailedUnit;

    public FrameDecoder(DecodeContext context, DecodeConfig config)
    {
        _context = context;
        _config = config;
        _outputChannels = context.Setup.Channels;
        _isSbr = config.IsSbrSignaled;
        _isPs = config.IsSbrSignaled && config.IsPsSignaled;
        _channels = _isPs ? 1 : _outputChannels;
        _elements = new SbrElement?[_channels];
        _sbrPlanar = _isSbr ? new float[_outputChannels * 2 * Lines] : [];
        int index = Math.Min(config.SampleRateIndex, BandTables.Long.Length - 1);
        _rateIndex = index;
        _longOffsets = BandTables.Long[index];
        _shortOffsets = BandTables.Short[index];
        _longBands = BandTables.NumLong[index];
        _shortBands = BandTables.NumShort[index];
        _spectrum = new float[_channels][];
        _overlap = new float[_channels][];
        for (int ch = 0; ch < _channels; ch++)
        {
            _spectrum[ch] = new float[Lines];
            _overlap[ch] = new float[Lines];
        }

        _layout = new int[_channels];
        Array.Fill(_layout, -1);
        _previousShape = new int[_channels];
        Array.Fill(_previousShape, FirstShape);
        _isSilent = new bool[_channels];
        _planar = new float[_channels * Lines];
    }

    public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];

    public int Decode(in CodedFrame frame)
    {
        SampleBuffers lent = _context.LentBuffers;
        if (!frame.IsWalked || lent.IsEmpty)
        {
            _context.AddFinding(Severity.Info, "decode.frame-too-large", $"The block at 0x{frame.Bytes.FileStart:X} could hold more channel streams than the host lends buffers for, so it is not decoded.");
            _lostBefore = true;
            _followsFailedUnit = false;
            return 0;
        }

        ReadOnlySpan<int> record = lent.Narrow;
        Validate(record);
        if ((record[BlockRecord.BlockFlags] & BlockRecord.BlockHasCoupling) != 0)
        {
            _context.AddFinding(Severity.Warning, "decode.coupling-not-applied",
                $"The block at 0x{frame.Bytes.FileStart:X} holds a coupling channel element, whose channels are read and not applied, so the decoded audio lacks what they add to the channels they couple.");
        }

        // A block whose channel elements differ from the last block's (another kind in a slot) is a loss as a change of the slot count is: the per-element state of SBR no longer fits it.
        bool layoutChanged = false;
        for (int ch = 0; ch < _channels; ch++)
        {
            int kind = record[(ch * BlockRecord.Stride) + BlockRecord.Kind];
            layoutChanged |= _layout[ch] != kind && _layout[ch] >= 0;
            _layout[ch] = kind;
        }

        bool follows = frame.FollowsGap || _lostBefore || layoutChanged;
        _lostBefore = false;
        if (follows)
        {
            Reset();
        }

        _context.CancellationToken.ThrowIfCancellationRequested();
        Charge(record);

        for (int ch = 0; ch < _channels; ch++)
        {
            Dequantize(ch, record.Slice(ch * BlockRecord.Stride, BlockRecord.Stride));
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            ReadOnlySpan<int> slot = record.Slice(ch * BlockRecord.Stride, BlockRecord.Stride);
            if (slot[BlockRecord.Kind] == BlockRecord.KindPairFirst)
            {
                MidSide(slot, record.Slice((ch + 1) * BlockRecord.Stride, BlockRecord.Stride), ch);
            }
        }

        Array.Clear(_hasNoiseSeed);
        for (int ch = 0; ch < _channels; ch++)
        {
            ReadOnlySpan<int> slot = record.Slice(ch * BlockRecord.Stride, BlockRecord.Stride);
            NoiseSubstitution(ch, slot, ch > 0 && slot[BlockRecord.Kind] == BlockRecord.KindPairSecond ? record.Slice((ch - 1) * BlockRecord.Stride, BlockRecord.Stride) : default);
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            ReadOnlySpan<int> slot = record.Slice(ch * BlockRecord.Stride, BlockRecord.Stride);
            if (slot[BlockRecord.Kind] == BlockRecord.KindPairFirst)
            {
                IntensityStereo(slot, record.Slice((ch + 1) * BlockRecord.Stride, BlockRecord.Stride), ch);
            }
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            ReadOnlySpan<int> slot = record.Slice(ch * BlockRecord.Stride, BlockRecord.Stride);
            if ((slot[BlockRecord.Flags] & BlockRecord.FlagTns) != 0)
            {
                TemporalNoiseShaping(ch, slot);
            }
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            Synthesize(ch, record.Slice(ch * BlockRecord.Stride, BlockRecord.Stride));
        }

        int count = Lines;
        float[] pcm = _planar;
        int stride = Lines;
        if (_isSbr)
        {
            RunSbr(record);
            count = 2 * Lines;
            pcm = _sbrPlanar;
            stride = 2 * Lines;
        }

        if (follows)
        {
            _context.Withhold(frame.FirstSample, count, WithholdReason.AfterGap);
            _followsFailedUnit = frame.FailsChecksum;
            return 0;
        }

        if (_followsFailedUnit)
        {
            _context.WithholdFromMeasurements(frame.FirstSample, count);
        }

        _followsFailedUnit = frame.FailsChecksum;
        _sink ??= _context.DeclareOutput(new PcmFormat(_isSbr ? 2 * _config.SampleRate : _config.SampleRate, _outputChannels, 32, SampleFormat.Float32) { ChannelLayout = _config.Layout });
        _sink.Write(new PcmBlock(frame.FirstSample, count, _outputChannels, stride, pcm));
        return count;
    }

    // The SBR stage of every channel element of the block: the high band of its channels, and the 64-band synthesis to samples at twice the core's rate (ISO/IEC 14496-3 4.6.18).
    private void RunSbr(ReadOnlySpan<int> record)
    {
        int channelSlots = record[BlockRecord.Slots];
        int sbrSlots = record[BlockRecord.SbrSlots];
        int ch = 0;
        int output = 0;
        while (ch < _channels)
        {
            int kind = record[(ch * BlockRecord.Stride) + BlockRecord.Kind];
            int width = kind == BlockRecord.KindPairFirst ? 2 : 1;
            SbrElement element = _elements[ch] ??= new SbrElement(2 * _config.SampleRate);
            element.Configure(width);
            ReadOnlySpan<int> slot = default;
            if (kind != BlockRecord.KindLfe)
            {
                for (int s = 0; s < sbrSlots; s++)
                {
                    ReadOnlySpan<int> candidate = record.Slice((channelSlots + s) * BlockRecord.Stride, BlockRecord.Stride);
                    if (candidate[SbrRecord.ChannelSlot] == ch)
                    {
                        slot = candidate;
                        break;
                    }
                }
            }

            element.Process(_planar, ch, Lines, slot);
            if (_isPs)
            {
                // Parametric stereo: the single channel's QMF domain becomes two, which the two syntheses turn into the output's left and right.
                PsDecoder ps = _ps ??= new PsDecoder();
                ReadOnlySpan<int> data = !slot.IsEmpty && slot[SbrRecord.HasPs] == 1 ? slot.Slice(SbrRecord.Ps) : default;
                ps.Process(element.Real(0), element.Imaginary(0), element.LookAheadReal(0), element.LookAheadImaginary(0), data, element.K2);
                element.Synthesize(0, ps.LeftRe, ps.LeftIm, _sbrPlanar.AsSpan(0, 2 * Lines));
                element.SynthesizeSecond(ps.RightRe, ps.RightIm, _sbrPlanar.AsSpan(2 * Lines, 2 * Lines));
                output += 2;
                ch += width;
                continue;
            }

            for (int c = 0; c < width; c++)
            {
                element.Synthesize(c, element.Real(c), element.Imaginary(c), _sbrPlanar.AsSpan((output + c) * 2 * Lines, 2 * Lines));
            }

            output += width;
            ch += width;
        }
    }

    // What the walk stored must be something the reconstruction can take; a block that is not is not decoded, and what follows it follows a loss.
    private void Validate(ReadOnlySpan<int> record)
    {
        int slots = record[BlockRecord.Slots];
        if (slots != _channels)
        {
            _lostBefore = true;
            throw new InvalidDataException($"the block holds {slots} channel {(slots == 1 ? "stream" : "streams")}, the stream {_channels}");
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            ReadOnlySpan<int> slot = record.Slice(ch * BlockRecord.Stride, BlockRecord.Stride);
            int kind = slot[BlockRecord.Kind];
            bool isPairFirst = kind == BlockRecord.KindPairFirst;
            if (isPairFirst && (ch + 1 >= _channels || record[((ch + 1) * BlockRecord.Stride) + BlockRecord.Kind] != BlockRecord.KindPairSecond))
            {
                _lostBefore = true;
                throw new InvalidDataException("the block's channel pair has no second channel");
            }

            int count = slot[BlockRecord.TnsFilters];
            if (count > BlockRecord.TnsMaxFilters)
            {
                _lostBefore = true;
                throw new InvalidDataException("a channel stream has more TNS filters than the decoder takes");
            }
        }
    }

    private void Reset()
    {
        for (int ch = 0; ch < _channels; ch++)
        {
            Array.Clear(_overlap[ch]);
            _previousShape[ch] = FirstShape;
            _elements[ch]?.Reset();
            _ps?.Reset();
        }

        _seed = 0;
    }

    // The work of the block before it is done: the floor of every output sample, and the stages of each channel that has anything to transform.
    private void Charge(ReadOnlySpan<int> record)
    {
        long units = DecodeWork.ForSample(4) * Lines * _outputChannels * (_isSbr ? 2 : 1);
        if (_isSbr)
        {
            units += (long)SbrUnits * 2 * Lines * _outputChannels;
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            ReadOnlySpan<int> slot = record.Slice(ch * BlockRecord.Stride, BlockRecord.Stride);
            _isSilent[ch] = IsSilent(slot);
        }

        for (int ch = 0; ch + 1 < _channels; ch++)
        {
            ReadOnlySpan<int> slot = record.Slice(ch * BlockRecord.Stride, BlockRecord.Stride);
            if (slot[BlockRecord.Kind] == BlockRecord.KindPairFirst
                && (slot[BlockRecord.MsMask] is 1 or 2 || HasIntensity(record.Slice((ch + 1) * BlockRecord.Stride, BlockRecord.Stride))))
            {
                // Mid/side and intensity stereo mix the pair: its channels are silent only together.
                bool both = _isSilent[ch] && _isSilent[ch + 1];
                _isSilent[ch] = both;
                _isSilent[ch + 1] = both;
            }
        }

        for (int ch = 0; ch < _channels; ch++)
        {
            ReadOnlySpan<int> slot = record.Slice(ch * BlockRecord.Stride, BlockRecord.Stride);
            bool isShort = slot[BlockRecord.WindowSequence] == 2;
            ushort[] offsets = isShort ? _shortOffsets : _longOffsets;
            int coded = offsets[Math.Min(slot[BlockRecord.MaxSfb], isShort ? _shortBands : _longBands)] * (isShort ? 8 : 1);
            units += (long)OverlapUnits * Lines;
            if (_isSilent[ch])
            {
                continue;
            }

            units += (long)DequantizeUnits * coded;
            units += (long)(isShort ? ShortTransformUnits : LongTransformUnits) * Lines;
            if (slot[BlockRecord.Kind] is BlockRecord.KindPairFirst or BlockRecord.KindPairSecond)
            {
                units += (long)StereoUnits * coded;
            }

            if (HasNoise(slot))
            {
                units += (long)NoiseUnits * coded;
            }

            if ((slot[BlockRecord.Flags] & BlockRecord.FlagTns) != 0)
            {
                int filters = Math.Min(slot[BlockRecord.TnsFilters], BlockRecord.TnsMaxFilters);
                for (int f = 0; f < filters; f++)
                {
                    units += (long)Math.Min(slot[BlockRecord.Tns + (f * BlockRecord.TnsFilterInts) + 2], MaxTnsOrder) * (isShort ? ShortLines : Lines);
                }
            }
        }

        _context.AddWork(units);
    }

    // A channel stream with no quantized value, no noise substitution and no intensity stereo reconstructs to nothing.
    private static bool IsSilent(ReadOnlySpan<int> slot)
    {
        if ((slot[BlockRecord.Flags] & BlockRecord.FlagPulse) != 0 && slot[BlockRecord.PulseCount] > 0)
        {
            return false;
        }

        int groups = slot[BlockRecord.Groups];
        int maxSfb = slot[BlockRecord.MaxSfb];
        for (int g = 0; g < groups; g++)
        {
            for (int sfb = 0; sfb < maxSfb; sfb++)
            {
                if (slot[BlockRecord.Codebooks + (g * 64) + sfb] >= 13)
                {
                    return false;
                }
            }
        }

        ReadOnlySpan<int> values = slot.Slice(BlockRecord.Spectrum, Lines);
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasIntensity(ReadOnlySpan<int> second)
    {
        int groups = second[BlockRecord.Groups];
        int maxSfb = second[BlockRecord.MaxSfb];
        for (int g = 0; g < groups; g++)
        {
            for (int sfb = 0; sfb < maxSfb; sfb++)
            {
                if (second[BlockRecord.Codebooks + (g * 64) + sfb] >= 14)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasNoise(ReadOnlySpan<int> slot)
    {
        int groups = slot[BlockRecord.Groups];
        int maxSfb = slot[BlockRecord.MaxSfb];
        for (int g = 0; g < groups; g++)
        {
            for (int sfb = 0; sfb < maxSfb; sfb++)
            {
                if (slot[BlockRecord.Codebooks + (g * 64) + sfb] == 13)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Inverse quantization (4.6.2.1) and the scalefactors (4.6.2.3) into the channel's spectrum, in the order of the windows' lines: for eight short windows, window w's line k at
    // w * 128 + k. The values of bands that code noise or intensity stereo stay zero.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Dequantize(int ch, ReadOnlySpan<int> slot)
    {
        float[] spectrum = _spectrum[ch];
        Array.Clear(spectrum);
        if (_isSilent[ch])
        {
            return;
        }

        int sequence = slot[BlockRecord.WindowSequence];
        bool isShort = sequence == 2;
        int maxSfb = Math.Min(slot[BlockRecord.MaxSfb], isShort ? _shortBands : _longBands);
        ReadOnlySpan<int> values = slot.Slice(BlockRecord.Spectrum, Lines);
        if (!isShort && (slot[BlockRecord.Flags] & BlockRecord.FlagPulse) != 0)
        {
            // Pulse data (4.6.3.3) adds to the magnitudes of long windows; a value of zero takes the pulse as a negative one, as libxaac's ixheaacd_huffman_dec does.
            values.CopyTo(_quantized);
            int count = slot[BlockRecord.PulseCount];
            int start = slot[BlockRecord.PulseStart];
            if (start < _longBands)
            {
                int k = _longOffsets[start];
                for (int i = 0; i < count && i < 4; i++)
                {
                    k += slot[BlockRecord.Pulses + (2 * i)];
                    if (k >= Lines)
                    {
                        break;
                    }

                    int amplitude = slot[BlockRecord.Pulses + (2 * i) + 1];
                    _quantized[k] += _quantized[k] > 0 ? amplitude : -amplitude;
                }
            }

            values = _quantized;
        }

        if (!isShort)
        {
            for (int sfb = 0; sfb < maxSfb; sfb++)
            {
                int book = slot[BlockRecord.Codebooks + sfb];
                if (book is < 1 or > 11)
                {
                    continue;
                }

                float gain = Tables.Gain[Math.Clamp(slot[BlockRecord.ScaleFactors + sfb], 0, 255)];
                for (int k = _longOffsets[sfb]; k < _longOffsets[sfb + 1]; k++)
                {
                    spectrum[k] = Dequantized(values[k], gain);
                }
            }

            return;
        }

        int groups = slot[BlockRecord.Groups];
        int lengths = slot[BlockRecord.GroupLengths];
        int window = 0;
        int position = 0;
        for (int g = 0; g < groups; g++)
        {
            int length = (lengths >> (4 * g)) & 15;
            for (int sfb = 0; sfb < maxSfb; sfb++)
            {
                int width = _shortOffsets[sfb + 1] - _shortOffsets[sfb];
                int book = slot[BlockRecord.Codebooks + (g * 64) + sfb];
                if (book is >= 1 and <= 11)
                {
                    float gain = Tables.Gain[Math.Clamp(slot[BlockRecord.ScaleFactors + (g * 64) + sfb], 0, 255)];
                    for (int w = 0; w < length; w++)
                    {
                        int target = ((window + w) * ShortLines) + _shortOffsets[sfb];
                        int source = position + (w * width);
                        for (int k = 0; k < width; k++)
                        {
                            spectrum[target + k] = Dequantized(values[source + k], gain);
                        }
                    }
                }

                position += length * width;
            }

            window += length;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Dequantized(int value, float gain)
    {
        if (value == 0)
        {
            return 0;
        }

        float magnitude = Tables.Pow43[Math.Min(Math.Abs(value), Tables.MaxQuantized)] * gain;
        return value < 0 ? -magnitude : magnitude;
    }

    // Mid/side stereo (4.6.8.1) where the pair's common window sets it, in the order of the tools of Figure 4.2 (4.1.1.2): mid/side first, then noise substitution
    // (NoiseSubstitution), then intensity stereo (IntensityStereo). Mid/side needs the common window, so both channels share their layout; it skips a band where either channel codes noise
    // (4.6.8.1.3, 4.6.13.3: the shared noise of a band that both code is NoiseSubstitution's) and a band of intensity stereo in the second channel (4.6.8.1.3).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void MidSide(ReadOnlySpan<int> first, ReadOnlySpan<int> second, int ch)
    {
        int mask = first[BlockRecord.MsMask];
        if (!IsMidSide(first, second))
        {
            return;
        }

        float[] left = _spectrum[ch];
        float[] right = _spectrum[ch + 1];
        bool isShort = second[BlockRecord.WindowSequence] == 2;
        int groups = second[BlockRecord.Groups];
        int lengths = second[BlockRecord.GroupLengths];
        ushort[] offsets = isShort ? _shortOffsets : _longOffsets;
        int bands = isShort ? _shortBands : _longBands;
        int maxSfb = Math.Min(first[BlockRecord.MaxSfb], bands);
        int rightMaxSfb = Math.Min(second[BlockRecord.MaxSfb], bands);
        int window = 0;
        for (int g = 0; g < groups; g++)
        {
            int length = isShort ? (lengths >> (4 * g)) & 15 : 1;
            for (int sfb = 0; sfb < maxSfb; sfb++)
            {
                int band = (g * 64) + sfb;
                int rightBook = sfb < rightMaxSfb ? second[BlockRecord.Codebooks + band] : 0;
                if ((mask == 2 || first[BlockRecord.MidSide + band] != 0) && first[BlockRecord.Codebooks + band] != 13 && rightBook < 13)
                {
                    for (int w = 0; w < length; w++)
                    {
                        int at = ((window + w) * (isShort ? ShortLines : 0)) + offsets[sfb];
                        for (int k = 0; k < offsets[sfb + 1] - offsets[sfb]; k++)
                        {
                            float l = left[at + k];
                            float r = right[at + k];
                            left[at + k] = l + r;
                            right[at + k] = l - r;
                        }
                    }
                }
            }

            window += length;
        }
    }

    private static bool IsMidSide(ReadOnlySpan<int> first, ReadOnlySpan<int> second) => first[BlockRecord.MsMask] is 1 or 2
        && first[BlockRecord.WindowSequence] == second[BlockRecord.WindowSequence] && first[BlockRecord.Groups] == second[BlockRecord.Groups]
        && first[BlockRecord.GroupLengths] == second[BlockRecord.GroupLengths];

    // Intensity stereo (4.6.8.2) from the first channel, noise included, into the bands of the second that code it, as libxaac does: it follows the second channel's own layout (its
    // window sequence, groups and bands) over the first channel's spectrum at the same lines, whatever the first channel's window is (ixheaacd_intensity_stereo_process). The
    // sign inverts where the band's mid/side flag is set and the mask is 1 (4.6.8.2.3: invert_intensity is 1 - 2 * ms_used only for ms_mask_present 1, else 1).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void IntensityStereo(ReadOnlySpan<int> first, ReadOnlySpan<int> second, int ch)
    {
        float[] left = _spectrum[ch];
        float[] right = _spectrum[ch + 1];
        bool isFlagged = first[BlockRecord.MsMask] == 1 && IsMidSide(first, second);
        bool isShort = second[BlockRecord.WindowSequence] == 2;
        int groups = second[BlockRecord.Groups];
        int lengths = second[BlockRecord.GroupLengths];
        ushort[] offsets = isShort ? _shortOffsets : _longOffsets;
        int bands = isShort ? _shortBands : _longBands;
        int maxSfb = Math.Min(first[BlockRecord.MaxSfb], bands);
        int rightMaxSfb = Math.Min(second[BlockRecord.MaxSfb], bands);
        int window = 0;
        for (int g = 0; g < groups; g++)
        {
            int length = isShort ? (lengths >> (4 * g)) & 15 : 1;
            for (int sfb = 0; sfb < rightMaxSfb; sfb++)
            {
                int band = (g * 64) + sfb;
                int rightBook = second[BlockRecord.Codebooks + band];
                if (rightBook >= 14)
                {
                    float scale = Tables.IntensityGain[Math.Clamp(second[BlockRecord.ScaleFactors + band], -255, 255) + 255];
                    bool isInverted = isFlagged && sfb < maxSfb && first[BlockRecord.MidSide + band] != 0;
                    if (isInverted == ((rightBook & 1) != 0))
                    {
                        scale = -scale;
                    }

                    for (int w = 0; w < length; w++)
                    {
                        int at = ((window + w) * (isShort ? ShortLines : 0)) + offsets[sfb];
                        for (int k = 0; k < offsets[sfb + 1] - offsets[sfb]; k++)
                        {
                            right[at + k] = left[at + k] * scale;
                        }
                    }
                }
            }

            window += length;
        }
    }

    // Noise substitution (4.6.13) after mid/side stereo and before intensity stereo (Figure 4.2): each band of codebook 13 takes random values of the energy its noise energy gives. The generator is the
    // linear congruential one of ixheaacd_gen_rand_vec (seed 0 at the start of the stream and after a gap, one for all channels); a band that mid/side stereo flags in a pair gets the
    // same noise in both channels, the second channel starting from the seed the first started that band with. libxaac keeps that seed for each band and not for each window of a
    // group, which gives the second channel another noise than the first in eight short windows grouped; here each window of the group has its own.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void NoiseSubstitution(int ch, ReadOnlySpan<int> slot, ReadOnlySpan<int> first)
    {
        int groups = slot[BlockRecord.Groups];
        int maxSfb = slot[BlockRecord.MaxSfb];
        bool isShort = slot[BlockRecord.WindowSequence] == 2;
        ushort[] offsets = isShort ? _shortOffsets : _longOffsets;
        maxSfb = Math.Min(maxSfb, isShort ? _shortBands : _longBands);
        int lengths = slot[BlockRecord.GroupLengths];
        float[] spectrum = _spectrum[ch];
        bool isPair = slot[BlockRecord.Kind] is BlockRecord.KindPairFirst or BlockRecord.KindPairSecond;
        int mask = slot[BlockRecord.MsMask];
        int window = 0;
        for (int g = 0; g < groups; g++)
        {
            int length = isShort ? (lengths >> (4 * g)) & 15 : 1;
            for (int w = 0; w < length; w++)
            {
                for (int sfb = 0; sfb < maxSfb; sfb++)
                {
                    int band = (g * 64) + sfb;
                    if (slot[BlockRecord.Codebooks + band] != 13)
                    {
                        continue;
                    }

                    bool isCorrelated = isPair && mask is 1 or 2 && (mask == 2 || (first.IsEmpty ? slot[BlockRecord.MidSide + band] : first[BlockRecord.MidSide + band]) != 0);
                    int index = ((window + w) * 64) + sfb;
                    bool isSecond = slot[BlockRecord.Kind] == BlockRecord.KindPairSecond;
                    bool isShared = isCorrelated && isSecond && _hasNoiseSeed[index];
                    int seed;
                    if (isShared)
                    {
                        seed = _noiseSeeds[index];
                    }
                    else
                    {
                        seed = _seed;
                        if (isCorrelated && !isSecond)
                        {
                            _noiseSeeds[index] = seed;
                            _hasNoiseSeed[index] = true;
                        }
                    }

                    int energy = Math.Clamp(slot[BlockRecord.ScaleFactors + band], -100, 155);
                    int at = ((window + w) * (isShort ? ShortLines : 0)) + offsets[sfb];
                    Noise(spectrum.AsSpan(at, offsets[sfb + 1] - offsets[sfb]), ref seed, Tables.NoiseGain[energy + 100]);
                    if (!isShared)
                    {
                        _seed = seed;
                    }
                }
            }

            window += length;
        }
    }

    // ixheaacd_gen_rand_vec: seed = 1664525 * seed + 1013904223, each value the seed shifted right by 3, scaled to the energy 'gain' squared.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Noise(Span<float> band, ref int seed, float gain)
    {
        double energy = 0;
        for (int k = 0; k < band.Length; k++)
        {
            seed = unchecked((1664525 * seed) + 1013904223);
            int value = seed >> 3;
            band[k] = value;
            energy += (double)value * value;
        }

        if (energy <= 0)
        {
            return;
        }

        float scale = (float)(gain / Math.Sqrt(energy));
        for (int k = 0; k < band.Length; k++)
        {
            band[k] *= scale;
        }
    }

    // Temporal noise shaping (4.6.9.3): per window the filters of the stored list, each an all-pole filter over the lines of the bands it covers, as libxaac's
    // ixheaacd_aac_tns_process takes the ranges: the filters of a window chain downward from the window's last band, clamped to the most bands TNS covers at the sample rate and to
    // max_sfb.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void TemporalNoiseShaping(int ch, ReadOnlySpan<int> slot)
    {
        if (_isSilent[ch])
        {
            return;
        }

        bool isShort = slot[BlockRecord.WindowSequence] == 2;
        ushort[] offsets = isShort ? _shortOffsets : _longOffsets;
        int bands = isShort ? _shortBands : _longBands;
        int maxBands = Tables.TnsMaxBands[Math.Min(_rateIndex, Tables.TnsMaxBands.Length - 1)][isShort ? 1 : 0];
        int maxSfb = Math.Min(slot[BlockRecord.MaxSfb], bands);
        float[] spectrum = _spectrum[ch];
        int filters = Math.Min(slot[BlockRecord.TnsFilters], BlockRecord.TnsMaxFilters);
        int lastWindow = -1;
        int top = bands;
        for (int f = 0; f < filters; f++)
        {
            int at = BlockRecord.Tns + (f * BlockRecord.TnsFilterInts);
            int window = slot[at];
            if (window != lastWindow)
            {
                lastWindow = window;
                top = bands;
            }

            int length = slot[at + 1];
            int order = Math.Min(slot[at + 2], MaxTnsOrder);
            int bottom = Math.Max(top - length, 0);
            int stop = top;
            top = bottom;
            if (order == 0)
            {
                continue;
            }

            bool downward = slot[at + 3] != 0;
            int resolution = slot[at + 4];
            int compress = slot[at + 5];
            int coefficientBits = resolution - compress;
            float[] table = Tables.Tns[resolution - 3];
            int levels = 1 << (resolution - 1);
            for (int i = 0; i < order; i++)
            {
                int coded = slot[at + 6 + i];
                int signed = coded >= 1 << (coefficientBits - 1) ? coded - (1 << coefficientBits) : coded;
                _parcor[i + 1] = table[signed + levels];
            }

            int start = offsets[Math.Min(Math.Min(bottom, maxBands), maxSfb)];
            int end = offsets[Math.Min(Math.Min(stop, maxBands), maxSfb)];
            if (end - start <= 0)
            {
                continue;
            }

            // The parcor coefficients to the filter's: a(m, i) = a(m - 1, i) + k(m) a(m - 1, m - i) (4.6.9.3).
            _lpc[0] = 1;
            for (int m = 1; m <= order; m++)
            {
                float k = _parcor[m];
                for (int i = 1; i < m; i++)
                {
                    _history[i] = _lpc[i] + (k * _lpc[m - i]);
                }

                for (int i = 1; i < m; i++)
                {
                    _lpc[i] = _history[i];
                }

                _lpc[m] = k;
            }

            Array.Clear(_history);
            int baseLine = isShort ? window * ShortLines : 0;
            int size = end - start;
            for (int n = 0; n < size; n++)
            {
                int line = baseLine + (downward ? end - 1 - n : start + n);
                float y = spectrum[line];
                int past = Math.Min(n, order);
                for (int j = 1; j <= past; j++)
                {
                    y -= _lpc[j] * _history[j - 1];
                }

                for (int j = order - 1; j > 0; j--)
                {
                    _history[j] = _history[j - 1];
                }

                // A filter near its poles on crafted values can reach infinity, and an infinity minus an infinity in the transform is a NaN whose sign depends on the platform: the values
                // saturate, as libxaac's do (a spectrum of 10^30 sums to 10^33 over 1,024 lines).
                y = Math.Clamp(y, -SpectrumLimit, SpectrumLimit);
                _history[0] = y;
                spectrum[line] = y;
            }
        }
    }

    // The filter bank (4.6.11): the inverse MDCT of the window or windows, the window shape of the sequence, and the overlap-add with the previous window's second half, into the
    // channel's samples; the second half of this window's output is kept.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Synthesize(int ch, ReadOnlySpan<int> slot)
    {
        Span<float> output = _planar.AsSpan(ch * Lines, Lines);
        float[] overlap = _overlap[ch];
        int sequence = slot[BlockRecord.WindowSequence];
        int shape = slot[BlockRecord.WindowShape] & 1;
        int previous = _previousShape[ch] == FirstShape ? shape : _previousShape[ch];
        _previousShape[ch] = shape;
        if (_isSilent[ch])
        {
            overlap.AsSpan(0, Lines).CopyTo(output);
            Array.Clear(overlap);
            return;
        }

        Span<float> block = _window;
        if (sequence != 2)
        {
            _longTransform.Transform(_spectrum[ch], _raw);
            float[] rise = Tables.LongRise[previous];
            float[] fall = Tables.LongRise[shape];
            for (int n = 0; n < Lines; n++)
            {
                block[n] = _raw[n] * rise[n];
            }

            if (sequence == 3)
            {
                // Long stop: the left half is zero for 448 samples, then the short rise of the previous shape, then one.
                float[] shortRise = Tables.ShortRise[previous];
                for (int n = 0; n < 448; n++)
                {
                    block[n] = 0;
                }

                for (int n = 0; n < ShortLines; n++)
                {
                    block[448 + n] = _raw[448 + n] * shortRise[n];
                }

                for (int n = 448 + ShortLines; n < Lines; n++)
                {
                    block[n] = _raw[n];
                }
            }

            if (sequence == 1)
            {
                // Long start: one for 448 samples, then the short fall, then zero.
                float[] shortFall = Tables.ShortRise[shape];
                for (int n = 0; n < 448; n++)
                {
                    block[Lines + n] = _raw[Lines + n];
                }

                for (int n = 0; n < ShortLines; n++)
                {
                    block[Lines + 448 + n] = _raw[Lines + 448 + n] * shortFall[ShortLines - 1 - n];
                }

                for (int n = 448 + ShortLines; n < Lines; n++)
                {
                    block[Lines + n] = 0;
                }
            }
            else
            {
                for (int n = 0; n < Lines; n++)
                {
                    block[Lines + n] = _raw[Lines + n] * fall[Lines - 1 - n];
                }
            }
        }
        else
        {
            block.Clear();
            float[] currentRise = Tables.ShortRise[shape];
            for (int w = 0; w < 8; w++)
            {
                _shortTransform.Transform(_spectrum[ch].AsSpan(w * ShortLines, ShortLines), _partial);
                float[] rise = w == 0 ? Tables.ShortRise[previous] : currentRise;
                int at = 448 + (w * ShortLines);
                for (int n = 0; n < ShortLines; n++)
                {
                    block[at + n] += _partial[n] * rise[n];
                    block[at + ShortLines + n] += _partial[ShortLines + n] * currentRise[ShortLines - 1 - n];
                }
            }
        }

        for (int n = 0; n < Lines; n++)
        {
            output[n] = overlap[n] + block[n];
            overlap[n] = block[Lines + n];
        }
    }
}
