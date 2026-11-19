using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio;

// The state of one analysis: the header, where everything lies, the records the nodes are built from, the figures worked out as frames are read
// and the session the frames are handed to.
internal sealed class State
{
    // The most frames kept as entries; the rest are read, counted and checked, and listed as one stretch.
    public const int DefaultMaxListedFrames = 1 << 20;

    // The most entries of the table of start bits of version 3.80 that the analyzer keeps.
    public const int MaxSeekBits = 1 << 20;

    // The most the analyzer reads into memory as one frame to decode it.
    public const long MaxDecodedFrameBytes = 64L << 20;

    public State(ParseContext context, ByteReader reader, int maxListedFrames = DefaultMaxListedFrames)
    {
        MaxListedFrames = maxListedFrames;
        Context = context;
        Reader = reader;
    }

    public int MaxListedFrames { get; }

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

    // The frames that are not cut and have a byte in the file: the frame count of the statistics.
    public long WholeFrames { get; set; }

    public long CutFrames { get; set; }

    public long MisplacedFrames { get; set; }

    public long ShortFrames { get; set; }

    // Frames the parser could not hand to a decoder: misplaced, too short for their start fields, or too large to read into memory.
    public long UnreadableFrames { get; set; }

    public long StoredCrcs { get; set; }

    public long SilentFrames { get; set; }

    // The coding of each two-channel frame that the series of bitrates has a point for. Silence takes precedence over pseudo-stereo, as in both
    // decoders; one silent channel alone changes nothing in decoding, so it counts as mid/side stereo.
    public static readonly Category[] StereoCodings =
    [
        new("Mid/side stereo", Explain.StatMidSide) { Topic = Topics.MidSideStereo },
        new("Pseudo-stereo", Explain.StatPseudoStereo) { Topic = Topics.Frames },
        new("Silence", Explain.StatSilence) { Topic = Topics.Frames },
    ];

    public UnitCategoryTally Stereo { get; } = new("Stereo Coding", StereoCodings, "frame", GraphConcept.StereoCoding);

    // A mono stream's flags, one per frame the series of bitrates has a point for: the frame is flagged silent or not. The "with" count equals "Silent Frames".
    public static readonly Category[] SilenceFlags =
    [
        new("With silence", Explain.CategorySilent) { Topic = Topics.Frames },
        new("Without silence", Explain.CategoryNotSilent) { Topic = Topics.Frames },
    ];

    public CategorySeriesBuilder SilentFlags { get; } = new("Silent Frames", SilenceFlags, "frame", 1, concept: GraphConcept.StereoCoding);

    public static int StereoCodeOf(uint special, bool hasCodes) =>
        !hasCodes ? 0 : (special & 3) == 3 ? 2 : (special & 4) != 0 ? 1 : 0;

    public long SpecialFrames { get; set; }

    // The declared samples of the frames whose first byte is in the file, and of those the samples of the frames that are cut or too short
    // for their start fields.
    public long Samples { get; set; }

    public long LostSamples { get; set; }

    public bool HasGap { get; set; }

    // The digest of the coded data, or null where the file does not hold all of it.
    public byte[]? ComputedMd5 { get; set; }

    public void AddLeading(Node node) => Leading.Add(node);
}
