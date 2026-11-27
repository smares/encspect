using System.Numerics;
using Encspect.Core;

namespace Encspect.Formats.Aac;

// Reads a raw_data_block (ISO/IEC 14496-3 4.4.2.1) element by element, the channel
// elements' individual_channel_streams included: ics_info, section_data, scale_factor_data with its
// Huffman-coded differences, pulse_data, tns_data, gain_control_data and spectral_data decoded
// through codebooks 1 to 11 with their escapes, without dequantizing; and the fill elements'
// extension payloads, SBR data up to its extensions. One pass reads a stream's blocks in order and
// counts their coding (CodingTally), noting what a skeleton leaves out (Cuts) and where each
// element lies (Elements, for the ADTS CRC); another builds a block's nodes, from its bytes or from
// its skeleton. A reader serves one stream on one thread; its fields are scratch space for one block
// at a time, and the SBR headers it keeps are the stream's.
internal sealed partial class BlockReader
{
    private const int MaxGroups = 8;
    private const int MaxBands = 64;

    private readonly StreamSetup _setup;
    private readonly CodingTally? _tally;
    private readonly CodingRecorder? _recorder;
    private readonly Dictionary<int, SbrHeader> _sbr = [];
    private readonly Dictionary<int, int> _sbrHeaderBlock = [];
    private readonly Dictionary<int, int[]> _psState = [];
    private readonly List<BlockCut> _cuts = [];
    private readonly List<ElementSpan> _elements = [];
    private readonly List<SoftFinding> _soft = [];
    private readonly Ics[] _ics = [new(), new()];
    private readonly bool[] _midSide = new bool[MaxGroups * MaxBands];

    private RecordStore? _store;
    private bool _nodes;
    private UnitBytes _where;
    private int _anchor;
    private int _channel;

    // The window_sequence of each channel stream (by its number in its block) in the block read before, or -1; whether this block has been flagged for a transition already.
    private readonly int[] _previousSequence = Enumerable.Repeat(-1, BlockRecord.MaxSlots).ToArray();
    private bool _windowFlagged;
    private int _block;
    private int _lastChannelElement;
    private int _lastChannelTag;
    private int _elementCount;
    private int _elementIds;
    private bool _hasSbr;
    private bool _hasSbrEvidence;
    private bool _hasPs;
    private bool _hasCoupling;

    public BlockReader(StreamSetup setup, CodingTally? tally, CodingRecorder? recorder = null)
    {
        _setup = setup;
        _tally = tally;
        _recorder = recorder;
    }

    // What the last block's skeleton leaves out, in order.
    public IReadOnlyList<BlockCut> Cuts => _cuts;

    // The last block's elements, in order.
    public IReadOnlyList<ElementSpan> Elements => _elements;

    // What the last block breaks that does not stop its reading.
    public IReadOnlyList<SoftFinding> SoftFindings => _soft;

    // Reads the block in data from bit start, within limit, counting its coding; blockIndex is the
    // access unit's index in the stream. where places nodes the soft findings point to.
    // With store, the block's values are stored in it as well (BlockRecord): the walk then decodes the spectral values, which it otherwise only steps over.
    public BlockResult Read(ReadOnlySpan<byte> data, int start, int limit, int blockIndex, UnitBytes where, RecordStore? store = null)
    {
        _store = store;
        store?.Reset();
        _nodes = false;
        _where = where;
        _block = blockIndex;
        var bits = new AacBits(data, start, limit);
        return Run(ref bits, null);
    }

    // The block's nodes, read from its bytes; error is what stopped the reading, with its node.
    public List<Node> Nodes(ReadOnlySpan<byte> data, int start, int limit, UnitBytes where, out BlockError? error)
    {
        _store = null;
        _nodes = true;
        _where = where;
        var bits = new AacBits(data, start, limit);
        var root = new Node("Scratch", 0, 0);
        BlockResult result = Run(ref bits, root);
        error = result.Error;
        return [.. root.Children];
    }

    // The block's nodes, read again from what the store kept of it; where is the block's bytes.
    public List<Node> Nodes(UnitStore.Entry entry, UnitBytes where)
    {
        _store = null;
        _nodes = true;
        _where = where;
        var root = new Node("Scratch", 0, 0);
        var bits = entry.IsWhole ? new AacBits(entry.Data.Span, 0, entry.Bits) : new AacBits(entry.Data.Span, entry.Bits, entry.Cuts.Span, 0);
        Run(ref bits, root);
        return [.. root.Children];
    }

    private BlockResult Run(ref AacBits bits, Node? root)
    {
        _cuts.Clear();
        _elements.Clear();
        _soft.Clear();
        _anchor = bits.Original;
        _channel = 0;
        _windowFlagged = false;
        _lastChannelElement = -1;
        _lastChannelTag = 0;
        _elementCount = 0;
        _elementIds = 0;
        _hasSbr = false;
        _hasSbrEvidence = false;
        _hasPs = false;
        _hasCoupling = false;
        int sce = 0;
        int cpe = 0;
        int lfe = 0;
        try
        {
            while (true)
            {
                int start = bits.Original;
                int id = (int)bits.Read(3);
                if (id == 7)
                {
                    int alignStart = bits.Original;
                    bits.Align(_anchor);
                    if (root is not null)
                    {
                        Node end = Close(root, Scratch()!, "END", start, alignStart, "end of the raw data block", Explain.EndElement, Topics.RawDataBlocks);
                        end.Add(_where.Bits("Element ID", start, start + 3, "7 (END)", Explain.ElementId));
                        if (bits.Original > alignStart)
                        {
                            root.Add(_where.Bits("Byte Alignment", alignStart, bits.Original, NodeValue.Integer(bits.Original - alignStart, "bits"), Explain.ByteAlignment));
                        }
                    }

                    _elements.Add(new ElementSpan(7, start, bits.Original, -1));
                    break;
                }

                _elementCount++;
                if (_elementCount <= 8)
                {
                    _elementIds |= id << (3 * (_elementCount - 1));
                }

                switch (id)
                {
                    case 0 or 3:
                        SingleChannel(ref bits, root, id, start);
                        if (id == 0)
                        {
                            sce++;
                        }
                        else
                        {
                            lfe++;
                        }

                        break;
                    case 1:
                        ChannelPair(ref bits, root, start);
                        cpe++;
                        break;
                    case 2:
                        Coupling(ref bits, root, start);
                        break;
                    case 4:
                        DataStream(ref bits, root, start);
                        break;
                    case 5:
                        ProgramConfig(ref bits, root, start);
                        break;
                    default:
                        Fill(ref bits, root, start);
                        break;
                }

                if (_tally is not null)
                {
                    _tally.Elements[id]++;
                }
            }
        }
        catch (BlockError error)
        {
            error.Node ??= root?.Children.Count > 0 ? root.Children[^1] : null;
            CountDamaged();
            return new BlockResult(false, bits.Original, Packed(), _hasSbr, _hasPs, error);
        }
        catch (BlockOverrun)
        {
            // Every element catches its own overrun, so this one is the next element ID's: the block holds no END element.
            var error = new BlockError("aac.element-past-end", "ends before its END element", null, toEnd: true) { Element = "raw data block" };
            CountDamaged();
            return new BlockResult(false, bits.Original, Packed(), _hasSbr, _hasPs, error);
        }

        if (_tally is not null)
        {
            _tally.Blocks++;
            _recorder?.Block(_hasSbr, _hasPs);
            if (_setup.ExpectedSce >= 0 && (sce != _setup.ExpectedSce || cpe != _setup.ExpectedCpe || lfe != _setup.ExpectedLfe))
            {
                _tally.Mismatched++;
                Soft("aac.elements-mismatch", $"{Count(sce, "single channel element")}, {Count(cpe, "channel pair element")} and {Count(lfe, "LFE element")}, other than channel configuration {_setup.ChannelConfiguration} defines",
                    0, 0, null);
            }

            if (_hasSbr)
            {
                _tally.SbrBlocks++;
                if (_tally.FirstSbrBlock < 0)
                {
                    _tally.FirstSbrBlock = _block;
                }
            }

            if (_hasPs)
            {
                _tally.PsBlocks++;
                if (_tally.FirstPsBlock < 0)
                {
                    _tally.FirstPsBlock = _block;
                }
            }
        }

        if (_store is not null)
        {
            _store.Flags = (_hasSbr ? BlockRecord.BlockHasSbr : 0) | (_hasPs ? BlockRecord.BlockHasPs : 0) | (_hasCoupling ? BlockRecord.BlockHasCoupling : 0) | (_hasSbrEvidence ? BlockRecord.BlockHasSbrEvidence : 0);
        }

        return new BlockResult(true, bits.Original, Packed(), _hasSbr, _hasPs, null, _hasSbrEvidence);
    }

    private void CountDamaged()
    {
        if (_tally is not null)
        {
            _tally.Blocks++;
            _tally.DamagedBlocks++;
            _recorder?.Block(false, false);
        }
    }

    private int Packed() => Math.Min(_elementCount, 15) << 24 | (_elementIds & 0xFFFFFF);

    private static string Count(int count, string what) => count == 1 ? $"1 {what}" : $"{count} {what}s";

    // single_channel_element and lfe_channel_element (ISO/IEC 14496-3 4.4.2): an instance tag
    // and an individual_channel_stream.
    private void SingleChannel(ref AacBits bits, Node? parent, int id, int start)
    {
        Node? n = Scratch();
        try
        {
            Head(n, id, start);
            int tag = (int)bits.Read(4);
            Int(n, "Element Instance Tag", start + 3, start + 7, tag, Explain.ElementInstanceTag);
            _lastChannelElement = id;
            _lastChannelTag = tag;
            _ics[0].Slot = _store?.Begin(id == 0 ? BlockRecord.KindSingle : BlockRecord.KindLfe, tag) ?? -1;
            ChannelStream(ref bits, n, _ics[0], false, 0, "Channel", start + 7);
            _elements.Add(new ElementSpan(id, start, bits.Original, -1));
            if (parent is not null)
            {
                Close(parent, n!, AacTables.ElementName(id), start, bits.Original, $"{AacTables.ElementDescription(id)}, tag {tag}", Explain.ChannelElement, Topics.RawDataBlocks);
            }
        }
        catch (Exception e) when (n is not null && e is BlockError or BlockOverrun)
        {
            throw Fail(e, parent, n, AacTables.ElementName(id), start, bits, AacTables.ElementDescription(id), Explain.ChannelElement);
        }

        if (_tally is not null)
        {
            _channel++;
        }
    }

    // channel_pair_element (ISO/IEC 14496-3 4.4.2): an instance tag, common_window, a shared ics_info
    // and the mid/side mask where the window is common, then both individual_channel_streams.
    private void ChannelPair(ref AacBits bits, Node? parent, int start)
    {
        Node? n = Scratch();
        try
        {
            Head(n, 1, start);
            int tag = (int)bits.Read(4);
            Int(n, "Element Instance Tag", start + 3, start + 7, tag, Explain.ElementInstanceTag);
            _lastChannelElement = 1;
            _lastChannelTag = tag;
            bool common = bits.ReadBit();
            Field(n, "HasCommonWindow", start + 7, start + 8, NodeValue.Boolean(common), Explain.HasCommonWindow, Topics.Windows);
            int mask = 0;
            int msStart = -1;
            int msEnd = -1;
            Ics first = _ics[0];
            Ics second = _ics[1];
            second.Ltp = false;
            first.Slot = _store?.Begin(BlockRecord.KindPairFirst, tag) ?? -1;
            second.Slot = _store?.Begin(BlockRecord.KindPairSecond, tag) ?? -1;
            if (common)
            {
                IcsInfo(ref bits, n, first, true);
                second.CopyWindow(first);
                StoreWindow(second);
                int maskStart = bits.Original;
                mask = (int)bits.Read(2);
                Field(n, "Mid/Side Mask Present", maskStart, maskStart + 2, MaskText(mask), Explain.MsMaskPresent, Topics.MidSide);
                msStart = bits.Original;
                Array.Clear(_midSide, 0, first.Groups * MaxBands);
                if (mask == 1)
                {
                    for (int g = 0; g < first.Groups; g++)
                    {
                        for (int sfb = 0; sfb < first.MaxSfb; sfb++)
                        {
                            _midSide[(g * MaxBands) + sfb] = bits.ReadBit();
                        }
                    }
                }
                else if (mask == 2)
                {
                    for (int g = 0; g < first.Groups; g++)
                    {
                        Array.Fill(_midSide, true, g * MaxBands, first.MaxSfb);
                    }
                }
                else if (mask == 3)
                {
                    Soft("aac.ms-mask-reserved", "ms_mask_present 3, which the specification reserves", maskStart, maskStart + 2, "Mid/Side Mask Present");
                }

                msEnd = bits.Original;
                StoreMidSide(first, second, mask);
                if (n is not null && mask == 1)
                {
                    n.Add(_where.Bits("Mid/Side Bands", msStart, msEnd, BandList(first, (g, sfb) => _midSide[(g * MaxBands) + sfb]), Explain.MidSideBands, Topics.MidSide));
                }
            }

            ChannelStream(ref bits, n, first, common, 0, "Channel 0", bits.Original);
            int secondStart = bits.Original;
            ChannelStream(ref bits, n, second, common, 1, "Channel 1", secondStart);
            if (!common && HasBand(second, b => b is 14 or 15))
            {
                // fdk-aac applies intensity stereo only with a common window (channel.cpp) and its stereo.cpp quotes 4.6.8.2.3 as allowing the
                // codebooks only there; FFmpeg and faad2 apply it anyway, and so is it counted.
                Soft("aac.intensity-without-common-window", "intensity stereo bands in a channel pair without a common window, outside what ISO/IEC 14496-3 4.6.8.2.3 allows as fdk-aac's source quotes it, though FFmpeg and faad2 decode them",
                    second.SectionStart, second.SectionEnd, "Intensity Stereo Bands");
            }

            if (n is not null)
            {
                IntensityNode(n, second, common && mask == 1);
            }

            _elements.Add(new ElementSpan(1, start, bits.Original, secondStart));
            if (_tally is not null)
            {
                TallyPair(first, second, common, mask);
                _channel += 2;
            }

            if (parent is not null)
            {
                Close(parent, n!, "CPE", start, bits.Original, $"channel pair element, tag {tag}", Explain.ChannelElement, Topics.RawDataBlocks);
            }
        }
        catch (Exception e) when (n is not null && e is BlockError or BlockOverrun)
        {
            throw Fail(e, parent, n, "CPE", start, bits, "channel pair element", Explain.ChannelElement);
        }
    }

    // The pair's stereo coding (ISO/IEC 14496-3 4.6.8), whether or not it has a common window: intensity stereo is in any band of the
    // second channel with codebook 14 or 15, and mid/side stereo in any band whose ms_used flag is set (ms_mask_present 2 sets every
    // band, and the flags need a common window) where neither channel codes noise substitution (codebook 13) or intensity stereo:
    // on such a band ms_used says something else, and decoders apply no mid/side there (FFmpeg 6.1.1 apply_mid_side_stereo requires
    // both band types below NOISE_BT; faad2's ms_decode checks the first channel's noise and the second's intensity stereo, and
    // fdk-aac's MapMidSideMaskToPnsCorrelation skips only bands where both channels code noise, so they differ on a band where only
    // one channel does, and FFmpeg's rule is followed). ms_mask_present 3 is reserved
    // and takes precedence.
    private void TallyPair(Ics first, Ics second, bool common, int mask)
    {
        CodingTally tally = _tally!;
        tally.Pairs++;
        if (common)
        {
            tally.CommonWindow++;
            tally.MidSideMasks[mask]++;
        }

        bool intensity = false;
        bool midSide = false;
        int midSideBands = 0;
        int pairBands = 0;
        for (int g = 0; g < second.Groups; g++)
        {
            for (int sfb = 0; sfb < second.MaxSfb; sfb++)
            {
                int band = (g * MaxBands) + sfb;
                int book = second.Codebooks[band];
                bool flagged = common && mask != 0 && mask != 3 && _midSide[band];
                tally.ChannelBands++;
                tally.PairBands += common ? 1 : 0;
                pairBands += common ? 1 : 0;
                if (book is 14 or 15)
                {
                    intensity = true;
                    tally.IntensityBands++;
                    _recorder?.Intensity(second.WindowSequence == 2, second.Offsets[sfb]);
                    // 4.6.8.2.3: INTENSITY_HCB2 (14) codes out of phase; ms_used inverts the sign, with ms_mask_present 1 only: faad2 and
                    // fdk-aac (stereo.cpp clears the mid/side flags before intensity stereo) leave mask 2 alone, FFmpeg inverts for it too.
                    if ((book == 14) != (flagged && mask == 1))
                    {
                        tally.IntensityOutOfPhase++;
                    }
                }
                else if (flagged && book != 13 && first.Codebooks[band] < 13)
                {
                    midSide = true;
                    tally.MidSideBands++;
                    midSideBands++;
                }
            }
        }

        _recorder?.PairBands(midSideBands, pairBands);
        tally.IntensityPairs += intensity ? 1 : 0;
        byte code = mask == 3 && common ? CodingCategories.CodeReserved
            : intensity ? (midSide ? CodingCategories.CodeMidSideAndIntensity : CodingCategories.CodeIntensity)
            : midSide ? CodingCategories.CodeMidSide : CodingCategories.CodeLeftRight;
        tally.StereoCodings[code]++;
        _recorder?.Pair(code);
    }

    // individual_channel_stream (ISO/IEC 14496-3 4.4.2): global_gain, ics_info unless the pair shares
    // one, section_data, scale_factor_data, pulse, TNS and gain control data, spectral_data.
    private void ChannelStream(ref AacBits bits, Node? parent, Ics ics, bool common, int channel, string name, int start)
    {
        Node? n = Scratch();
        int gain = 0;
        try
        {
            gain = (int)bits.Read(8);
            Int(n, "Global Gain", start, start + 8, gain, Explain.GlobalGain);
            Put(ics, BlockRecord.GlobalGain, gain);
            Put(ics, BlockRecord.Flags, common ? BlockRecord.FlagCommonWindow : 0);
            if (!common)
            {
                IcsInfo(ref bits, n, ics, false);
            }

            SectionData(ref bits, n, ics);
            ScaleFactors(ref bits, n, ics, gain);
            if (n is not null)
            {
                BandsNode(n, ics, 13, "Noise Substitution Bands", Explain.NoiseBands, Topics.Pns);
            }

            int flagStart = bits.Original;
            bool pulse = bits.ReadBit();
            Field(n, "HasPulseData", flagStart, flagStart + 1, NodeValue.Boolean(pulse), Explain.HasPulseData, Topics.PulseData);
            Or(ics, BlockRecord.Flags, pulse ? BlockRecord.FlagPulse : 0);
            if (pulse)
            {
                PulseData(ref bits, n, ics, flagStart);
            }

            flagStart = bits.Original;
            bool tns = bits.ReadBit();
            Field(n, "HasTnsData", flagStart, flagStart + 1, NodeValue.Boolean(tns), Explain.HasTnsData, Topics.Tns);
            Or(ics, BlockRecord.Flags, tns ? BlockRecord.FlagTns : 0);
            int filters = 0;
            if (tns)
            {
                filters = TnsData(ref bits, n, ics);
            }

            flagStart = bits.Original;
            bool gainControl = bits.ReadBit();
            Field(n, "HasGainControlData", flagStart, flagStart + 1, NodeValue.Boolean(gainControl), Explain.HasGainControlData);
            if (gainControl)
            {
                if (_setup.ObjectType != 3)
                {
                    Soft("aac.gain-control-not-allowed", $"gain control data, which only AAC SSR defines and which decoders reject in {AacTables.ObjectTypeName(_setup.ObjectType)}",
                        flagStart, flagStart + 1, "HasGainControlData");
                }

                GainControl(ref bits, n, ics);
            }

            Spectral(ref bits, n, ics);
            if (_tally is not null && _lastChannelElement != 2)
            {
                TallyStream(ics, gain, pulse, tns, filters, gainControl, channel);
            }

            if (parent is not null)
            {
                Close(parent, n!, name, start, bits.Original, $"global gain {gain}", Explain.ChannelStream, Topics.Windows, NodeKind.SideInfo);
            }
        }
        catch (Exception e) when (n is not null && e is BlockError or BlockOverrun)
        {
            throw Fail(e, parent, n, name, start, bits, $"global gain {gain}", Explain.ChannelStream, NodeKind.SideInfo);
        }
    }

    // The window sequences of the blocks read after this call follow no block read before it.
    internal void ForgetWindows() => Array.Fill(_previousSequence, -1);

    // Whether a window of sequence next follows one of sequence previous as 4.6.11.3.1 lists the meaningful transitions: the right slope of the one (long for only long and long stop,
    // short for long start and eight short) is the left slope of the other (long for only long and long start, short for eight short and long stop). The other pairs are possible there too.
    internal static bool SlopesMatch(int previous, int next) => (previous is 0 or 3) == (next is 0 or 1);

    private void TallyStream(Ics ics, int gain, bool pulse, bool tns, int filters, bool gainControl, int channel)
    {
        CodingTally tally = _tally!;
        int stream = _channel + channel;
        if (stream < _previousSequence.Length)
        {
            int previous = _previousSequence[stream];
            _previousSequence[stream] = ics.WindowSequence;
            if (previous >= 0 && !SlopesMatch(previous, ics.WindowSequence) && !_windowFlagged)
            {
                _windowFlagged = true;
                Soft("aac.window-slopes-differ", "a window sequence whose left slope differs from the right slope of the window before it in the same channel stream, as a long window followed by eight short ones does: ISO/IEC 14496-3 4.6.11.3.1 lists such transitions as possible and not as meaningful",
                    ics.SequenceAt, ics.SequenceAt + 2, "Window Sequence");
            }
        }

        tally.ChannelStreams++;
        tally.WindowSequences[ics.WindowSequence]++;
        tally.WindowShapes[ics.WindowShape]++;
        if (tns)
        {
            tally.Tns[ics.WindowSequence]++;
            tally.TnsFilters += filters;
        }

        tally.Pulses += pulse ? 1 : 0;
        tally.GainControl += gainControl ? 1 : 0;
        if (ics.Predictor)
        {
            tally.Prediction++;
            tally.PredictorResets += ics.PredictorReset ? 1 : 0;
        }

        tally.LongTermPrediction += ics.Ltp ? 1 : 0;
        if (ics.WindowSequence == 2)
        {
            tally.MaxSfbShort[ics.MaxSfb]++;
        }
        else
        {
            tally.MaxSfbLong[ics.MaxSfb]++;
        }

        bool noise = false;
        for (int g = 0; g < ics.Groups; g++)
        {
            for (int sfb = 0; sfb < ics.MaxSfb; sfb++)
            {
                int book = ics.Codebooks[(g * MaxBands) + sfb];
                tally.Codebooks[book]++;
                tally.Bands++;
                if (book == 13)
                {
                    noise = true;
                    tally.NoiseBands++;
                }
            }
        }

        tally.NoiseStreams += noise ? 1 : 0;
        tally.NonZero += ics.NonZero;
        tally.Escapes += ics.Escapes;
        _recorder?.Stream(ics.WindowSequence, ics.Offsets[ics.MaxSfb], ics.LastLine, ics.NonZero, ics.Escapes);
        _recorder?.Tools(ics.WindowShape, tns, pulse, noise);
        tally.Gain(_channel + channel, gain);
    }

    // ics_info (ISO/IEC 14496-3 4.4.2): the window sequence and shape, max_sfb, the grouping of eight
    // short windows, and for long windows the predictor data of AAC Main or LTP.
    private void IcsInfo(ref AacBits bits, Node? parent, Ics ics, bool common)
    {
        Node? n = Scratch();
        int start = bits.Original;
        try
        {
            int reserved = (int)bits.Read(1);
            Int(n, "ICS Reserved Bit", start, start + 1, reserved, Explain.IcsReservedBit);
            if (reserved != 0)
            {
                Soft("aac.ics-reserved-bit", "an ics_reserved_bit of 1, where the specification requires 0", start, start + 1, "ICS Reserved Bit");
            }

            ics.WindowSequence = (int)bits.Read(2);
            ics.SequenceAt = start + 1;
            Field(n, "Window Sequence", start + 1, start + 3, n is null ? null : WindowSequenceText(ics.WindowSequence), Explain.WindowSequence, Topics.Windows);
            ics.WindowShape = (int)bits.Read(1);
            Field(n, "Window Shape", start + 3, start + 4, ics.WindowShape == 0 ? "0 (sine)" : "1 (KBD)", Explain.WindowShape, Topics.Windows);
            ics.Predictor = false;
            ics.PredictorReset = false;
            ics.Ltp = false;
            bool eight = ics.WindowSequence == 2;
            int sfbStart = bits.Original;
            ics.MaxSfb = (int)bits.Read(eight ? 4 : 6);
            ics.Bands = eight ? _setup.ShortBands : _setup.LongBands;
            ics.Offsets = eight ? _setup.ShortOffsets : _setup.LongOffsets;
            Node? maxSfbNode = n is null ? null : _where.Bits("Max SFB", sfbStart, bits.Original, NodeValue.Integer(ics.MaxSfb), Explain.MaxSfb, Topics.Windows);
            n?.Add(maxSfbNode!);
            if (ics.MaxSfb > ics.Bands)
            {
                throw new BlockError("aac.max-sfb-too-large", $"gives max_sfb {ics.MaxSfb}, more than the {ics.Bands} scalefactor bands of its window at {_setup.SampleRate} Hz", maxSfbNode);
            }

            ics.Groups = 1;
            ics.GroupLengths[0] = 1;
            if (eight)
            {
                int groupingStart = bits.Original;
                int grouping = (int)bits.Read(7);
                for (int i = 6; i >= 0; i--)
                {
                    if (((grouping >> i) & 1) != 0)
                    {
                        ics.GroupLengths[ics.Groups - 1]++;
                    }
                    else
                    {
                        ics.GroupLengths[ics.Groups++] = 1;
                    }
                }

                if (n is not null)
                {
                    n.Add(_where.Bits("Scale Factor Grouping", groupingStart, groupingStart + 7, $"0x{grouping:X2} ({GroupText(ics)})", Explain.ScaleFactorGrouping, Topics.Windows));
                }
            }
            else
            {
                int flag = bits.Original;
                ics.Predictor = bits.ReadBit();
                Field(n, "HasPredictorData", flag, flag + 1, NodeValue.Boolean(ics.Predictor), Explain.HasPredictorData);
                if (ics.Predictor)
                {
                    Predictor(ref bits, n, ics, common);
                }
            }

            StoreWindow(ics);
            if (parent is not null)
            {
                string summary = $"{WindowSequenceName(ics.WindowSequence)}, {(ics.WindowShape == 0 ? "sine" : "KBD")}, max_sfb {ics.MaxSfb}"
                    + (eight ? $", {ics.Groups} {(ics.Groups == 1 ? "group" : "groups")}" : string.Empty);
                Close(parent, n!, "ICS Info", start, bits.Original, summary, Explain.IcsInfo, Topics.Windows, NodeKind.SideInfo);
            }
        }
        catch (Exception e) when (n is not null && e is BlockError or BlockOverrun)
        {
            throw Fail(e, parent, n, "ICS Info", start, bits, null, Explain.IcsInfo, NodeKind.SideInfo);
        }
    }

    // The predictor data of ics_info: AAC Main's prediction (predictor_reset and prediction_used) or
    // AAC LTP's ltp_data, for the second channel of a pair with a common window too. AAC LC and SSR
    // have no predictor data.
    private void Predictor(ref AacBits bits, Node? n, Ics ics, bool common)
    {
        int type = _setup.ObjectType;
        if (type is 2 or 3)
        {
            int at = bits.Original - 1;
            throw new BlockError("aac.prediction-not-allowed", $"sets predictor_data_present, which {AacTables.ObjectTypeName(type)} does not define",
                n is null ? null : _where.Bits("HasPredictorData", at, at + 1, NodeValue.Boolean(true), Explain.HasPredictorData));
        }

        if (type == 1)
        {
            int start = bits.Original;
            ics.PredictorReset = bits.ReadBit();
            Field(n, "HasPredictorReset", start, start + 1, NodeValue.Boolean(ics.PredictorReset), Explain.HasPredictorReset);
            if (ics.PredictorReset)
            {
                int group = (int)bits.Read(5);
                Int(n, "Predictor Reset Group", start + 1, start + 6, group, Explain.PredictorResetGroup);
                if (group is 0 or > 30)
                {
                    Soft("aac.predictor-reset-group-invalid", $"predictor reset group {group}, outside 1 to 30", start + 1, start + 6, "Predictor Reset Group");
                }
            }

            int used = Math.Min(ics.MaxSfb, _setup.PredictionBands);
            int usedStart = bits.Original;
            ulong flags = 0;
            for (int sfb = 0; sfb < used; sfb++)
            {
                if (bits.ReadBit())
                {
                    flags |= 1UL << sfb;
                }
            }

            if (n is not null)
            {
                n.Add(_where.Bits("Prediction Used", usedStart, bits.Original, FlagList(flags, used), Explain.PredictionUsed));
            }

            return;
        }

        LtpData(ref bits, n, ics, ics.MaxSfb, "HasLtpData", "LTP Data");
        if (common)
        {
            LtpData(ref bits, n, _ics[1], ics.MaxSfb, "HasSecondLtpData", "Second LTP Data");
        }
    }

    // ltp_data (ISO/IEC 14496-3 4.4.2): lag, coefficient and the long window bands that use it, up to
    // MAX_LTP_LONG_SFB, 40.
    private void LtpData(ref AacBits bits, Node? n, Ics ics, int maxSfb, string flagName, string name)
    {
        int start = bits.Original;
        bool present = bits.ReadBit();
        Field(n, flagName, start, start + 1, NodeValue.Boolean(present), Explain.HasLtpData);
        ics.Ltp = present;

        if (!present)
        {
            return;
        }

        int dataStart = bits.Original;
        int lag = (int)bits.Read(11);
        int coefficient = (int)bits.Read(3);
        int used = Math.Min(maxSfb, 40);
        int usedStart = bits.Original;
        bits.Skip(used);
        if (n is not null)
        {
            Node data = _where.Bits(name, dataStart, bits.Original, $"lag {lag}", Explain.LtpData);
            data.Add(_where.Bits("LTP Lag", dataStart, dataStart + 11, NodeValue.Integer(lag, "samples"), Explain.LtpLag));
            data.Add(_where.Bits("LTP Coefficient", dataStart + 11, dataStart + 14, NodeValue.Integer(coefficient), Explain.LtpCoefficient));
            data.Add(_where.Bits("LTP Long Used", usedStart, bits.Original, NodeValue.Integer(used, "bands"), Explain.LtpLongUsed));
            n.Add(data);
        }
    }

    // section_data (ISO/IEC 14496-3 4.4.2): per window group, runs of scalefactor bands that share a
    // codebook, each a 4-bit codebook number and its length in increments, 5 bits each for long and
    // 3 for short windows, where the escape value adds another increment.
    private void SectionData(ref AacBits bits, Node? parent, Ics ics)
    {
        Node? n = Scratch();
        int start = bits.Original;
        int sections = 0;
        try
        {
            int width = ics.WindowSequence == 2 ? 3 : 5;
            int escape = (1 << width) - 1;
            for (int g = 0; g < ics.Groups; g++)
            {
                int k = 0;
                while (k < ics.MaxSfb)
                {
                    int sectionStart = bits.Original;
                    int book = (int)bits.Read(4);
                    int length = 0;
                    int increment;
                    do
                    {
                        increment = (int)bits.Read(width);
                        length += increment;
                    }
                    while (increment == escape);

                    sections++;
                    Node? section = null;
                    if (n is not null)
                    {
                        string bands = length == 0 ? "no bands" : length == 1 ? $"band {k}" : $"bands {k}-{k + length - 1}";
                        section = _where.Bits("Section", sectionStart, bits.Original,
                            (ics.Groups > 1 ? $"group {g}: " : string.Empty) + $"codebook {book} ({CodebookName(book)}), {bands}", Explain.Section, Topics.Codebooks);
                        n.Add(section);
                    }

                    if (book == 12)
                    {
                        throw new BlockError("aac.codebook-reserved", "names codebook 12, which the specification reserves", section);
                    }

                    if (k + length > ics.MaxSfb)
                    {
                        throw new BlockError("aac.section-overrun", $"has a section that ends at band {k + length}, past max_sfb {ics.MaxSfb}", section);
                    }

                    Array.Fill(ics.Codebooks, (byte)book, (g * MaxBands) + k, length);
                    k += length;
                }
            }

            if (parent is not null)
            {
                Close(parent, n!, "Section Data", start, bits.Original, NodeValue.Integer(sections, "sections", "section"), Explain.SectionData, Topics.Codebooks, NodeKind.SideInfo);
            }

            ics.SectionStart = start;
            ics.SectionEnd = bits.Original;
            StoreCodebooks(ics);
        }
        catch (Exception e) when (n is not null && e is BlockError or BlockOverrun)
        {
            throw Fail(e, parent, n, "Section Data", start, bits, null, Explain.SectionData, NodeKind.SideInfo);
        }
    }

    // scale_factor_data (ISO/IEC 14496-3 4.4.2): per band that is not of ZERO_HCB, the difference to
    // the previous scalefactor, intensity stereo position or noise energy, Huffman-coded with the
    // scalefactor codebook, but the first noise energy, 9 bits (4.6.2, 4.6.3). A skeleton cuts
    // it; its node shows its bits.
    private void ScaleFactors(ref AacBits bits, Node? parent, Ics ics, int gain)
    {
        int start = bits.Original;
        int length;
        if (bits.IsSkeleton)
        {
            length = Cut(ref bits, CutKind.ScaleFactors);
        }
        else
        {
            int scalefactor = gain;
            bool firstNoise = true;
            bool outOfRange = false;
            Span<int> stored = _store is not null && ics.Slot >= 0 ? _store.At(ics.Slot).Slice(BlockRecord.ScaleFactors, 512) : default;
            // The noise energy and the intensity position are coded as differences of their own (ISO/IEC 14496-3 4.6.2 and 4.6.3, as libxaac's ixheaacd_read_scale_factor_data takes them).
            int noise = gain - 90;
            int position = 0;
            for (int g = 0; g < ics.Groups; g++)
            {
                for (int sfb = 0; sfb < ics.MaxSfb; sfb++)
                {
                    int book = ics.Codebooks[(g * MaxBands) + sfb];
                    if (book == 0)
                    {
                        continue;
                    }

                    if (book == 13 && firstNoise)
                    {
                        firstNoise = false;
                        noise += (int)bits.Read(9) - 256;
                        if (!stored.IsEmpty)
                        {
                            stored[(g * MaxBands) + sfb] = noise;
                        }
                    }
                    else
                    {
                        int difference = Codebooks.Scalefactor.Decode(ref bits) - 60;
                        if (book < 12)
                        {
                            scalefactor += difference;
                            outOfRange |= scalefactor is < 0 or > 255;
                            if (!stored.IsEmpty)
                            {
                                stored[(g * MaxBands) + sfb] = scalefactor;
                            }
                        }
                        else if (!stored.IsEmpty)
                        {
                            if (book == 13)
                            {
                                noise += difference;
                                stored[(g * MaxBands) + sfb] = noise;
                            }
                            else
                            {
                                position += difference;
                                stored[(g * MaxBands) + sfb] = position;
                            }
                        }
                    }
                }
            }

            length = bits.Original - start;
            _cuts.Add(new BlockCut(CutKind.ScaleFactors, start, length, -1));
            if (outOfRange)
            {
                Soft("aac.scalefactor-out-of-range", "a scalefactor outside 0 to 255, for which ffmpeg's decoder rejects the access unit", start, start + length,
                    "Scale Factor Data");
            }
        }

        if (parent is not null)
        {
            parent.Add(_where.Bits("Scale Factor Data", start, start + length, NodeValue.Integer(length, "bits"), Explain.ScaleFactorData, Topics.Codebooks, NodeKind.SideInfo));
        }
    }

    // pulse_data (ISO/IEC 14496-3 4.4.2): up to four pulses added to the quantized values of a long
    // window, from a start band.
    private void PulseData(ref AacBits bits, Node? parent, Ics ics, int flagStart)
    {
        Node? n = Scratch();
        int start = bits.Original;
        if (ics.WindowSequence == 2)
        {
            Soft("aac.pulse-in-short-window", "pulse data in eight short windows, which the specification allows only in long ones", flagStart, flagStart + 1,
                "HasPulseData");
        }

        int count = (int)bits.Read(2) + 1;
        Int(n, "Pulses", start, start + 2, count, Explain.PulseCount);
        int startBand = (int)bits.Read(6);
        Int(n, "Pulse Start SFB", start + 2, start + 8, startBand, Explain.PulseStartSfb);
        Put(ics, BlockRecord.PulseCount, count);
        Put(ics, BlockRecord.PulseStart, startBand);
        int line = startBand < ics.Offsets.Length ? ics.Offsets[startBand] : int.MaxValue;
        bool invalid = startBand >= ics.Bands;
        int end = ics.Offsets[Math.Min(ics.Bands, ics.Offsets.Length - 1)];
        for (int i = 0; i < count; i++)
        {
            int at = bits.Original;
            int offset = (int)bits.Read(5);
            int amplitude = (int)bits.Read(4);
            Put(ics, BlockRecord.Pulses + (2 * i), offset);
            Put(ics, BlockRecord.Pulses + (2 * i) + 1, amplitude);
            line = line == int.MaxValue ? line : line + offset;
            invalid |= line >= end;
            Field(n, "Pulse", at, at + 9, n is null ? null : $"offset {offset}, amplitude {amplitude}", Explain.Pulse);
        }

        if (invalid)
        {
            Soft("aac.pulse-out-of-range", "pulses that start at or run past the last scalefactor band, which ffmpeg's decoder rejects", start, bits.Original, "Pulse Data");
        }

        if (parent is not null)
        {
            Close(parent, n!, "Pulse Data", start, bits.Original, NodeValue.Integer(count, "pulses", "pulse"), Explain.PulseData, Topics.PulseData, NodeKind.SideInfo);
        }
    }

    // tns_data (ISO/IEC 14496-3 4.4.2): per window, up to three filters (one for each short window),
    // each with its length in bands, order, direction and coefficients (4.6.9). Returns the filters.
    private int TnsData(ref AacBits bits, Node? parent, Ics ics)
    {
        Node? n = Scratch();
        int start = bits.Original;
        bool eight = ics.WindowSequence == 2;
        int windows = eight ? 8 : 1;
        // TNS_MAX_ORDER: 12 for long windows of LC and SSR and 20 for Main, as ffmpeg checks; for LTP
        // the references differ (ffmpeg 12, faad2 and fdk-aac 20), so only orders above 20 count.
        int maxOrder = eight ? 7 : _setup.ObjectType is 1 or 4 ? 20 : 12;
        int total = 0;
        bool tooHigh = false;
        for (int w = 0; w < windows; w++)
        {
            Node? window = Scratch();
            int windowStart = bits.Original;
            int filters = (int)bits.Read(eight ? 1 : 2);
            Int(window, "Filters", windowStart, bits.Original, filters, Explain.TnsFilters);
            int resolution = 0;
            if (filters > 0)
            {
                int at = bits.Original;
                resolution = (int)bits.Read(1);
                Field(window, "Coefficient Resolution", at, at + 1, resolution == 0 ? "0 (3 bits)" : "1 (4 bits)", Explain.TnsCoefficientResolution);
            }

            for (int f = 0; f < filters; f++)
            {
                Node? filter = Scratch();
                int filterStart = bits.Original;
                int length = (int)bits.Read(eight ? 4 : 6);
                Int(filter, "Length", filterStart, bits.Original, length, Explain.TnsLength, "bands");
                int orderStart = bits.Original;
                int order = (int)bits.Read(eight ? 3 : 5);
                Int(filter, "Order", orderStart, bits.Original, order, Explain.TnsOrder);
                tooHigh |= order > maxOrder;
                string direction = string.Empty;
                if (order > 0)
                {
                    int at = bits.Original;
                    bool downward = bits.ReadBit();
                    direction = downward ? ", downward" : ", upward";
                    Field(filter, "Direction", at, at + 1, downward ? "1 (downward)" : "0 (upward)", Explain.TnsDirection);
                    int compress = (int)bits.Read(1);
                    Int(filter, "Coefficient Compression", at + 1, at + 2, compress, Explain.TnsCompression);
                    int coefficientBits = 3 + resolution - compress;
                    int coefficientsStart = bits.Original;
                    if (_store is not null && ics.Slot >= 0)
                    {
                        StoreFilter(ref bits, ics, w, length, order, downward, resolution, compress, coefficientBits);
                    }
                    else
                    {
                        bits.Skip(order * coefficientBits);
                    }
                    Field(filter, "Coefficients", coefficientsStart, bits.Original, n is null ? null : $"{order} of {Counts.Of(coefficientBits, "bit", "bits")}", Explain.TnsCoefficients);
                }

                if (order == 0 && _store is not null && ics.Slot >= 0)
                {
                    StoreFilter(ref bits, ics, w, length, 0, false, resolution, 0, 0);
                }

                total++;
                if (window is not null)
                {
                    Close(window, filter!, "Filter", filterStart, bits.Original, $"order {order}, {Counts.Of(length, "band", "bands")}{direction}", Explain.TnsFilter, Topics.Tns);
                }
            }

            if (n is not null)
            {
                Close(n, window!, eight ? $"Window {w}" : "Window", windowStart, bits.Original, NodeValue.Integer(filters, "filters", "filter"), Explain.TnsWindow, Topics.Tns);
            }
        }

        if (tooHigh)
        {
            Soft("aac.tns-order-too-high", $"a TNS filter of a higher order than the {maxOrder} its window allows", start, bits.Original, "TNS Data");
        }

        if (parent is not null)
        {
            Close(parent, n!, "TNS Data", start, bits.Original, NodeValue.Integer(total, "filters", "filter"), Explain.TnsData, Topics.Tns, NodeKind.SideInfo);
        }

        return total;
    }

    // gain_control_data (ISO/IEC 14496-3 4.4.2): AAC SSR's gain changes per band of its filter bank
    // and per window, with locations as wide as the window sequence gives.
    private void GainControl(ref AacBits bits, Node? parent, Ics ics)
    {
        Node? n = Scratch();
        int start = bits.Original;
        int maxBand = (int)bits.Read(2);
        Int(n, "Max Band", start, start + 2, maxBand, Explain.GainControlMaxBand);
        (int windows, bool firstWide, int locationBits) = ics.WindowSequence switch
        {
            0 => (1, false, 5),
            1 => (2, true, 2),
            2 => (8, false, 2),
            _ => (2, true, 5),
        };
        int adjustments = 0;
        for (int band = 1; band <= maxBand; band++)
        {
            for (int w = 0; w < windows; w++)
            {
                int at = bits.Original;
                int count = (int)bits.Read(3);
                adjustments += count;
                bits.Skip(count * (4 + (w == 0 && firstWide ? 4 : locationBits)));
                Field(n, "Adjustments", at, bits.Original, n is null ? null : $"band {band}, window {w}: {count}", Explain.GainControlAdjustments);
            }
        }

        if (parent is not null)
        {
            Close(parent, n!, "Gain Control Data", start, bits.Original, NodeValue.Integer(adjustments, "adjustments", "adjustment"), Explain.GainControlData, null, NodeKind.SideInfo);
        }
    }

    // spectral_data (ISO/IEC 14496-3 4.4.2; 4.6.3): per band of a codebook from 1 to 11, the
    // quantized values two or four at a time, sign bits for the unsigned codebooks and the escape
    // sequences of codebook 11 (escape_prefix of at most 8 ones, escape_word of 4 more bits). A
    // skeleton cuts it; its node shows its bits.
    private void Spectral(ref AacBits bits, Node? parent, Ics ics)
    {
        int start = bits.Original;
        int length;
        if (bits.IsSkeleton)
        {
            length = Cut(ref bits, CutKind.Spectral);
        }
        else if (_store is not null && ics.Slot >= 0)
        {
            SpectralValues(ref bits, ics);
            length = bits.Original - start;
            _cuts.Add(new BlockCut(CutKind.Spectral, start, length, -1));
        }
        else
        {
            bool eight = ics.WindowSequence == 2;
            int last = 0;
            long nonZero = 0;
            long escapes = 0;
            for (int g = 0; g < ics.Groups; g++)
            {
                int windows = eight ? ics.GroupLengths[g] : 1;
                for (int sfb = 0; sfb < ics.MaxSfb; sfb++)
                {
                    int book = ics.Codebooks[(g * MaxBands) + sfb];
                    if (book is 0 or > 11)
                    {
                        continue;
                    }

                    int first = ics.Offsets[sfb];
                    int width = ics.Offsets[sfb + 1] - first;
                    int dimension = Codebooks.Dimension(book);
                    int codewords = width * windows / dimension;
                    Huffman huffman = Codebooks.Spectral[book];
                    byte[] follow = Codebooks.Follow[book];
                    byte[] masks = Codebooks.NonZero[book];
                    int[] shortcut = Codebooks.Shortcuts[book];
                    int rootWidth = huffman.RootWidth;
                    int bandLast = 0;
                    for (int i = 0; i < codewords; i++)
                    {
                        // Most codewords fit the root: one lookup gives their bits and sign bits, and which of their values are nonzero.
                        // Peek reads zero bits past the data, so the limit is checked once per band.
                        int entry = shortcut[bits.Peek(rootWidth)];
                        int mask;
                        if (entry >= 0 && (entry & 0x300) == 0)
                        {
                            bits.Advance(entry & 0xFF);
                            mask = (entry >> 10) & 15;
                        }
                        else
                        {
                            bits.Check();
                            int after;
                            if (entry >= 0)
                            {
                                bits.Skip(entry & 0xFF);
                                after = entry >> 5;
                                mask = (entry >> 10) & 15;
                            }
                            else
                            {
                                int symbol = huffman.Decode(ref bits);
                                after = follow[symbol];
                                mask = masks[symbol];
                                bits.Skip(after & 7);
                            }

                            if ((after & 8) != 0)
                            {
                                Escape(ref bits);
                                escapes++;
                            }

                            if ((after & 16) != 0)
                            {
                                Escape(ref bits);
                                escapes++;
                            }
                        }

                        if (mask != 0)
                        {
                            nonZero += BitOperations.PopCount((uint)mask);
                            int at = (i * dimension) + BitOperations.Log2((uint)mask);
                            bandLast = Math.Max(bandLast, windows == 1 ? at + 1 : (at % width) + 1);
                        }
                    }

                    if (bandLast > 0)
                    {
                        last = Math.Max(last, first + bandLast);
                    }

                    bits.Check();
                }
            }

            ics.LastLine = last;
            ics.NonZero = nonZero;
            ics.Escapes = escapes;
            length = bits.Original - start;
            _cuts.Add(new BlockCut(CutKind.Spectral, start, length, -1));
        }

        parent?.Add(_where.Bits("Spectral Data", start, start + length, NodeValue.Integer(length, "bits"), Explain.SpectralData, Topics.Codebooks));
    }

    // The same walk as the one above, reading the values: per band of a codebook from 1 to 11 the symbol's values (the index in the codebook's table, split by the
    // codebook's range as ISO/IEC 14496-3 4.6.3.3 lays them out), then the sign bits, then the escape sequences. The values go into the record in the order they are coded;
    // the lines of the other bands stay zero.
    private void SpectralValues(ref AacBits bits, Ics ics)
    {
        Span<int> record = _store!.At(ics.Slot);
        Span<int> spectrum = record.Slice(BlockRecord.Spectrum, 1024);
        Span<int> values = stackalloc int[4];
        bool eight = ics.WindowSequence == 2;
        int position = 0;
        int last = 0;
        long nonZero = 0;
        long escapes = 0;
        for (int g = 0; g < ics.Groups; g++)
        {
            int windows = eight ? ics.GroupLengths[g] : 1;
            for (int sfb = 0; sfb < ics.MaxSfb; sfb++)
            {
                int book = ics.Codebooks[(g * MaxBands) + sfb];
                int width = ics.Offsets[sfb + 1] - ics.Offsets[sfb];
                int lines = width * windows;
                if (book is 0 or > 11)
                {
                    position += lines;
                    continue;
                }

                int dimension = Codebooks.Dimension(book);
                Huffman huffman = Codebooks.Spectral[book];
                int bandStart = position;
                for (int i = 0; i < lines / dimension; i++)
                {
                    bits.Check();
                    int symbol = huffman.Decode(ref bits);
                    switch (book)
                    {
                        case <= 2:
                            values[0] = (symbol / 27) - 1;
                            values[1] = ((symbol / 9) % 3) - 1;
                            values[2] = ((symbol / 3) % 3) - 1;
                            values[3] = (symbol % 3) - 1;
                            break;
                        case <= 4:
                            values[0] = symbol / 27;
                            values[1] = (symbol / 9) % 3;
                            values[2] = (symbol / 3) % 3;
                            values[3] = symbol % 3;
                            break;
                        case <= 6:
                            values[0] = (symbol / 9) - 4;
                            values[1] = (symbol % 9) - 4;
                            break;
                        case <= 8:
                            values[0] = symbol / 8;
                            values[1] = symbol % 8;
                            break;
                        case <= 10:
                            values[0] = symbol / 13;
                            values[1] = symbol % 13;
                            break;
                        default:
                            values[0] = symbol / 17;
                            values[1] = symbol % 17;
                            break;
                    }

                    if (book is not (1 or 2 or 5 or 6))
                    {
                        for (int k = 0; k < dimension; k++)
                        {
                            if (values[k] != 0 && bits.ReadBit())
                            {
                                values[k] = -values[k];
                            }
                        }
                    }

                    if (book == 11)
                    {
                        for (int k = 0; k < 2; k++)
                        {
                            if (Math.Abs(values[k]) == 16)
                            {
                                escapes++;
                                int escape = EscapeValue(ref bits);
                                values[k] = values[k] < 0 ? -escape : escape;
                            }
                        }
                    }

                    for (int k = 0; k < dimension; k++)
                    {
                        if (values[k] != 0)
                        {
                            nonZero++;
                            last = Math.Max(last, ics.Offsets[sfb] + ((position - bandStart) % width) + 1);
                        }

                        spectrum[position++] = values[k];
                    }
                }

                bits.Check();
            }
        }

        ics.LastLine = last;
        ics.NonZero = nonZero;
        ics.Escapes = escapes;
    }

    // escape_prefix and escape_word (4.6.3.3): the value is 2^N plus the N-bit word, N being 4 plus the ones before the zero.
    private static int EscapeValue(ref AacBits bits)
    {
        int ones = 0;
        while (bits.ReadBit())
        {
            if (++ones > 8)
            {
                throw new BlockError("aac.escape-too-long", "holds an escape sequence of more than 8 leading ones, for a value above 8191", null);
            }
        }

        return (1 << (ones + 4)) + (int)bits.Read(ones + 4);
    }

    private void Put(Ics ics, int index, int value)
    {
        if (_store is not null && ics.Slot >= 0)
        {
            _store.At(ics.Slot)[index] = value;
        }
    }

    private void Or(Ics ics, int index, int value)
    {
        if (_store is not null && ics.Slot >= 0)
        {
            _store.At(ics.Slot)[index] |= value;
        }
    }

    private void StoreWindow(Ics ics)
    {
        if (_store is null || ics.Slot < 0)
        {
            return;
        }

        Span<int> record = _store.At(ics.Slot);
        record[BlockRecord.WindowSequence] = ics.WindowSequence;
        record[BlockRecord.WindowShape] = ics.WindowShape;
        record[BlockRecord.MaxSfb] = ics.MaxSfb;
        record[BlockRecord.Groups] = ics.Groups;
        int lengths = 0;
        for (int g = 0; g < ics.Groups; g++)
        {
            lengths |= ics.GroupLengths[g] << (4 * g);
        }

        record[BlockRecord.GroupLengths] = lengths;
    }

    private void StoreCodebooks(Ics ics)
    {
        if (_store is null || ics.Slot < 0)
        {
            return;
        }

        Span<int> codebooks = _store.At(ics.Slot).Slice(BlockRecord.Codebooks, 512);
        for (int g = 0; g < ics.Groups; g++)
        {
            for (int sfb = 0; sfb < ics.MaxSfb; sfb++)
            {
                codebooks[(g * MaxBands) + sfb] = ics.Codebooks[(g * MaxBands) + sfb];
            }
        }
    }

    // The pair's mid/side mask and flags: the flags go with the first channel's slot, the mask with both.
    private void StoreMidSide(Ics first, Ics second, int mask)
    {
        if (_store is null)
        {
            return;
        }

        Put(first, BlockRecord.MsMask, mask);
        Put(second, BlockRecord.MsMask, mask);
        if (first.Slot < 0 || mask is 0 or 3)
        {
            return;
        }

        Span<int> flags = _store.At(first.Slot).Slice(BlockRecord.MidSide, 512);
        for (int g = 0; g < first.Groups; g++)
        {
            for (int sfb = 0; sfb < first.MaxSfb; sfb++)
            {
                flags[(g * MaxBands) + sfb] = _midSide[(g * MaxBands) + sfb] ? 1 : 0;
            }
        }
    }

    // One TNS filter (4.4.2.11): its coefficients are read as coded, up to the 20 a slot holds; the decoder takes the filter's range from the length and the ones before it.
    private void StoreFilter(ref AacBits bits, Ics ics, int window, int length, int order, bool downward, int resolution, int compress, int coefficientBits)
    {
        Span<int> record = _store!.At(ics.Slot);
        int count = record[BlockRecord.TnsFilters];
        for (int i = 0; i < order; i++)
        {
            int value = (int)bits.Read(coefficientBits);
            if (count < BlockRecord.TnsMaxFilters && i < BlockRecord.TnsMaxCoefficients)
            {
                record[BlockRecord.Tns + (count * BlockRecord.TnsFilterInts) + 6 + i] = value;
            }
        }

        if (count < BlockRecord.TnsMaxFilters)
        {
            int at = BlockRecord.Tns + (count * BlockRecord.TnsFilterInts);
            record[at] = window;
            record[at + 1] = length;
            record[at + 2] = order;
            record[at + 3] = downward ? 1 : 0;
            record[at + 4] = 3 + resolution;
            record[at + 5] = compress;
        }

        record[BlockRecord.TnsFilters] = count + 1;
    }

    private void Escape(ref AacBits bits)
    {
        int ones = 0;
        while (bits.ReadBit())
        {
            if (++ones > 8)
            {
                throw new BlockError("aac.escape-too-long", "holds an escape sequence of more than 8 leading ones, for a value above 8191", null);
            }
        }

        bits.Skip(ones + 4);
    }

    private int Cut(ref AacBits bits, CutKind kind)
    {
        (CutKind found, int length, _) = bits.TakeCut();
        return found == kind ? length : throw new BlockError("aac.store-mismatch", "was stored inconsistently", null);
    }

    private sealed class Ics
    {
        // The slot of the block's record this stream stores into, or -1.
        public int Slot { get; set; } = -1;

        public int WindowSequence { get; set; }

        // The bit of its window_sequence field.
        public int SequenceAt { get; set; }

        public int WindowShape { get; set; }

        public int MaxSfb { get; set; }

        public int Bands { get; set; }

        public ushort[] Offsets { get; set; } = [];

        public int Groups { get; set; } = 1;

        public int[] GroupLengths { get; } = new int[MaxGroups];

        public bool Predictor { get; set; }

        public bool PredictorReset { get; set; }

        public bool Ltp { get; set; }

        // What the spectral data holds: the upper edge of its highest nonzero line within a window (0 where all are zero), and how many values are nonzero and how many are
        // escape-coded.
        public int LastLine { get; set; }

        public long NonZero { get; set; }

        public long Escapes { get; set; }

        public byte[] Codebooks { get; } = new byte[MaxGroups * MaxBands];

        public int SectionStart { get; set; }

        public int SectionEnd { get; set; }

        public void CopyWindow(Ics other)
        {
            WindowSequence = other.WindowSequence;
            SequenceAt = other.SequenceAt;
            WindowShape = other.WindowShape;
            MaxSfb = other.MaxSfb;
            Bands = other.Bands;
            Offsets = other.Offsets;
            Groups = other.Groups;
            other.GroupLengths.CopyTo(GroupLengths, 0);
            Predictor = other.Predictor;
            PredictorReset = other.PredictorReset;
        }
    }
}

// Where an element lies in its block: its ID, the bit its ID starts at, the bit after it, and for a
// channel pair where its second channel starts.
internal readonly record struct ElementSpan(int Id, int Start, int End, int SecondChannel);

// What reading a block came to: whether it was read to its end without error, the bit after its
// END and alignment (or where reading stopped), its elements packed (count in bits 24 to 27, up to
// eight 3-bit IDs below), whether it holds SBR data and the PS extension, and the error.
// HasSbr: the block holds an SBR payload; HasSbrEvidence: one that follows a channel element, which decoders take for SBR whether or not
// they can read it (BlockReader.TallySbr).
internal readonly record struct BlockResult(bool Ok, int End, int Elements, bool HasSbr, bool HasPs, BlockError? Error, bool HasSbrEvidence = false);

// Something a block breaks that stops its reading, with the node it happened in where nodes were
// built; ToEnd says the node runs to the end of the data.
internal sealed class BlockError(string code, string message, Node? node, bool toEnd = false) : Exception(message)
{
    public string Code => code;

    // The name of the element it happened in.
    public string? Element { get; set; }

    public Node? Node { get; set; } = node;

    public bool ToEnd => toEnd;
}

// Something a block breaks that does not stop its reading: its code, what it is, and the node, built
// from the bits it lies at.
internal readonly record struct SoftFinding(string Code, string Message, Node? Node);
