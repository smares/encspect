using Encspect.Core;

namespace Encspect.Formats.Aac;

// adif_header (ISO/IEC 14496-3 1.A.2.1, Table 1.A.4; ISO/IEC 13818-7 6.1): one header, then raw data
// blocks with no length or sync between them (raw_data_stream, Table 1.A.3), each byte-aligned after
// its END element. Each block's end is only known once every element, the Huffman-coded spectral
// data included, is read, so a block that cannot be read ends the reading: nothing marks where the
// next one starts.
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

    public static void Parse(ByteReader reader, Node parent, AnalysisContext context, IReadOnlyList<EncoderClaim> claims)
    {
        var leading = new List<Node>();
        var blocks = new ChunkedList<Block>();
        var kept = new KeptBlocks();
        Node region = parent.Add(RegionNode(reader.Start, reader.Length, leading, blocks, kept));
        Header? header;
        try
        {
            header = Read(reader, region, out Node? node);
            if (node is not null)
            {
                leading.Add(node);
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

        var config = new AacConfig
        {
            ObjectType = header.ObjectType,
            CoreSampleRate = header.SampleRate,
            ChannelConfiguration = 0,
            Channels = header.Channels,
            Layout = header.Layout,
            CoreFrameLength = 1024,
        };
        long dataStart = reader.Start + header.Length;
        if (!StreamSetup.CanRead(config))
        {
            long length = reader.End - dataStart;
            leading.Add(new Node("Raw Data Blocks", dataStart, length, NodeValue.Integer(length, "bytes")) { Description = Explain.AdifBlocks, Topic = Topics.RawDataBlocks });
            context.AddStream(new StreamInfo { Nodes = [region], Codec = config.Codec, SampleRate = config.OutputSampleRate, Channels = header.Channels, ChannelMode = config.ChannelMode, ChannelLayout = config.OutputLayout, IsVbr = header.IsVbr });
            return;
        }

        var findings = new FindingCollector();
        var log = new BlockLog(new StreamSetup(config), findings);
        kept.Store = log.Store;
        kept.Setup = log.Setup;
        (string Text, int Index, long Offset)? signature = null;
        long position = ReadBlocks(reader, dataStart, log, blocks, findings, context.CancellationToken, ref signature);
        if (position < reader.End)
        {
            long rest = reader.End - position;
            var unread = new Node("Unread Bytes", position, rest, NodeValue.Integer(rest, "bytes")) { Description = Explain.AdifUnread };
            leading.Add(unread);
            findings.AddNow(Severity.Warning, "aac.adif-blocks-unread",
                $"The {rest} bytes from 0x{position:X} are not read: ADIF marks no raw data block's start, so none can be found after one that cannot be read to its end.", unread);
        }

        config = config with { SbrFound = log.Tally.SbrBlocks > 0, PsFound = log.Tally.PsBlocks > 0 };
        var figures = new StreamFigures(config.OutputSampleRate);
        for (int i = 0; i < blocks.Count && blocks[i].IsRead; i++)
        {
            figures.Add(blocks[i].Offset, blocks[i].Length, config.OutputFrameLength);
        }

        Node FirstBlock() => blocks.Count > 0 ? BlockNode(blocks, 0, null, null) : region;
        log.ReportSignaling(config, FirstBlock(), "ADIF");
        Node Cover(string name, NodeValue? value, NodeKind kind, string description, string? topic = null) =>
            new(name, region.Offset, region.Length, value) { Kind = kind, Description = description, Topic = topic };
        Node? encoder = AacEncoder.Detect(
            signature is { } found ? new AacEncoder.Signature(found.Text, BlockNode(blocks, found.Index, null, null), found.Index) : null,
            claims,
            (name, value, description) => Cover(name, value, NodeKind.Detail, description, name == "Encoder" ? Topics.EncoderDetection : null),
            (claim, text, name) => findings.Add(Severity.Info, "aac.encoder-claim-differs", claim.Source, _ =>
                $"The {claim.Description} names \"{text}\", but the stream points to {name}; the tag may have been written by other software, or kept when the audio was re-encoded."),
            tally: log.Tally);
        if (encoder is not null)
        {
            leading.Add(encoder);
        }

        Node statistics = figures.Statistics((name, value, kind, description) => Cover(name, value, kind, description), "Block", "Blocks", out Node bitrate);
        CodingStatistics.Add(statistics, log.Tally, log.Setup, (name, value, description, topic) => Cover(name, value, NodeKind.Detail, description, topic));
        leading.Add(statistics);
        findings.Flush(context);

        string? confidence = encoder?.Children.FirstOrDefault(c => c.Name == "Confidence")?.Value?.ToString();
        context.AddStream(new StreamInfo
        {
            Nodes = [region],
            Codec = config.Codec,
            SampleRate = config.OutputSampleRate,
            Channels = config.OutputChannels ?? 0,
            ChannelMode = config.ChannelMode,
            ChannelLayout = config.OutputLayout,
            Samples = figures.Samples,
            Duration = figures.Duration,
            AverageBitrate = figures.AverageBitrate,
            IsVbr = header.IsVbr,
            Encoder = encoder?.Value?.ToString(),
            EncoderConfidence = Enum.TryParse(confidence, out Confidence parsed) ? parsed : null,
            Series = figures.Series(bitrate, context.CancellationToken),
        });
    }

    // Reads raw data blocks one after another from start; returns where the reading ended. The data
    // is read through a window twice the longest block, moved on once the next block could run past it.
    private static long ReadBlocks(ByteReader reader, long start, BlockLog log, ChunkedList<Block> blocks, FindingCollector findings, CancellationToken cancellation,
        ref (string Text, int Index, long Offset)? signature)
    {
        var cache = new BlockCache(reader);
        var window = new byte[2 * BlockLog.MaxBlockBytes];
        long windowStart = start;
        int windowLength = 0;
        long position = start;
        while (position < reader.End)
        {
            if (blocks.Count % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            if (position + BlockLog.MaxBlockBytes > windowStart + windowLength && windowStart + windowLength < reader.End)
            {
                windowStart = position;
                windowLength = cache.Read(position, window);
            }

            int at = (int)(position - windowStart);
            ReadOnlySpan<byte> data = window.AsSpan(at, Math.Min(windowLength - at, BlockLog.MaxBlockBytes));
            var where = new UnitBytes(position, data.Length, null);
            int index = blocks.Count;
            long blockStart = position;
            int available = data.Length;
            (long skeleton, BlockResult read) = log.Read(data, 0, available * 8, where,
                () => new Node("Raw Data Block", blockStart, available) { Kind = NodeKind.Frame, Description = Explain.RawDataBlock, Topic = Topics.RawDataBlocks });
            if (!read.Ok)
            {
                // The block that breaks reaches as far as a block can, where its finding places it.
                blocks.Add(new Block(position, available, read.Elements, skeleton, false));
                return position + available;
            }

            int length = (read.End + 7) / 8;
            blocks.Add(new Block(position, length, read.Elements, skeleton, true));
            if (signature is null && log.Tally.FirstText is { } text)
            {
                signature = (text, index, position);
            }

            position += length;
        }

        return position;
    }

    // Built in a method of its own so that the node's factory captures these arguments and not the
    // locals of Parse, whose block reader, findings and reader would stay alive with the report, and
    // with them the input.
    private static Node RegionNode(long offset, long length, List<Node> leading, ChunkedList<Block> blocks, KeptBlocks kept) =>
        new("ADIF", offset, length, null, () => new BlockedChildren(leading, blocks.Count, i => BlockNode(blocks, i, kept.Store, kept.Setup)))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Adif,
            Topic = Topics.Adts,
        };

    // What the blocks' nodes are rebuilt from, set once the blocks are read.
    private sealed class KeptBlocks
    {
        public UnitStore? Store { get; set; }

        public StreamSetup? Setup { get; set; }
    }

    private static Node BlockNode(ChunkedList<Block> blocks, int index, UnitStore? store, StreamSetup? setup)
    {
        Block block = blocks[index];
        return new Node("Raw Data Block", block.Offset, block.Length, BlockLog.ElementList(block.Elements),
            store is null || setup is null ? null : () => BlockLog.Nodes(store, setup, block.Skeleton, new UnitBytes(block.Offset, block.Length, null)))
        {
            Kind = NodeKind.Frame,
            Description = Explain.RawDataBlock,
            Topic = Topics.RawDataBlocks,
        };
    }

    private readonly record struct Block(long Offset, int Length, int Elements, long Skeleton, bool IsRead);

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
