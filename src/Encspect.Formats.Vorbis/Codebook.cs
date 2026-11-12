using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Vorbis;

// A codebook of the setup header (Vorbis I specification, section 3): the Huffman code that gives an entry number, and the value lookup that turns an
// entry into a vector. The code follows libvorbis 1.3.7 (lib/sharedbook.c and lib/codebook.c, BSD-3-Clause, Copyright (c) 2002-2020 Xiph.Org Foundation;
// its notice is in THIRD-PARTY-NOTICES.md): how codewords are assigned (_make_words), what libvorbis refuses (an overpopulated or an incomplete code, but a
// single entry of length 1), and what a decode does at the end of a packet (decode_packed_entry_number). The vectors the lookup gives are built by the
// decoder (Decoding/CodebookVectors), so this class holds no floating-point arithmetic but the unpacking of the two header values.
internal sealed class Codebook
{
    // Codewords of at most this many bits are found by one table look-up.
    private const int FastBits = 10;

    private uint[] _codes = [];
    private byte[] _lengths = [];
    private int[] _entries = [];
    private uint[] _fast = [];
    private int _fastBits;
    private int _maxLength;

    public int Dimensions { get; private set; }

    public int EntryCount { get; private set; }

    // The entries that have a codeword.
    public int UsedEntries { get; private set; }

    // 0: no value lookup; 1: implicit (lookup1_values per dimension); 2: explicit.
    public int LookupType { get; private set; }

    // The two floats of the header as float32_unpack gives them (exponent clamped to 2^-63 to 2^63, as libvorbis does).
    public float Minimum { get; private set; }

    public float Delta { get; private set; }

    public bool IsSequence { get; private set; }

    public int[] Multiplicands { get; private set; } = [];

    // The longest codeword, in bits.
    public int MaxLength => _maxLength;

    // The codeword length of each entry, 0 for an entry without one.
    public byte[] Lengths { get; private set; } = [];

    public static int ILog(long value) => value <= 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)value);

    // Reads one codebook (section 3.2.1) at the bits' position, or returns null where libvorbis refuses it: the sync pattern, a book of more than 24 bits
    // of ilog(dimensions) + ilog(entries), codeword lengths that are not a complete prefix code, a lookup type other than 0 to 2, or the end of the packet.
    public static Codebook? Read(ref PacketBits bits, long entryBudget, CancellationToken cancellationToken, out bool exceedsBudget)
    {
        exceedsBudget = false;
        if (bits.Read(24) != 0x564342)
        {
            return null;
        }

        long dimensions = bits.Read(16);
        long entries = bits.Read(24);
        if (dimensions < 0 || entries < 0 || ILog(dimensions) + ILog(entries) > 24)
        {
            return null;
        }

        if (entries > entryBudget)
        {
            exceedsBudget = true;
            return null;
        }

        var lengths = new byte[entries];
        long ordered = bits.Read(1);
        if (ordered < 0)
        {
            return null;
        }

        if (ordered == 0)
        {
            long sparse = bits.Read(1);
            if (sparse < 0)
            {
                return null;
            }

            for (long i = 0; i < entries; i++)
            {
                if (i % 65536 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (sparse == 1)
                {
                    long flag = bits.Read(1);
                    if (flag < 0)
                    {
                        return null;
                    }

                    if (flag == 0)
                    {
                        continue;
                    }
                }

                long length = bits.Read(5);
                if (length < 0)
                {
                    return null;
                }

                lengths[i] = (byte)(length + 1);
            }
        }
        else
        {
            long length = bits.Read(5) + 1;
            if (length < 1)
            {
                return null;
            }

            long current = 0;
            while (current < entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long number = bits.Read(ILog(entries - current));
                if (number < 0 || length > 32 || number > entries - current || (number > 0 && ((number - 1) >> (int)(length - 1)) > 1))
                {
                    return null;
                }

                for (long i = 0; i < number; i++)
                {
                    lengths[current + i] = (byte)length;
                }

                current += number;
                length++;
            }
        }

        var book = new Codebook { Dimensions = (int)dimensions, EntryCount = (int)entries };
        long lookup = bits.Read(4);
        if (lookup is < 0 or > 2)
        {
            return null;
        }

        book.LookupType = (int)lookup;
        if (lookup > 0)
        {
            long minimum = bits.Read(32);
            long delta = bits.Read(32);
            long valueBits = bits.Read(4) + 1;
            long sequence = bits.Read(1);
            if (minimum < 0 || delta < 0 || valueBits < 1 || sequence < 0)
            {
                return null;
            }

            book.Minimum = Float32Unpack(minimum);
            book.Delta = Float32Unpack(delta);
            book.IsSequence = sequence == 1;
            long values = lookup == 1 ? Lookup1Values(entries, (int)dimensions) : entries * dimensions;
            if (values > bits.Remaining / valueBits)
            {
                return null;
            }

            var multiplicands = new int[values];
            for (long i = 0; i < values; i++)
            {
                multiplicands[i] = (int)bits.Read((int)valueBits);
            }

            book.Multiplicands = multiplicands;
        }

        return book.Validate(lengths) ? book : null;
    }

    // lookup1_values (section 9.2.3): the largest r with r^dimensions not above entries.
    public static long Lookup1Values(long entries, int dimensions) => SetupDecoder.Lookup1Values(entries, dimensions);

    // float32_unpack (section 9.2.2) as libvorbis does it: the exponent is clamped to the range of 2^-63 to 2^63.
    private static float Float32Unpack(long value)
    {
        double mantissa = value & 0x1FFFFF;
        if ((value & 0x80000000L) != 0)
        {
            mantissa = -mantissa;
        }

        int exponent = (int)((value & 0x7FE00000L) >> 21) - 20 - 768;
        exponent = Math.Clamp(exponent, -63, 63);
        return (float)(mantissa * DetMath.PowerOfTwo(exponent));
    }

    // _make_words and vorbis_book_init_decode: assigns the codewords, refuses an overpopulated or incomplete code and builds the decoding tables.
    // The codewords the lengths give, kept for BuildTables (the sorted arrays and the fast table are built when a packet first decodes with the book).
    private uint[]? _words;

    private bool Validate(byte[] lengths)
    {
        int used = 0;
        foreach (byte length in lengths)
        {
            if (length > 0)
            {
                used++;
            }
        }

        UsedEntries = used;
        Lengths = lengths;
        if (used == 0)
        {
            return true;
        }

        uint[]? words = MakeWords(lengths, used);
        if (words is null)
        {
            return false;
        }

        _words = words;
        return true;
    }

    private void BuildTables()
    {
        byte[] lengths = Lengths;
        uint[] words = _words!;
        int used = UsedEntries;
        _words = null;
        // The codewords as the packet holds them, least significant bit first, and left justified by reversing them over 32 bits for the sort.
        var left = new uint[used];
        for (int i = 0; i < used; i++)
        {
            left[i] = BitReverse(words[i]);
        }

        var order = new int[used];
        for (int i = 0; i < used; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => left[a].CompareTo(left[b]));
        _codes = new uint[used];
        _lengths = new byte[used];
        _entries = new int[used];
        var byUsed = new int[used];
        for (int e = 0, n = 0; e < lengths.Length; e++)
        {
            if (lengths[e] > 0)
            {
                byUsed[n++] = e;
            }
        }

        for (int sorted = 0; sorted < used; sorted++)
        {
            int source = order[sorted];
            _codes[sorted] = left[source];
            _entries[sorted] = byUsed[source];
            _lengths[sorted] = lengths[byUsed[source]];
            _maxLength = Math.Max(_maxLength, _lengths[sorted]);
        }

        if (used == 1 && _maxLength == 1)
        {
            // A single entry of length 1 decodes from either bit value (libvorbis's one-bit table).
            _fastBits = 1;
            _fast = [(1u << 24) | (uint)_entries[0], (1u << 24) | (uint)_entries[0]];
            return;
        }

        _fastBits = Math.Min(FastBits, _maxLength);
        _fast = new uint[1 << _fastBits];
        for (int i = 0; i < used; i++)
        {
            int length = _lengths[i];
            if (length > _fastBits)
            {
                continue;
            }

            uint original = BitReverse(_codes[i]);
            uint packed = ((uint)length << 24) | (uint)_entries[i];
            for (int j = 0; j < 1 << (_fastBits - length); j++)
            {
                _fast[original | ((uint)j << length)] = packed;
            }
        }

        return;
    }

    private static uint[]? MakeWords(byte[] lengths, int count)
    {
        var marker = new uint[33];
        var result = new uint[count];
        int n = 0;
        foreach (byte length in lengths)
        {
            if (length == 0)
            {
                continue;
            }

            uint entry = marker[length];
            if (length < 32 && (entry >> length) != 0)
            {
                return null;
            }

            result[n++] = entry;
            for (int j = length; j > 0; j--)
            {
                if ((marker[j] & 1) != 0)
                {
                    if (j == 1)
                    {
                        marker[1]++;
                    }
                    else
                    {
                        marker[j] = marker[j - 1] << 1;
                    }

                    break;
                }

                marker[j]++;
            }

            for (int j = length + 1; j < 33; j++)
            {
                if ((marker[j] >> 1) == entry)
                {
                    entry = marker[j];
                    marker[j] = marker[j - 1] << 1;
                }
                else
                {
                    break;
                }
            }
        }

        if (!(count == 1 && marker[2] == 2))
        {
            for (int i = 1; i < 33; i++)
            {
                if ((marker[i] & (0xFFFFFFFFu >> (32 - i))) != 0)
                {
                    return null;
                }
            }
        }

        // The packet holds the first bit of a codeword in its least significant bit: reverse each word over its length.
        int index = 0;
        foreach (byte length in lengths)
        {
            if (length == 0)
            {
                continue;
            }

            uint word = result[index];
            uint reversed = 0;
            for (int j = 0; j < length; j++)
            {
                reversed = (reversed << 1) | ((word >> j) & 1);
            }

            result[index++] = reversed;
        }

        return result;
    }

    private static uint BitReverse(uint x)
    {
        x = ((x >> 16) & 0x0000FFFFu) | ((x << 16) & 0xFFFF0000u);
        x = ((x >> 8) & 0x00FF00FFu) | ((x << 8) & 0xFF00FF00u);
        x = ((x >> 4) & 0x0F0F0F0Fu) | ((x << 4) & 0xF0F0F0F0u);
        x = ((x >> 2) & 0x33333333u) | ((x << 2) & 0xCCCCCCCCu);
        return ((x >> 1) & 0x55555555u) | ((x << 1) & 0xAAAAAAAAu);
    }

    // The entry number of the next codeword, or -1 where no whole codeword remains in the packet (which ends it, as libvorbis does).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Decode(ref PacketBits bits)
    {
        if (UsedEntries == 0)
        {
            return -1;
        }

        if (_fast.Length == 0)
        {
            BuildTables();
        }

        long remaining = bits.Remaining;
        int read = _maxLength;
        if (remaining >= _fastBits)
        {
            uint packed = _fast[bits.Peek(_fastBits)];
            if (packed != 0)
            {
                bits.Advance((int)(packed >> 24));
                return (int)(packed & 0xFFFFFF);
            }
        }

        if (remaining < read)
        {
            read = (int)remaining;
        }

        if (read < 1)
        {
            return -1;
        }

        uint testWord = BitReverse(bits.Peek(read));
        int lo = 0;
        int hi = UsedEntries;
        while (hi - lo > 1)
        {
            int p = (hi - lo) >> 1;
            if (_codes[lo + p] > testWord)
            {
                hi -= p;
            }
            else
            {
                lo += p;
            }
        }

        if (_lengths[lo] <= read)
        {
            bits.Advance(_lengths[lo]);
            return _entries[lo];
        }

        bits.Advance(read);
        return -1;
    }
}
