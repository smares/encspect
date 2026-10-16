using Encspect.Core;

namespace Encspect.Formats.Wma;

// Windows Media Audio Voice (the format tag 0x000A), handed over by a container as the media objects of one stream (ASF:
// ParsePackets) with the WAVEFORMATEX and its 46 bytes of codec-specific data. Read as FFmpeg's wmavoice.c reads it: a media
// object is one or more packets of the block alignment's size; a packet starts with a header and holds the end of the
// superframe before it, whole superframes, and the start of the one that goes on. A superframe is a header, three frames of
// 160 samples and optional additional data; a frame is divided into blocks by its type. Every frame is read down to its type,
// pitch and the widths of its excitation fields, without building the audio, so that the report knows where each superframe
// ends and what it holds. Microsoft has published no specification of the codec; FFmpeg's decoder is the reference.
public sealed class WmaVoiceParser : ICodecParser
{
    // Media objects are read up to this count, so a crafted file of millions of tiny ones stays bounded; the rest are counted but
    // not read. The packets (blocks) in them are limited separately, in VoiceStream.
    internal const int MaxPackets = 1 << 18;

    public const string CodecId = "wma-voice";

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Packets is not { } packets)
        {
            context.AddFinding(Severity.Info, "wma.payload-not-analyzed", "Windows Media Audio handed over as one payload is not analyzed; only the media objects of a container are.", context.Parent);
            return;
        }

        CodecConfiguration? configuration = context.Configuration;
        if (configuration?.Format is not { } format)
        {
            context.AddFinding(Severity.Info, "wma.no-format", "The container states no WAVEFORMATEX for the stream, so its Windows Media Audio is not analyzed.", context.Parent);
            return;
        }

        VoiceParameters parameters = VoiceParameters.Create(format, configuration.Data.Span);
        if (!parameters.IsUsable || configuration.AreSamplesEncrypted)
        {
            // What FFmpeg's decoder rejects: the stream is described by what the container states and its packets are counted.
            WmaHeaderOnly.Parse(context, VoiceReport.CodecName, Topics.WmaVoice, "wma.voice-not-read",
                "The frames of Windows Media Audio Voice are not read: FFmpeg's decoder rejects the stream's configuration, so the stream is described by what the container and the codec-specific data state, and its packets are counted.",
                (config, findings) => AddConfiguration(config, findings, parameters));
            return;
        }

        var findings = new FindingCollector();
        Node parent = context.Parent;
        AddConfiguration(configuration, findings, parameters);
        if (format.Channels != 1)
        {
            findings.AddNow(Severity.Info, "wma.voice-channels",
                $"The container states {format.Channels} channels; FFmpeg's decoder outputs mono whatever the container says (wmavoice.c), and a superframe holds one channel.", configuration.Parent);
        }

        VoiceStream stream = null!;
        VoiceNodes nodes = null!;
        stream = new VoiceStream(parameters, findings, i => nodes.PacketNode(i), i => nodes.SuperframeNode(i), context.CancellationToken);
        nodes = new VoiceNodes(stream.Records);

        int index = 0;
        long unread = 0;
        foreach (MappedBytes packet in packets)
        {
            if (index++ % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (index > MaxPackets)
            {
                unread++;
                continue;
            }

            stream.Add(packet);
        }

        if (unread > 0)
        {
            findings.AddNow(Severity.Info, "wma.records-limit",
                $"The stream has more than {MaxPackets} media objects; the {unread} after them are not read, so the statistics and the series cover the first {MaxPackets}.", parent);
        }

        if (stream.Truncated)
        {
            findings.AddNow(Severity.Info, "wma.records-limit",
                $"The stream has more than {stream.LimitCount} {stream.Limit}; the rest are not read, so the statistics and the series cover the first {stream.LimitCount}.", parent);
        }

        stream.Finish();
        VoiceReport.Finish(context, parent, findings, stream, nodes, format);
        findings.Flush(context);
    }

    // The nodes of the codec-specific data under the container's node for it, and the findings about what FFmpeg's decoder rejects
    // or cannot use.
    private static void AddConfiguration(CodecConfiguration configuration, FindingCollector findings, VoiceParameters parameters)
    {
        Node holder = configuration.Parent;
        MappedBytes data = configuration.Data;
        ReadOnlySpan<byte> bytes = data.Span;
        long offset = data.FileStart;

        foreach ((string code, string message) in parameters.Issues)
        {
            findings.AddNow(Severity.Warning, code, message + ".", holder);
        }

        if (bytes.Length >= VoiceParameters.FlagsOffset)
        {
            holder.Add("Professional Header", offset, VoiceParameters.FlagsOffset, NodeValue.Integer(VoiceParameters.FlagsOffset, "bytes"), VoiceExplain.ProfessionalHeader, Topics.WmaVoice);
        }

        if (bytes.Length >= VoiceParameters.FlagsOffset + 4)
        {
            uint flags = parameters.Flags;
            Node node = holder.Add("Flags", offset + VoiceParameters.FlagsOffset, 4, NodeValue.Hex(flags, 8), VoiceExplain.Flags, Topics.WmaVoice);
            long at = node.Offset;
            Bits(node, at, 0, 1, NodeValue.Boolean((flags & 1) != 0), "Uses Postfilter", VoiceExplain.FlagPostfilter, Topics.WmaVoice);
            Bits(node, at, 1, 2, NodeValue.Integer((flags >> 1) & 1), "Unread Flag", VoiceExplain.FlagUnread);
            Bits(node, at, 2, 6, NodeValue.Integer(parameters.DenoiseStrength), "Denoise Strength", VoiceExplain.FlagDenoiseStrength, Topics.WmaVoice);
            Bits(node, at, 6, 7, NodeValue.Boolean(parameters.HasDenoiseTiltCorrection), "Uses Denoise Tilt Correction", VoiceExplain.FlagTiltCorrection, Topics.WmaVoice);
            Bits(node, at, 7, 11, NodeValue.Integer(parameters.DcLevel), "DC Level", VoiceExplain.FlagDcLevel, Topics.WmaVoice);
            Bits(node, at, 11, 12, NodeValue.Integer((flags >> 11) & 1), "Unread Flag", VoiceExplain.FlagUnread);
            Bits(node, at, 12, 13, NodeValue.Integer(parameters.LspCount), "LSP Count", VoiceExplain.FlagLspCount, Topics.WmaVoiceFrames);
            Bits(node, at, 13, 14, NodeValue.Integer(parameters.LspQuantizerMode), "LSP Quantizer Mode", VoiceExplain.FlagLspQuantizerMode, Topics.WmaVoiceFrames);
            Bits(node, at, 14, 15, NodeValue.Integer(parameters.LspDefaultMode), "LSP Default Mode", VoiceExplain.FlagLspDefaultMode, Topics.WmaVoiceFrames);
            Bits(node, at, 15, 32, NodeValue.Hex(flags >> 15, 5), "Other Flags", VoiceExplain.FlagOthers);
        }

        if (bytes.Length >= VoiceParameters.TableOffset + 7)
        {
            ReadOnlySpan<byte> table = bytes[VoiceParameters.TableOffset..];
            Node node = holder.Add("Frame Type Table", offset + VoiceParameters.TableOffset, bytes.Length - VoiceParameters.TableOffset,
                NodeValue.Integer(VoiceParameters.FrameTypes, "frame types", "frame type"), VoiceExplain.FrameTypeTable, Topics.WmaVoiceFrames);
            long at = node.Offset;
            int[] slots = VoiceParameters.Slots(table);
            for (int type = 0; type < VoiceParameters.FrameTypes; type++)
            {
                int slot = slots[type];
                NodeValue code = slot >= 0 ? NodeValue.Integer(VoiceTables.FrameTypeCodeLengths[slot], "bits") : NodeValue.Text("no code");
                node.Add(Node.FromBits($"Frame Type {type}", at, type * 3, (type * 3) + 3, code,
                    description: VoiceExplain.FrameTypeGroup, topic: Topics.WmaVoiceFrames));
            }

            int used = VoiceParameters.FrameTypes * 3;
            int unused = (table.Length * 8) - used;
            if (unused > 0)
            {
                node.Add(Node.FromBits("Unused", at, used, table.Length * 8, NodeValue.Integer(unused, "bits"),
                    description: VoiceExplain.FrameTypeTableUnused));
            }
        }

        if (bytes.Length > VoiceParameters.ExtraDataSize)
        {
            holder.Add("Extra Data", offset + VoiceParameters.ExtraDataSize, bytes.Length - VoiceParameters.ExtraDataSize, NodeValue.Integer(bytes.Length - VoiceParameters.ExtraDataSize, "bytes", "byte"), VoiceExplain.ExtraData);
        }

        if (!parameters.IsUsable)
        {
            return;
        }

        if (parameters.OverfullGroup >= 0)
        {
            findings.AddNow(Severity.Info, "wma.voice-frame-type-overlap",
                $"The frame type table puts four frame types in group {parameters.OverfullGroup}; the fourth takes the first slot of the next group, which another type's entry may then overwrite. The comment of FFmpeg's decoder says a group should hold three (wmavoice.c: decode_vbmtree).", holder.Children.FirstOrDefault(c => c.Name == "Frame Type Table") ?? holder);
        }

        var unreachable = new List<int>();
        for (int type = 0; type < VoiceParameters.FrameTypes; type++)
        {
            if (parameters.SlotOfType[type] < 0)
            {
                unreachable.Add(type);
            }
        }

        if (unreachable.Count > 0)
        {
            findings.AddNow(Severity.Info, "wma.voice-frame-type-unreachable",
                $"The frame type table places {(unreachable.Count == 1 ? "type" : "types")} {string.Join(", ", unreachable)} in no slot that a frame type code can reach (codes 0 to 21), so a stream cannot use {(unreachable.Count == 1 ? "it" : "them")}.", holder.Children.FirstOrDefault(c => c.Name == "Frame Type Table") ?? holder);
        }
    }

    private static void Bits(Node flags, long offset, int first, int end, NodeValue value, string name, string description, string? topic = null) =>
        flags.Add(Node.FromBits(name, offset, first, end, value, description: description, topic: topic, order: BitOrder.LeastSignificantFirst));
}
