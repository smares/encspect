using System.Buffers.Binary;
using System.Text;
using Encspect.Core;

// Reading the layout of a WavPack file of format version 1, 2 or 3 follows libwavpack 5.6.0's src/unpack3_open.c and src/unpack3.h (BSD 3-Clause, Copyright (c)
// 1998 - 2022 David Bryant, all rights reserved; the notice is in THIRD-PARTY-NOTICES.md) and the encoder of WavPack 3.97 (wavpack.c, BSD): the WAV header a file
// starts with, the WavPack header of 10, 12 or 36 bytes behind it, and the flags of version 3.
namespace Encspect.Formats.WavPack;

// A chunk of the source file's header that precedes the audio, with where it starts and the size it states.
internal readonly record struct StoredChunk(string Id, long At, uint Size);

// Where the parts of a legacy file lie. A file starts with the source WAV file's header up to the data chunk's 8-byte header and the WavPack header follows at
// once; a raw file of version 3 starts with the WavPack header.
internal sealed class LegacyLayout
{
    // How many chunks the stored header may hold before the data chunk; a header with more is not read as one.
    public const int MaxChunks = 256;

    public const int RiffLength = 12;

    public const int ChunkHeaderLength = 8;

    public bool IsRaw { get; init; }

    public long RiffAt { get; init; }

    public uint RiffSize { get; init; }

    public IReadOnlyList<StoredChunk> Chunks { get; init; } = [];

    // Where the WavPack header's signature lies.
    public long HeaderAt { get; init; }

    // The version the header states, or -1 where the data ends before it.
    public int Version { get; init; } = -1;

    // The end of the stored header: where the WavPack header begins.
    public long StoredEnd => IsRaw ? RiffAt : HeaderAt;

    public static int HeaderLength(int version) => version switch { 1 => 10, 2 => 12, 3 => 36, _ => 0 };

    // The layout of a file that holds a WavPack header of version 1 to 3 behind a RIFF WAVE header's chunks, or at its start; null where it holds none. A header of
    // another version behind the data chunk's header counts, so a file of one is reported as such (wavpack.version-unsupported); at the start only the versions 1 to 3
    // do, so that a stream of version 4 or 5 is read as one.
    public static LegacyLayout? Locate(ByteReader reader)
    {
        long start = reader.Start;
        if (reader.Length < 10)
        {
            return null;
        }

        byte[] head = reader.ReadBytesAt(start, (int)Math.Min(12, reader.Length));
        if (head.AsSpan(0, 4).SequenceEqual("wvpk"u8))
        {
            int version = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(8));
            return version is >= 1 and <= 3 ? new LegacyLayout { IsRaw = true, RiffAt = start, HeaderAt = start, Version = version } : null;
        }

        if (head.Length < 12 || !head.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !head.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return null;
        }

        var chunks = new List<StoredChunk>();
        long position = start + RiffLength;
        for (int i = 0; i < MaxChunks && position + ChunkHeaderLength <= reader.End; i++)
        {
            byte[] chunk = reader.ReadBytesAt(position, ChunkHeaderLength);
            string id = Encoding.Latin1.GetString(chunk, 0, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            chunks.Add(new StoredChunk(id, position, size));
            long body = position + ChunkHeaderLength;
            if (id == "data")
            {
                if (body + 4 > reader.End || !reader.MatchesAt(body, "wvpk"u8))
                {
                    return null;
                }

                int version = body + 10 <= reader.End ? BinaryPrimitives.ReadUInt16LittleEndian(reader.ReadBytesAt(body + 8, 2)) : -1;
                return new LegacyLayout { RiffAt = start, RiffSize = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)), Chunks = chunks, HeaderAt = body, Version = version };
            }

            long next = body + size + (size & 1);
            if (next > reader.End)
            {
                return null;
            }

            position = next;
        }

        return null;
    }
}

// The fields of a WavPack header of version 1, 2 or 3 that the data holds, read as unpack3.h's WavpackHeader3 lays them out: ckID, ckSize and version in 10
// bytes, bits in version 2 and 3, and from version 3 the flags, the shift, the total samples, the two checks, the source's extension and the bytes after a
// raw file's last whole sample.
internal readonly record struct LegacyHeader(long At, int Held, uint Size, int Version, int Bits, int Flags, int Shift, uint TotalSamples, uint Crc, uint Crc2, string Extension, int ExtraCount, byte[] Extras)
{
    public const int MonoFlag = 0x0001;
    public const int FastFlag = 0x0002;
    public const int RawFlag = 0x0004;
    public const int CalcNoise = 0x0008;
    public const int HighFlag = 0x0010;
    public const int Bytes3Flag = 0x0020;
    public const int Over20Flag = 0x0040;
    public const int WvcFlag = 0x0080;
    public const int LossyShapeFlag = 0x0100;
    public const int VeryFastFlag = 0x0200;
    public const int NewHighFlag = 0x0400;
    public const int CancelExtremeFlag = 0x0800;
    public const int CrossDecorrFlag = 0x1000;
    public const int NewDecorrFlag = 0x2000;
    public const int JointStereoFlag = 0x4000;
    public const int ExtremeDecorrFlag = 0x8000;

    // The flags no encoder writes any more (unpack3.h NOT_STORED_FLAGS): a reader that finds one with the extreme flag refuses the file.
    public const int NotStoredFlags = CalcNoise | WvcFlag | VeryFastFlag;

    public int Length => LegacyLayout.HeaderLength(Version);

    // A header of a version this reader does not know is complete at its 10 bytes: version-unsupported, not a cut.
    public bool IsComplete => Version < 0 ? false : Held >= (Length == 0 ? 10 : Length);

    public bool IsVersion3 => Version == 3;

    // The flags as the decoder takes them after the kludge in unpack3_open.c: with the extreme flag, "cancel extreme" clears both; without it the cross-channel flag is
    // cleared. Versions 1 and 2 store none.
    public int EffectiveFlags
    {
        get
        {
            if (!IsVersion3)
            {
                return 0;
            }

            if ((Flags & ExtremeDecorrFlag) == 0)
            {
                return Flags & ~CrossDecorrFlag;
            }

            return (Flags & CancelExtremeFlag) != 0 ? Flags & ~(ExtremeDecorrFlag | CancelExtremeFlag) : Flags;
        }
    }

    public bool IsHybrid => IsVersion3 && Bits != 0 && (Flags & NewHighFlag) != 0;

    public bool IsLossy => Bits != 0 && !IsHybrid;

    public static LegacyHeader Read(ByteReader reader, long at)
    {
        int held = (int)Math.Min(36, reader.End - at);
        byte[] bytes = reader.ReadBytesAt(at, held);
        ReadOnlySpan<byte> b = bytes;
        uint size = held >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(b[4..]) : 0;
        int version = held >= 10 ? BinaryPrimitives.ReadUInt16LittleEndian(b[8..]) : -1;
        int bits = version >= 2 && held >= 12 ? BinaryPrimitives.ReadUInt16LittleEndian(b[10..]) : 0;
        if (version != 3 || held < 36)
        {
            return new LegacyHeader(at, held, size, version, bits, 0, 0, 0, 0, 0, string.Empty, 0, []);
        }

        return new LegacyHeader(at, held, size, version, bits, BinaryPrimitives.ReadUInt16LittleEndian(b[12..]), BinaryPrimitives.ReadUInt16LittleEndian(b[14..]),
            BinaryPrimitives.ReadUInt32LittleEndian(b[16..]), BinaryPrimitives.ReadUInt32LittleEndian(b[20..]), BinaryPrimitives.ReadUInt32LittleEndian(b[24..]),
            Encoding.Latin1.GetString(bytes, 28, 4).TrimEnd('\0'), b[32], b.Slice(33, 3).ToArray());
    }
}
