using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Wma;

// Windows Media Audio Voice (the format tag 0x000A): the 46 bytes of codec-specific data with the checks FFmpeg's decoder makes, then the packets,
// superframes and frames of the stream as docs/design/wma-voice-format.md describes them. A stream whose configuration the decoder rejects, or whose
// samples are encrypted, is described by what the container states, and its packets are counted.
public sealed class WmaVoiceParser : ICodecParser
{
    public const string CodecId = "wma-voice";

    // Media objects read: more are not.
    internal const int MaxPackets = 262_144;

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Packets is not { } packets)
        {
            context.AddFinding(Severity.Info, "wma.payload-not-analyzed", "Windows Media Audio handed over as one payload is not analyzed; only the media objects of a container are.", context.Parent);
            return;
        }

        if (context.Configuration?.Format is not { } format)
        {
            context.AddFinding(Severity.Info, "wma.no-format", "The container states no WAVEFORMATEX for the stream, so its Windows Media Audio is not analyzed.", context.Parent);
            return;
        }

        CodecConfiguration config = context.Configuration;
        VoiceParameters parameters = VoiceParameters.Create(format, config.Data.Span);
        if (!parameters.IsUsable || config.AreSamplesEncrypted)
        {
            WmaHeaderOnly.Parse(context, VoiceReport.CodecName, Topics.WmaVoice, "wma.voice-not-read",
                "The frames of Windows Media Audio Voice are not read: FFmpeg's decoder rejects the stream's configuration, so the stream is described by what the container and the codec-specific data state, and its packets are counted.",
                (configuration, findings) => AddConfiguration(configuration, parameters, findings, accepted: parameters.IsUsable), channelMaskInData: true);
            return;
        }

        var findings = new FindingCollector();
        AddConfiguration(config, parameters, findings, accepted: true);
        if (format.Channels != 1)
        {
            findings.AddNow(Severity.Info, "wma.voice-channels",
                $"The container states {Counts.Of(format.Channels, "channel", "channels")}; FFmpeg's decoder outputs mono whatever the container says (wmavoice.c), and a superframe holds one channel.", config.Parent);
        }

        Node parent = context.Parent;
        Node? streamNode = null;
        VoiceNodes? nodes = null;
        VoiceStream? voice = null;
        voice = new VoiceStream(parameters, findings, i => nodes!.PacketNode(i), i => nodes!.SuperframeNode(i), context.CancellationToken);
        nodes = new VoiceNodes(voice);

        int mediaObjects = 0;
        long afterLimit = 0;
        foreach (MappedBytes mediaObject in packets)
        {
            if (mediaObjects >= MaxPackets)
            {
                afterLimit++;
                continue;
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            mediaObjects++;
            voice.Add(mediaObject);
        }

        voice.Finish();

        VoiceTotals totals = voice.Totals;
        VoiceStream stream = voice;
        streamNode = parent.Add(new Node(VoiceReport.CodecName, parent.Offset, parent.Length, VoiceReport.Count(totals.Superframes, "superframe", "superframes"))
        {
            Kind = NodeKind.Stream,
            Description = VoiceExplain.Stream,
            Topic = Topics.WmaVoice,
        });

        WmaEncoder.Detected detected = WmaEncoder.Apply(context, streamNode, findings);
        Node? statistics = null;
        VoiceStats stats = default;
        if (totals.Packets > 0)
        {
            statistics = VoiceReport.Statistics(stream, format, config.Duration, parent, out double duration);
            streamNode.Add(statistics);
            stats = new VoiceStats(duration);
        }

        if (stream.Packets > 0)
        {
            var packetNodes = new BlockedChildren([], stream.Packets, nodes.PacketNode);
            streamNode.Add(new Node("Packets", parent.Offset, parent.Length, VoiceReport.Count(stream.Packets, "packet", "packets"), () => packetNodes)
            {
                Description = VoiceExplain.Packets,
                Topic = Topics.WmaVoice,
            });
            var superframeNodes = new BlockedChildren([], stream.Superframes, nodes.SuperframeNode);
            streamNode.Add(new Node("Superframes", parent.Offset, parent.Length, VoiceReport.Count(stream.Superframes, "superframe", "superframes"), () => superframeNodes)
            {
                Description = VoiceExplain.Superframes,
                Topic = Topics.WmaVoiceSuperframes,
            });
        }

        if (afterLimit > 0)
        {
            findings.AddNow(Severity.Info, "wma.records-limit",
                $"The stream has more than {MaxPackets} media objects; the {afterLimit} after them are not read, so the statistics and the series cover the first {MaxPackets}.", parent);
        }

        if (stream.Truncated)
        {
            findings.AddNow(Severity.Info, "wma.records-limit",
                $"The stream has more than {stream.LimitCount} {stream.Limit}; the rest are not read, so the statistics and the series cover the first {stream.LimitCount}.", parent);
        }

        double seconds = stats.Duration;
        IReadOnlyList<Series> series = statistics is null ? [] : VoiceReport.SeriesOf(stream, nodes, format.SampleRate, statistics);
        context.AddStream(new StreamInfo
        {
            Nodes = [streamNode],
            Codec = VoiceReport.CodecName,
            SampleRate = format.SampleRate,
            Channels = format.Channels,
            ChannelMode = null,
            ChannelLayout = format.Channels == 1 ? ChannelLayouts.Mono : format.Channels == 2 ? ChannelLayouts.Stereo : null,
            Samples = totals.Samples,
            Duration = seconds,
            AverageBitrate = seconds > 0 ? totals.PacketBytes * 8.0 / seconds / 1000 : null,
            IsVbr = config.IsVbr ?? false,
            Encoder = detected.Encoder,
            EncoderConfidence = detected.EncoderConfidence,
            Writer = detected.Writer,
            WriterConfidence = detected.WriterConfidence,
            Series = series,
        });

        findings.Flush(context);
    }

    private readonly record struct VoiceStats(double Duration);

    // The nodes of the 46 bytes of codec-specific data, as far as the bytes go, and the findings the configuration gives.
    private static void AddConfiguration(CodecConfiguration configuration, VoiceParameters parameters, FindingCollector findings, bool accepted)
    {
        Node holder = configuration.Parent;
        MappedBytes data = configuration.Data;
        ReadOnlySpan<byte> bytes = data.Span;
        long offset = data.FileStart;
        if (bytes.Length >= VoiceParameters.FlagsOffset)
        {
            holder.Add("Professional Header", offset, VoiceParameters.FlagsOffset, NodeValue.Integer(VoiceParameters.FlagsOffset, "bytes"), VoiceExplain.ProfessionalHeader, Topics.WmaVoice);
        }

        if (bytes.Length >= VoiceParameters.TableOffset)
        {
            AddFlags(holder, offset + VoiceParameters.FlagsOffset, BinaryPrimitives.ReadUInt32LittleEndian(bytes[VoiceParameters.FlagsOffset..]));
        }

        Node? table = null;
        if (bytes.Length >= VoiceParameters.TableOffset + 7)
        {
            table = AddTable(holder, offset + VoiceParameters.TableOffset, bytes[VoiceParameters.TableOffset..]);
        }

        if (bytes.Length > VoiceParameters.ExtraDataSize)
        {
            holder.Add("Extra Data", offset + VoiceParameters.ExtraDataSize, bytes.Length - VoiceParameters.ExtraDataSize,
                NodeValue.Integer(bytes.Length - VoiceParameters.ExtraDataSize, "bytes"), VoiceExplain.ExtraData);
        }

        foreach ((string code, string message) in parameters.Issues)
        {
            // The table's finding points to the table where there is one; the other checks to the node that holds the codec data.
            findings.AddNow(Severity.Warning, code, message + ".", code == "wma.voice-frame-type-table" && table is not null ? holder : holder);
        }

        if (!accepted)
        {
            return;
        }

        if (table is null)
        {
            return;
        }

        if (parameters.OverfullGroup >= 0)
        {
            int group = parameters.OverfullGroup;
            findings.AddNow(Severity.Info, "wma.voice-frame-type-overlap",
                $"The frame type table puts four frame types in group {group}; the fourth takes the first slot of the next group, which another type's entry may then overwrite. The comment of FFmpeg's decoder says a group should hold three (wmavoice.c).", table);
        }

        int[] unreachable = [.. Enumerable.Range(0, VoiceParameters.FrameTypes).Where(t => parameters.SlotOfType[t] < 0)];
        if (unreachable.Length > 0)
        {
            string list = string.Join(", ", unreachable);
            findings.AddNow(Severity.Info, "wma.voice-frame-type-unreachable",
                $"The frame type table places {(unreachable.Length == 1 ? "type" : "types")} {list} in no slot that a frame type code can reach (codes 0 to 21), so a stream cannot use {(unreachable.Length == 1 ? "it" : "them")}.", table);
        }
    }

    private static void AddFlags(Node holder, long offset, uint flags)
    {
        Node node = holder.Add("Flags", offset, 4, NodeValue.Hex((long)flags, 8), VoiceExplain.Flags, Topics.WmaVoice);
        void Bit(int first, int width, NodeValue value, string name, string description) =>
            node.Add(Node.FromBits(name, offset, first, first + width, value, description: description, topic: Topics.WmaVoice, order: BitOrder.LeastSignificantFirst));

        Bit(0, 1, NodeValue.Boolean((flags & 1) != 0), "Uses Postfilter", VoiceExplain.FlagPostfilter);
        Bit(1, 1, NodeValue.Integer((flags >> 1) & 1), "Unread Flag", VoiceExplain.FlagUnread);
        Bit(2, 4, NodeValue.Integer((flags >> 2) & 15), "Denoise Strength", VoiceExplain.FlagDenoiseStrength);
        Bit(6, 1, NodeValue.Boolean((flags & 0x40) != 0), "Uses Denoise Tilt Correction", VoiceExplain.FlagDenoiseTilt);
        Bit(7, 4, NodeValue.Integer((flags >> 7) & 15), "DC Level", VoiceExplain.FlagDcLevel);
        Bit(11, 1, NodeValue.Integer((flags >> 11) & 1), "Unread Flag", VoiceExplain.FlagUnread);
        Bit(12, 1, NodeValue.Integer((flags & 0x1000) != 0 ? 16 : 10), "LSP Count", VoiceExplain.FlagLspCount);
        Bit(13, 1, NodeValue.Integer((flags >> 13) & 1), "LSP Quantizer Mode", VoiceExplain.FlagLspQuantizerMode);
        Bit(14, 1, NodeValue.Integer((flags >> 14) & 1), "LSP Default Mode", VoiceExplain.FlagLspDefaultMode);
        Bit(15, 17, NodeValue.Hex((long)(flags >> 15), 5), "Other Flags", VoiceExplain.FlagOthers);
    }

    private static Node AddTable(Node holder, long offset, ReadOnlySpan<byte> table)
    {
        int[] slots = VoiceParameters.SlotsOfTable(table);
        Node node = holder.Add("Frame Type Table", offset, table.Length, NodeValue.Integer(VoiceParameters.FrameTypes, "frame types", "frame type"), VoiceExplain.FrameTypeTable, Topics.WmaVoice);
        for (int t = 0; t < VoiceParameters.FrameTypes; t++)
        {
            NodeValue value = slots[t] < 0 ? "no code" : NodeValue.Integer(VoiceTables.FrameTypeCodeLengths[slots[t]], "bits");
            node.Add(Node.FromBits($"Frame Type {t}", offset, 3 * t, (3 * t) + 3, value, description: VoiceExplain.FrameTypeEntry, topic: Topics.WmaVoice));
        }

        int unused = (table.Length * 8) - (3 * VoiceParameters.FrameTypes);
        if (unused > 0)
        {
            node.Add(Node.FromBits("Unused", offset, 3 * VoiceParameters.FrameTypes, table.Length * 8, NodeValue.Integer(unused, "bits"), description: VoiceExplain.Unused, topic: Topics.WmaVoice));
        }

        return node;
    }
}
