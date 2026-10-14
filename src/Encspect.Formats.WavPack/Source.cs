using Encspect.Core;

namespace Encspect.Formats.WavPack;

// Finds blocks in the data. A header counts only with the checks libwavpack's read_next_header makes
// (BlockHeader.IsPlausible), and a header found after bytes that are no block counts only where the
// block it starts ends at the end of the data or at another such header: the signature is four
// bytes, which the audio can hold.
internal sealed class Source
{
    private readonly BlockCache _cache;

    // Probes for headers read through a cache of their own: a probe at the end of a block, a
    // megabyte on, would otherwise make the scan that found it read its block again after each one.
    private readonly BlockCache _peek;

    public Source(ByteReader reader)
    {
        _cache = new BlockCache(reader);
        _peek = new BlockCache(reader);
    }

    public long Start => _cache.Start;

    public long End => _cache.End;

    public BlockCache Cache => _cache;

    public bool TryHeader(long position, out BlockHeader header)
    {
        header = default;
        if (position < Start || position + BlockHeader.Length > End)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[BlockHeader.Length];
        _peek.Read(position, bytes);
        if (!BlockHeader.IsPlausible(bytes))
        {
            return false;
        }

        header = new BlockHeader(bytes);
        return true;
    }

    // Whether the block at position ends where the data ends, or where another header begins.
    public bool IsChained(long position, in BlockHeader header)
    {
        long end = position + header.BlockSize;
        if (end == End)
        {
            return true;
        }

        return end < End && TryHeader(end, out _);
    }

    // The first block in [from, limit) that is followed by another header or the end of the data, or
    // -1.
    public long FindChain(long from, long limit)
    {
        limit = Math.Min(limit, End);
        long position = Math.Max(from, Start);
        int steps = 0;
        while (position < limit)
        {
            if (++steps % 4096 == 0)
            {
                _cache.Reader.CancellationToken.ThrowIfCancellationRequested();
            }

            long found = _cache.IndexOf((byte)'w', position, limit);
            if (found < 0)
            {
                return -1;
            }

            if (TryHeader(found, out BlockHeader header) && IsChained(found, header))
            {
                return found;
            }

            position = found + 1;
        }

        return -1;
    }
}
