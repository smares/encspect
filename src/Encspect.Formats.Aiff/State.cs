using Encspect.Core;

namespace Encspect.Formats.Aiff;

// What one analysis of an AIFF file collects while it walks the chunks.
internal sealed class State(ParseContext context)
{
    public ParseContext Context => context;

    public ByteReader Reader => context.Reader;

    public FindingCollector Findings { get; } = new();

    public long Start { get; set; }

    public bool IsAifc { get; set; }

    public Node Container { get; set; } = null!;

    public long ContainerEnd { get; set; }

    public CommonChunk? Common { get; set; }

    public int CommonChunks { get; set; }

    public SoundChunk? Sound { get; set; }

    public int SoundChunks { get; set; }

    public int VersionChunks { get; set; }

    public uint? Version { get; set; }

    public Node? VersionNode { get; set; }

    public Node? FirstSoundNode { get; set; }

    // The chunk being read: the size its header states, and whether that size is a placeholder.
    public uint ChunkSize32 { get; set; }

    public bool ChunkSizeIsUnknown { get; set; }

    public Dictionary<string, int> Counts { get; } = new(StringComparer.Ordinal);

    public HashSet<int>? MarkerIds { get; set; }

    public List<(int Marker, Node Chunk)> MarkerReferences { get; } = [];

    public int ChunkCount { get; set; }

    public List<Node> TagNodes { get; } = [];
}
