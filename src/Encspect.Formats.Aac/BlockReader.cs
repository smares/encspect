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
    private readonly Dictionary<int, SbrHeader> _sbr = [];
    private readonly Dictionary<int, int> _sbrHeaderBlock = [];
    private readonly List<BlockCut> _cuts = [];
    private readonly List<ElementSpan> _elements = [];
    private readonly List<SoftFinding> _soft = [];
    private readonly Ics[] _ics = [new(), new()];
    private readonly bool[] _midSide = new bool[MaxGroups * MaxBands];

    private bool _nodes;
    private UnitBytes _where;
    private int _anchor;
    private int _channel;
    private int _block;
    private int _lastChannelElement;
    private int _lastChannelTag;
    private int _elementCount;
    private int _elementIds;
    private bool _hasSbr;
    private bool _hasSbrEvidence;
    private bool _hasPs;

    public BlockReader(StreamSetup setup, CodingTally? tally)
    {
        _setup = setup;
        _tally = tally;
    }

    // What the last block's skeleton leaves out, in order.
    public IReadOnlyList<BlockCut> Cuts => _cuts;

    // The last block's elements, in order.
    public IReadOnlyList<ElementSpan> Elements => _elements;

    // What the last block breaks that does not stop its reading.
    public IReadOnlyList<SoftFinding> SoftFindings => _soft;

    // Reads the block in data from bit start, within limit, counting its coding; blockIndex is the
    // access unit's index in the stream. where places nodes the soft findings point to.
    public BlockResult Read(ReadOnlySpan<byte> data, int start, int limit, int blockIndex, UnitBytes where)
    {
        _nodes = false;
        _where = where;
        _block = blockIndex;
        var bits = new AacBits(data, start, limit);
        return Run(ref bits, null);
    }

    // The block's nodes, read from its bytes; error is what stopped the reading, with its node.
    public List<Node> Nodes(ReadOnlySpan<byte> data, int start, int limit, UnitBytes where, out BlockError? error)
    {
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
        _lastChannelElement = -1;
        _lastChannelTag = 0;
        _elementCount = 0;
        _elementIds = 0;
        _hasSbr = false;
        _hasSbrEvidence = false;
        _hasPs = false;
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

        return new BlockResult(true, bits.Original, Packed(), _hasSbr, _hasPs, null, _hasSbrEvidence);
    }

    private void CountDamaged()
    {
        if (_tally is not null)
        {
            _tally.Blocks++;
            _tally.DamagedBlocks++;
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
            if (common)
            {
                IcsInfo(ref bits, n, first, true);
                second.CopyWindow(first);
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
                if (n is not null && mask == 1)
                {
                    n.Add(_where.Bits("Mid/Side Bands", msStart, msEnd, BandList(first, (g, sfb) => _midSide[(g * MaxBands) + sfb]), Explain.MidSideBands, Topics.MidSide));
                }
            }

            ChannelStream(ref bits, n, first, common, 0, "Channel 0", bits.Original);
            int secondStart = bits.Original;
            ChannelStream(ref bits, n, second, common, 1, "Channel 1", secondStart);
            if (n is not null)
            {
                IntensityNode(n, second, common && mask != 0);
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

    private void TallyPair(Ics first, Ics second, bool common, int mask)
    {
        CodingTally tally = _tally!;
        tally.Pairs++;
        if (!common)
        {
            return;
        }

        tally.CommonWindow++;
        tally.MidSideMasks[mask]++;
        bool intensity = false;
        for (int g = 0; g < first.Groups; g++)
        {
            for (int sfb = 0; sfb < first.MaxSfb; sfb++)
            {
                int book = second.Codebooks[(g * MaxBands) + sfb];
                bool ms = mask != 0 && mask != 3 && _midSide[(g * MaxBands) + sfb];
                tally.PairBands++;
                if (book is 14 or 15)
                {
                    intensity = true;
                    tally.IntensityBands++;
                    // 4.6.8.2.3: INTENSITY_HCB2 (14) codes out of phase; ms_used inverts the sign.
                    if ((book == 14) != ms)
                    {
                        tally.IntensityOutOfPhase++;
                    }
                }
                else if (ms)
                {
                    tally.MidSideBands++;
                }
            }
        }

        if (intensity)
        {
            tally.IntensityPairs++;
        }
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
            if (pulse)
            {
                PulseData(ref bits, n, ics, flagStart);
            }

            flagStart = bits.Original;
            bool tns = bits.ReadBit();
            Field(n, "HasTnsData", flagStart, flagStart + 1, NodeValue.Boolean(tns), Explain.HasTnsData, Topics.Tns);
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

    private void TallyStream(Ics ics, int gain, bool pulse, bool tns, int filters, bool gainControl, int channel)
    {
        CodingTally tally = _tally!;
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
                        bits.Skip(9);
                    }
                    else
                    {
                        int difference = Codebooks.Scalefactor.Decode(ref bits) - 60;
                        if (book < 12)
                        {
                            scalefactor += difference;
                            outOfRange |= scalefactor is < 0 or > 255;
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
        int line = startBand < ics.Offsets.Length ? ics.Offsets[startBand] : int.MaxValue;
        bool invalid = startBand >= ics.Bands;
        int end = ics.Offsets[Math.Min(ics.Bands, ics.Offsets.Length - 1)];
        for (int i = 0; i < count; i++)
        {
            int at = bits.Original;
            int offset = (int)bits.Read(5);
            int amplitude = (int)bits.Read(4);
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
                    bits.Skip(order * coefficientBits);
                    Field(filter, "Coefficients", coefficientsStart, bits.Original, n is null ? null : $"{order} of {coefficientBits} bits", Explain.TnsCoefficients);
                }

                total++;
                if (window is not null)
                {
                    Close(window, filter!, "Filter", filterStart, bits.Original, $"order {order}, {length} bands{direction}", Explain.TnsFilter, Topics.Tns);
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
        else
        {
            bool eight = ics.WindowSequence == 2;
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

                    int lines = (ics.Offsets[sfb + 1] - ics.Offsets[sfb]) * windows;
                    int codewords = lines / Codebooks.Dimension(book);
                    Huffman huffman = Codebooks.Spectral[book];
                    byte[] follow = Codebooks.Follow[book];
                    int[] shortcut = Codebooks.Shortcuts[book];
                    int width = huffman.RootWidth;
                    for (int i = 0; i < codewords; i++)
                    {
                        // Most codewords fit the root: one lookup gives their bits and sign bits.
                        // Peek reads zero bits past the data, so the limit is checked once per band.
                        int entry = shortcut[bits.Peek(width)];
                        if (entry >= 0 && entry < 0x100)
                        {
                            bits.Advance(entry);
                            continue;
                        }

                        bits.Check();
                        int after;
                        if (entry >= 0)
                        {
                            bits.Skip(entry & 0xFF);
                            after = entry >> 5;
                        }
                        else
                        {
                            after = follow[huffman.Decode(ref bits)];
                            bits.Skip(after & 7);
                        }

                        if ((after & 8) != 0)
                        {
                            Escape(ref bits);
                        }

                        if ((after & 16) != 0)
                        {
                            Escape(ref bits);
                        }
                    }

                    bits.Check();
                }
            }

            length = bits.Original - start;
            _cuts.Add(new BlockCut(CutKind.Spectral, start, length, -1));
        }

        parent?.Add(_where.Bits("Spectral Data", start, start + length, NodeValue.Integer(length, "bits"), Explain.SpectralData, Topics.Codebooks));
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
        public int WindowSequence { get; set; }

        public int WindowShape { get; set; }

        public int MaxSfb { get; set; }

        public int Bands { get; set; }

        public ushort[] Offsets { get; set; } = [];

        public int Groups { get; set; } = 1;

        public int[] GroupLengths { get; } = new int[MaxGroups];

        public bool Predictor { get; set; }

        public bool PredictorReset { get; set; }

        public bool Ltp { get; set; }

        public byte[] Codebooks { get; } = new byte[MaxGroups * MaxBands];

        public int SectionStart { get; set; }

        public int SectionEnd { get; set; }

        public void CopyWindow(Ics other)
        {
            WindowSequence = other.WindowSequence;
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
