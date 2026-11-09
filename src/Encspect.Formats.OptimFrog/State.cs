using Encspect.Core;

namespace Encspect.Formats.OptimFrog;

// What one analysis of one file collects. The parser instance keeps none of it.
internal sealed class State
{
    public State(ParseContext context)
    {
        Context = context;
        Source = new Source(Records);
    }

    public ParseContext Context { get; }

    public FindingCollector Findings { get; } = new();

    public Records Records { get; } = new();

    public Source Source { get; }

    public Node Stream { get; set; } = null!;

    // The nodes in front of the blocks: the main block's, the statistics, the claims.
    public List<Node> Leading { get; } = [];

    public StreamFigures? Figures { get; set; }

    public MainBlock? Main => Records.Main;

    // Whole COMP blocks, those compared, the sum of their samples and the CRC-32 failures with the first.
    public long CompBlocks { get; set; }

    public long CutBlocks { get; set; }

    public long ShortBlocks { get; set; }

    public long CompSamples { get; set; }

    public long CrcFailures { get; set; }

    public int FirstCrcFailure { get; set; } = -1;

    public BlockRecord FirstCrcFailureBlock { get; set; }

    public long DifferingBlocks { get; set; }

    public int FirstDiffering { get; set; } = -1;

    public int FirstCut { get; set; } = -1;

    public int FirstShort { get; set; } = -1;

    public long OutOfOrderBlocks { get; set; }

    public int FirstOutOfOrder { get; set; } = -1;

    public long UnknownBlocks { get; set; }

    public int FirstUnknown { get; set; } = -1;

    public long SizeInvalidBlocks { get; set; }

    public int FirstSizeInvalid { get; set; } = -1;

    public bool HasHead { get; set; }

    public bool HasTail { get; set; }

    public bool HasMd5 { get; set; }

    public int BlockCount => Records.Blocks.Count;

    public void AddLeading(Node node) => Leading.Add(node);
}
