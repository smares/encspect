using Encspect.Core;

namespace Encspect.Formats.WavPack;

// Finds blocks in the data. A header counts only with the checks libwavpack's read_next_header makes
// (BlockHeader.IsPlausible), and a header found after bytes that are no block counts only where the
// block it starts ends at the end of the data or at another such header: the signature is four
// bytes, which the audio can hold. Tags that an ID3v2.4 SEEK frame found inside the data are
// skipped, as the stream's own bytes end where they begin.
internal sealed class Source
{
    private readonly BlockCache _cache;

    // Probes for headers read through a cache of their own: a probe at the end of a block, a
    // megabyte on, would otherwise make the scan that found it read its block again after each one.
    private readonly BlockCache _peek;
    private readonly (long Start, long End)[] _tags;

    public Source(ByteReader reader, IEnumerable<(long Start, long End)>? tags = null)
    {
        _cache = new BlockCache(reader);
        _peek = new BlockCache(reader);
        _tags = [.. (tags ?? []).OrderBy(t => t.Start)];
    }

    public long Start => _cache.Start;

    public long End => _cache.End;

    public BlockCache Cache => _cache;

    // The first offset at or after position that no embedded tag covers.
    public long SkipTags(long position)
    {
        foreach ((long start, long end) in _tags)
        {
            if (position >= start && position < end)
            {
                position = end;
            }
        }

        return position;
    }

    // The start of the first embedded tag at or after position, or the end of the data.
    public long NextTag(long position)
    {
        foreach ((long start, _) in _tags)
        {
            if (start >= position)
            {
                return start;
            }
        }

        return End;
    }

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

        end = SkipTags(end);
        return end == End || (end < End && TryHeader(end, out _));
    }

    // The first block in [from, limit) that is followed by another header or the end of the data, or
    // -1.
    public long FindChain(long from, long limit)
    {
        limit = Math.Min(limit, End);
        long position = SkipTags(Math.Max(from, Start));
        int steps = 0;
        while (position < limit)
        {
            if (++steps % 4096 == 0)
            {
                _cache.Reader.CancellationToken.ThrowIfCancellationRequested();
            }

            long stop = Math.Min(limit, NextTag(position));
            long found = _cache.IndexOf((byte)'w', position, stop);
            if (found < 0)
            {
                if (stop >= limit)
                {
                    return -1;
                }

                position = SkipTags(stop);
                continue;
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
