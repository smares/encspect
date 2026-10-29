using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio;

// The state of one analysis: the header, where everything lies, the records the nodes are built from, the figures worked out as frames are read
// and the session the frames are handed to.
internal sealed class State
{
    // The most frames kept as entries; the rest are read, counted and checked, and listed as one stretch.
    public const int MaxListedFrames = 1 << 20;

    // The most entries of the table of start bits of version 3.80 that the analyzer keeps.
    public const int MaxSeekBits = 1 << 20;

    // The most the analyzer reads into memory as one frame to decode it.
    public const long MaxDecodedFrameBytes = 64L << 20;

    public State(ParseContext context, ByteReader reader)
    {
        Context = context;
        Reader = reader;
    }

    public ParseContext Context { get; }

    public ByteReader Reader { get; }

    public Node Stream { get; set; } = null!;

    public Records Records { get; } = new();

    public ChunkedList<Entry> Entries => Records.Entries;

    public FindingCollector Findings { get; } = new();

    // The nodes before the frames, in file order, then the encoder and the statistics.
    public List<Node> Leading { get; } = [];

    public ApeHeader Header { get; set; }

    // The fmt chunk of the stored WAV header, read before the frames so the decoder can be given its channel mask; null where the header data is no WAVE header.
    public WavFormat? Wav { get; set; }

    public bool HasHeader { get; set; }

    // Where the structures lie in the file.
    public long Junk { get; set; }

    public long HeaderAt { get; set; }

    public long SeekTableAt { get; set; }

    public long HeaderDataAt { get; set; }

    public long DataStart { get; set; }

    public long FrameDataEnd { get; set; }

    public long TerminatingEnd { get; set; }

    // The seek table's entries that the file holds.
    public long SeekEntries { get; set; }

    public Node? DescriptorNode { get; set; }

    public Node? HeaderNode { get; set; }

    public Node? SeekNode { get; set; }

    public Node? TerminatingNode { get; set; }

    public IDecodeSession? Session { get; set; }

    // Version 3.80 only: the table of the bit each frame starts at in the word its seek entry names (the first MaxSeekBits frames).
    public byte[] SeekBits { get; set; } = [];

    public StreamFigures? Figures { get; set; }

    // Counts over the frames.
    public long Frames { get; set; }

    public long CutFrames { get; set; }

    public long MisplacedFrames { get; set; }

    public long ShortFrames { get; set; }

    // Frames the parser could not hand to a decoder: misplaced, too short for their start fields, or too large to read into memory.
    public long UnreadableFrames { get; set; }

    public long StoredCrcs { get; set; }

    public long SilentFrames { get; set; }

    public long PseudoStereoFrames { get; set; }

    public long SpecialFrames { get; set; }

    public long Samples { get; set; }

    public bool HasGap { get; set; }

    // The digest of the coded data, or null where the file does not hold all of it.
    public byte[]? ComputedMd5 { get; set; }

    public void AddLeading(Node node) => Leading.Add(node);
}
