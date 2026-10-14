using Encspect.Core;

namespace Encspect.Formats.Ac3;

// AC-3 and E-AC-3 handed over by a container, such as MP4's ac-3 and ec-3 sample entries (ETSI TS
// 102 366 Annex F): one packet per sample, which holds one AC-3 syncframe (F.2) or the E-AC-3
// syncframes of six blocks of every substream; the container's AC3SpecificBox or EC3SpecificBox
// (F.4, F.6) comes as the configuration, and is read field by field and compared with the frames.
// The IDs are named after the codings, "ac-3" and "e-ac-3", not after MP4's codes for them.
public sealed class Ac3CodecParser(string id) : ICodecParser
{
    public const string Ac3 = "ac-3";
    public const string Eac3 = "e-ac-3";

    public string Id => id;

    // Set by a test to check that frame nodes read the bits the analysis kept (see FrameRecords).
    internal Action<long, int, int>? RecordMismatch { get; init; }

    public void Parse(CodecContext context)
    {
        Node parent = context.Parent;
        var analysis = new Ac3Analysis(context, RecordMismatch);
        var region = new Ac3Analysis.Region(0);
        analysis.Regions.Add(region);
        var observed = new Observed();
        if (context.Payload is { } payload)
        {
            ReadPayload(analysis, region, payload, observed);
        }
        else
        {
            int index = 0;
            foreach (MappedBytes packet in context.Packets!)
            {
                if (index++ % 4096 == 0)
                {
                    context.CancellationToken.ThrowIfCancellationRequested();
                }

                ReadPacket(analysis, region, packet, observed);
            }
        }

        observed.StartSample();
        region.End = analysis.Frames.Count;
        int count = region.End.Value;
        FrameRecords records = analysis.Records;
        region.Node = parent.Add(new Node(observed.HasEac3 ? "E-AC-3" : "AC-3", parent.Offset, parent.Length, NodeValue.Integer(analysis.Frames.Count, "frames", "frame"),
            () => new BlockedChildren(region.Leading, count, i => records.FrameNode(i)))
        {
            Kind = NodeKind.Stream,
            Ranges = parent.Ranges,
            Description = Explain.Stream,
            Topic = Topics.FramesAndBsi,
        });

        if (context.Configuration is { } configuration)
        {
            Configuration(analysis, configuration, observed, id == Eac3);
        }

        analysis.Finish(context.EncoderClaims);
    }

    private static void ReadPayload(Ac3Analysis analysis, Ac3Analysis.Region region, ByteReader payload, Observed observed)
    {
        var cache = new BlockCache(payload);
        var buffer = new byte[Ac3Analysis.MaxFrame];
        long position = payload.Start;
        while (position < payload.End)
        {
            if (Ac3Parser.HeaderAt(cache, position, payload.End) is not { } header)
            {
                analysis.AddGap(region, position, payload.End, leading: position == payload.Start, trailing: position > payload.Start, cache);
                return;
            }

            int length = (int)Math.Min(header.FrameLength, payload.End - position);
            cache.Read(position, buffer.AsSpan(0, length));
            analysis.AddFrame(region, position, buffer.AsSpan(0, length), header, truncated: length < header.FrameLength);
            observed.Add(header, analysis.LastResult, length, isSampleStart: !header.IsDependent);
            position += length;
        }
    }

    // The frames of one sample, one after another from its start.
    private static void ReadPacket(Ac3Analysis analysis, Ac3Analysis.Region region, MappedBytes packet, Observed observed)
    {
        ReadOnlySpan<byte> bytes = packet.Span;
        int position = 0;
        observed.StartSample();
        while (position < bytes.Length)
        {
            long offset = packet.OffsetOf(position);
            SyncFrame? header = bytes.Length - position >= SyncFrame.Length ? SyncFrame.Read(bytes[position..]) : null;
            if (header is not { IsValid: true } valid)
            {
                int rest = bytes.Length - position;
                if (bytes.Length - position >= 2 && bytes[position] == 0x77 && bytes[position + 1] == 0x0B)
                {
                    analysis.Findings.Add(Severity.Info, "ac3.little-endian", () => new Node("Gap", offset, rest), count =>
                        $"{(count == 1 ? "A sample holds" : $"{count} samples hold")} AC-3 in little-endian byte order (sync word 0x770B), which ETSI TS 102 366 F.1 allows but recommends against; it is not analyzed. The first is at 0x{offset:X}.");
                }
                else
                {
                    analysis.Findings.Add(Severity.Warning, "ac3.sample-not-frame", () => new Node("Gap", offset, rest), count =>
                        $"{(count == 1 ? "A sample holds" : $"{count} samples hold")} bytes that are no syncframe where one should start, first at 0x{offset:X}.");
                }

                analysis.Frames.Add(Ac3Analysis.Frame.Gap(offset, bytes.Length - position));
                return;
            }

            int length = Math.Min(valid.FrameLength, bytes.Length - position);
            analysis.AddFrame(region, offset, bytes.Slice(position, length), valid, truncated: length < valid.FrameLength);
            observed.Add(valid, analysis.LastResult, length, isSampleStart: false);
            if (length < valid.FrameLength)
            {
                return;
            }

            position += length;
        }
    }

    // ETSI TS 102 366 F.4 (AC3SpecificBox) and F.6 (EC3SpecificBox): read field by field into the
    // box's node, and compared with what the frames hold.
    private static void Configuration(Ac3Analysis analysis, CodecConfiguration configuration, Observed observed, bool entryIsEac3)
    {
        MappedBytes data = configuration.Data;
        Node box = configuration.Parent;
        int position = 0;

        // MP4 names a box's node by its type; the type decides the syntax, and a box of the other
        // codec's type under the sample entry is reported.
        bool eac3 = box.Name switch
        {
            "dac3" => false,
            "dec3" => true,
            _ => entryIsEac3,
        };
        if (eac3 != entryIsEac3)
        {
            analysis.Findings.AddNow(Severity.Warning, "ac3.config-box-type",
                $"An {(entryIsEac3 ? "ec-3" : "ac-3")} sample entry holds a {box.Name} box, where ETSI TS 102 366 Annex F gives it {(entryIsEac3 ? "dec3" : "dac3")}.", box);
        }

        int Read(string name, int width, string description, Func<int, NodeValue>? value = null)
        {
            if (position + width > data.Length * 8)
            {
                throw new EndOfStreamException();
            }

            int read = 0;
            for (int bit = position; bit < position + width; bit++)
            {
                read = (read << 1) | ((data.Span[bit >> 3] >> (7 - (bit & 7))) & 1);
            }

            box.Add(Node.FromBits(name, data, position, position + width, value is null ? NodeValue.Integer(read) : value(read), description: description, topic: Topics.FramesAndBsi));
            position += width;
            return read;
        }

        Node Last() => box.Children[^1];
        void Differs(string field, object stated, object coded) =>
            analysis.Findings.AddNow(Severity.Warning, "ac3.config-differs",
                $"The {(eac3 ? "dec3" : "dac3")} box gives {field} {stated}, but the frames hold {coded}; players that set up their decoder from the box and those that read the frames differ.", Last());
        void Reserved(int value)
        {
            if (value != 0)
            {
                analysis.Findings.AddNow(Severity.Warning, "ac3.config-reserved", $"Reserved bits of the {(eac3 ? "dec3" : "dac3")} box are not 0, as ETSI TS 102 366 Annex F requires.", Last());
            }
        }

        try
        {
            if (!eac3)
            {
                Observed.Substream? first = observed.Substreams.GetValueOrDefault(0);
                int fscod = Read("Sample Rate Code", 2, Explain.ConfigFscod, v => v < 3 ? NodeValue.Integer(SyncFrame.Rates[v], "Hz") : "3 (reserved)");
                Compare("fscod", fscod, first?.Fscod);
                int bsid = Read("Bit Stream ID", 5, Explain.ConfigBsid);
                Compare("bsid", bsid, first?.Bsid);
                int bsmod = Read("Bit Stream Mode", 3, Explain.ConfigBsmod);
                Compare("bsmod", bsmod, first?.Bsmod);
                int acmod = Read("Audio Coding Mode", 3, Explain.ConfigAcmod, v => $"{v}: {Ac3Names.CodingMode(v)}");
                Compare("acmod", acmod, first?.Acmod);
                int lfeon = Read("HasLfe", 1, Explain.ConfigLfeon, v => NodeValue.Boolean(v != 0));
                Compare("lfeon", lfeon, first?.Lfeon);
                int rate = Read("Bit Rate Code", 5, Explain.ConfigBitRateCode, v => v < 19 ? NodeValue.Integer(Tables.NominalBitrates[v], "kbps") : $"{v} (reserved)");
                Compare("bit_rate_code", rate, first?.FrameSizeCode >> 1);
                Reserved(Read("Reserved", 5, Explain.ConfigReserved));
                return;
            }

            int dataRate = Read("Data Rate", 13, Explain.ConfigDataRate, v => NodeValue.Integer(v, "kbps"));
            if (observed.MaxDataRate > 0 && dataRate != observed.MaxDataRate)
            {
                Differs("data_rate", $"{dataRate} kbps", $"{observed.MaxDataRate} kbps at most");
            }

            int independent = Read("Independent Substreams", 3, Explain.ConfigNumIndSub, v => NodeValue.Integer(v + 1, "substreams", "substream"));
            if (observed.Substreams.Count > 0 && independent != observed.Substreams.Keys.Max())
            {
                Differs("num_ind_sub", independent, observed.Substreams.Keys.Max());
            }

            for (int i = 0; i <= independent; i++)
            {
                Observed.Substream? sub = observed.Substreams.GetValueOrDefault(i);
                Compare("fscod", Read("Sample Rate Code", 2, Explain.ConfigFscod, v => v < 3 ? NodeValue.Integer(SyncFrame.Rates[v], "Hz") : "3 (reduced rate)"), sub?.Fscod);
                Compare("bsid", Read("Bit Stream ID", 5, Explain.ConfigBsid), sub?.Bsid);
                Reserved(Read("Reserved", 1, Explain.ConfigReserved));
                Read("IsAssociatedService", 1, Explain.ConfigAsvc, v => NodeValue.Boolean(v != 0));
                Compare("bsmod", Read("Bit Stream Mode", 3, Explain.ConfigBsmod), sub?.Bsmod);
                Compare("acmod", Read("Audio Coding Mode", 3, Explain.ConfigAcmod, v => $"{v}: {Ac3Names.CodingMode(v)}"), sub?.Acmod);
                Compare("lfeon", Read("HasLfe", 1, Explain.ConfigLfeon, v => NodeValue.Boolean(v != 0)), sub?.Lfeon);
                Reserved(Read("Reserved", 3, Explain.ConfigReserved));
                int dependents = Read("Dependent Substreams", 4, Explain.ConfigNumDepSub, v => NodeValue.Integer(v, "substreams", "substream"));
                Compare("num_dep_sub", dependents, sub?.Dependents);
                if (dependents > 0)
                {
                    Compare("chan_loc", Read("Channel Locations", 9, Explain.ConfigChanLoc, ChannelLocations), sub?.ChannelLocations, hex: true);
                }
                else
                {
                    Reserved(Read("Reserved", 1, Explain.ConfigReserved));
                }
            }

            if (position < data.Length * 8)
            {
                box.Add(Node.FromBits("Reserved", data, position, data.Length * 8, NodeValue.Integer(data.Length * 8 - position, "bits"), description: Explain.ConfigTrailing));
            }
        }
        catch (EndOfStreamException)
        {
            analysis.Findings.AddNow(Severity.Warning, "ac3.config-truncated", $"The {(eac3 ? "dec3" : "dac3")} box ends inside a field.", box);
        }

        void Compare(string field, int stated, int? coded, bool hex = false)
        {
            if (coded is { } value && value != stated)
            {
                Differs(field, hex ? $"0x{stated:X3}" : stated.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    hex ? $"0x{value:X3}" : value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    // Table F.6.1: bit 0 (the least significant) Lc/Rc to bit 8 LFE2.
    private static NodeValue ChannelLocations(int value)
    {
        string[] names = ["left/right center", "left/right rear surround", "center surround", "top surround", "left/right surround direct", "left/right wide",
            "left/right vertical height", "center vertical height", "LFE 2"];
        var set = new List<string>();
        for (int bit = 0; bit < 9; bit++)
        {
            if ((value & (1 << bit)) != 0)
            {
                set.Add(names[bit]);
            }
        }

        return $"0x{value:X3} ({(set.Count == 0 ? "none" : string.Join(", ", set))})";
    }

    // What the frames hold that the configuration states: per independent substream the fields of
    // its first frame, how many dependent substreams follow it and the locations they add, and the
    // highest data rate of a sample.
    private sealed class Observed
    {
        private long _sampleBits;
        private int _sampleSamples;
        private int _sampleRate;
        private int _current = -1;
        private int _dependentsOfCurrent;
        private bool _counting;

        public Dictionary<int, Substream> Substreams { get; } = [];

        public bool HasEac3 { get; private set; }

        public int MaxDataRate { get; private set; }

        public void StartSample()
        {
            Flush();
            _sampleBits = 0;
            _sampleSamples = 0;
            _counting = true;
        }

        public void Add(SyncFrame header, FrameResult result, int length, bool isSampleStart)
        {
            if (isSampleStart)
            {
                StartSample();
            }

            HasEac3 |= header.IsEac3;
            _sampleBits += length * 8L;
            _sampleRate = header.SampleRate;
            if (header.IsDependent)
            {
                if (_current >= 0 && Substreams[_current] is { } owner && owner.Counting)
                {
                    _dependentsOfCurrent++;
                    for (int bit = 0; bit < 16 && result.HasChannelMap; bit++)
                    {
                        if ((result.ChannelMap & (0x8000 >> bit)) != 0)
                        {
                            owner.ChannelLocations |= bit is >= 5 and <= 12 ? 1 << (bit - 5) : bit == 14 ? 1 << 8 : 0;
                        }
                    }

                    owner.Dependents = _dependentsOfCurrent;
                }

                return;
            }

            int id = header.IsEac3 ? header.SubstreamId : 0;
            if (id == 0)
            {
                _sampleSamples += header.Samples;
            }

            foreach (Substream other in Substreams.Values)
            {
                other.Counting = false;
            }

            _current = id;
            _dependentsOfCurrent = 0;
            if (!Substreams.ContainsKey(id))
            {
                Substreams[id] = new Substream
                {
                    Fscod = header.Fscod,
                    Bsid = header.Bsid,
                    Bsmod = Math.Max(0, result.Bsmod),
                    Acmod = header.Acmod,
                    Lfeon = header.Lfeon ? 1 : 0,
                    FrameSizeCode = header.IsEac3 ? null : header.FrameSizeCode,
                    Counting = true,
                };
            }
        }

        private void Flush()
        {
            if (_counting && _sampleSamples > 0 && _sampleRate > 0)
            {
                MaxDataRate = Math.Max(MaxDataRate, (int)(_sampleBits * _sampleRate / _sampleSamples / 1000));
            }
        }

        public sealed class Substream
        {
            public int Fscod { get; init; }

            public int Bsid { get; init; }

            public int Bsmod { get; init; }

            public int Acmod { get; init; }

            public int Lfeon { get; init; }

            public int? FrameSizeCode { get; init; }

            public int Dependents { get; set; }

            public int ChannelLocations { get; set; }

            // Only the dependents of a substream's first frame are counted.
            public bool Counting { get; set; }
        }
    }
}
