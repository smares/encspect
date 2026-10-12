namespace Encspect.Formats.Aac;

// A Huffman codebook as a lookup table: the first RootBits bits index the root, whose entry is
// either a codeword (its symbol and length) or a subtable for the codewords that share those bits.
// Built once from the codewords and lengths of HuffmanData.
internal sealed class Huffman
{
    private const int RootBits = 9;
    private const int SubtableFlag = int.MinValue;

    // A leaf: symbol << 8 | length counted from the table's own first bit. A subtable entry:
    // SubtableFlag | offset << 8 | width.
    private readonly int[] _table;

    public Huffman(IReadOnlyList<uint> codes, IReadOnlyList<byte> lengths)
    {
        MaxLength = lengths.Max();
        var entries = new List<int>();
        Build(entries, Enumerable.Range(0, codes.Count).ToList(), codes, lengths, 0, Math.Min(RootBits, MaxLength));
        _table = [.. entries];
    }

    public int MaxLength { get; }

    // The bits the root is indexed by.
    public int RootWidth => Math.Min(RootBits, MaxLength);

    // The next symbol: its index in the codebook's table. Reads past the limit throw BlockOverrun.
    public int Decode(ref AacBits bits)
    {
        int offset = 0;
        int width = Math.Min(RootBits, MaxLength);
        while (true)
        {
            int entry = _table[offset + (int)bits.Peek(width)];
            if (entry >= 0)
            {
                bits.Skip(entry & 0xFF);
                return entry >> 8;
            }

            bits.Skip(width);
            offset = (entry >> 8) & 0x7FFFFF;
            width = entry & 0xFF;
        }
    }

    // For each value of the root's bits, where a codeword no longer than them starts: its length plus
    // the sign bits follow gives for its symbol in the low byte, and follow's escape flags (8 and
    // 16) as bits 8 and 9; -1 where the codeword is longer, for Decode to read.
    public int[] Shortcut(byte[] follow)
    {
        var table = new int[1 << RootWidth];
        for (int i = 0; i < table.Length; i++)
        {
            int entry = _table[i];
            table[i] = entry < 0 ? -1 : ((entry & 0xFF) + (follow[entry >> 8] & 7)) | ((follow[entry >> 8] & 24) << 5);
        }

        return table;
    }

    // Writes the table for the codewords in symbols, whose first consumed bits are already read, at
    // the end of entries, width bits wide.
    private static void Build(List<int> entries, List<int> symbols, IReadOnlyList<uint> codes, IReadOnlyList<byte> lengths, int consumed, int width)
    {
        int start = entries.Count;
        int size = 1 << width;
        for (int i = 0; i < size; i++)
        {
            entries.Add(0);
        }

        var longer = new Dictionary<int, List<int>>();
        foreach (int symbol in symbols)
        {
            int length = lengths[symbol] - consumed;
            uint rest = codes[symbol] & (uint)((1L << (lengths[symbol] - consumed)) - 1);
            if (length <= width)
            {
                int first = (int)(rest << (width - length));
                for (int i = 0; i < 1 << (width - length); i++)
                {
                    entries[start + first + i] = (symbol << 8) | length;
                }
            }
            else
            {
                int prefix = (int)(rest >> (length - width));
                if (!longer.TryGetValue(prefix, out List<int>? group))
                {
                    longer[prefix] = group = [];
                }

                group.Add(symbol);
            }
        }

        foreach ((int prefix, List<int> group) in longer.OrderBy(g => g.Key))
        {
            int sub = group.Max(s => lengths[s]) - consumed - width;
            int offset = entries.Count;
            entries[start + prefix] = SubtableFlag | (offset << 8) | sub;
            Build(entries, group, codes, lengths, consumed + width, sub);
        }
    }
}

// The codebooks of ISO/IEC 14496-3: spectral codebooks 1 to 11 (4.6.3.3, Annex 4.A), whose leaves
// carry what follows the codeword (sign bits and escapes), the scalefactor codebook and SBR's.
internal static class Codebooks
{
    // By codebook number; 0 is unused.
    public static readonly Huffman[] Spectral = BuildSpectral();

    public static readonly Huffman Scalefactor = new(HuffmanData.ScalefactorCodes, HuffmanData.ScalefactorBits);

    // Per spectral codebook and symbol: the sign bits after the codeword (unsigned codebooks) in the
    // low 3 bits, 8 where the pair's first value escapes and 16 where its second does (codebook 11).
    public static readonly byte[][] Follow = BuildFollow();

    // Per spectral codebook, Huffman.Shortcut of its Follow.
    public static readonly int[][] Shortcuts = [[], .. Enumerable.Range(1, 11).Select(book => Spectral[book].Shortcut(Follow[book]))];

    // Codebooks 1 to 4 code four lines per codeword, 5 to 11 two.
    public static int Dimension(int codebook) => codebook < 5 ? 4 : 2;

    public static readonly Huffman SbrEnvelope15Time = new(HuffmanData.THuffmanEnv15Codes, HuffmanData.THuffmanEnv15Bits);
    public static readonly Huffman SbrEnvelope15Frequency = new(HuffmanData.FHuffmanEnv15Codes, HuffmanData.FHuffmanEnv15Bits);
    public static readonly Huffman SbrBalance15Time = new(HuffmanData.THuffmanEnvBal15Codes, HuffmanData.THuffmanEnvBal15Bits);
    public static readonly Huffman SbrBalance15Frequency = new(HuffmanData.FHuffmanEnvBal15Codes, HuffmanData.FHuffmanEnvBal15Bits);
    public static readonly Huffman SbrEnvelope30Time = new(HuffmanData.THuffmanEnv30Codes, HuffmanData.THuffmanEnv30Bits);
    public static readonly Huffman SbrEnvelope30Frequency = new(HuffmanData.FHuffmanEnv30Codes, HuffmanData.FHuffmanEnv30Bits);
    public static readonly Huffman SbrBalance30Time = new(HuffmanData.THuffmanEnvBal30Codes, HuffmanData.THuffmanEnvBal30Bits);
    public static readonly Huffman SbrBalance30Frequency = new(HuffmanData.FHuffmanEnvBal30Codes, HuffmanData.FHuffmanEnvBal30Bits);
    public static readonly Huffman SbrNoiseTime = new(HuffmanData.THuffmanNoise30Codes, HuffmanData.THuffmanNoise30Bits);
    public static readonly Huffman SbrNoiseBalanceTime = new(HuffmanData.THuffmanNoiseBal30Codes, HuffmanData.THuffmanNoiseBal30Bits);

    private static Huffman[] BuildSpectral()
    {
        ushort[][] codes = [HuffmanData.Codes1, HuffmanData.Codes2, HuffmanData.Codes3, HuffmanData.Codes4, HuffmanData.Codes5, HuffmanData.Codes6,
            HuffmanData.Codes7, HuffmanData.Codes8, HuffmanData.Codes9, HuffmanData.Codes10, HuffmanData.Codes11];
        byte[][] bits = [HuffmanData.Bits1, HuffmanData.Bits2, HuffmanData.Bits3, HuffmanData.Bits4, HuffmanData.Bits5, HuffmanData.Bits6,
            HuffmanData.Bits7, HuffmanData.Bits8, HuffmanData.Bits9, HuffmanData.Bits10, HuffmanData.Bits11];
        var books = new Huffman[12];
        for (int i = 0; i < 11; i++)
        {
            books[i + 1] = new Huffman(codes[i].Select(c => (uint)c).ToArray(), bits[i]);
        }

        return books;
    }

    // 4.6.3.3: codebooks 1, 2, 5 and 6 are signed; 3, 4 and 7 to 11 unsigned, with a sign bit after
    // the codeword for each nonzero value; codebook 11 codes 16 as an escape.
    private static byte[][] BuildFollow()
    {
        var follow = new byte[12][];
        for (int book = 1; book <= 11; book++)
        {
            int count = book <= 4 ? 81 : book <= 6 ? 81 : book <= 8 ? 64 : book <= 10 ? 169 : 289;
            follow[book] = new byte[count];
            for (int index = 0; index < count; index++)
            {
                int[] values = book switch
                {
                    <= 4 => [(index / 27) % 3, (index / 9) % 3, (index / 3) % 3, index % 3],
                    <= 6 => [index / 9, index % 9],
                    <= 8 => [index / 8, index % 8],
                    <= 10 => [index / 13, index % 13],
                    _ => [index / 17, index % 17],
                };
                int signs = book is 1 or 2 or 5 or 6 ? 0 : values.Count(v => v != 0);
                int escapes = book == 11 ? (values[0] == 16 ? 8 : 0) | (values[1] == 16 ? 16 : 0) : 0;
                follow[book][index] = (byte)(signs | escapes);
            }
        }

        return follow;
    }
}
