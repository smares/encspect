using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.DtsHd;

// The .dtshd file: chunks one after another, each an 8-character ID, a 64-bit big endian size and a payload, the first being DTSHDHDR
// (ATSC A/103:2014 Annex E; FFmpeg's libavformat/dtshddec.c; MediaInfo's File_Dts.cpp). STRMDATA holds the DTS-HD stream, which the host's
// codec route hands to the DTS parser; the other chunks are metadata, and BUILDVER is text that names the build that wrote the stream.
public sealed class DtsHdParser : IFormatParser
{
    private const int ChunkHeader = 16;
    private const int MaxCounted = SniffResult.MaxFrames;

    // A file has about 15 chunks; a file of millions of empty ones must not become millions of nodes or a walk through all of it.
    internal const int MaxChunks = 1024;

    // FFmpeg refuses sizes beyond this.
    private const ulong MaxSize = 1UL << 61;

    private static readonly byte[] Signature = "DTSHDHDR"u8.ToArray();

    public string Name => "DTS-HD";

    // The signature at the start with a chain of chunks that fit: class 100, above every container's 95 and every chain of audio frames; a
    // chain that breaks after the first chunk is still a file whose first eight bytes say what it is (80, as for WavPack's header followed
    // by bytes that are none). No checksum is defined, so no frame is verified.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (!reader.MatchesAt(reader.Start, Signature))
        {
            return SniffResult.None;
        }

        long position = reader.Start;
        int frames = 0;
        bool broken = false;
        while (frames < MaxCounted && position < reader.End)
        {
            if (reader.End - position < ChunkHeader)
            {
                break;
            }

            if (!TryChunk(reader, position, out string id, out ulong size))
            {
                broken = true;
                break;
            }

            long end = position + ChunkHeader + (long)Math.Min(size, long.MaxValue / 4);
            if (end > reader.End)
            {
                // The data cuts the chunk short: it is not counted, but STRMDATA's size is the stream's, which a cut file ends inside.
                break;
            }

            frames++;
            position = end;
        }

        if (frames == 0)
        {
            return new SniffResult(broken ? 70 : 80, reader.Start, 0, Note: "the signature DTSHDHDR");
        }

        return new SniffResult(broken && frames < 2 ? 80 : 100, reader.Start, frames, End: position);
    }

    // Where the chunks that follow one another from the start end by their own sizes.
    public long? DeclaredEnd(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (!reader.MatchesAt(reader.Start, Signature))
        {
            return null;
        }

        long position = reader.Start;
        for (int count = 0; position < reader.End && count < MaxChunks; count++)
        {
            reader.CancellationToken.ThrowIfCancellationRequested();
            if (!TryChunk(reader, position, out _, out ulong size, strict: false))
            {
                break;
            }

            position += ChunkHeader + (long)Math.Min(size, long.MaxValue / 4);
        }

        return position;
    }

    // A chunk header at position: an ID of printable characters and a size FFmpeg accepts (4 to 2^61); the parser itself reads a smaller size,
    // with a finding, so the chain's declared end does not ask for it (strict: false).
    private static bool TryChunk(ByteReader reader, long position, out string id, out ulong size, bool strict = true)
    {
        id = string.Empty;
        size = 0;
        if (reader.End - position < ChunkHeader)
        {
            return false;
        }

        byte[] header = reader.ReadBytesAt(position, ChunkHeader);
        foreach (byte b in header.AsSpan(0, 8))
        {
            if (b is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        id = Encoding.ASCII.GetString(header, 0, 8);
        size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
        return size <= MaxSize && (!strict || size >= 4);
    }

    public void Parse(ParseContext context)
    {
        ByteReader reader = context.Reader;
        Node root = context.Root;
        var state = new State(context);
        long position = reader.Start;
        int count = 0;
        bool capped = false;
        while (position < reader.End)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (count++ >= MaxChunks)
            {
                Trailing(state, position, reader.End, "dtshd.chunk-count", $"The file has more than {MaxChunks} chunks; the bytes from 0x{position:X} are not read, so the file may hold more than was found.");
                capped = true;
                position = reader.End;
                break;
            }

            long remaining = reader.End - position;
            if (remaining < ChunkHeader)
            {
                Trailing(state, position, reader.End, "dtshd.trailing-data", $"{remaining} bytes after the last chunk are too few for a chunk header.");
                position = reader.End;
                break;
            }

            byte[] header = reader.ReadBytesAt(position, ChunkHeader);
            bool printable = header.AsSpan(0, 8).IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) < 0;
            ulong size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
            if (!printable || size > MaxSize)
            {
                Trailing(state, position, reader.End, "dtshd.chunk-header", printable
                    ? $"The chunk at 0x{position:X} has a size of {size} bytes, more than FFmpeg reads (2^61); the bytes from there are not read."
                    : $"The bytes at 0x{position:X} are not a chunk header (an ID of eight printable characters); the bytes from there are not read.");
                position = reader.End;
                break;
            }

            string id = Encoding.ASCII.GetString(header, 0, 8);
            long available = reader.End - position - ChunkHeader;
            long end = position + ChunkHeader + (long)Math.Min(size, (ulong)available);
            bool truncated = size > (ulong)available;
            var chunk = new Node(id, position, end - position, NodeValue.Integer((long)Math.Min(size, long.MaxValue), "bytes", "byte"))
            {
                Description = Describe(id),
                Topic = Topics.File,
            };
            root.Add(chunk);
            chunk.Add(new Node("Chunk Size", position + 8, 8, NodeValue.Integer((long)size, "bytes", "byte")) { Description = Explain.ChunkSize, Topic = Topics.File });
            if (truncated)
            {
                state.Context.AddFinding(Severity.Warning, "dtshd.chunk-truncated", $"The {id} chunk at 0x{position:X} has a size of {size} bytes, but the file has {available} after its header.", chunk);
            }

            if (size < 4)
            {
                state.Note("dtshd.chunk-size", $"The {id} chunk at 0x{position:X} has a size of {size} bytes; FFmpeg refuses a size under 4.", chunk);
            }

            if ((position - reader.Start) % 4 != 0)
            {
                state.Note("dtshd.chunk-misaligned", $"The {id} chunk starts at 0x{position:X}, not on a 32-bit boundary; the standard pads every chunk to one (A/103 E.2.1).", chunk);
            }

            if (state.Chunks.Count == 0 && id != "DTSHDHDR")
            {
                state.Context.AddFinding(Severity.Warning, "dtshd.header-first", $"The first chunk is {id}, but DTSHDHDR shall be the first chunk of the file (A/103 E.2.2).", chunk);
            }

            ChunkReader.Read(state, reader, chunk, id, position + ChunkHeader, end, truncated);
            state.Chunks.Add(chunk);
            position = end;
        }

        if (state.Stream is not null)
        {
            state.ParseStream(reader);
        }
        else if (!capped)
        {
            context.AddFinding(Severity.Warning, "dtshd.no-stream-data", "The file has no STRMDATA chunk, so it holds no audio.", root);
        }

        state.Flush();
    }

    private static void Trailing(State state, long start, long end, string code, string message)
    {
        var node = new Node(code == "dtshd.trailing-data" ? "Trailing Data" : "Unread Data", start, end - start, NodeValue.Integer(end - start, "bytes", "byte"))
        {
            Description = code == "dtshd.trailing-data" ? Explain.TrailingData : Explain.UnreadData,
        };
        state.Context.Root.Add(node);
        state.Context.AddFinding(Severity.Warning, code, message, node);
    }

    internal static string Describe(string id) => id switch
    {
        "DTSHDHDR" => Explain.Header,
        "FILEINFO" => Explain.FileInfo,
        "CORESSMD" => Explain.CoreMetadata,
        "EXTSS_MD" => Explain.ExtensionMetadata,
        "AUPR-HDR" => Explain.PresentationHeader,
        "AUPRINFO" => Explain.PresentationInfo,
        "NAVI-TBL" => Explain.NavigationTable,
        "BITSHVTB" => Explain.BitShaving,
        "STRMDATA" => Explain.StreamData,
        "TIMECODE" => Explain.Timecode,
        "BUILDVER" => Explain.BuildVersion,
        "BLACKOUT" => Explain.Blackout,
        "BRANCHPT" => Explain.BranchPoints,
        _ => Explain.UnknownChunk,
    };
}

// What one analysis learns while it reads the chunks: the fields the later checks compare and the stream chunk.
internal sealed class State(ParseContext context)
{
    public ParseContext Context => context;

    private readonly Dictionary<string, Aggregate> _aggregated = [];
    private readonly List<string> _order = [];

    // A finding of a kind that can occur at every chunk is reported once with a count, pointing at the first case.
    public void Note(string code, string message, Node node)
    {
        if (_aggregated.TryGetValue(code, out Aggregate? known))
        {
            known.Count++;
            return;
        }

        _aggregated[code] = new Aggregate(message, node);
        _order.Add(code);
    }

    public void Flush()
    {
        foreach (string code in _order)
        {
            Aggregate found = _aggregated[code];
            context.AddFinding(Severity.Warning, code, found.Count == 1 ? found.Message : $"{found.Message} The same finding comes {found.Count - 1} more times in the file.", found.Node, found.Count);
        }

        _order.Clear();
        _aggregated.Clear();
    }

    private sealed class Aggregate(string message, Node node)
    {
        public string Message => message;

        public Node Node => node;

        public int Count { get; set; } = 1;
    }

    public List<Node> Chunks { get; } = [];

    // The STRMDATA chunk and the byte range of its payload.
    public Node? Stream { get; set; }

    public long StreamStart { get; set; }

    public long StreamEnd { get; set; }

    // Bitw_Stream_Metadata of DTSHDHDR, or -1 before it.
    public int StreamFlags { get; set; } = -1;

    public Presentation? Presentation { get; set; }

    public int Presentations { get; set; }

    public void ParseStream(ByteReader reader)
    {
        if (Stream is null)
        {
            return;
        }

        int streams = context.Streams.Count;
        ByteReader payload = reader.Slice(StreamStart, StreamEnd - StreamStart);
        if (!context.ParseStream("dts", Stream, payload))
        {
            context.AddFinding(Severity.Info, "dtshd.stream-not-analyzed", "The STRMDATA chunk holds a DTS-HD stream, which no parser here reads.", Stream);
            return;
        }

        if (Presentations == 1 && Presentation is { } presentation && context.Streams.Count > streams)
        {
            presentation.Compare(this, context.Streams[streams]);
        }
    }
}

// The fields of an AUPR-HDR chunk that the stream the file holds is compared with.
internal sealed record Presentation(Node Chunk, int MaxRate, int Channels, bool HasChannels)
{
    public void Compare(State state, StreamInfo stream)
    {
        if (MaxRate > 0 && stream.SampleRate > 0 && MaxRate != stream.SampleRate)
        {
            state.Context.AddFinding(Severity.Warning, "dtshd.metadata-differs",
                $"AUPR-HDR gives Max_Sample_Rate_Hz {MaxRate.ToString(CultureInfo.InvariantCulture)}, but the stream's sampling frequency is {stream.SampleRate.ToString(CultureInfo.InvariantCulture)} Hz; FFmpeg sets up its decoder from the chunk.", Chunk);
        }

        if (HasChannels && Channels > 0 && stream.Channels > 0 && Channels != stream.Channels)
        {
            state.Context.AddFinding(Severity.Warning, "dtshd.metadata-differs",
                $"AUPR-HDR's Channel_Mask names {Channels} channels, but the stream has {stream.Channels}; FFmpeg sets up its decoder from the chunk.", Chunk);
        }
    }
}
