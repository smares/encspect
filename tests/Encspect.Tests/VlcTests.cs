using Encspect.Formats.Wma;

namespace Encspect.Tests;

// Every code of every variable-length code table the WMA parsers read, read back as the symbol the table gives it
// and with the length its code has, whatever bits follow. The high-band gain table has symbols below zero.
public class VlcTests
{
    public static TheoryData<string> Names
    {
        get
        {
            var names = new TheoryData<string> { "exponent", "high-band-gain" };
            for (int i = 0; i < 6; i++)
            {
                names.Add($"coefficient-{i}");
            }

            foreach (string name in new[] { "pro-scale", "pro-scale-run-level", "pro-vector-4", "pro-vector-2", "pro-vector-1", "pro-coefficient-0", "pro-coefficient-1" })
            {
                names.Add(name);
            }

            return names;
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void EveryCodeReadsAsItsSymbol(string name)
    {
        (uint[] codes, byte[] lengths, int[] symbols) = Table(name);
        var vlc = new Vlc(codes, lengths, symbols);
        int checkedCodes = 0;
        foreach (bool trailingOnes in new[] { false, true })
        {
            for (int i = 0; i < lengths.Length; i++)
            {
                if (lengths[i] == 0)
                {
                    continue;
                }

                var bits = new Bits(Pack(codes[i], lengths[i], trailingOnes));
                int symbol = vlc.Read(bits);
                Assert.True(symbol == symbols[i], $"{name}: code {i} ({Convert.ToString(codes[i], 2).PadLeft(lengths[i], '0')}) read as {symbol}, not {symbols[i]}");
                Assert.True(bits.Position == lengths[i], $"{name}: code {i} read {bits.Position} bits, not {lengths[i]}");
                checkedCodes++;
            }
        }

        Assert.True(checkedCodes > 0);
    }

    [Fact]
    public void NegativeSymbolsOfTheHighBandGainTableAreRead()
    {
        (uint[] codes, byte[] lengths, int[] symbols) = Table("high-band-gain");
        Assert.Contains(symbols, s => s < 0);
        var vlc = new Vlc(codes, lengths, symbols);
        // 00000011 is -11 in 8 bits, which a lookup of (symbol << 5) | length read as a long code, and 110 is -5 in 3 bits.
        var first = new Bits(Pack(0b00000011, 8, false));
        Assert.Equal(-11, vlc.Read(first));
        Assert.Equal(8, first.Position);
        var second = new Bits(Pack(0b110, 3, false));
        Assert.Equal(-5, vlc.Read(second));
        Assert.Equal(3, second.Position);
    }

    private static byte[] Pack(uint code, int length, bool trailingOnes)
    {
        var data = new byte[12];
        if (trailingOnes)
        {
            Array.Fill(data, (byte)0xFF);
        }

        for (int i = 0; i < length; i++)
        {
            bool bit = ((code >> (length - 1 - i)) & 1) != 0;
            if (bit)
            {
                data[i / 8] |= (byte)(0x80 >> (i % 8));
            }
            else
            {
                data[i / 8] &= (byte)~(0x80 >> (i % 8));
            }
        }

        return data;
    }

    // The codes of a table given by lengths: the running total of 2^-length, left-aligned in 32 bits
    // (FFmpeg's ff_vlc_init_from_lengths).
    private static uint[] FromLengths(byte[] lengths)
    {
        var codes = new uint[lengths.Length];
        ulong code = 0;
        for (int i = 0; i < lengths.Length; i++)
        {
            if (lengths[i] == 0)
            {
                continue;
            }

            codes[i] = (uint)(code >> (32 - lengths[i]));
            code += 1UL << (32 - lengths[i]);
        }

        return codes;
    }

    private static (uint[] Codes, byte[] Lengths, int[] Symbols) Table(string name)
    {
        static int[] Identity(int count) => [.. Enumerable.Range(0, count)];
        static (uint[], byte[], int[]) Derived(byte[] lengths, IEnumerable<int> symbols) => (FromLengths(lengths), lengths, [.. symbols]);
        if (name.StartsWith("coefficient-", StringComparison.Ordinal))
        {
            int index = int.Parse(name["coefficient-".Length..], System.Globalization.CultureInfo.InvariantCulture);
            return (Tables.CoefficientCodes[index], Tables.CoefficientLengths[index], Identity(Tables.CoefficientLengths[index].Length));
        }

        return name switch
        {
            "exponent" => (Tables.ExponentCodes, Tables.ExponentLengths, Identity(Tables.ExponentLengths.Length)),
            "high-band-gain" => Derived(Tables.HighBandGainLengths, Tables.HighBandGainSymbols.Select(s => s - 18)),
            "pro-scale" => Derived(ProTables.ScaleLengths, ProTables.ScaleSymbols.Select(s => (int)s)),
            "pro-scale-run-level" => Derived(ProTables.ScaleRlLengths, ProTables.ScaleRlSymbols.Select(s => (int)s)),
            "pro-vector-4" => Derived(ProTables.Vector4Lengths, ProTables.Vector4Symbols.Select(s => (int)s)),
            "pro-vector-2" => Derived(ProTables.Vector2Lengths, ProTables.Vector2Symbols.Select(s => (int)s)),
            "pro-vector-1" => Derived(ProTables.Vector1Lengths, ProTables.Vector1Symbols.Select(s => (int)s)),
            "pro-coefficient-0" => Derived(ProTables.Coefficient0Lengths, ProTables.Coefficient0Symbols.Select(s => (int)s)),
            "pro-coefficient-1" => Derived(ProTables.Coefficient1Lengths, ProTables.Coefficient1Symbols.Select(s => (int)s)),
            _ => throw new ArgumentException(name),
        };
    }
}
