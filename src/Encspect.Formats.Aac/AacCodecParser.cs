using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.Aac;

// AAC handed over by a container (ICodecParser "aac"): the AudioSpecificConfig that the container
// stores apart (CodecContext.Configuration, MP4's DecoderSpecificInfo) and one access unit per
// packet, each a raw_data_block (ISO/IEC 14496-3 4.4.2.1) of 1,024 or 960 samples at the core's
// rate. For AAC Main, LC, SSR and LTP every access unit is read element by element (BlockReader),
// which counts the stream's coding and finds SBR and PS that only the fill elements signal; of the
// other GA types the first element is read. A payload handed over in one piece (ParseStream) is
// read as a raw AAC file: ADTS or ADIF.
public sealed class AacCodecParser : ICodecParser
{
    private const byte IsSplit = 1;
    private const byte IsUsac = 2;
    private const byte IsErrorResilient = 4;
    private const byte IsRead = 8;
    private const byte IsFirstOnly = 16;
    private const byte HasTrailingBytes = 32;

    public string Id => "aac";

    public void Parse(CodecContext context)
    {
        if (context.Payload is { } payload)
        {
            RawAacParser.ParseInto(payload, context.Parent, context, context.EncoderClaims);
            return;
        }

        Node parent = context.Parent;
        var findings = new FindingCollector();
        AacConfig? config = null;
        Node? configNode = null;
        if (context.Configuration is { } configuration)
        {
            configNode = Node.FromMapped("AudioSpecificConfig", configuration.Data, 0, configuration.Data.Length,
                description: Explain.AudioSpecificConfig, topic: Topics.AudioSpecificConfig);
            configuration.Parent.Add(configNode);
            config = AudioSpecificConfig.Parse(configuration.Data, configNode, (severity, code, message, node) => findings.AddNow(severity, code, message, node));
            configNode.Value = config?.Codec ?? "unreadable";
        }
        else
        {
            findings.AddNow(Severity.Warning, "aac.no-configuration",
                "The container hands over AAC without an AudioSpecificConfig, so the object type, sample rate and channels are unknown.", parent);
        }

        var log = new UnitLog(config, findings);
        int index = 0;
        foreach (MappedBytes packet in context.Packets!)
        {
            if (index % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            log.Add(packet);
            index++;
        }

        if (config is not null && log.Blocks is { } blocks)
        {
            config = config with { SbrFound = blocks.Tally.IsSbrEvident, PsFound = blocks.Tally.IsPsEvident };
            blocks.ReportSignaling(config, configNode ?? parent, "The AudioSpecificConfig");
            if (configNode is not null)
            {
                configNode.Value = config.Codec;
            }
        }

        Report(context, parent, config, configNode, log, findings);
        findings.Flush(context);
    }

    private static void Report(CodecContext context, Node parent, AacConfig? config, Node? configNode, UnitLog log, FindingCollector findings)
    {
        Node Cover(string name, NodeValue? value, string description, NodeKind kind = NodeKind.Detail, string? topic = null) =>
            new(name, parent.Offset, parent.Length, value) { Kind = kind, Ranges = parent.Ranges, Description = description, Topic = topic };

        int rate = config?.OutputSampleRate ?? 0;
        int frame = config?.OutputFrameLength ?? 1024;
        var figures = new StreamFigures(rate);
        for (int i = 0; i < log.Units.Count; i++)
        {
            if (i % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            ref readonly Unit unit = ref log.Units[i];
            figures.Add(unit.Offset, unit.Length, frame);
        }

        var leading = new List<Node>();
        bool encrypted = context.Configuration is { AreSamplesEncrypted: true };
        if (config is { ImplicitPossible: true } && !encrypted)
        {
            findings.AddNow(Severity.Info, "aac.implicit-signaling-not-checked",
                config.Sbr is null
                    ? "No SBR is signaled in the AudioSpecificConfig. SBR and PS can also be signaled implicitly, in the fill elements of the access units, which are not read for this object type; the stream may be HE-AAC or HE-AAC v2."
                    : "No PS is signaled in the AudioSpecificConfig. PS can also be signaled implicitly, in the fill elements of the access units, which are not read for this object type; the stream may be HE-AAC v2.",
                configNode ?? parent);
        }

        if (config is { IsUsac: true })
        {
            findings.AddNow(Severity.Info, "aac.usac-frames-not-analyzed",
                "USAC access units are read only up to their usacIndependencyFlag; their coding is not analyzed yet.", configNode ?? parent);
        }

        if (log.Empty > 0)
        {
            findings.AddNow(Severity.Warning, "aac.empty-access-units", $"{log.Empty} access units hold no bytes.", parent);
        }

        (string Text, int Index)? signature = log.Blocks is { Tally.FirstText: { } text } blocks ? (text, blocks.Tally.FirstTextBlock) : log.Signature;
        Node? signatureNode = signature is { } found ? UnitNode(log.Records, found.Index, withChildren: false) : null;
        Node? encoder = AacEncoder.Detect(
            signature is { } named ? new AacEncoder.Signature(named.Text, signatureNode!, named.Index) : null,
            context.EncoderClaims,
            (name, value, description) => Cover(name, value, description, topic: ClaimRoles.TopicOf(name)),
            (claim, text, name) => findings.Add(Severity.Info, "aac.encoder-claim-differs", claim.Source, _ =>
                AacEncoder.Disagreement(claim, text, name, "the file may have been written by other software, or kept its metadata when the audio was re-encoded.")),
            config?.UsacLayout,
            log.Blocks?.Tally);
        if (encoder is not null)
        {
            leading.Add(encoder);
        }

        Node? writer = WriterDetection.Detect(context.EncoderClaims, (name, value, description) => Cover(name, value, description, topic: ClaimRoles.TopicOf(name)),
            (claim, text, name) => findings.Add(Severity.Info, "aac.writer-claim-differs", claim.Source, _ => WriterDetection.Disagreement(claim, text, name)),
            AacEncoder.WriterName,
            format: config is { IsUsac: true }
                ? new WriterDetection.StreamFormat(KnownWriters.Usac, "xHE-AAC (USAC)", (claim, message, node) => findings.Add(Severity.Info, "aac.writer-other-format", node, _ => message))
                : new WriterDetection.StreamFormat(KnownWriters.Aac, "AAC", (claim, message, node) => findings.Add(Severity.Info, "aac.writer-other-format", node, _ => message)));
        if (writer is not null)
        {
            leading.Add(writer);
        }

        ClaimRoles.CompleteVersion(encoder, context.EncoderClaims, AacEncoder.WriterName);

        Node statistics = figures.Statistics((name, value, kind, description) => Cover(name, value, description, kind), "Access Unit", "Access Units", out Node bitrate);
        if (log.HasElements)
        {
            Node elements = statistics.Add(Cover("First Elements", null, Explain.FirstElements, topic: Topics.RawDataBlocks));
            for (int id = 0; id < log.ElementCounts.Length; id++)
            {
                if (log.ElementCounts[id] > 0)
                {
                    string name = id < 8 ? AacTables.ElementName(id) : "None";
                    elements.Add(Cover(name, NodeValue.Integer(log.ElementCounts[id], "access units", "access unit"), Explain.FirstElementCount));
                }
            }
        }

        if (log.Blocks is { } read)
        {
            CodingStatistics.Add(statistics, read.Tally, read.Setup, (name, value, description, topic) => Cover(name, value, description, topic: topic));
        }

        if (config is { IsUsac: true })
        {
            statistics.Add(Cover("Independent Access Units", NodeValue.Integer(log.Independent, "access units", "access unit"), Explain.IndependentUnits, topic: Topics.Usac));
        }

        if (!encrypted)
        {
            leading.Add(statistics);
        }

        UnitRecords records = log.Records;
        var children = new BlockedChildren(leading, records.Units.Count, i => UnitNode(records, i, withChildren: true));
        NodeValue streamValue = encrypted ? NodeValue.Text("frames not read: encrypted") : NodeValue.Integer(log.Units.Count, "access units", "access unit");
        Node stream = parent.Add(new Node("AAC", parent.Offset, parent.Length, streamValue, () => children)
        {
            Kind = NodeKind.Stream,
            Ranges = parent.Ranges,
            Description = Explain.Stream,
            Topic = config switch { { IsUsac: true } => Topics.Usac, { HasSbr: true } => Topics.Sbr, _ => Topics.ObjectTypes },
        });

        if (config is null)
        {
            return;
        }

        (string? encoderName, Confidence? encoderConfidence) = ClaimRoles.Resolved(encoder);
        (string? writerName, Confidence? writerConfidence) = ClaimRoles.Resolved(writer);
        context.AddStream(new StreamInfo
        {
            Nodes = [stream],
            Codec = config.Codec,
            SampleRate = rate,
            Channels = config.OutputChannels ?? 0,
            ChannelMode = config.ChannelMode,
            ChannelLayout = config.OutputLayout,
            Samples = figures.Samples,
            Duration = figures.Duration,
            AverageBitrate = figures.AverageBitrate,
            IsVbr = context.Configuration?.IsVbr ?? false,
            Encoder = encoderName,
            EncoderConfidence = encoderConfidence,
            Writer = writerName,
            WriterConfidence = writerConfidence,
            Series = figures.Series(bitrate, context.CancellationToken),
        });
    }

    private static Node UnitNode(UnitRecords log, int index, bool withChildren)
    {
        Unit unit = log.Units[index];
        IReadOnlyList<ByteRange>? ranges = (unit.Flags & IsSplit) != 0 ? log.Splits[index] : null;
        string summary;
        if ((unit.Flags & IsRead) != 0)
        {
            summary = $"{Counts.Of(unit.Length, "byte", "bytes")}, {BlockLog.ElementList(unit.Elements)}";
        }
        else if ((unit.Flags & IsUsac) != 0)
        {
            summary = $"{Counts.Of(unit.Length, "byte", "bytes")}, {(unit.Tag != 0 ? "independent" : "dependent")}";
        }
        else if ((unit.Flags & IsFirstOnly) != 0 && unit.Elements >= 0)
        {
            summary = $"{Counts.Of(unit.Length, "byte", "bytes")}, {AacTables.ElementName(unit.Elements)}";
        }
        else
        {
            summary = unit.Length == 1 ? "1 byte" : $"{unit.Length} bytes";
        }

        bool usac = (unit.Flags & IsUsac) != 0;
        return new Node("Access Unit", unit.Offset, Span(unit.Length, ranges), summary, withChildren ? () => UnitChildren(log, index, unit, ranges) : null)
        {
            Kind = NodeKind.Frame,
            Ranges = ranges,
            Description = usac ? Explain.UsacAccessUnit : Explain.AccessUnit,
            Topic = usac ? Topics.Usac : Topics.RawDataBlocks,
        };
    }

    private static List<Node> UnitChildren(UnitRecords log, int index, Unit unit, IReadOnlyList<ByteRange>? ranges)
    {
        var bytes = new UnitBytes(unit.Offset, unit.Length, ranges);
        if ((unit.Flags & IsUsac) != 0)
        {
            return unit.Length > 0 ? [bytes.Bits("IsIndependent", 0, 1, NodeValue.Boolean(unit.Tag != 0), Explain.IsIndependent)] : [];
        }

        if ((unit.Flags & IsRead) != 0)
        {
            List<Node> nodes = BlockLog.Nodes(log.Store!, log.Setup!, unit.Skeleton, bytes);
            long end = nodes.Count > 0 ? nodes.Max(n => n.End) - unit.Offset : 0;
            // A block read to its END owns the rest of its unit as trailing bytes, however long the
            // unit, since the aac.unit-trailing-bytes finding points to that node.
            if ((unit.Flags & HasTrailingBytes) != 0)
            {
                nodes.Add(TrailingNode(bytes, UnitStore.BlockBits(log.Store!.Read(unit.Skeleton)), unit.Length));
            }
            else if (unit.Length > BlockLog.MaxBlockBytes && end < unit.Length)
            {
                nodes.Add(bytes.Bits("Unread Bytes", BlockLog.MaxBlockBytes * 8, unit.Length * 8, NodeValue.Integer(unit.Length - BlockLog.MaxBlockBytes, "bytes"), Explain.UnreadUnitBytes));
            }

            return nodes;
        }

        if ((unit.Flags & IsFirstOnly) == 0 || unit.Elements < 0)
        {
            return [];
        }

        FirstElement element = log.Payloads.TryGetValue(index, out FirstElement stored)
            ? stored
            : FirstElement.Empty with { Id = unit.Elements, Tag = unit.Tag };
        log.Texts.TryGetValue(index, out string? text);
        return [.. element.Nodes(bytes, 0, text)];
    }

    // The bytes an access unit's node covers: its length, or for a unit split across the file
    // from its first range to its last, so the node in the tree and the one a finding names agree.
    private static long Span(int length, IReadOnlyList<ByteRange>? ranges) => ranges is null ? length : ranges[^1].End - ranges[0].Offset;

    private static Node TrailingNode(UnitBytes bytes, int blockEnd, int length) =>
        bytes.Bits("Trailing Bytes", blockEnd, length * 8, NodeValue.Integer(length - (blockEnd / 8), "bytes"), Explain.TrailingUnitBytes);

    // The records an access unit's node is built from, and nothing else: node factories capture this
    // and not the UnitLog, whose block reader and findings would stay alive with the report, and with
    // them the input.
    private sealed class UnitRecords(StreamSetup? setup, UnitStore? store)
    {
        public StreamSetup? Setup => setup;

        public UnitStore? Store => store;

        public ChunkedList<Unit> Units { get; } = new();

        public Dictionary<int, IReadOnlyList<ByteRange>> Splits { get; } = [];

        public Dictionary<int, FirstElement> Payloads { get; } = [];

        public Dictionary<int, string> Texts { get; } = [];
    }

    // What the access units of a stream come to while they are read: a 24-byte record each, the
    // coding statistics and what their nodes are rebuilt from (BlockLog); split units, and for the
    // types whose first element alone is read their fill and data stream elements and texts, which
    // are rare, are kept on the side.
    private sealed class UnitLog
    {
        private readonly AacConfig? _config;
        private readonly FindingCollector _findings;

        public UnitLog(AacConfig? config, FindingCollector findings)
        {
            _config = config;
            _findings = findings;
            if (config is not null && StreamSetup.CanRead(config))
            {
                Blocks = new BlockLog(new StreamSetup(config), findings);
            }

            Records = new UnitRecords(Blocks?.Setup, Blocks?.Store);
        }

        // What the access units' nodes are built from; the log keeps the rest.
        public UnitRecords Records { get; }

        public ChunkedList<Unit> Units => Records.Units;

        public BlockLog? Blocks { get; }

        public Dictionary<int, IReadOnlyList<ByteRange>> Splits => Records.Splits;

        public Dictionary<int, FirstElement> Payloads => Records.Payloads;

        public Dictionary<int, string> Texts => Records.Texts;

        // Per element ID, and at 8 the access units whose first element could not be read.
        public long[] ElementCounts { get; } = new long[9];

        public bool HasElements { get; private set; }

        public long Independent { get; private set; }

        public int Empty { get; private set; }

        public (string Text, int Index)? Signature { get; private set; }

        public void Add(MappedBytes packet)
        {
            int index = Units.Count;
            IReadOnlyList<ByteRange> ranges = packet.FileRanges();
            byte flags = 0;
            if (ranges.Count > 1)
            {
                Splits[index] = ranges;
                flags |= IsSplit;
            }

            if (packet.Length == 0)
            {
                Empty++;
            }

            int elements = -1;
            byte tag = 0;
            long skeleton = -1;
            if (_config is { IsUsac: true })
            {
                flags |= IsUsac;
                if (packet.Length > 0)
                {
                    tag = (byte)(packet.Span[0] >> 7);
                    Independent += tag;
                }
            }
            else if (_config is not null && AacTables.IsErrorResilient(_config.ObjectType))
            {
                flags |= IsErrorResilient;
            }
            else if (Blocks is not null)
            {
                flags |= IsRead;
                var where = new UnitBytes(packet.FileStart, packet.Length, ranges.Count > 1 ? ranges : null);
                (skeleton, BlockResult result) = Blocks.Read(packet.Span, 0, packet.Length * 8, where, () => UnitNodeAt(packet, ranges));
                elements = result.Elements;
                Blocks.AddEvidence(result, Blocks.Count - 1);
                int first = BlockLog.FirstElement(elements);
                ElementCounts[(elements >> 24) == 0 && !result.Ok ? 8 : first]++;
                HasElements = true;
                if (result.Ok)
                {
                    if (result.End < packet.Length * 8)
                    {
                        flags |= HasTrailingBytes;
                        Node unit = UnitNodeAt(packet, ranges);
                        Node trailing = TrailingNode(where, result.End, packet.Length);
                        long offset = unit.Offset;
                        int count = packet.Length - (result.End / 8);
                        _findings.Add(Severity.Warning, "aac.unit-trailing-bytes", trailing, n => n == 1
                            ? $"The access unit at 0x{offset:X} holds {Counts.Of(count, "byte", "bytes")} after the END element of its raw data block."
                            : $"{n} access units hold bytes after the END element of their raw data block, first the one at 0x{offset:X}.");
                    }
                }
            }
            else if (_config is { HasRawDataBlocks: true })
            {
                flags |= IsFirstOnly;
                FirstElement first = FirstElement.Read(packet.Span, 0);
                elements = first.Id;
                tag = (byte)Math.Max(0, first.Tag);
                ElementCounts[first.Id >= 0 ? first.Id : 8]++;
                HasElements = true;
                if (first.Id is 4 or 6)
                {
                    Payloads[index] = first;
                }

                if (first.Text(packet.Span) is { } text)
                {
                    Texts[index] = text;
                    Signature ??= (text, index);
                }
            }

            Units.Add(new Unit(packet.FileStart, packet.Length, elements, skeleton, flags, tag));
        }

        private static Node UnitNodeAt(MappedBytes packet, IReadOnlyList<ByteRange> ranges)
        {
            IReadOnlyList<ByteRange>? split = ranges.Count > 1 ? ranges : null;
            return new("Access Unit", packet.FileStart, Span(packet.Length, split)) { Kind = NodeKind.Frame, Ranges = split };
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly struct Unit(long offset, int length, int elements, long skeleton, byte flags, byte tag)
    {
        // Where the store keeps the unit's block, plus one, above the flags and the tag: 24 bytes in all.
        private readonly long _packed = ((skeleton + 1) << 16) | ((long)flags << 8) | tag;

        public long Offset => offset;

        public int Length => length;

        // The packed element IDs of a read unit (BlockResult.Elements), or the first element's ID.
        public int Elements => elements;

        // Where the store keeps the unit's block, or -1.
        public long Skeleton => (_packed >> 16) - 1;

        public byte Flags => (byte)(_packed >> 8);

        // The first element's instance tag or count, or usacIndependencyFlag.
        public byte Tag => (byte)_packed;
    }
}
