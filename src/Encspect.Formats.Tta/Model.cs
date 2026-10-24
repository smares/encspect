using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.Tta;

// The names the checksums of a TTA stream are declared with (StreamVerification.Checksums).
internal static class ChecksumNames
{
    public const string HeaderCrc32 = "header CRC-32";
    public const string SeekTableCrc32 = "seek table CRC-32";
    public const string FrameCrc32 = "frame CRC-32";
}

// The TTA1 header (the TTA format description, "TTA1 header"): the signature, then little-endian fields and a CRC-32 over the 18 bytes before it.
internal readonly record struct TtaHeader(int AudioFormat, int Channels, int BitsPerSample, uint SampleRate, uint Samples, uint StoredCrc, uint ComputedCrc)
{
    public const int Length = 22;

    public const int CrcOffset = 18;

    // The audio formats Encspect's test files hold: 1 in the files FFmpeg 6.1.1 and libtta-c 2.3 write, and 2 in the one libtta-c 2.3 writes for a
    // stream encoded with a password (FFmpeg 6.1.1 asks for a password to open it: "Missing password for encrypted stream"). The description
    // defines neither.
    public const int FormatSimple = 1;
    public const int FormatEncrypted = 2;

    public static ReadOnlySpan<byte> Signature => "TTA1"u8;

    public static ReadOnlySpan<byte> SignatureV2 => "TTA2"u8;

    public bool IsCrcValid => StoredCrc == ComputedCrc;

    // Samples in every frame but the last: the sample rate times the "frame time" of 256/245 s, truncated (the description's 1.04489795918...;
    // truncation is what the seek tables of the files written by FFmpeg 6.1.1 and libtta-c 2.3 show, with a frame of 33,436 samples at 32,000 Hz).
    public long FrameLength => FrameLengthOf(SampleRate);

    public static long FrameLengthOf(long sampleRate) => sampleRate * 256 / 245;

    // The seek table has one entry for each frame; 0 where the rate leaves the frame length undefined.
    public long FrameCount => FrameLength == 0 ? 0 : (Samples + FrameLength - 1) / FrameLength;

    public static TtaHeader Read(ReadOnlySpan<byte> bytes) => new(
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]),
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[CrcOffset..]),
        Crc32.Of(bytes[..CrcOffset]));

    public static bool HasSignature(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Signature);
}

[Flags]
internal enum EntryStatus : byte
{
    None = 0,

    // The data ends inside the frame.
    Cut = 1,

    // The frame is shorter than its CRC-32.
    TooSmall = 2,

    // The seek table lists the frame, and the data ends before it begins.
    Missing = 4,
}

// One entry of the seek table, or one packet of a stream a container holds: where the frame is, how long, the CRC-32 it stores and the one
// computed over its bytes before it.
internal struct Entry
{
    public long Offset;
    public uint Size;
    public uint StoredCrc;
    public uint ComputedCrc;
    public EntryStatus Status;

    public readonly bool IsComplete => (Status & (EntryStatus.Cut | EntryStatus.TooSmall | EntryStatus.Missing)) == 0;

    public readonly bool IsCrcValid => StoredCrc == ComputedCrc;
}

// What the nodes are built from: one entry for each frame, the header and the facts about the seek table.
internal sealed class Records
{
    // The most entries of the seek table, and so the most frames, the records keep: a crafted table can list billions. The entries past it are
    // counted and their CRC-32s compared, but they have no record, no node and no place in the graphs.
    public const int MaxEntries = 1 << 21;

    // The entries whose frame the data holds, wholly or in part, as far as MaxEntries: entry i of the seek table is Entries[i]. The entries
    // past the end of the data have none (they would cost 24 bytes each for 4 bytes of the table).
    public ChunkedList<Entry> Entries { get; } = new();

    // The length each of the first MaxEntries entries of the seek table states, for the seek point nodes.
    public ChunkedList<uint> Sizes { get; } = new();

    // The entries of the seek table the data holds (all of them, MaxEntries or not).
    public long TableEntries { get; set; }

    // Frames whose bytes are not contiguous in the file (a packet a container stored in pieces).
    public Dictionary<int, IReadOnlyList<ByteRange>> Ranges { get; } = [];

    public TtaHeader? Header { get; set; }

    // The number of entries that have a frame node: every recorded one.
    public int FrameNodes => Entries.Count;

    public long HeaderOffset { get; set; }

    // The seek table: where it starts, how many entries it holds in the file, and its CRC-32 as stored and as computed (null when the data
    // ends before the CRC-32).
    public long TableOffset { get; set; }

    public uint? TableStoredCrc { get; set; }

    public uint TableComputedCrc { get; set; }

    // The end of the data the stream was read from, which cuts the last frame.
    public long DataEnd { get; set; }

    // Samples in frame index; -1 where the sample rate or the sample count is not known.
    public long SamplesOf(long index)
    {
        if (Header is not { SampleRate: > 0 } header)
        {
            return -1;
        }

        long frameLength = header.FrameLength;
        long count = header.FrameCount;
        return index < count - 1 ? frameLength : index == count - 1 ? header.Samples - ((count - 1) * frameLength) : frameLength;
    }
}

// The background articles the TTA nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string Stream = "tta-stream";
    public const string SeekTable = "tta-seek-table";
    public const string Coding = "tta-coding";
    public const string InMatroska = "tta-in-matroska";
    public const string Crc = "crc";
}
