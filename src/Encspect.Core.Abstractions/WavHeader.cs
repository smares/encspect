using System.Buffers.Binary;

namespace Encspect.Core;

// Shared by the parsers of the formats that store a WAVE file's header (Monkey's Audio, OptimFROG). What the stored header data holds that the analyzer reads: the kind of file it begins like, and for a RIFF, RF64, BW64 or Wave64 header the few fields
// of its fmt chunk that give the audio's layout, with where each lies. It is not a WAV parser: the chunks before the fmt chunk are skipped by their sizes.
public sealed record WavFormat
{
    // The reading stopped for this reason, or None where the fmt chunk was read whole.
    public WavProblem Problem { get; init; }

    // What the problem is about: the position of the chunk the header data ends in (or of the position where a chunk should be), its ID and its stated size.
    public long ProblemAt { get; init; }

    public string ProblemChunk { get; init; } = string.Empty;

    public long ProblemSize { get; init; }

    public long ProblemHeld { get; init; }

    public int Tag { get; init; } = -1;

    public long TagAt { get; init; }

    public int Channels { get; init; } = -1;

    // The cbSize of an extensible fmt chunk (-1 where the chunk holds none), and the bytes that follow it in the chunk.
    public int ExtraSize { get; init; } = -1;

    public long ExtraHeld { get; init; }

    // An extensible fmt chunk of 16 or 17 bytes, which ends before the cbSize.
    public bool IsCbSizeMissing { get; init; }

    public long ChannelsAt { get; init; }

    public uint? Mask { get; init; }

    public long MaskAt { get; init; }

    // The common fields after the channels, read with the mask, which the decoder's AudioFormat states.
    public int SampleRate { get; init; }

    public int BytesPerSecond { get; init; }

    public int BlockAlign { get; init; }

    public int BitsPerSample { get; init; }
}

public enum WavProblem
{
    None,

    // The header data ends inside a chunk's 8-byte header, or inside a chunk before the fmt chunk.
    CutInChunk,

    // The header data ends inside the fmt chunk.
    CutInFormat,

    // The fmt chunk states fewer bytes than the 16 of the common fields.
    FormatShort,

    // The data chunk or the end of the header data comes before a fmt chunk.
    NoFormat,

    // A Wave64 chunk states a size under the 24 bytes of its own header.
    ChunkSizeInvalid,

    // More chunks than the walk follows.
    TooManyChunks,
}

public static class WavHeader
{
    // A header that holds more chunks than this before its fmt chunk is not a plausible WAVE header.
    private const int MaxChunks = 64;

    public const string RiffContainer = "a RIFF WAVE header";

    public const string Rf64Container = "an RF64 WAVE header";

    public const string Bw64Container = "a BW64 WAVE header";

    public const string Wave64Container = "a Wave64 header";

    // The sizes of Wave64: the riff GUID, the 64-bit size and the wave GUID, and a chunk's GUID with its size, which counts these 24 bytes.
    private const int Wave64HeaderLength = 40;

    private const int Wave64ChunkHeaderLength = 24;

    private static readonly byte[] Wave64Riff = Convert.FromHexString("726966662E91CF11A5D628DB04C10000");

    private static readonly byte[] Wave64Wave = Convert.FromHexString("77617665F3ACD3118CD100C04F8EDB8A");

    // The twelve bytes after the four characters in the GUID of fmt, data and the other chunks named by four characters.
    private static readonly byte[] Wave64TextTail = Convert.FromHexString("F3ACD3118CD100C04F8EDB8A");

    public static string? Container(ByteReader reader, long at, long length)
    {
        if (length >= Wave64HeaderLength && at + Wave64HeaderLength <= reader.End)
        {
            byte[] guids = reader.ReadBytesAt(at, Wave64HeaderLength);
            if (guids.AsSpan(0, 16).SequenceEqual(Wave64Riff) && guids.AsSpan(24, 16).SequenceEqual(Wave64Wave))
            {
                return Wave64Container;
            }
        }

        if (length < 12 || at + 12 > reader.End)
        {
            return null;
        }

        byte[] head = reader.ReadBytesAt(at, 12);
        ReadOnlySpan<byte> id = head.AsSpan(0, 4);
        ReadOnlySpan<byte> kind = head.AsSpan(8, 4);
        if (kind.SequenceEqual("WAVE"u8))
        {
            if (id.SequenceEqual("RIFF"u8))
            {
                return RiffContainer;
            }

            if (id.SequenceEqual("RF64"u8))
            {
                return Rf64Container;
            }

            // The SDK takes BW64 as it takes RIFF and RF64 (WAVInputSource.cpp, AnalyzeSource).
            if (id.SequenceEqual("BW64"u8))
            {
                return Bw64Container;
            }
        }

        if (id.SequenceEqual("FORM"u8))
        {
            return "an AIFF header";
        }

        return id.SequenceEqual("caff"u8) ? "a CAF header" : null;
    }

    // Walks the chunks of a RIFF, RF64 or BW64 WAVE header or a Wave64 header in the length bytes at `at` to the fmt chunk (Multimedia Programming
    // Interface and Data Specifications 1.0, "WAVE Format Chunk"; Microsoft, WAVEFORMATEXTENSIBLE) and reads its tag and channels, and for the
    // extensible form with a cbSize of 22 or more the channel mask. The ds64 chunk of RF64 and BW64 is skipped by its size like any chunk before the
    // fmt chunk. Null where the data is no such header. Every read stays within the length bytes.
    public static WavFormat? Read(ByteReader reader, long at, long length)
    {
        long end = Math.Min(at + length, reader.End);
        return Container(reader, at, end - at) switch
        {
            Wave64Container => ReadWave64(reader, at, end),
            RiffContainer or Rf64Container or Bw64Container => ReadRiff(reader, at, end),
            _ => null,
        };
    }

    private static WavFormat ReadRiff(ByteReader reader, long at, long end)
    {
        long position = at + 12;
        for (int i = 0; i < MaxChunks; i++)
        {
            if (position >= end)
            {
                return new WavFormat { Problem = WavProblem.NoFormat, ProblemAt = position };
            }

            if (end - position < 8)
            {
                return new WavFormat { Problem = WavProblem.CutInChunk, ProblemAt = position, ProblemSize = -1, ProblemHeld = end - position };
            }

            byte[] head = reader.ReadBytesAt(position, 8);
            string id = System.Text.Encoding.Latin1.GetString(head, 0, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
            long body = position + 8;
            if (id == "data")
            {
                return new WavFormat { Problem = WavProblem.NoFormat, ProblemAt = position };
            }

            if (id == "fmt ")
            {
                return ReadFormat(reader, position, body, size, end);
            }

            long next = body + size + (size & 1);
            if (body + size > end)
            {
                return new WavFormat { Problem = WavProblem.CutInChunk, ProblemAt = position, ProblemChunk = id, ProblemSize = size, ProblemHeld = end - body };
            }

            position = next;
        }

        return new WavFormat { Problem = WavProblem.TooManyChunks, ProblemAt = position };
    }

    // The chunks of Wave64 have a GUID for an ID, a 64-bit size that counts the 24 bytes of the chunk's header, and start at multiples of 8 bytes
    // from the start of the file (WAVInputSource.cpp, CW64InputSource::AnalyzeSource, aligns the size the same way).
    private static WavFormat ReadWave64(ByteReader reader, long at, long end)
    {
        long position = at + Wave64HeaderLength;
        for (int i = 0; i < MaxChunks; i++)
        {
            if (position >= end)
            {
                return new WavFormat { Problem = WavProblem.NoFormat, ProblemAt = position };
            }

            if (end - position < Wave64ChunkHeaderLength)
            {
                return new WavFormat { Problem = WavProblem.CutInChunk, ProblemAt = position, ProblemSize = -1, ProblemHeld = end - position };
            }

            byte[] head = reader.ReadBytesAt(position, Wave64ChunkHeaderLength);
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(16));
            if (size < Wave64ChunkHeaderLength)
            {
                return new WavFormat { Problem = WavProblem.ChunkSizeInvalid, ProblemAt = position, ProblemSize = (long)Math.Min(size, long.MaxValue) };
            }

            string? id = head.AsSpan(4, 12).SequenceEqual(Wave64TextTail) && head.AsSpan(0, 4).IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) < 0 ? System.Text.Encoding.Latin1.GetString(head, 0, 4) : null;
            long body = position + Wave64ChunkHeaderLength;
            long stated = (long)Math.Min(size - Wave64ChunkHeaderLength, long.MaxValue);
            if (id == "data")
            {
                return new WavFormat { Problem = WavProblem.NoFormat, ProblemAt = position };
            }

            if (id == "fmt ")
            {
                return ReadFormat(reader, position, body, stated, end);
            }

            if (size > (ulong)(end - position))
            {
                return new WavFormat { Problem = WavProblem.CutInChunk, ProblemAt = position, ProblemChunk = id ?? new Guid(head.AsSpan(0, 16)).ToString(), ProblemSize = stated, ProblemHeld = end - body };
            }

            position += (long)((size + 7) & ~7UL);
        }

        return new WavFormat { Problem = WavProblem.TooManyChunks, ProblemAt = position };
    }

    private static WavFormat ReadFormat(ByteReader reader, long chunkAt, long body, long size, long end)
    {
        long held = Math.Min(size, end - body);
        WavProblem problem = size > held ? WavProblem.CutInFormat : size < 16 ? WavProblem.FormatShort : WavProblem.None;
        var format = new WavFormat { Problem = problem, ProblemAt = chunkAt, ProblemChunk = "fmt ", ProblemSize = size, ProblemHeld = held };
        if (held < 4)
        {
            return format;
        }

        byte[] fields = reader.ReadBytesAt(body, (int)Math.Min(held, 40));
        format = format with { Tag = BinaryPrimitives.ReadUInt16LittleEndian(fields), TagAt = body, Channels = BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(2)), ChannelsAt = body + 2 };
        if (format.Tag == WaveExtensible && held >= 18)
        {
            // The cbSize counts the bytes after it, as many as the chunk holds: the mask is read only where the whole extension (valid bits, mask and sub-format) is.
            int stated = BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(16));
            int extra = (int)Math.Min(stated, held - 18);
            format = format with { ExtraSize = stated, ExtraHeld = held - 18 };
            if (extra >= 22)
            {
                format = format with
                {
                    Mask = BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(20)),
                    MaskAt = body + 20,
                    SampleRate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(4)), int.MaxValue),
                    BytesPerSecond = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(8)), int.MaxValue),
                    BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(12)),
                    BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fields.AsSpan(14)),
                };
            }
        }

        if (format.Tag == WaveExtensible && held is >= 16 and < 18)
        {
            format = format with { IsCbSizeMissing = true };
        }

        return format;
    }

    public const int WaveExtensible = 0xFFFE;

    public static string TagText(int tag) => WaveFormatNames.Show(tag);

    // A header that holds more chunks than this before its fmt chunk is not a plausible WAVE header.
    public const int MaxChunksBeforeFormat = MaxChunks;
}
