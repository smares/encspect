namespace Encspect.Formats.Aac;

// What an access unit's nodes are rebuilt from, since the report outlives the file it was read
// from: the bits of each raw data block without the ranges its nodes only show as a whole (the
// scalefactors, the Huffman-coded spectral data, fill bytes, SBR data), plus where those ranges were
// and how long; a block that could not be read is kept whole, to be read again as it was. Entries
// lie one after another in shared chunks, as the MPEG parser keeps its side information, so a
// stream keeps a few tens of bytes per access unit and never one array per unit.
//
// An entry: a flag byte (1: kept whole), the bits kept, the length of the cut list, the kept bytes
// and the cut list (kind, length and extra value per cut, as varints).
internal sealed class UnitStore
{
    private const int ChunkBits = 20;
    private const int ChunkSize = 1 << ChunkBits;

    private readonly List<byte[]> _chunks = [];
    private int _used;

    public long Bytes { get; private set; }

    // Stores the bits [0, end) of a block that its reader read without error, minus the cuts, which
    // lie in order inside it; returns where the entry starts.
    public long AddSkeleton(ReadOnlySpan<byte> block, int start, int end, IReadOnlyList<BlockCut> cuts)
    {
        int kept = end - start;
        foreach (BlockCut cut in cuts)
        {
            kept -= cut.Length;
        }

        var cutBytes = new List<byte>(cuts.Count * 4);
        foreach (BlockCut cut in cuts)
        {
            cutBytes.Add((byte)cut.Kind);
            WriteVarint(cutBytes, cut.Length);
            WriteVarint(cutBytes, cut.Extra + 1);
        }

        int keptBytes = (kept + 7) >> 3;
        Span<byte> bits = keptBytes <= 4096 ? stackalloc byte[keptBytes] : new byte[keptBytes];
        bits.Clear();
        int to = 0;
        int from = start;
        foreach (BlockCut cut in cuts)
        {
            CopyBits(block, from, cut.Start - from, bits, to);
            to += cut.Start - from;
            from = cut.Start + cut.Length;
        }

        CopyBits(block, from, end - from, bits, to);
        var head = new List<byte>(12) { 0 };
        WriteVarint(head, kept);
        WriteVarint(head, cutBytes.Count);
        return Write(head, bits, cutBytes);
    }

    // Stores a block whole, as a block of bits from start to end.
    public long AddWhole(ReadOnlySpan<byte> block, int start, int end)
    {
        int length = end - start;
        int bytes = (length + 7) >> 3;
        Span<byte> bits = bytes <= 4096 ? stackalloc byte[bytes] : new byte[bytes];
        bits.Clear();
        CopyBits(block, start, length, bits, 0);
        var head = new List<byte>(12) { 1 };
        WriteVarint(head, length);
        WriteVarint(head, 0);
        return Write(head, bits, []);
    }

    // The entry at position: whether it is kept whole, its bits and its cut list, and where the
    // next entry starts.
    public Entry Read(long position)
    {
        byte[] chunk = _chunks[(int)(position >> ChunkBits)];
        int at = (int)(position & (ChunkSize - 1));
        bool whole = chunk[at++] == 1;
        int bits = ReadVarint(chunk, ref at);
        int cuts = ReadVarint(chunk, ref at);
        int bytes = (bits + 7) >> 3;
        var data = new ReadOnlyMemory<byte>(chunk, at, bytes);
        var cutList = new ReadOnlyMemory<byte>(chunk, at + bytes, cuts);
        return new Entry(whole, bits, data, cutList, position + (at - (int)(position & (ChunkSize - 1))) + bytes + cuts);
    }

    public static int ReadVarint(ReadOnlySpan<byte> data, ref int at)
    {
        int value = 0;
        int shift = 0;
        while (true)
        {
            byte b = data[at++];
            value |= (b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value;
            }

            shift += 7;
        }
    }

    private static void WriteVarint(List<byte> to, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            to.Add((byte)(v | 0x80));
            v >>= 7;
        }

        to.Add((byte)v);
    }

    private long Write(List<byte> head, ReadOnlySpan<byte> bits, List<byte> cuts)
    {
        int size = head.Count + bits.Length + cuts.Count;
        if (_chunks.Count == 0 || _used + size > _chunks[^1].Length)
        {
            // An entry larger than a chunk gets a chunk of its own, which nothing else joins.
            _chunks.Add(new byte[Math.Max(ChunkSize, size)]);
            _used = 0;
        }

        long position = ((long)(_chunks.Count - 1) << ChunkBits) | (long)_used;
        Copy(_chunks[^1], _used, head, bits, cuts);
        _used += size;
        Bytes += size;
        return position;
    }

    private static void Copy(byte[] chunk, int at, List<byte> head, ReadOnlySpan<byte> bits, List<byte> cuts)
    {
        foreach (byte b in head)
        {
            chunk[at++] = b;
        }

        bits.CopyTo(chunk.AsSpan(at));
        at += bits.Length;
        foreach (byte b in cuts)
        {
            chunk[at++] = b;
        }
    }

    // Copies count bits from bit from of source to bit to of target, which is zero there.
    private static void CopyBits(ReadOnlySpan<byte> source, int from, int count, Span<byte> target, int to)
    {
        // Eight bits at a time while they last, read from wherever they start and written where
        // they go; the target is clear.
        for (; count >= 8; from += 8, to += 8, count -= 8)
        {
            int at = from >> 3;
            int shift = from & 7;
            int value = shift == 0 ? source[at] : ((source[at] << shift) | (source[at + 1] >> (8 - shift))) & 0xFF;
            int into = to >> 3;
            int offset = to & 7;
            target[into] |= (byte)(value >> offset);
            if (offset != 0)
            {
                target[into + 1] |= (byte)(value << (8 - offset));
            }
        }

        for (int i = 0; i < count; i++)
        {
            int bit = (source[(from + i) >> 3] >> (7 - ((from + i) & 7))) & 1;
            target[(to + i) >> 3] |= (byte)(bit << (7 - ((to + i) & 7)));
        }
    }

    public readonly record struct Entry(bool IsWhole, int Bits, ReadOnlyMemory<byte> Data, ReadOnlyMemory<byte> Cuts, long Next);
}

// A range of a raw data block that a skeleton leaves out: its kind, first bit and length, and an
// extra value (where in an SBR payload the parametric stereo extension's ID lies, or -1).
internal readonly record struct BlockCut(CutKind Kind, int Start, int Length, int Extra);
