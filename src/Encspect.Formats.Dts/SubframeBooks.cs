namespace Encspect.Formats.Dts;

// The Huffman code books of Annex D.5 as decoders: a table indexed by the next bits of the stream, as long as the book's longest code, that holds the symbol and the code's length.
// Built on first use, since a stream uses a few of the books.
internal sealed class HuffmanBook
{
    private readonly ushort[] _table;
    private readonly int[] _symbols;

    private HuffmanBook(ReadOnlySpan<int> entries)
    {
        int count = entries.Length / 3;
        int longest = 0;
        for (int i = 0; i < count; i++)
        {
            longest = Math.Max(longest, entries[(i * 3) + 1]);
        }

        Longest = longest;
        _symbols = new int[count];
        _table = new ushort[1 << longest];
        for (int i = 0; i < count; i++)
        {
            int symbol = entries[i * 3];
            int length = entries[(i * 3) + 1];
            int code = entries[(i * 3) + 2];
            _symbols[i] = symbol;
            int first = code << (longest - length);
            // The code space of a complete prefix code is covered exactly once (dts_tables.py checks the Kraft sum), so every slot is set.
            for (int slot = 0; slot < 1 << (longest - length); slot++)
            {
                _table[first + slot] = (ushort)((i << 5) | length);
            }
        }
    }

    public int Longest { get; }

    // The symbol the next Longest bits start with and the bits the code takes.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public int Decode(uint window, out int length)
    {
        ushort entry = _table[window >> (32 - Longest)];
        length = entry & 31;
        return _symbols[entry >> 5];
    }

    public static readonly string[] Names =
    [
        "A3", "A4", "B4", "C4", "D4", "A5", "B5", "C5", "A7", "B7", "C7", "A9", "B9", "C9", "A12", "B12", "C12", "D12", "E12", "A13", "B13", "C13",
        "A17", "B17", "C17", "D17", "E17", "F17", "G17", "A25", "B25", "C25", "D25", "E25", "F25", "G25",
        "A33", "B33", "C33", "D33", "E33", "F33", "G33", "A65", "B65", "C65", "D65", "E65", "F65", "G65",
        "SA129", "SB129", "SC129", "SD129", "SE129", "A129", "B129", "C129", "D129", "E129", "F129", "G129",
    ];

    // The books by name, as Annex D.5 names them.
    private static readonly Lazy<HuffmanBook>[] Books = Make();

    private static Lazy<HuffmanBook>[] Make()
    {
        var books = new Lazy<HuffmanBook>[Names.Length];
        for (int i = 0; i < books.Length; i++)
        {
            int index = i;
            books[i] = new Lazy<HuffmanBook>(() => new HuffmanBook(Data(index)), true);
        }

        return books;
    }

    public static HuffmanBook Get(string name) => Books[Array.IndexOf(Names, name)].Value;

    private static ReadOnlySpan<int> Data(int index) => Names[index] switch
    {
        "A3" => SubframeTables.BookA3,
        "A4" => SubframeTables.BookA4,
        "B4" => SubframeTables.BookB4,
        "C4" => SubframeTables.BookC4,
        "D4" => SubframeTables.BookD4,
        "A5" => SubframeTables.BookA5,
        "B5" => SubframeTables.BookB5,
        "C5" => SubframeTables.BookC5,
        "A7" => SubframeTables.BookA7,
        "B7" => SubframeTables.BookB7,
        "C7" => SubframeTables.BookC7,
        "A9" => SubframeTables.BookA9,
        "B9" => SubframeTables.BookB9,
        "C9" => SubframeTables.BookC9,
        "A12" => SubframeTables.BookA12,
        "B12" => SubframeTables.BookB12,
        "C12" => SubframeTables.BookC12,
        "D12" => SubframeTables.BookD12,
        "E12" => SubframeTables.BookE12,
        "A13" => SubframeTables.BookA13,
        "B13" => SubframeTables.BookB13,
        "C13" => SubframeTables.BookC13,
        "A17" => SubframeTables.BookA17,
        "B17" => SubframeTables.BookB17,
        "C17" => SubframeTables.BookC17,
        "D17" => SubframeTables.BookD17,
        "E17" => SubframeTables.BookE17,
        "F17" => SubframeTables.BookF17,
        "G17" => SubframeTables.BookG17,
        "A25" => SubframeTables.BookA25,
        "B25" => SubframeTables.BookB25,
        "C25" => SubframeTables.BookC25,
        "D25" => SubframeTables.BookD25,
        "E25" => SubframeTables.BookE25,
        "F25" => SubframeTables.BookF25,
        "G25" => SubframeTables.BookG25,
        "A33" => SubframeTables.BookA33,
        "B33" => SubframeTables.BookB33,
        "C33" => SubframeTables.BookC33,
        "D33" => SubframeTables.BookD33,
        "E33" => SubframeTables.BookE33,
        "F33" => SubframeTables.BookF33,
        "G33" => SubframeTables.BookG33,
        "A65" => SubframeTables.BookA65,
        "B65" => SubframeTables.BookB65,
        "C65" => SubframeTables.BookC65,
        "D65" => SubframeTables.BookD65,
        "E65" => SubframeTables.BookE65,
        "F65" => SubframeTables.BookF65,
        "G65" => SubframeTables.BookG65,
        "SA129" => SubframeTables.BookSA129,
        "SB129" => SubframeTables.BookSB129,
        "SC129" => SubframeTables.BookSC129,
        "SD129" => SubframeTables.BookSD129,
        "SE129" => SubframeTables.BookSE129,
        "A129" => SubframeTables.BookA129,
        "B129" => SubframeTables.BookB129,
        "C129" => SubframeTables.BookC129,
        "D129" => SubframeTables.BookD129,
        "E129" => SubframeTables.BookE129,
        "F129" => SubframeTables.BookF129,
        "G129" => SubframeTables.BookG129,
        _ => throw new InvalidOperationException(),
    };
}
