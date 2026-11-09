using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.OptimFrog;

// The names the checksums of an OptimFROG file are declared with (StreamVerification.Checksums).
internal static class ChecksumNames
{
    public const string BlockCrc32 = "block CRC-32";
    public const string Md5 = "MD5";
}

// The background articles the OptimFROG nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string Blocks = "optimfrog-blocks";
    public const string Modes = "optimfrog-modes";
    public const string Crc = "crc";
    public const string Channels = "channel-layouts";
    public const string RiffChunks = "riff-chunks";
    public const string WaveFormat = "waveformatex";
    public const string WaveFormatExtensible = "wave-format-extensible";
    public const string Verification = "verification";
    public const string BitrateModes = "bitrate-modes";
}

// The layout of the compressed file format (SDK/Documentation/format.txt): every block is a 4-byte ID, a 4-byte length and the data.
internal static class Layout
{
    public const int BlockHeaderLength = 8;

    // The main block's fields before the optional ones: 6 bytes of samples, the sample type, the channel configuration, 4 bytes of sample rate, 2 of
    // encoder ID and the compression byte.
    public const int MainFields = 15;

    // The minDecoderVersion field, added in 5.100: 2 bytes after the main fields.
    public const int MinDecoderVersionLength = 2;

    // A COMP block's fields before the compressed data: the CRC-32, the samples, the sample type, the channel configuration, 2 reserved bytes
    // and the encoder ID.
    public const int CompFields = 14;

    public const int Md5Length = 16;

    public const uint Main = 0x2052464F;

    // "OFRX": the main block of the files `off` 5.100 writes for IEEE float audio in Encspect's tests, with the fields of "OFR " (format.txt does not define it).
    public const uint MainFloat = 0x5852464F;
    public const uint Head = 0x44414548;
    public const uint Comp = 0x504D4F43;
    public const uint Tail = 0x4C494154;
    public const uint Recovery = 0x56434552;
    public const uint Md5Id = 0x2035444D;

    public static string Text(uint id) => string.Create(4, id, static (span, v) =>
    {
        for (int i = 0; i < 4; i++)
        {
            span[i] = (char)((v >> (8 * i)) & 0xFF);
        }
    });

    public static bool IsKnown(uint id) => id is Main or MainFloat or Head or Comp or Tail or Recovery or Md5Id;

    public static bool IsMain(uint id) => id is Main or MainFloat;

    // Four printable ASCII bytes.
    public static bool IsPrintable(uint id)
    {
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(id >> (8 * i));
            if (b is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    // Where a known block belongs in the order the specification's relation allows: ['ID3'] 'OFR ' [...] 'HEAD' 'COMP'* 'TAIL' [...] ['MD5 '] ['RECV'].
    public static int Rank(uint id) => id switch { Main or MainFloat => 0, Head => 1, Comp => 2, Tail => 3, Md5Id => 4, Recovery => 5, _ => -1 };

    public static readonly string[] SampleTypes =
        ["UINT8", "SINT8", "UINT16", "SINT16", "UINT24", "SINT24", "UINT32", "SINT32", "FLOAT32_0", "FLOAT32_16", "FLOAT32_24"];

    // The bits of the container each sample type names; the float types are 32-bit.
    public static readonly int[] SampleTypeBits = [8, 8, 16, 16, 24, 24, 32, 32, 32, 32, 32];

    public static readonly string[] ChannelConfigurations = ["MONO", "STEREO_LR"];

    public static readonly string[] Modes =
        ["fast", "normal", "high", "extra", "best", "ultra", "insane", "highnew", "extranew", "bestnew", "ultranew", "extrafast", "turbonew", "fastnew", "normalnew"];

    public static readonly string[] Speedups = ["1x", "2x", "4x"];

    public static readonly string[] BuildSystems =
        ["Windows x86", "Linux x86", "Linux x64", "OS X ppc", "OS X x86", "OS X x64", "Windows x64", "FreeBSD x86", "FreeBSD x64"];

    public const int UnknownBuildSystem = 15;

    public static string? SampleType(int value) => value < SampleTypes.Length ? SampleTypes[value] : null;

    public static string? ChannelConfiguration(int value) => value < ChannelConfigurations.Length ? ChannelConfigurations[value] : null;

    public static int ChannelsOf(int configuration) => configuration == 0 ? 1 : 2;

    public static string? Mode(int value) => value < Modes.Length ? Modes[value] : null;

    public static string? Speedup(int value) => value < Speedups.Length ? Speedups[value] : null;

    public static string? BuildSystem(int value) => value < BuildSystems.Length ? BuildSystems[value] : value == UnknownBuildSystem ? "unknown" : null;

    // The encoder version is stored as ID >> 4 plus 4500, written x.xxx: 5100 is 5.100.
    public static string VersionText(int version) => $"{version / 1000}.{version % 1000:D3}";
}

// The fields of the main block (format.txt, section 1).
internal readonly record struct MainBlock(long Samples, int SampleType, int ChannelConfiguration, uint SampleRate, int EncoderId, int Compression, int? MinDecoderVersion, long Size)
{
    public int Version => (EncoderId >> 4) + 4500;

    public int BuildSystem => EncoderId & 0xF;

    public int Mode => Compression >> 3;

    public int Speedup => Compression & 7;

    public int Channels => Layout.ChannelsOf(ChannelConfiguration);

    public int BitsPerSample => SampleType < Layout.SampleTypeBits.Length ? Layout.SampleTypeBits[SampleType] : 0;

    public bool IsFloat => SampleType >= 8;

    // The samples of each channel: the specification counts every value, not every sample point.
    public long SamplesPerChannel => Samples / Channels;

    public static MainBlock Read(ReadOnlySpan<byte> data, long size)
    {
        long samples = BinaryPrimitives.ReadUInt32LittleEndian(data) | ((long)BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) << 32);
        int? minimum = data.Length >= Layout.MainFields + Layout.MinDecoderVersionLength ? BinaryPrimitives.ReadUInt16LittleEndian(data[Layout.MainFields..]) + 4500 : null;
        return new MainBlock(samples, data[6], data[7], BinaryPrimitives.ReadUInt32LittleEndian(data[8..]), BinaryPrimitives.ReadUInt16LittleEndian(data[12..]), data[14], minimum, size);
    }
}

[Flags]
internal enum BlockStatus : byte
{
    None = 0,

    // The data ends inside the block.
    Cut = 1,

    // The block states fewer bytes than its fields need.
    SizeInvalid = 2,

    // A block of an ID the specification does not define.
    Unknown = 4,

    // A COMP block whose sample type, channel configuration or encoder ID differs from the main block's.
    Differs = 8,

    // A known block that comes before one it should follow, or a second one of a kind that occurs once.
    OutOfOrder = 16,
}

// One block of the file as the walk read it. A COMP block also has its fields and CRC-32.
internal struct BlockRecord
{
    public long Offset;
    public uint Length;
    public uint Id;
    public uint StoredCrc;
    public uint ComputedCrc;
    public uint Samples;
    public ushort Reserved;
    public ushort EncoderId;
    public byte SampleType;
    public byte ChannelConfiguration;
    public BlockStatus Status;

    // Whether the CRC-32 was compared (the block is a whole COMP block).
    public bool HasCrc;

    public readonly bool IsCrcValid => StoredCrc == ComputedCrc;

    public readonly long End => Offset + Layout.BlockHeaderLength + Length;
}

// What the walk read: the blocks in order, up to MaxBlocks, and the totals over all of them.
internal sealed class Records
{
    // The blocks kept one by one; the rest are counted and their CRC-32s compared, but they have no nodes.
    public const int MaxBlocks = 1 << 20;

    public ChunkedList<BlockRecord> Blocks { get; } = new();

    public long Offset { get; set; }

    // The end of the last block the walk accepted.
    public long End { get; set; }

    public long DataEnd { get; set; }

    public long Counted { get; set; }

    public MainBlock? Main { get; set; }

    public int MainIndex { get; set; } = -1;

    public int HeadIndex { get; set; } = -1;

    public int TailIndex { get; set; } = -1;

    public int Md5Index { get; set; } = -1;

    public byte[]? Md5 { get; set; }
}
