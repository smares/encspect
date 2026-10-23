using System.Runtime.InteropServices;

namespace Encspect.Formats.WavPack;

// The function numbers of the metadata sub-blocks: the low six bits of a sub-block's ID byte, the
// 0x20 bit among them marking an optional one (wavpack.h ID_*, format document section 3.0).
internal static class SubBlockIds
{
    public const int Dummy = 0x00;
    public const int EncoderInfo = 0x01;
    public const int DecorrTerms = 0x02;
    public const int DecorrWeights = 0x03;
    public const int DecorrSamples = 0x04;
    public const int EntropyVars = 0x05;
    public const int HybridProfile = 0x06;
    public const int ShapingWeights = 0x07;
    public const int FloatInfo = 0x08;
    public const int Int32Info = 0x09;
    public const int WvBitstream = 0x0A;
    public const int WvcBitstream = 0x0B;
    public const int WvxBitstream = 0x0C;
    public const int ChannelInfo = 0x0D;
    public const int DsdBlock = 0x0E;
    public const int RiffHeader = 0x21;
    public const int RiffTrailer = 0x22;
    public const int AltHeader = 0x23;
    public const int AltTrailer = 0x24;
    public const int ConfigBlock = 0x25;
    public const int Md5Checksum = 0x26;
    public const int SampleRate = 0x27;
    public const int AltExtension = 0x28;
    public const int AltMd5Checksum = 0x29;
    public const int NewConfigBlock = 0x2A;
    public const int ChannelIdentities = 0x2B;
    public const int BlockChecksum = 0x2F;

    public const int OptionalBit = 0x20;
    public const int OddSizeBit = 0x40;
    public const int LargeBit = 0x80;

    public static string Name(int function) => function switch
    {
        Dummy => "Dummy",
        EncoderInfo => "Encoder Info",
        DecorrTerms => "Decorrelation Terms",
        DecorrWeights => "Decorrelation Weights",
        DecorrSamples => "Decorrelation Samples",
        EntropyVars => "Entropy Variables",
        HybridProfile => "Hybrid Profile",
        ShapingWeights => "Shaping Weights",
        FloatInfo => "Float Info",
        Int32Info => "Int32 Info",
        WvBitstream => "WavPack Bitstream",
        WvcBitstream => "Correction Bitstream",
        WvxBitstream => "Extra Bitstream",
        ChannelInfo => "Channel Info",
        DsdBlock => "DSD Block",
        RiffHeader => "RIFF Header",
        RiffTrailer => "RIFF Trailer",
        AltHeader => "Alternate Header",
        AltTrailer => "Alternate Trailer",
        ConfigBlock => "Config Block",
        Md5Checksum => "MD5 Checksum",
        SampleRate => "Sample Rate",
        AltExtension => "Alternate Extension",
        AltMd5Checksum => "Alternate MD5 Checksum",
        NewConfigBlock => "New Config Block",
        ChannelIdentities => "Channel Identities",
        BlockChecksum => "Block Checksum",
        _ => "Unknown Sub-block",
    };

    public static bool IsKnown(int function) => Name(function) != "Unknown Sub-block";

    public static bool IsBitstream(int function) => function is WvBitstream or WvcBitstream or WvxBitstream or DsdBlock;
}

internal enum EntryKind : byte
{
    Block,
    Skipped,

    // Blocks of 32 bytes that count no sample, past the ones the report details, as one entry.
    Run,
}

[Flags]
internal enum BlockStatus : ushort
{
    None = 0,
    HasWv = 1,
    HasWvc = 2,
    HasWvx = 4,
    HasDsd = 8,
    ChecksumValid = 0x10,
    ChecksumInvalid = 0x20,
    ChecksumMissing = 0x40,

    // The data ends inside the block.
    Truncated = 0x80,

    // The block states more bytes than it has before the next header.
    Oversized = 0x100,

    // A sub-block runs past the block, or a byte is left that holds no sub-block.
    Malformed = 0x200,

    // A skipped run that starts a block header the data ends in.
    CutHeader = 0x400,

    // The block holds something libwavpack's decoder refuses (a sub-block of a size or content it does not take, no bitstream of
    // its kind, flags it does not decode), so the block is not decoded; the findings tell what.
    Refused = 0x800,
}

// A block, or a run of bytes between blocks that hold none, in 64 bytes.
[StructLayout(LayoutKind.Auto)]
internal readonly struct Entry(EntryKind kind, long offset, long length, BlockHeader header, BlockStatus status, int subBlocks, int detail)
{
    public EntryKind Kind => kind;

    public long Offset => offset;

    public long Length => length;

    public BlockHeader Header => header;

    public BlockStatus Status => status;

    // How many sub-blocks the block holds; for a run, how many blocks.
    public int SubBlocks => subBlocks;

    // The index of the block's Detail, or -1 for a block past the ones the report details.
    public int Detail => detail;

    public long End => offset + length;
}

// A sub-block of a block that is detailed: where it is and the start of its data, kept so the
// report can build its nodes after the file is closed.
[StructLayout(LayoutKind.Auto)]
internal readonly struct SubBlock(int id, long offset, int headerLength, int size, int dataLength, int stored, int storedLength, bool isClipped)
{
    // The ID byte.
    public int Id => id;

    public int Function => id & 0x3F;

    public long Offset => offset;

    // 2 bytes, or 4 for a large sub-block.
    public int HeaderLength => headerLength;

    // The size field in bytes: the data and the byte that pads it to an even length. A sub-block that
    // runs past the block states more than the block holds.
    public int Size => size;

    // The data's length: the size less the pad byte of an odd one.
    public int DataLength => dataLength;

    // Where the kept bytes of the data start in the detail's array, and how many there are.
    public int Stored => stored;

    public int StoredLength => storedLength;

    // The sub-block runs past the block, so only the bytes in the block count.
    public bool IsClipped => isClipped;

    public bool IsOdd => (id & SubBlockIds.OddSizeBit) != 0;

    public bool IsLarge => (id & SubBlockIds.LargeBit) != 0;

    public int PadLength => IsOdd && !isClipped && size > 0 ? 1 : 0;

    // The bytes the sub-block takes in the block.
    public long Length => headerLength + (isClipped ? dataLength : size);

    public long End => offset + Length;
}

// What a detailed block keeps: its sub-blocks, the start of each one's data and the checksum
// worked out over the block.
internal sealed class Detail
{
    public List<SubBlock> SubBlocks { get; } = [];

    public byte[] Data { get; set; } = [];

    // Sub-blocks past the cap, which the report counts and does not list, and the bytes they cover.
    public int Further { get; set; }

    public long FurtherOffset { get; set; }

    public long FurtherLength { get; set; }

    // The checksum worked out over the bytes before the block's checksum sub-block, and where that
    // sub-block is in SubBlocks; -1 without one.
    public uint Computed { get; set; }

    public int ChecksumIndex { get; set; } = -1;

    // A byte after the last sub-block that holds none, and where it is.
    public long TrailingOffset { get; set; }

    public int Trailing { get; set; }

    // Sub-block indexes of the decorrelation terms and the weights, which the nodes of the
    // weights and samples read the passes from.
    public int TermsIndex { get; set; } = -1;
}
