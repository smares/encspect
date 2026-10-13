using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Encspect.Formats.WavPack;

// The 32-byte header of every block, in the main file and in the correction file (WavPack 4 and 5
// binary file/block format, section 2.0; libwavpack 5.6.0 wavpack.h WavpackHeader).
[StructLayout(LayoutKind.Auto)]
internal readonly struct BlockHeader
{
    public const int Length = 32;

    // libwavpack refuses blocks past 1 MiB (read_next_header: the third byte of the size is below 16).
    public const long MaxBlockSize = 1 << 20;

    // The versions libwavpack decodes (wavpack.h MIN_STREAM_VERS, MAX_STREAM_VERS).
    public const int MinVersion = 0x402;
    public const int MaxVersion = 0x410;

    public const uint BytesStoredMask = 3;
    public const uint MonoFlag = 4;
    public const uint HybridFlag = 8;
    public const uint JointStereoFlag = 0x10;
    public const uint CrossDecorrFlag = 0x20;
    public const uint HybridShapeFlag = 0x40;
    public const uint FloatFlag = 0x80;
    public const uint Int32Flag = 0x100;
    public const uint HybridBitrateFlag = 0x200;
    public const uint HybridBalanceFlag = 0x400;
    public const uint InitialBlockFlag = 0x800;
    public const uint FinalBlockFlag = 0x1000;
    public const int ShiftLsb = 13;
    public const int MagnitudeLsb = 18;
    public const int SampleRateLsb = 23;
    public const uint ReservedFlag = 0x08000000;
    public const uint HasChecksumFlag = 0x10000000;
    public const uint NewShapingFlag = 0x20000000;
    public const uint FalseStereoFlag = 0x40000000;
    public const uint DsdFlag = 0x80000000;

    public static readonly int[] SampleRates = [6000, 8000, 9600, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000, 192000];

    public BlockHeader(ReadOnlySpan<byte> bytes)
    {
        CkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        Version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        IndexHigh = bytes[10];
        TotalHigh = bytes[11];
        TotalLow = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        IndexLow = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        Samples = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]);
        Flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
        Crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
    }

    public uint CkSize { get; }

    public ushort Version { get; }

    public byte IndexHigh { get; }

    public byte TotalHigh { get; }

    public uint TotalLow { get; }

    public uint IndexLow { get; }

    public uint Samples { get; }

    public uint Flags { get; }

    public uint Crc { get; }

    // The size of the block: the stored size counts from the byte after itself.
    public long BlockSize => CkSize + 8L;

    // The 40-bit index of the block's first sample (GET_BLOCK_INDEX).
    public long BlockIndex => IndexLow + ((long)IndexHigh << 32);

    // The 40-bit sample count, where all ones in the low 32 bits mean unknown whatever the high byte
    // says (GET_TOTAL_SAMPLES).
    public long? TotalSamples => TotalLow == uint.MaxValue ? null : TotalLow + ((long)TotalHigh << 32) - TotalHigh;

    public bool IsMono => (Flags & MonoFlag) != 0;

    public bool IsHybrid => (Flags & HybridFlag) != 0;

    public bool IsFloat => (Flags & FloatFlag) != 0;

    public bool IsDsd => (Flags & DsdFlag) != 0;

    public bool IsFalseStereo => (Flags & FalseStereoFlag) != 0;

    // Mono data (the mono flag or false stereo) is coded as one channel; with both flags libwavpack
    // refuses the block (unpack_init).
    public bool IsMonoData => (Flags & (MonoFlag | FalseStereoFlag)) != 0;

    public bool IsInitial => (Flags & InitialBlockFlag) != 0;

    public bool IsFinal => (Flags & FinalBlockFlag) != 0;

    public bool HasAudio => Samples != 0;

    public int BytesPerSample => (int)(Flags & BytesStoredMask) + 1;

    public int LeftShift => (int)((Flags >> ShiftLsb) & 0x1F);

    public int MaxMagnitude => (int)((Flags >> MagnitudeLsb) & 0x1F);

    public int SampleRateIndex => (int)((Flags >> SampleRateLsb) & 0xF);

    // The rate the flags give, or 0 for index 15, which stands for a rate in a Sample Rate sub-block.
    public int SampleRate => SampleRateIndex < SampleRates.Length ? SampleRates[SampleRateIndex] : 0;

    // True where the bytes are a header libwavpack's read_next_header takes (open_utils.c): the
    // signature, an even size from 24 bytes to under 1 MiB, the major version 4 and fewer than
    // 0x30000 samples. The minor version is left to a finding, so a block of a version libwavpack
    // refuses still reads as a block.
    public static bool IsPlausible(ReadOnlySpan<byte> b) =>
        b.Length >= Length && b[0] == 'w' && b[1] == 'v' && b[2] == 'p' && b[3] == 'k' && (b[4] & 1) == 0 && b[6] < 16 && b[7] == 0
        && (b[6] != 0 || b[5] != 0 || b[4] >= 24) && b[9] == 4 && b[22] < 3 && b[23] == 0;

    public static bool HasSignature(ReadOnlySpan<byte> b) => b.Length >= 4 && b[0] == 'w' && b[1] == 'v' && b[2] == 'p' && b[3] == 'k';
}
