using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.Dts;

// One analysis of DTS data, whether a raw file's (DtsParser) or a container's packets
// (DtsCodecParser): each unit (a core frame and the extension substreams after it) is read field
// by field once (UnitSyntax), checked, and kept as a compact record from which its nodes are built
// again when read; findings of a kind are counted once per file; the units are grouped into streams
// (one sampling frequency, arrangement and extension), each with its statistics and series.
internal sealed class DtsAnalysis
{
    private readonly AnalysisContext _context;
    // The most streams a file gets: a file whose configuration changes at every unit would otherwise make a stream, a statistics node and
    // a series for each.
    internal const int MaxStreams = 64;

    private readonly List<DtsStream> _streams = [];
    private DtsStream? _current;
    private StreamKey? _previousKey;
    private Findings.Counted? _changes;
    private int _excluded;
    private long _unitOffset;
    private Locator _locator;
    private int _unitIndex;

    // The static fields in force for each extension substream index (7.5.2): the last header that carried them.
    private readonly StaticState?[] _staticStates = new StaticState?[4];

    public DtsAnalysis(AnalysisContext context)
    {
        _context = context;
        Records = new DtsRecords(Frames, Store);
    }

    // What unit nodes are built from, apart from the analysis, which holds the context and with it
    // the input.
    public DtsRecords Records { get; }

    public ChunkedList<Frame> Frames { get; } = new();

    public ByteStore Store { get; } = new();

    public List<Region> Regions { get; } = [];

    public Findings Findings { get; } = new();

    public CancellationToken Cancellation => _context.CancellationToken;

    // What reading the last unit found.
    public UnitFacts LastFacts { get; private set; } = new();

    // Reads the unit laid out as layout, whose bytes source gives.
    public void AddUnit(Region region, UnitLayout layout, ISource source)
    {
        var view = new UnitView();
        if (layout.CoreLength > 0)
        {
            var file = new byte[layout.CoreLength];
            int read = source.Read(layout.Offset, file);
            var packed = new byte[read + 2];
            int packedLength = Forms.Unpack(file.AsSpan(0, read), layout.Form, packed);
            view.Add(0, packed, packedLength);
        }

        int index = Frames.Count;
        _unitIndex = index;
        _unitOffset = layout.Offset;
        _locator = new Locator(layout.Form, layout.Offset, layout.Length);
        var syntax = new UnitSyntax(view, layout, source, OnIssue, _staticStates);
        syntax.Run();
        UnitFacts facts = syntax.Facts;
        LastFacts = facts;
        uint store = UnitRecord.Write(Store, layout, syntax.Verdicts, view, out int kept);
        Frames.Add(Frame.Of(layout.Offset, store, layout.Length, kept));
        Node UnitAt() => new("Frame", layout.Offset, layout.Length) { Kind = NodeKind.Frame };
        if (layout.IsTruncated)
        {
            Findings.AddNow(Severity.Warning, "dts.frame-truncated", $"The last frame at 0x{layout.Offset:X} has {layout.Length} bytes, fewer than its header says.", UnitAt());
        }

        if (layout.IsCut)
        {
            Findings.Add(Severity.Warning, "dts.frame-damaged", UnitAt, count =>
                $"{(count == 1 ? "A frame is" : $"{count} frames are")} cut short by the next frame's sync word, first at 0x{layout.Offset:X}: their header or data is damaged, or bytes are missing.");
        }

        Assign(region, index, facts, layout);
    }

    public void AddGap(long start, long end, bool leading, bool trailing, ISource source)
    {
        Frames.Add(Frame.Gap(start, end - start));
        var node = new Node("Gap", start, end - start, NodeValue.Integer(end - start, "bytes")) { Description = Explain.Gap };
        long length = end - start;
        if (leading)
        {
            Span<byte> probe = stackalloc byte[64];
            int read = source.Read(start, probe[..(int)Math.Min(64, length)]);
            bool zeros = read == length && probe[..read].IndexOfAnyExcept((byte)0) < 0;
            if (length > 64)
            {
                zeros = false;
            }

            Findings.AddNow(Severity.Warning, "dts.leading-junk", $"{length} bytes before the first frame are not audio{(zeros ? " (all zero bytes)" : string.Empty)}.", node);
        }
        else if (trailing)
        {
            Findings.AddNow(Severity.Warning, "dts.trailing-data", $"{length} bytes after the last frame at 0x{start:X} are not a frame.", node);
        }
        else
        {
            Findings.AddNow(Severity.Warning, "dts.sync-lost", $"Sync is lost for {length} bytes at 0x{start:X}.", node);
        }

    }

    // A gap that starts with a sync word that makes no header: what is wrong with it.
    public void AddInvalidHeader(long offset, long length, string reason)
    {
        Findings.Add(Severity.Warning, "dts.header-invalid", () => new Node("Gap", offset, length), count =>
            $"{(count == 1 ? "A core frame's header is" : $"{count} core frames' headers are")} invalid, first at 0x{offset:X}: {reason}; the bytes are not read as a frame.");
    }

    private static string Count(int count) => count == 1 ? "1 frame" : $"{count} frames";

    private static string Times(int count) => count == 1 ? "once" : $"{count} times";

    // The finding of each kind the syntax reports: one code, the first case's node, a count.
    private void OnIssue(Issue issue, string field, int start, int end, string detail, bool isGroup)
    {
        long offset = _unitOffset;
        Locator locator = _locator;
        Node Field() => isGroup ? locator.Group(field, start, end) : locator.Field(field, start, end);
        string at = $"the frame at 0x{offset:X}";
        switch (issue)
        {
            case Issue.ReservedValue:
                Findings.Add(Severity.Warning, "dts.reserved-value", Field, count =>
                    $"{field} holds a value ETSI TS 102 114 reserves or calls invalid: {detail}. First in {at} ({Times(count)}).");
                break;
            case Issue.UserDefinedArrangement:
                Findings.Add(Severity.Info, "dts.user-defined-arrangement", Field, count =>
                    $"The channel arrangement is a user defined code ({detail}, 5.4.2 Table 5-4), whose channels and downmix a player has to be told; the report lists the channels the core codes. First in {at} ({Times(count)}).");
                break;
            case Issue.RateCode:
                Findings.Add(Severity.Warning, "dts.rate-code", Field, count =>
                    $"RATE is not one of the codes Table 5-7 lists: {detail}. First in {at} ({Times(count)}).");
                break;
            case Issue.RateLossless:
                Findings.Add(Severity.Info, "dts.rate-lossless", Field, count =>
                    $"RATE is 31, which Table 5-7 does not list; 5.6 uses it to select the step-size table of a lossless stream, and FFmpeg and libdca read it as lossless. First in {at} ({Times(count)}).");
                break;
            case Issue.RevisionIncompatible:
                Findings.Add(Severity.Warning, "dts.revision-incompatible", Field, count =>
                    $"{detail} is a revision incompatible with ETSI TS 102 114 (Tables 5-16 and 6-2); a decoder that does not know it mutes its output. First in {at} ({Times(count)}).");
                break;
            case Issue.CrcPresent:
                Findings.Add(Severity.Info, "dts.crc-present", Field, count =>
                    $"CPF is 1: CRC words follow the headers and subframes, which the specification says should not be so (5.4.2); the words are read and not verified, as 5.4.2 says. First in {at} ({Times(count)}).");
                break;
            case Issue.FrameTypeInconsistent:
                Findings.Add(Severity.Warning, "dts.frame-type", Field, count =>
                    $"The frame type and the deficit sample count disagree: {detail} (5.3, Table 5-3). First in {at} ({Times(count)}).");
                break;
            case Issue.BlockCount:
                Findings.Add(Severity.Warning, "dts.block-count", Field, count =>
                    $"A normal frame holds {detail}, where 5.4.2 gives 256, 512, 1,024, 2,048 or 4,096 samples (8, 16, 32, 64 or 128 blocks). First in {at} ({Times(count)}).");
                break;
            case Issue.SubframeCount:
                Findings.Add(Severity.Warning, "dts.subframe-count", Field, count =>
                    $"The frame's subframes and its blocks do not fit: {detail} (5.4.3, 5.5.1). First in {at} ({Times(count)}).");
                break;
            case Issue.RateExceeded:
                Findings.Add(Severity.Warning, "dts.rate-exceeded", Field, count =>
                    $"A frame is larger than the targeted bit rate allows: {detail}; 5.4.2 says the targeted rate is at least the actual one. First in {at} ({Times(count)}).");
                break;
            case Issue.ChannelCount:
                Findings.Add(Severity.Warning, "dts.channel-count", Field, count =>
                    $"The number of primary channels does not fit the arrangement: {detail} (5.4.3). First in {at} ({Times(count)}).");
                break;
            case Issue.SubbandCount:
                Findings.Add(Severity.Warning, "dts.subband-count", Field, count =>
                    $"A channel is coded with more subbands than the 32 of the filter bank: {detail} (5.4.3). First in {at} ({Times(count)}).");
                break;
            case Issue.JointSource:
                Findings.Add(Severity.Warning, "dts.joint-source", Field, count =>
                    $"Joint intensity coding copies a later channel: {detail}; 5.4.3 assumes the source channel's index is smaller. First in {at} ({Times(count)}).");
                break;
            case Issue.HeaderOverrun:
                Findings.Add(Severity.Warning, "dts.header-overrun", Field, count =>
                    $"A header runs past the end of its data: {detail}. First in {at} ({Times(count)}).");
                break;
            case Issue.ExtensionMissing:
                Findings.Add(Severity.Warning, "dts.extension-missing", Field, count =>
                    $"The header says the frame holds an extension, but {detail}. First in {at} ({Times(count)}).");
                break;
            case Issue.ExtensionSizeLegacy:
                Findings.Add(Severity.Info, "dts.extension-size-legacy", Field, count =>
                    $"The extension's size field gives the distance itself, not one less, which 6.4.2's note accepts for legacy streams: {detail}. First in {at} ({Times(count)}).");
                break;
            case Issue.CrcMismatch:
                Findings.Add(Severity.Warning, "dts.crc-mismatch", Field, count =>
                    $"{detail}. A decoder that checks it does not trust the header. First in {at} ({Times(count)}).");
                break;
            case Issue.SubstreamOverrun:
                Findings.Add(Severity.Warning, "dts.substream-overrun", Field, count =>
                    $"A size in an extension substream runs past what contains it: {detail}. First in {at} ({Times(count)}).");
                break;
            case Issue.AssetDescriptorOverrun:
                Findings.Add(Severity.Warning, "dts.asset-descriptor-overrun", Field, count =>
                    $"An audio asset descriptor's fields run past its own size: {detail} (7.5.3.2). First in {at} ({Times(count)}).");
                break;
            case Issue.ComponentSync:
                Findings.Add(Severity.Warning, "dts.component-sync", Field, count =>
                    $"A component of an asset does not start with its sync word at the place the sizes in the header lead to: {detail} (7.5.3.4). First in {at} ({Times(count)}).");
                break;
            case Issue.SubstreamMisaligned:
                Findings.Add(Severity.Warning, "dts.substream-misaligned", Field, count =>
                    $"An extension substream starts {detail} ; a decoder that follows FFmpeg looks for it on the boundary only, so the stream plays as its core. First in {at} ({Times(count)}).");
                break;
            case Issue.LosslessInconsistent:
                Findings.Add(Severity.Warning, "dts.lossless-inconsistent", Field, count =>
                    $"The lossless extension contradicts itself: {detail}. First in {at} ({Times(count)}).");
                break;
            case Issue.CodingModeAuxiliary:
                Findings.Add(Severity.Info, "dts.coding-mode-auxiliary", Field, count =>
                    $"An asset has coding mode 3, auxiliary, which 7.5.3.4 reserves for the future; its data is not read. First in {at} ({Times(count)}).");
                break;
        }
    }

    // Units in one configuration make a stream; a change of the configuration ends it and starts another, as for the other formats, and
    // after MaxStreams streams the units of a further configuration are in no stream. A unit whose assets have no figures yet
    // (before any header with static fields) matches a configuration of the same core and structure, which the stream then takes: it joins the
    // stream after it, or one before it that has the same structure, and any other structure is a change.
    private void Assign(Region region, int index, UnitFacts facts, UnitLayout layout)
    {
        StreamKey key = StreamKey.Of(facts);
        bool changed = _previousKey is { } previous && !previous.Matches(key);
        if (changed)
        {
            RecordChange(_previousKey!.Value, key, layout);
        }

        if (_current is null || (changed && _streams.Count < MaxStreams))
        {
            _current = new DtsStream(key, index);
            _streams.Add(_current);
        }
        else if (_current.Key.IsPartial && !key.IsPartial)
        {
            _current.Complete(key);
        }

        if (changed || !key.IsPartial || _previousKey is not { IsPartial: false })
        {
            _previousKey = key;
        }

        if (!_current.Key.Matches(key))
        {
            // Past the limit a unit of another configuration is in no stream: the last stream's figures are those of its own units.
            _excluded++;
            _changes!.Severity = Severity.Warning;
            return;
        }

        _current.Regions.Add(region);
        _current.Add(layout.Offset, layout.Length, Samples(facts), facts, layout);
    }

    // The stream-changed finding, counted once: the first change gives its place and wording, a count follows.
    private void RecordChange(StreamKey previous, StreamKey key, UnitLayout layout)
    {
        if (_changes is { } counted)
        {
            counted.Count++;
            return;
        }

        long offset = layout.Offset;
        string from = previous.Describe();
        string to = key.Describe();
        if (from == to)
        {
            // Neither the form nor the extension substream's figures are in the description, so say which one differs.
            if (previous.Form != key.Form)
            {
                from += $" ({previous.Form.Describe()})";
                to += $" ({key.Form.Describe()})";
            }
            else
            {
                to += " (the figures of its extension substream's assets differ)";
            }
        }

        _changes = Findings.AddNowCounted(Severity.Info, "dts.stream-changed", new Node("Frame", layout.Offset, layout.Length) { Kind = NodeKind.Frame }, count =>
            (count == 1 ? $"The stream changes at 0x{offset:X} from {from} to {to}." : $"The stream changes {count} times, first at 0x{offset:X} from {from} to {to}.")
            + (_excluded > 0 ? $" Streams are limited to {MaxStreams}: {_excluded} units after that have a configuration other than the last stream's, are in no stream, and are left out of its figures." : string.Empty));
    }

    // The samples a unit holds at the stream's sampling frequency: the core's blocks of 32, or an extension substream's duration.
    private static int Samples(UnitFacts facts)
    {
        if (facts.HasCore)
        {
            return facts.Core.Samples;
        }

        SubstreamFacts? substream = facts.Substreams.FirstOrDefault(s => s.Duration > 0);
        AssetFacts? asset = substream?.Assets.FirstOrDefault();
        int rate = StreamKey.RateOf(asset);
        return substream is null || rate <= 0 ? 0 : (int)Math.Round(substream.Duration * rate);
    }

    // The streams, in the order their first units appear, with each one's statistics in the first region it spans.
    public void Finish(IReadOnlyList<EncoderClaim> claims)
    {
        _current = null;
        bool several = _streams.Count > 1;
        foreach (DtsStream stream in _streams)
        {
            Cancellation.ThrowIfCancellationRequested();
            AddStream(stream, claims, several);
        }

        _streams.Clear();
        Findings.Flush(_context);
    }

    private void AddStream(DtsStream stream, IReadOnlyList<EncoderClaim> claims, bool named)
    {
        Region first = Regions.First(r => stream.Regions.Contains(r));
        Node Cover(string name, NodeValue? value, NodeKind kind, string description, string? topic = null) =>
            new(name, first.Node!.Offset, first.Node.Length, value) { Kind = kind, Ranges = first.Node.Ranges, Description = description, Topic = topic };

        Node? encoder = DtsEncoder.Detect(claims, (name, value, description) => Cover(name, value, NodeKind.Detail, description, ClaimRoles.TopicOf(name)),
            (claim, text) => Findings.Add(Severity.Info, "dts.encoder-claim", () => claim.Source, _ =>
                $"The {claim.Description} names \"{text}\"; nothing in the stream confirms or contradicts it."));
        if (encoder is not null)
        {
            first.Leading.Add(encoder);
        }

        Node? writer = WriterDetection.Detect(claims, (name, value, description) => Cover(name, value, NodeKind.Detail, description, ClaimRoles.TopicOf(name)),
            (claim, text, name) => Findings.Add(Severity.Info, "dts.writer-claim-differs", () => claim.Source, _ => WriterDetection.Disagreement(claim, text, name)),
            format: new WriterDetection.StreamFormat(KnownWriters.Dts, "DTS", (claim, message, node) => Findings.Add(Severity.Info, "dts.writer-other-format", () => node, _ => message)));
        if (writer is not null)
        {
            first.Leading.Add(writer);
        }

        ClaimRoles.CompleteVersion(encoder, claims);

        stream.Figures.DeliveredRate = stream.DeliveredRate;
        Node statistics = stream.Statistics(Cover, out Node bitrate);
        if (named)
        {
            statistics.Value = stream.Key.Describe();
        }

        first.Leading.Add(statistics);
        (string? encoderName, Confidence? encoderConfidence) = ClaimRoles.Resolved(encoder);
        (string? writerName, Confidence? writerConfidence) = ClaimRoles.Resolved(writer);
        _context.AddStream(new StreamInfo
        {
            Nodes = [.. Regions.Where(r => stream.Regions.Contains(r)).Select(r => r.Node!)],
            Codec = stream.Codec,
            SampleRate = stream.DeliveredRate,
            Channels = stream.Channels,
            ChannelMode = stream.ChannelMode,
            ChannelLayout = stream.Layout,
            BitsPerSample = stream.BitsPerSample,
            Samples = stream.Figures.Samples,
            Duration = stream.Figures.Duration,
            AverageBitrate = stream.Figures.AverageBitrate,
            IsVbr = stream.IsVbr,
            Encoder = encoderName,
            EncoderConfidence = encoderConfidence,
            Writer = writerName,
            WriterConfidence = writerConfidence,
            Series = stream.Figures.Series(bitrate, Cancellation),
        });
    }

    // A unit's record, 24 bytes: its offset, where its kept bytes lie in the store, and its length. The kept bytes are the layout, the
    // verdicts of its checks and exactly the bytes the syntax read.
    [StructLayout(LayoutKind.Auto)]
    internal readonly struct Frame(long offset, uint store, int length, ushort kept, bool isGap)
    {
        public long Offset => offset;

        public uint Store => store;

        public int Length => length;

        public ushort Kept => kept;

        public bool IsGap => isGap;

        public static Frame Of(long offset, uint store, int length, int kept) => new(offset, store, length, (ushort)kept, false);

        public static Frame Gap(long offset, long length) => new(offset, (uint)Math.Min(length, uint.MaxValue), 0, 0, true);
    }

    internal sealed class Region(int first)
    {
        public int First => first;

        public int? End { get; set; }

        public Node? Node { get; set; }

        public List<Node> Leading { get; } = [];
    }
}

// How a unit's record is written and read back: a header of the layout, the verdicts, and the byte ranges the syntax read, each with its
// unit coordinate.
internal static class UnitRecord
{
    public static uint Write(ByteStore store, UnitLayout layout, Verdicts verdicts, UnitView view, out int kept)
    {
        var buffer = new List<byte>(256);
        int flags = (int)layout.Form | (layout.CoreLength > 0 ? 4 : 0) | (layout.IsCut ? 8 : 0) | (layout.IsTruncated ? 16 : 0) | (layout.Extension is { IsLegacySize: true } ? 32 : 0);
        buffer.Add((byte)flags);
        Put16(buffer, layout.CoreLength);
        buffer.Add((byte)(layout.Extension?.Kind ?? CoreExtension.None));
        Put16(buffer, layout.Extension?.Byte ?? 0);
        buffer.Add((byte)layout.Substreams.Count);
        foreach (ExssRef substream in layout.Substreams)
        {
            Put32(buffer, substream.Start);
            Put32(buffer, substream.Length);
            buffer.Add((byte)(substream.Index | (substream.IsMisaligned ? 0x80 : 0)));
            Put32(buffer, substream.Available);
        }

        buffer.Add((byte)verdicts.Count);
        foreach (byte verdict in verdicts.Values)
        {
            buffer.Add(verdict);
        }

        buffer.Add((byte)verdicts.Carries.Count);
        foreach (StaticState? carry in verdicts.Carries)
        {
            if (carry is null)
            {
                buffer.Add(0);
                continue;
            }

            var bytes = new byte[1 + StaticState.Bytes];
            bytes[0] = 1;
            carry.WriteTo(bytes.AsSpan(1));
            buffer.AddRange(bytes);
        }

        List<(int Start, int End)> touched = view.Touched();
        int total = buffer.Count + 2;
        var pieces = new List<(int Start, int End)>();
        foreach ((int start, int end) in touched)
        {
            int length = end - start + 1;
            // A record holds up to 60,000 bytes; what lies beyond is read as missing, and the nodes there are left out.
            if (total + 6 + length > 60_000)
            {
                break;
            }

            pieces.Add((start, end));
            total += 6 + length;
        }

        Put16(buffer, pieces.Count);
        foreach ((int start, int end) in pieces)
        {
            Put32(buffer, start);
            Put16(buffer, end - start + 1);
            buffer.AddRange(view.Peek(start, end - start + 1).ToArray());
        }

        kept = buffer.Count;
        return store.Add(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer));
    }

    public static Open Read(ByteStore store, DtsAnalysis.Frame frame)
    {
        ReadOnlySpan<byte> data = store.Span(frame.Store, frame.Kept);
        int at = 0;
        int flags = data[at++];
        var layout = new UnitLayout
        {
            Form = (StreamForm)(flags & 3),
            CoreLength = BinaryPrimitives.ReadUInt16BigEndian(data[at..]),
            IsCut = (flags & 8) != 0,
            IsTruncated = (flags & 16) != 0,
            Offset = frame.Offset,
            Length = frame.Length,
        };
        at += 2;
        var kind = (CoreExtension)data[at++];
        int position = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
        at += 2;
        if (kind != CoreExtension.None)
        {
            layout.Extension = new FoundExtension(kind, position, (flags & 32) != 0);
        }

        int substreams = data[at++];
        for (int i = 0; i < substreams; i++)
        {
            int start = BinaryPrimitives.ReadInt32BigEndian(data[at..]);
            int length = BinaryPrimitives.ReadInt32BigEndian(data[(at + 4)..]);
            int index = data[at + 8] & 3;
            bool misaligned = (data[at + 8] & 0x80) != 0;
            int available = BinaryPrimitives.ReadInt32BigEndian(data[(at + 9)..]);
            at += 13;
            layout.Substreams.Add(new ExssRef(start, length, index, available, misaligned));
        }

        int verdictCount = data[at++];
        var verdicts = new Verdicts(data.Slice(at, verdictCount));
        at += verdictCount;
        int carryCount = data[at++];
        var carries = new List<StaticState?>(carryCount);
        for (int i = 0; i < carryCount; i++)
        {
            if (data[at++] == 0)
            {
                carries.Add(null);
                continue;
            }

            carries.Add(StaticState.ReadFrom(data.Slice(at, StaticState.Bytes)));
            at += StaticState.Bytes;
        }

        verdicts = new Verdicts(verdicts.Values, carries);
        var view = new UnitView { IsBuildingNodes = true };
        int pieces = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
        at += 2;
        for (int i = 0; i < pieces; i++)
        {
            int start = BinaryPrimitives.ReadInt32BigEndian(data[at..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 4)..]);
            at += 6;
            view.Add(start, data.Slice(at, length).ToArray(), length);
            at += length;
        }

        return new Open(layout, verdicts, view);
    }

    private static void Put16(List<byte> buffer, int value)
    {
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private static void Put32(List<byte> buffer, int value)
    {
        Put16(buffer, value >> 16);
        Put16(buffer, value & 0xFFFF);
    }

    internal sealed class Open(UnitLayout layout, Verdicts verdicts, UnitView view)
    {
        public UnitLayout Layout => layout;

        // A node pass consumes its verdicts in order, and a frame's children are built again whenever the builder
        // runs (they are held weakly, and readers on several threads may each build them): each pass gets its own cursor.
        public Verdicts NewVerdicts() => new(verdicts.Values, verdicts.Carries);

        public UnitView View => view;

        // A frame's line in the tree, such as "3/2 + LFE, 2,012 bytes, 1 extension substream".
        public NodeValue Summary()
        {
            string text = $"{layout.Length} bytes";
            if (layout.CoreLength > 0 && view.PeekByte(11) >= 0)
            {
                CoreHeader header = Header();
                text = $"{Tables.ArrangementShort(header.Amode)}{(header.Lff is 1 or 2 ? " + LFE" : string.Empty)}, {text}";
            }

            return layout.Substreams.Count > 0 ? $"{text}, {layout.Substreams.Count} extension substream{(layout.Substreams.Count == 1 ? string.Empty : "s")}" : text;
        }

        private CoreHeader Header()
        {
            UInt128 raw = 0;
            foreach (byte b in view.Peek(0, CoreHeader.Bytes))
            {
                raw = (raw << 8) | b;
            }

            return new CoreHeader(raw);
        }
    }
}
