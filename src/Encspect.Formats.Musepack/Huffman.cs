using System.Runtime.CompilerServices;

namespace Encspect.Formats.Musepack;

// The Huffman codes of SV7 and SV8 (libmpcdec r495 huffman.c and mpc_bits_reader.h, BSD 3-clause, THIRD-PARTY-NOTICES.md). Each table lists its codes from the highest
// value of the first 16 bits down: a code belongs to the first entry whose Code is at most the next 16 bits. SV8's canonical tables give the entry's Value as the number of the
// code's first symbol, so the symbol is Symbols[(Value - (bits >> (16 - Length))) & 0xFF]. A lookup on the first 6 bits answers the short codes, as libmpcdec's does; the
// decoding it replaces is the search every code of an entry shares, so it changes no result.
internal sealed class CanonicalCode
{
    private const int LookupBits = 6;

    private readonly int[] _code;
    private readonly int[] _length;
    private readonly int[] _value;
    private readonly sbyte[]? _symbols;
    private readonly byte[] _lookupLength = new byte[1 << LookupBits];
    private readonly int[] _lookupValue = new int[1 << LookupBits];
    private readonly byte[] _lookupStart = new byte[1 << LookupBits];

    // triples: code, length and value of each entry; symbols: the canonical symbols, or empty where the value is the symbol.
    public CanonicalCode(ReadOnlySpan<int> triples, ReadOnlySpan<sbyte> symbols)
    {
        int count = triples.Length / 3;
        _code = new int[count];
        _length = new int[count];
        _value = new int[count];
        for (int i = 0; i < count; i++)
        {
            _code[i] = triples[3 * i];
            _length[i] = triples[(3 * i) + 1];
            _value[i] = triples[(3 * i) + 2];
        }

        _symbols = symbols.IsEmpty ? null : symbols.ToArray();
        int shift = 16 - LookupBits;
        for (int prefix = 0; prefix < _lookupLength.Length; prefix++)
        {
            int low = Find(prefix << shift);
            int high = Find((prefix << shift) | ((1 << shift) - 1));
            _lookupStart[prefix] = (byte)high;
            if (low == high && _length[high] <= LookupBits && _length[high] > 0 && TryResolve(high, prefix << shift, out int value))
            {
                _lookupLength[prefix] = (byte)_length[high];
                _lookupValue[prefix] = value;
            }
        }
    }

    // Decodes one code; false where it names a symbol the table does not hold (damaged data), with nothing consumed.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryDecode(MsbBits bits, out int value)
    {
        int code = bits.Peek16();
        int prefix = code >> (16 - LookupBits);
        int length = _lookupLength[prefix];
        if (length != 0)
        {
            bits.Skip(length);
            value = _lookupValue[prefix];
            return true;
        }

        int entry = _lookupStart[prefix];
        while (code < _code[entry])
        {
            entry++;
        }

        if (!TryResolve(entry, code, out value))
        {
            return false;
        }

        bits.Skip(_length[entry]);
        return true;
    }

    private int Find(int code)
    {
        int entry = 0;
        while (code < _code[entry])
        {
            entry++;
        }

        return entry;
    }

    private bool TryResolve(int entry, int code, out int value)
    {
        value = _value[entry];
        if (_symbols is null)
        {
            return true;
        }

        int index = (value - (code >> (16 - _length[entry]))) & 0xFF;
        if (index >= _symbols.Length)
        {
            return false;
        }

        value = _symbols[index];
        return true;
    }
}

// The codes of SV4 to SV6 (libmpcdec 1.2.6 huffsv46.c): the same search over 32-bit codes.
internal sealed class WideCode
{
    private readonly uint[] _code;
    private readonly int[] _length;
    private readonly int[] _value;

    public WideCode(ReadOnlySpan<long> triples)
    {
        int count = triples.Length / 3;
        _code = new uint[count];
        _length = new int[count];
        _value = new int[count];
        for (int i = 0; i < count; i++)
        {
            _code[i] = (uint)triples[3 * i];
            _length[i] = (int)triples[(3 * i) + 1];
            _value[i] = (int)triples[(3 * i) + 2];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Decode(MsbBits bits)
    {
        uint code = bits.Peek32();
        int entry = 0;
        while (code < _code[entry])
        {
            entry++;
        }

        bits.Skip(_length[entry]);
        return _value[entry];
    }
}
