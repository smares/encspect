using System.Buffers.Binary;
using System.Numerics;
using Encspect.Core;

namespace Encspect.Formats.Tak;

internal readonly record struct ChunkEntry(long Offset, string Id, uint Size, bool IsCut);

// What the walk read of the stored header or footer: the RIFF chunks listed, the fmt chunk's fields and lossyWAV's fact text.
internal sealed class HeaderInfo
{
    public string Name { get; init; } = string.Empty;

    public long At { get; init; }

    public long Held { get; init; }

    public string? Container { get; init; }

    public WavFormat? Format { get; init; }

    public List<ChunkEntry> Chunks { get; } = [];

    public Node? FactNode { get; set; }
}

// The wave data's header and footer, named as Monkey's Audio names them ("WAV Header Data", "WAV Terminating Data"). The format and the fact chunk are read from
// the header only; both list the RIFF chunks they hold.
internal static class HeaderData
{
    private const int MaxListed = 64;

    private const int MaxFactBytes = 1 << 16;

    public static HeaderInfo Analyze(State s, ByteReader reader, long at, long held, bool isHeader, bool isRiffFile)
    {
        string? container = isHeader ? WavHeader.Container(reader, at, held) : null;
        WavFormat? format = isHeader && container is not null ? WavHeader.Read(reader, at, held) : null;
        var info = new HeaderInfo { Name = isHeader ? "WAV Header Data" : "WAV Terminating Data", At = at, Held = held, Container = container, Format = format };
        long position = at;
        bool isRiff = container is WavHeader.RiffContainer or WavHeader.Rf64Container or WavHeader.Bw64Container;
        if (isRiff)
        {
            position = at + 12;
        }
        else if (isHeader || !isRiffFile)
        {
            return info;
        }

        long end = at + held;
        while (info.Chunks.Count < MaxListed && end - position >= 8)
        {
            byte[] head = reader.ReadBytesAt(position, 8);
            string id = System.Text.Encoding.Latin1.GetString(head, 0, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
            bool isCut = position + 8 + size > end;
            info.Chunks.Add(new ChunkEntry(position, id, size, isCut));
            if (isCut || id == "data")
            {
                break;
            }

            position += 8 + size + (size & 1);
        }

        if (isHeader && isRiff)
        {
            AddFactChunk(s, reader, at, held, info);
        }

        return info;
    }

    // The fact chunk before the data, when it holds lossyWAV's text.
    private static void AddFactChunk(State s, ByteReader reader, long at, long held, HeaderInfo info)
    {
        byte[] bytes = reader.ReadBytesAt(at, (int)Math.Min(held, MaxFactBytes));
        if (bytes.Length < 12 || !FactText.Find(bytes, 12, out int bodyAt, out int bodyLength) || FactText.Read(bytes.AsSpan(bodyAt, bodyLength)) is not { } text)
        {
            return;
        }

        long chunkAt = at + bodyAt - 8;
        var chunk = new Node("Fact Chunk", chunkAt, 8 + bodyLength) { Description = Explain.FactChunk, Topic = Topics.RiffChunks };
        chunk.Add("Chunk ID", chunkAt, 4, "fact", Explain.ChunkId, Topics.RiffChunks);
        chunk.Add("Chunk Size", chunkAt + 4, 4, NodeValue.Integer(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bodyAt - 4)), "bytes", "byte"), Explain.ChunkSize, Topics.RiffChunks);
        Node node = chunk.Add(text.ToNode(chunkAt + 8, bodyLength));
        info.FactNode = chunk;
        if (text.ToClaim(node, "fact chunk in the stored WAV header") is { } claim)
        {
            s.Context.AddEncoderClaim(claim);
        }
        else
        {
            s.Findings.Add(Severity.Info, "tak.header-fact-text-unrecognized", node, _ => FactText.UnrecognizedFinding);
        }
    }

    public static Node Node(HeaderInfo info, string description, bool isHeader)
    {
        string value = $"{Counts.Of(info.Held, "byte", "bytes")}{(info.Container is { } container ? ", " + container : string.Empty)}";
        var node = new Node(info.Name, info.At, info.Held, value) { Description = description, Topic = isHeader ? Topics.RiffChunks : Topics.Metadata };
        if (info.Format is { } format)
        {
            AddFormat(node, format);
        }

        if (info.Chunks.Count > 0)
        {
            ChunkEntry last = info.Chunks[^1];
            var list = new Node("Chunks", info.Chunks[0].Offset, last.Offset + 8 + Math.Min(last.Size, Math.Max(0, info.At + info.Held - last.Offset - 8)) - info.Chunks[0].Offset,
                NodeValue.Integer(info.Chunks.Count, "chunks", "chunk"))
            { Description = Explain.ChunkList, Topic = Topics.RiffChunks };
            foreach (ChunkEntry entry in info.Chunks)
            {
                long length = Math.Min(8 + (long)entry.Size, info.At + info.Held - entry.Offset);
                var chunk = new Node($"{entry.Id.Trim()} Chunk", entry.Offset, length, NodeValue.Integer(entry.Size, "bytes", "byte")) { Description = Explain.Chunk, Topic = Topics.RiffChunks };
                chunk.Add("Chunk ID", entry.Offset, 4, entry.Id, Explain.ChunkId, Topics.RiffChunks);
                chunk.Add("Chunk Size", entry.Offset + 4, 4, NodeValue.Integer(entry.Size, "bytes", "byte"), Explain.ChunkSize, Topics.RiffChunks);
                list.Add(chunk);
            }

            node.Add(list);
        }

        if (info.FactNode is { } fact)
        {
            node.Add(fact);
        }

        return node;
    }

    private static void AddFormat(Node data, WavFormat format)
    {
        if (format.Tag >= 0)
        {
            data.Add("Format Tag", format.TagAt, 2, WavHeader.TagText(format.Tag), Explain.FormatTag, Topics.WaveFormat);
            data.Add("Channels", format.ChannelsAt, 2, NodeValue.Integer(format.Channels, "channels", "channel"), Explain.WavChannels, Topics.WaveFormat);
        }

        if (format.Mask is { } value)
        {
            Node mask = data.Add("Channel Mask", format.MaskAt, 4, NodeValue.Hex(value, 8), Explain.ChannelMask, Topics.WaveFormatExtensible);
            if (value != 0)
            {
                mask.Add("Speakers", format.MaskAt, 4, ChannelLayouts.Speakers(ChannelLayouts.WaveMaskSpeakers(value)), Explain.Speakers, Topics.Channels);
            }
        }
    }

    // The findings about what the stored header says, once the stream info is known. dataNode builds the node of the wave data's header (the findings point into it).
    public static void Findings(State s, HeaderInfo info, Func<Node> dataNode)
    {
        if (info.Format is not { } format)
        {
            return;
        }

        Node Field(string field) => dataNode().Children.FirstOrDefault(c => c.Name == field) ?? dataNode();
        if (format.Problem != WavProblem.None)
        {
            string problem = format.Problem.ToString();
            long size = format.ProblemSize;
            long heldBytes = format.ProblemHeld;
            long problemAt = format.ProblemAt;
            string chunk = format.ProblemChunk.Trim();
            s.Findings.Add(Severity.Warning, "tak.wav-header-damaged", dataNode, _ => format.Problem switch
            {
                WavProblem.CutInChunk when size < 0 => $"The stored WAV header ends inside a chunk header at 0x{problemAt:X}, before any fmt chunk, so the channels and the channel mask are not read.",
                WavProblem.CutInChunk => $"The stored WAV header ends inside the {chunk} chunk at 0x{problemAt:X}, which states {Counts.Of(size, "byte", "bytes")} and has {heldBytes} in the header, before any fmt chunk, so the channels and the channel mask are not read.",
                WavProblem.CutInFormat => $"The stored WAV header ends inside the fmt chunk at 0x{problemAt:X}, which states {Counts.Of(size, "byte", "bytes")} and has {heldBytes} in the header; the fields it holds are read as far as they go.",
                WavProblem.FormatShort => $"The fmt chunk at 0x{problemAt:X} states {Counts.Of(size, "byte", "bytes")}, fewer than the 16 of the common fields of a WAVE format.",
                WavProblem.NoFormat => "The stored WAV header holds no fmt chunk before its data chunk or its end, so the channels and the channel mask are not read.",
                WavProblem.ChunkSizeInvalid => $"The Wave64 chunk at 0x{problemAt:X} states {Counts.Of(size, "byte", "bytes")}, but a size counts the 24 bytes of the chunk's own header, so none is under 24; the channels and the channel mask are not read.",
                _ => $"The stored WAV header holds more than {WavHeader.MaxChunksBeforeFormat} chunks before a fmt chunk, which the analyzer does not follow, so the channels and the channel mask are not read.",
            }, problem);
        }

        if (format.Tag == WavHeader.WaveExtensible && format.Mask is null && format.Problem == WavProblem.None)
        {
            s.Findings.Add(Severity.Warning, "tak.wav-header-damaged", () => Field("Format Tag"), _ =>
                "The format tag is 0xFFFE (WAVEFORMATEXTENSIBLE), whose cbSize is at least 22 for the valid bits, the channel mask and the sub-format, but the fmt chunk holds too little for them, so the channel mask is not read.", "Extensible");
        }

        if (format.Channels > 0 && s.Records.Stream is { } stream && format.Channels != stream.Channels)
        {
            int stated = format.Channels;
            int channels = stream.Channels;
            s.Findings.Add(Severity.Warning, "tak.wav-channels-differ", () => Field("Channels"), _ =>
                $"The fmt chunk in the stored WAV header states {Counts.Of(stated, "channel", "channels")}, but the stream info states {Counts.Of(channels, "channel", "channels")}; the header a decoder gives back does not describe the audio it writes.");
        }

        if (format.Mask is { } mask && mask != 0 && (mask & 0x80000000) == 0 && s.Records.Stream is { } info2 && BitOperations.PopCount(mask) != info2.Channels)
        {
            int positions = BitOperations.PopCount(mask);
            int channels = info2.Channels;
            s.Findings.Add(Severity.Info, "tak.channel-mask-mismatch", () => Field("Channel Mask"), _ =>
                $"The channel mask names {Counts.Of(positions, "speaker", "speakers")}, but the stream info states {Counts.Of(channels, "channel", "channels")}; {(positions > channels ? "the extra bits, the most significant, are ignored" : "the remaining channels are assigned to no speaker")}.", positions > channels ? "more" : "fewer");
        }
    }
}
