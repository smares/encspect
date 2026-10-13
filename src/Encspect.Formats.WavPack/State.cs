using Encspect.Core;

namespace Encspect.Formats.WavPack;

// What the first block that holds a kind of sub-block says, and where it is, for the stream's
// figures and the nodes that point back to it.
internal readonly record struct Located<T>(T Value, int Entry, int SubBlock);

internal readonly record struct ChannelInfoValue(int Channels, int Streams, uint Mask, int Length);

internal readonly record struct ConfigValue(uint Flags, int? ExtraLevel);

internal readonly record struct NewConfigValue(int FileFormat, int QualityFlags, int LayoutTag, int LayoutChannels);

// The state of one analysis: the entries (blocks and runs of bytes between them) in file order, the
// details of the first blocks, and the figures worked out as the blocks are read.
internal sealed class State(Source source, CancellationToken cancellation)
{
    // Blocks past these are kept as 64-byte entries without their sub-blocks, so a file of a million
    // blocks costs no more than that per block.
    public const int MaxDetailedBlocks = 4096;
    public const int MaxListedSubBlocks = 1024;
    public const int MaxStoredPerSubBlock = 4096;
    public const int MaxStoredPerBlock = 1 << 16;
    public const int MaxSetBlocks = 4096;

    public Source Source { get; } = source;

    public CancellationToken Cancellation { get; } = cancellation;

    public ChunkedList<Entry> Entries { get; } = new();

    public List<Detail> Details { get; } = [];

    public FindingCollector Findings { get; } = new();

    public List<Node> Leading { get; } = [];

    // Counts of blocks.
    public int Blocks { get; set; }

    public int AudioBlocks { get; set; }

    public int WvBlocks { get; set; }

    public int WvcBlocks { get; set; }

    public int WvxBlocks { get; set; }

    public int DsdBlocks { get; set; }

    public int MetadataBlocks { get; set; }

    public int ChecksumValid { get; set; }

    public int ChecksumInvalid { get; set; }

    public int ChecksumMissing { get; set; }

    public int ExtraOnly { get; set; }

    public int ExtraOnlyFirst { get; set; } = -1;

    public int StereoBlocks { get; set; }

    public int JointStereoBlocks { get; set; }

    public int CrossDecorrBlocks { get; set; }

    public int HybridBlocks { get; set; }

    public long BlockBytes { get; set; }

    public bool HasLossyBlocks { get; set; }

    public int TermsBlocks { get; set; }

    public int TermsMinimum { get; set; } = int.MaxValue;

    public int TermsMaximum { get; set; }

    public long SkippedBytes { get; set; }

    // The first block with audio, and what it says about the format.
    public int FirstAudio { get; set; } = -1;

    public BlockHeader FirstAudioHeader { get; set; }

    public int SampleRate { get; set; }

    public bool SampleRateMissing { get; set; }

    public int DsdPower { get; set; } = -1;

    public Located<ChannelInfoValue>? ChannelInfo { get; set; }

    public Located<ConfigValue>? Config { get; set; }

    public Located<NewConfigValue>? NewConfig { get; set; }

    public Located<string>? Md5 { get; set; }

    public Located<int>? SampleRateField { get; set; }

    public Located<string>? Extension { get; set; }

    public Located<double>? HybridTarget { get; set; }

    // The block that says how many samples the file holds (index 0 and a count other than unknown).
    public long? DeclaredTotal { get; set; }

    public int DeclaredEntry { get; set; } = -1;

    public bool IsDeclaredUnknown { get; set; }

    // The first block's float and integer details, for the lossless verdict.
    public bool HasFloatInfo { get; set; }

    public bool HasNewConfig { get; set; }

    // Where each kind of finding that points at a sub-block first happened is kept by the finding
    // collector; the block sets are followed here.
    public bool SetOpen { get; set; }

    public long SetOffset { get; set; }

    public long SetIndex { get; set; }

    public uint SetSamples { get; set; }

    public long SetBits { get; set; }

    public int SetBlocks { get; set; }

    public int SetChannels { get; set; }

    public int SetFirstEntry { get; set; }

    public bool SetSawFinal { get; set; }

    public uint SetFormat { get; set; }

    public long FiguresScale { get; set; } = 1;

    public long NextIndex { get; set; } = -1;

    public int Sets { get; set; }

    public long SetSampleTotal { get; set; }

    public long CountedSamples { get; set; }

    public int FirstSetChannels { get; set; }

    public uint FormatFlags { get; set; }

    public StreamFigures? Figures { get; set; }

    public long FirstIndex { get; set; } = -1;

    public int StereoSetsSamples { get; set; }
}
