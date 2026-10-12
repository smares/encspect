using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.Aac;

// AAC handed over by a container (ICodecParser "aac"): the AudioSpecificConfig that the container
// stores apart (CodecContext.Configuration, MP4's DecoderSpecificInfo) and one access unit per
// packet, each a raw_data_block (ISO/IEC 14496-3 4.4.2.1) of 1,024 or 960 samples at the core's
// rate. Of each access unit the length and the first syntax element are read; the elements after
// it follow the Huffman-coded spectral data of the channel elements, which is not parsed yet, so
// implicit SBR and PS signaling, which hides in fill elements after them, is not checked. A payload
// handed over in one piece (ParseStream) is read as a raw AAC file: ADTS or ADIF.
public sealed class AacCodecParser : ICodecParser
{
    private const sbyte Usac = -2;
    private const sbyte ErrorResilient = -3;

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
            configNode = Node.FromMapped("AudioSpecificConfig", configuration.Data, 0, configuration.Data.Length);
            configuration.Parent.Add(configNode);
            config = AudioSpecificConfig.Parse(configuration.Data, configNode, (severity, code, message, node) => findings.AddNow(severity, code, message, node));
            configNode.Value = config?.Codec ?? "unreadable";
        }
        else
        {
            findings.AddNow(Severity.Warning, "aac.no-configuration",
                "The container hands over AAC without an AudioSpecificConfig, so the object type, sample rate and channels are unknown.", parent);
        }

        var log = new UnitLog(config);
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

        Report(context, parent, config, configNode, log, findings);
        findings.Flush(context);
    }

    private static void Report(CodecContext context, Node parent, AacConfig? config, Node? configNode, UnitLog log, FindingCollector findings)
    {
        Node Cover(string name, NodeValue? value, NodeKind kind = NodeKind.Detail) =>
            new(name, parent.Offset, parent.Length, value) { Kind = kind, Ranges = parent.Ranges };

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
        if (config is { ImplicitPossible: true })
        {
            findings.AddNow(Severity.Info, "aac.implicit-signaling-not-checked",
                config.Sbr is null
                    ? "No SBR is signaled in the AudioSpecificConfig. SBR and PS can also be signaled implicitly, in the fill elements of the access units, which are not checked yet; the stream may be HE-AAC or HE-AAC v2."
                    : "No PS is signaled in the AudioSpecificConfig. PS can also be signaled implicitly, in the fill elements of the access units, which are not checked yet; the stream may be HE-AAC v2.",
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

        Node? signatureNode = log.Signature is { } signature ? UnitNode(log, signature.Index, config) : null;
        Node? encoder = AacEncoder.Detect(
            log.Signature is { } found ? new AacEncoder.Signature(found.Text, signatureNode!, found.Index) : null,
            context.EncoderClaims,
            (name, value) => Cover(name, value),
            (claim, text, name) => findings.Add(Severity.Info, "aac.encoder-claim-differs", claim.Source, _ =>
                $"The {claim.Description} names \"{text}\", but the stream points to {name}; the file may have been written by other software, or kept its metadata when the audio was re-encoded."));
        if (encoder is not null)
        {
            leading.Add(encoder);
        }

        Node statistics = figures.Statistics((name, value, kind) => Cover(name, value, kind), "Access Unit", "Access Units", out Node bitrate);
        if (log.HasElements)
        {
            Node elements = statistics.Add(Cover("First Elements", null));
            for (int id = 0; id < log.ElementCounts.Length; id++)
            {
                if (log.ElementCounts[id] > 0)
                {
                    string name = id < 8 ? AacTables.ElementName(id) : "None";
                    elements.Add(Cover(name, NodeValue.Integer(log.ElementCounts[id], "access units", "access unit")));
                }
            }
        }

        if (config is { IsUsac: true })
        {
            statistics.Add(Cover("Independent Access Units", NodeValue.Integer(log.Independent, "access units", "access unit")));
        }

        leading.Add(statistics);
        var children = new BlockedChildren(leading, log.Units.Count, i => UnitNode(log, i, config));
        Node stream = parent.Add(new Node("AAC", parent.Offset, parent.Length, NodeValue.Integer(log.Units.Count, "access units", "access unit"), () => children)
        {
            Kind = NodeKind.Stream,
            Ranges = parent.Ranges,
        });

        if (config is null)
        {
            return;
        }

        string? confidence = encoder?.Children.FirstOrDefault(c => c.Name == "Confidence")?.Value?.ToString();
        context.AddStream(new StreamInfo
        {
            Nodes = [stream],
            Codec = config.Codec,
            SampleRate = rate,
            Channels = config.OutputChannels ?? 0,
            ChannelMode = config.ChannelMode,
            Samples = figures.Samples,
            Duration = figures.Duration,
            AverageBitrate = figures.AverageBitrate,
            IsVbr = context.Configuration?.IsVbr ?? false,
            Encoder = encoder?.Value?.ToString(),
            EncoderConfidence = Enum.TryParse(confidence, out Confidence parsed) ? parsed : null,
            Series = figures.Series(bitrate, context.CancellationToken),
        });
    }

    private static Node UnitNode(UnitLog log, int index, AacConfig? config)
    {
        Unit unit = log.Units[index];
        IReadOnlyList<ByteRange>? ranges = unit.IsSplit ? log.Splits[index] : null;
        string summary = unit.Element switch
        {
            >= 0 => $"{unit.Length} bytes, {AacTables.ElementName(unit.Element)}",
            Usac => $"{unit.Length} bytes, {(unit.Tag != 0 ? "independent" : "dependent")}",
            _ => $"{unit.Length} bytes",
        };
        return new Node("Access Unit", unit.Offset, unit.Length, summary, () => UnitChildren(log, index, unit, ranges))
        {
            Kind = NodeKind.Frame,
            Ranges = ranges,
        };
    }

    private static List<Node> UnitChildren(UnitLog log, int index, Unit unit, IReadOnlyList<ByteRange>? ranges)
    {
        var bytes = new UnitBytes(unit.Offset, unit.Length, ranges);
        if (unit.Element == Usac)
        {
            return unit.Length > 0 ? [bytes.Bits("IsIndependent", 0, 1, NodeValue.Boolean(unit.Tag != 0))] : [];
        }

        if (unit.Element < 0)
        {
            return [];
        }

        FirstElement element = log.Payloads.TryGetValue(index, out FirstElement stored)
            ? stored
            : FirstElement.Empty with { Id = unit.Element, Tag = unit.Tag };
        log.Texts.TryGetValue(index, out string? text);
        return [.. element.Nodes(bytes, 0, text)];
    }

    // What the access units of a stream come to while they are read, so nothing is kept per unit
    // but a 16-byte record: fill and data stream elements, texts and split units, which are rare,
    // are kept on the side.
    private sealed class UnitLog(AacConfig? config)
    {
        public ChunkedList<Unit> Units { get; } = new();

        public Dictionary<int, IReadOnlyList<ByteRange>> Splits { get; } = [];

        public Dictionary<int, FirstElement> Payloads { get; } = [];

        public Dictionary<int, string> Texts { get; } = [];

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
            bool split = ranges.Count > 1;
            if (split)
            {
                Splits[index] = ranges;
            }

            if (packet.Length == 0)
            {
                Empty++;
            }

            sbyte element = FirstElement.None;
            byte tag = 0;
            if (config is { IsUsac: true })
            {
                element = Usac;
                if (packet.Length > 0)
                {
                    tag = (byte)(packet.Span[0] >> 7);
                    Independent += tag;
                }
            }
            else if (config is not null && AacTables.IsErrorResilient(config.ObjectType))
            {
                element = ErrorResilient;
            }
            else if (config is { HasRawDataBlocks: true })
            {
                FirstElement first = FirstElement.Read(packet.Span, 0);
                element = (sbyte)first.Id;
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

            Units.Add(new Unit(packet.FileStart, packet.Length, element, tag, split));
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly struct Unit(long offset, int length, sbyte element, byte tag, bool isSplit)
    {
        public long Offset => offset;

        public int Length => length;

        // The first element's ID, FirstElement.None, Usac or ErrorResilient.
        public sbyte Element => element;

        // The first element's instance tag or count, or usacIndependencyFlag.
        public byte Tag => tag;

        public bool IsSplit => isSplit;
    }
}
