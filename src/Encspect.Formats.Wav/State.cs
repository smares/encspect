using Encspect.Core;

namespace Encspect.Formats.Wav;

// What one analysis of a WAVE file collects while it walks the chunks.
internal sealed class State(ParseContext context)
{
    public ParseContext Context => context;

    public ByteReader Reader => context.Reader;

    public FindingCollector Findings { get; } = new();

    // The RIFF chunk: its first byte, its identifier and whether integers are big-endian (RIFX).
    public long Start { get; set; }

    public string Magic { get; set; } = "RIFF";

    public bool IsBigEndian => Magic == "RIFX";

    public bool IsRf64 => Magic == "RF64";

    public bool IsBw64 => Magic == "BW64";

    // RF64 and BW64: a size field of 0xFFFFFFFF stands for a size in the ds64 chunk.
    public bool Is64Bit => IsRf64 || IsBw64;

    // What the RIFF chunk is called in messages: Wave64 calls it the riff chunk.
    public string ContainerLabel { get; set; } = "RIFF chunk";

    public Node Container { get; set; } = null!;

    // Where the RIFF chunk ends: its stated end, cut to the data there is.
    public long ContainerEnd { get; set; }

    public Ds64? Ds64 { get; set; }

    public WaveFormat? Format { get; set; }

    public int FormatChunks { get; set; }

    public Node? FormatChunkNode { get; set; }

    public ulong FactSamples64 { get; set; }

    public Node? FactNode { get; set; }

    public DataChunk? Data { get; set; }

    public Node? FirstDataNode { get; set; }

    // The codec of a stream the samples of the first data chunk carry although the fmt chunk says PCM, found when the chunk was reached.
    public string? CarriedCodec { get; set; }

    // The nodes the walk has built, counted against Walker.MaxNodes, and whether the walk stopped at the budget.
    public int Nodes { get; set; }

    public bool IsNodeLimitReached { get; set; }

    public bool IsDataBeforeFormat { get; set; }

    // The samples of silence ('slnt' chunks of a wavl list), which the stream counts besides its data.
    public long SilentSamples { get; set; }



    public int Nesting { get; set; }

    // The type of the LIST chunk being read, or null at the top; a wavl list holds the pieces of one wave-data.
    public string? ListType { get; set; }

    public Node? CurrentList { get; set; }

    // The wavl list that holds the first data chunk, or null.
    public Node? WavlList { get; set; }

    // The bytes of the data chunks that follow the first one in the same wavl list.
    public long WavlExtraBytes { get; set; }





    // The names of the cue points of the file's cue chunks, up to Chunks.MaxCueNames of them; IsCueNamesComplete says whether that was all.
    public HashSet<uint>? CueIdentifiers { get; set; }

    public bool IsCueNamesIncomplete { get; set; }

    // The cue point names the labels, notes, labeled texts and embedded files of 'adtl' lists refer to.
    public List<(uint Name, Node Chunk)> LabelReferences { get; } = [];

    // The chna chunks read, for the checks that need the format chunk, and how many of each XML chunk of BS.2088 (axml, bxml, sxml) the file holds.
    public List<ChnaChunk> ChnaChunks { get; } = [];

    public Dictionary<string, int> XmlChunkCounts { get; } = [];

    // The first XML chunk that holds Audio Definition Model metadata, which needs a chna chunk.
    public Node? AdmNode { get; set; }

    // The chunk being read: the size its header states (after the ds64 chunk's say in an RF64 file), and whether that size is a placeholder.
    public ulong ChunkStated { get; set; }

    public bool ChunkSizeIsUnknown { get; set; }

    // The 32-bit size field of the chunk being read, and of the first data chunk.
    public uint ChunkSize32 { get; set; }

    public uint? DataSize32 { get; set; }

}
