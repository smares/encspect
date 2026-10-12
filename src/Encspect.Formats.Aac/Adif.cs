using Encspect.Core;

namespace Encspect.Formats.Aac;

// adif_header (ISO/IEC 14496-3 1.A.2.1, Table 1.A.4; ISO/IEC 13818-7 6.1): one header, then raw data
// blocks with no length or sync between them, so their bounds need every element parsed, the
// Huffman-coded spectral data included, which is not done yet.
internal static class Adif
{
    // The header with its program config elements fits in a few hundred bytes; comments make it
    // longer, up to 16 elements of 255 bytes each.
    private const int MaxHeader = 1 << 13;

    public static bool Plausible(ByteReader reader)
    {
        try
        {
            return Read(reader, new Node("ADIF", reader.Start, 1), out _) is not null;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    public static void Parse(ByteReader reader, Node parent, AnalysisContext context)
    {
        var region = parent.Add(new Node("ADIF", reader.Start, reader.Length) { Kind = NodeKind.Stream, Description = Explain.Adif, Topic = Topics.Adts });
        Header? header;
        try
        {
            header = Read(reader, region, out Node? node);
            if (node is not null)
            {
                region.Add(node);
            }
        }
        catch (EndOfStreamException)
        {
            context.AddFinding(Severity.Warning, "aac.adif-header-truncated", "The ADIF header ends inside a field.", region);
            return;
        }

        if (header is null)
        {
            context.AddFinding(Severity.Warning, "aac.adif-header-invalid", "The ADIF header holds no program config element with channels.", region);
            return;
        }

        long dataStart = reader.Start + header.Length;
        long dataLength = reader.End - dataStart;
        Node data = region.Add(new Node("Raw Data Blocks", dataStart, dataLength, NodeValue.Integer(dataLength, "bytes")) { Description = Explain.AdifBlocks, Topic = Topics.RawDataBlocks });
        context.AddFinding(Severity.Info, "aac.adif-blocks-not-analyzed",
            "ADIF delimits no raw data blocks; finding them needs the spectral data of every element parsed, which is not done yet, so they are not analyzed.", data);

        var config = new AacConfig
        {
            ObjectType = header.ObjectType,
            CoreSampleRate = header.SampleRate,
            ChannelConfiguration = 0,
            Channels = header.Channels,
            Layout = header.Layout,
            CoreFrameLength = 1024,
        };
        if (config.ImplicitPossible)
        {
            context.AddFinding(Severity.Info, "aac.implicit-signaling-not-checked",
                "ADIF cannot signal SBR or PS explicitly. They can be signaled implicitly, in the fill elements of the raw data blocks, which are not checked yet; the stream may be HE-AAC or HE-AAC v2.", region);
        }

        // A constant bitrate gives the duration; a variable one states only its peak.
        double? duration = header.IsVbr || header.Bitrate == 0 ? null : dataLength * 8.0 / header.Bitrate;
        context.AddStream(new StreamInfo
        {
            Nodes = [region],
            Codec = config.Codec,
            SampleRate = config.OutputSampleRate,
            Channels = config.OutputChannels ?? 0,
            ChannelMode = config.ChannelMode,
            ChannelLayout = config.OutputLayout,
            Duration = duration ?? 0,
            AverageBitrate = duration is null ? null : header.Bitrate / 1000.0,
            IsVbr = header.IsVbr,
        });
    }

    private static Header? Read(ByteReader reader, Node parent, out Node? node)
    {
        int length = (int)Math.Min(MaxHeader, reader.Length);
        byte[] bytes = reader.ReadBytesAt(reader.Start, length);
        var fields = new BitFields(new MappedBytes(bytes, reader.Start));
        var header = new Node("ADIF Header", reader.Start, 1);
        fields.Read(header, "ADIF ID", 32, Explain.AdifId, _ => "ADIF");
        if (fields.Flag(header, "HasCopyrightId", Explain.HasCopyrightId))
        {
            fields.Read(header, "Copyright ID", 72 - 63, Explain.CopyrightId, v => NodeValue.Integer(v));
            fields.Read(header, "Copyright ID (continued)", 63, Explain.CopyrightId, v => NodeValue.Hex(v, 16));
        }

        fields.Flag(header, "IsOriginal", Explain.AdifOriginal);
        fields.Flag(header, "IsHome", Explain.AdifHome);
        bool isVbr = fields.Read(header, "Bitstream Type", 1, Explain.BitstreamType, topic: Topics.BitrateModes, describe: v => v == 0 ? "0 (constant bitrate)" : "1 (variable bitrate)") != 0;
        long bitrate = fields.Read(header, "Bitrate", 23, Explain.AdifBitrate, v => NodeValue.Integer(v, "bit/s"));
        int count = (int)fields.Read(header, "Program Config Elements", 4, Explain.PceCount, v => NodeValue.Integer(v + 1)) + 1;
        int? channels = null;
        IReadOnlyList<Speaker>? layout = null;
        int objectType = 2;
        int sampleRate = 0;
        for (int i = 0; i < count; i++)
        {
            if (!isVbr)
            {
                fields.Read(header, "Buffer Fullness", 20, Explain.AdifBufferFullness);
            }

            int start = fields.Position;
            var pce = new Node("PCE", 0, 0);
            int profile = (int)((fields.Look(6) >> 0) & 3);
            int rateIndex = (int)(fields.Look(10) & 0xF);
            int elementChannels = ProgramConfigElement.Read(fields, pce, 0, (_, _, _, _) => { }, out IReadOnlyList<Speaker> elementLayout);
            header.Add(fields.Span("Program Config Element", start, NodeValue.Integer(elementChannels, "channels"), Explain.ProgramConfigElement, pce, Topics.ChannelLayouts));
            if (channels is null)
            {
                channels = elementChannels;
                layout = elementLayout;
                objectType = profile + 1;
                sampleRate = AacTables.SampleRate(rateIndex) ?? 0;
            }
        }

        fields.Align(0);
        node = fields.Span("ADIF Header", 0, null, Explain.AdifHeader, header);
        return channels is > 0 && sampleRate > 0 ? new Header(fields.Position / 8, objectType, sampleRate, channels.Value, layout, isVbr, bitrate) : null;
    }

    private sealed record Header(int Length, int ObjectType, int SampleRate, int Channels, IReadOnlyList<Speaker>? Layout, bool IsVbr, long Bitrate);
}
