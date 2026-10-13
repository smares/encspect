namespace Encspect.Formats.Wma;

// A variable-length code: the codes of a table, read bit by bit most significant first. A lookup table
// decides the codes of up to LookupBits bits in one step; the longer ones are followed in a binary
// tree. Symbols are the indexes into the table unless a list of symbols is given.
internal sealed class Vlc
{
    private const int LookupBits = 11;

    private readonly int[] _lookup = new int[1 << LookupBits];
    private readonly List<(int Zero, int One)> _nodes = [(-1, -1)];
    private readonly List<int> _leaves = [];
    private readonly int[] _symbols;
    private readonly byte[] _lengths;

    // Codes given outright, right-aligned in lengths[i] bits.
    public Vlc(ReadOnlySpan<uint> codes, ReadOnlySpan<byte> lengths, ReadOnlySpan<int> symbols = default)
    {
        _lengths = lengths.ToArray();
        _symbols = symbols.Length > 0 ? symbols.ToArray() : [.. Enumerable.Range(0, lengths.Length)];
        Array.Fill(_lookup, -1);
        for (int i = 0; i < lengths.Length; i++)
        {
            if (lengths[i] > 0)
            {
                Add(codes[i], lengths[i], i);
            }
        }
    }

    // The codes follow from the lengths in the order of the table: each code is the running total of
    // 2^-length, left-aligned in 32 bits, as FFmpeg's ff_vlc_init_from_lengths assigns them. A length
    // of 0 is an unused entry.
    public static Vlc FromLengths(ReadOnlySpan<byte> lengths, ReadOnlySpan<int> symbols)
    {
        var codes = new uint[lengths.Length];
        ulong code = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            int length = lengths[i];
            if (length == 0)
            {
                continue;
            }

            codes[i] = (uint)(code >> (32 - length));
            code += 1UL << (32 - length);
        }

        return new Vlc(codes, lengths, symbols);
    }

    public int Count => _lengths.Length;

    // Whether the codes are a complete prefix code: no code starts another and they use all of the code space.
    public bool IsComplete { get; private set; }

    // The lookup holds the index into the table, not the symbol, which may be negative.
    private void Add(uint code, int length, int symbol)
    {
        if (length <= LookupBits)
        {
            int first = (int)(code << (LookupBits - length));
            int count = 1 << (LookupBits - length);
            for (int i = 0; i < count; i++)
            {
                _lookup[first + i] = (symbol << 5) | length;
            }

            return;
        }

        // Longer codes: the lookup entry of their first LookupBits bits says to walk the tree from there.
        int prefix = (int)(code >> (length - LookupBits));
        if (_lookup[prefix] == -1)
        {
            _lookup[prefix] = -2;
        }

        int node = 0;
        for (int bit = length - 1; bit >= 0; bit--)
        {
            int next = (int)((code >> bit) & 1);
            (int zero, int one) = _nodes[node];
            int child = next == 0 ? zero : one;
            if (child < 0)
            {
                _nodes.Add((-1, -1));
                child = _nodes.Count - 1;
                _nodes[node] = next == 0 ? (child, one) : (zero, child);
            }

            node = child;
        }

        while (_leaves.Count < _nodes.Count)
        {
            _leaves.Add(-1);
        }

        _leaves[node] = (symbol << 5) | length;
    }

    // Reads one symbol; -1 where the bits are no code of the table. The position moves past the
    // code, or by LookupBits where none matches.
    public int Read(Bits bits)
    {
        uint peek = bits.Peek(LookupBits);
        int entry = _lookup[peek];
        if (entry >= 0)
        {
            bits.Skip(entry & 31);
            return _symbols[entry >> 5];
        }

        if (entry == -1)
        {
            bits.Skip(LookupBits);
            return -1;
        }

        int node = 0;
        int start = bits.Position;
        for (int depth = 0; depth < 32; depth++)
        {
            int bit = bits.ReadBit();
            (int zero, int one) = _nodes[node];
            node = bit == 0 ? zero : one;
            if (node < 0)
            {
                return -1;
            }

            if (node < _leaves.Count && _leaves[node] >= 0 && _nodes[node] == (-1, -1))
            {
                return _symbols[_leaves[node] >> 5];
            }
        }

        bits.Position = start + 32;
        return -1;
    }

    // Checks the Kraft sum of the table: 1 means the code is complete.
    public bool CheckComplete()
    {
        double sum = 0;
        foreach (byte length in _lengths)
        {
            if (length > 0)
            {
                sum += Math.Pow(2, -length);
            }
        }

        IsComplete = Math.Abs(sum - 1) < 1e-9;
        return IsComplete;
    }
}
