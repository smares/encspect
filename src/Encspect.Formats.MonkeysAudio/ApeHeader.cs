using System.Buffers.Binary;

namespace Encspect.Formats.MonkeysAudio;

// The bits of the header's format flags (Monkey's Audio SDK 13.42, MACLib.h, APE_FORMAT_FLAG_*).
[Flags]
internal enum FormatFlags : ushort
{
    None = 0,
    Is8Bit = 1 << 0,
    HasCrc = 1 << 1,
    HasPeakLevel = 1 << 2,
    Is24Bit = 1 << 3,
    HasSeekElements = 1 << 4,
    CreatesWavHeader = 1 << 5,
    IsAiff = 1 << 6,
    IsW64 = 1 << 7,
    IsSnd = 1 << 8,
    IsBigEndian = 1 << 9,
    IsCaf = 1 << 10,
    IsSigned8Bit = 1 << 11,
    IsFloatingPoint = 1 << 12,
}

// What the descriptor and the header say, in the SDK's terms (MACLib.h APE_DESCRIPTOR and APE_HEADER, APEHeader.h APE_HEADER_OLD, APEHeader.cpp).
// Files of version 3.98 and later start with a 52-byte descriptor and a 24-byte header; older files with one 32-byte header, an optional peak
// level and seek element count, the WAV header data and the seek tables. The fields are as stored; Faults say what the SDK would refuse.
internal readonly struct ApeHeader
{
    public const int DescriptorLength = 52;
    public const int HeaderLength = 24;
    public const int OldHeaderLength = 32;
    public const int Signature = 4;

    // The version from which the descriptor exists (APEHeader.cpp: CommonHeader.nVersion >= 3980).
    public const int FirstDescriptorVersion = 3980;

    // The most the SDK takes for the WAV header data or terminating data, 8 MiB (All.h APE_WAV_HEADER_OR_FOOTER_MAXIMUM_BYTES).
    public const long MaximumWavBytes = 8L << 20;

    public const int MaximumChannels = 32;

    public bool HasDescriptor { get; init; }

    public bool IsFloat { get; init; }

    // Encoder file version times 1000 (3.99 is 3990).
    public int FileVersion { get; init; }

    // The program's version times 1000 from 13.17 on (13.42 is 13420), 0 before and in old files.
    public int ProgramVersion { get; init; }

    public uint DescriptorBytes { get; init; }

    public uint HeaderBytes { get; init; }

    public uint SeekTableBytes { get; init; }

    // The stored WAV header data: the descriptor's count, or the old header's.
    public uint HeaderDataBytes { get; init; }

    public ulong FrameDataBytes { get; init; }

    public uint TerminatingBytes { get; init; }

    public ReadOnlyMemory<byte> Md5 { get; init; }

    public int CompressionLevel { get; init; }

    public FormatFlags Flags { get; init; }

    public uint BlocksPerFrame { get; init; }

    public uint FinalFrameBlocks { get; init; }

    public uint TotalFrames { get; init; }

    public int BitsPerSample { get; init; }

    public int Channels { get; init; }

    public uint SampleRate { get; init; }

    // Old files: the peak level and the seek element count that follow the header when their flags say so; the elements are the seek table's.
    public int? PeakLevel { get; init; }

    public uint? SeekElements { get; init; }

    // Where the header itself starts, relative to the descriptor's start.
    public long HeaderOffset => HasDescriptor ? DescriptorBytes : 0;

    public long TotalBlocks => TotalFrames == 0 ? 0 : ((TotalFrames - 1L) * BlocksPerFrame) + FinalFrameBlocks;

    public int BytesPerSample => BitsPerSample / 8;

    // Where the first frame's first byte lies for a descriptor file whose descriptor starts at descriptorAt: after the descriptor, the header,
    // the seek table and the WAV header data, each as long as the descriptor says (APEHeader.cpp, AnalyzeCurrent, which reads the descriptor and the
    // header in full and skips what the descriptor says is longer).
    public long DataOffset(long descriptorAt) =>
        descriptorAt + Math.Max(DescriptorLength, (long)DescriptorBytes) + Math.Max(HeaderLength, (long)HeaderBytes) + SeekTableBytes + HeaderDataBytes;

    public bool HasStoredCrc => FileVersion >= 3930 || (FileVersion >= 3900 && (Flags & FormatFlags.HasCrc) != 0);

    // Frames start on byte boundaries from 3.81, and special frames exist from 3.83 (APEInfo.h APE_GET_FRAMES_START_ON_BYTES_BOUNDARIES and
    // APE_GET_USES_SPECIAL_FRAMES).
    public bool FramesStartOnBytes => FileVersion > 3800;

    public bool HasSpecialFrames => FileVersion > 3820;

    public static string VersionText(int version) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{version / 1000}.{version % 1000 / 10:D2}");

    public static string LevelName(int level) => level switch
    {
        1000 => "fast",
        2000 => "normal",
        3000 => "high",
        4000 => "extra high",
        5000 => "insane",
        _ => "unknown",
    };

    // The compression levels whose predictors the SDK builds (NewPredictor.cpp: anything else throws).
    public static bool IsKnownLevel(int level) => level is 1000 or 2000 or 3000 or 4000 or 5000;

    // The largest frame the SDK accepts: 10 million blocks at level 5000 and above, a million below (APEHeader.cpp).
    public long MaximumBlocksPerFrame => HasDescriptor && CompressionLevel >= 5000 ? 10_000_000 : HasDescriptor ? 1_000_000 : 10_000_000;

    // The blocks per frame an old file implies (APEHeader.cpp AnalyzeOld).
    private static uint OldBlocksPerFrame(int version, int level) => version >= 3950 ? 73728 * 4 : (version >= 3900 || (version >= 3800 && level == 4000)) ? 73728u : 9216u;

    // The descriptor of 52 bytes. The bytes are the file's from the signature on.
    public static ApeHeader ReadDescriptor(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < DescriptorLength)
        {
            throw new EndOfStreamException();
        }

        return new ApeHeader
        {
            HasDescriptor = true,
            IsFloat = bytes[3] == (byte)'F',
            FileVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]),
            ProgramVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
            DescriptorBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            HeaderBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            SeekTableBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
            HeaderDataBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]),
            FrameDataBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]) | ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]) << 32),
            TerminatingBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[32..]),
            Md5 = bytes.Slice(36, 16).ToArray(),
        };
    }

    // Adds the 24-byte header that follows the descriptor.
    public ApeHeader WithHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength)
        {
            throw new EndOfStreamException();
        }

        return new ApeHeader
        {
            HasDescriptor = true,
            IsFloat = IsFloat,
            FileVersion = FileVersion,
            ProgramVersion = ProgramVersion,
            DescriptorBytes = DescriptorBytes,
            HeaderBytes = HeaderBytes,
            SeekTableBytes = SeekTableBytes,
            HeaderDataBytes = HeaderDataBytes,
            FrameDataBytes = FrameDataBytes,
            TerminatingBytes = TerminatingBytes,
            Md5 = Md5,
            CompressionLevel = BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            Flags = (FormatFlags)BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]),
            BlocksPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
            FinalFrameBlocks = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            TotalFrames = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]),
            Channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]),
            SampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]),
        };
    }

    // The version of the signature's file: the 16 bits after it.
    public static int VersionOf(ReadOnlySpan<byte> bytes) => bytes.Length < 6 ? 0 : BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);

    // The old header of 32 bytes (version 3.97 and earlier), with the peak level and the seek element count that follow when its flags say so.
    // bytes must hold the 32 bytes and then the optional fields.
    public static ApeHeader ReadOld(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < OldHeaderLength)
        {
            throw new EndOfStreamException();
        }

        int version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        int level = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var flags = (FormatFlags)BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        uint totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
        int at = OldHeaderLength;
        int? peak = null;
        if ((flags & FormatFlags.HasPeakLevel) != 0)
        {
            if (bytes.Length < at + 4)
            {
                throw new EndOfStreamException();
            }

            peak = BinaryPrimitives.ReadInt32LittleEndian(bytes[at..]);
            at += 4;
        }

        uint seek = totalFrames;
        if ((flags & FormatFlags.HasSeekElements) != 0)
        {
            if (bytes.Length < at + 4)
            {
                throw new EndOfStreamException();
            }

            seek = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
            at += 4;
        }

        return new ApeHeader
        {
            HasDescriptor = false,
            FileVersion = version,
            CompressionLevel = level,
            Flags = flags,
            Channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]),
            SampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]),
            HeaderDataBytes = (flags & FormatFlags.CreatesWavHeader) != 0 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
            TerminatingBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]),
            TotalFrames = totalFrames,
            FinalFrameBlocks = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]),
            BlocksPerFrame = OldBlocksPerFrame(version, level),
            BitsPerSample = (flags & FormatFlags.Is8Bit) != 0 ? 8 : (flags & FormatFlags.Is24Bit) != 0 ? 24 : 16,
            PeakLevel = peak,
            SeekElements = seek,
            OldHeaderBytes = at,
        };
    }

    // The bytes the old header takes up: its 32 and the optional fields after them.
    public int OldHeaderBytes { get; init; }

    // The bytes of the descriptor and the header, or of the old header with its optional fields.
    public long FixedLength => HasDescriptor ? (long)DescriptorBytes + HeaderBytes : OldHeaderBytes;

    public long SeekElementCount => HasDescriptor ? SeekTableBytes / 4 : SeekElements ?? TotalFrames;
}
