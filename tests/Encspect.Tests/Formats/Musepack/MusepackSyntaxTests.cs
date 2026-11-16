using Encspect.Formats.Musepack;

namespace Encspect.Tests;

// The Huffman tables of the entropy pass (scripts/fixtures/musepack_tables.py writes them from libmpcdec's sources): each is searched as libmpcdec's decoder searches it, and the
// lookup on the first 6 bits that answers the short codes changes no result.
public class MusepackSyntaxTests
{
    public static TheoryData<string> Canonical => new()
    {
        "Sv7Header", "Sv7Scfi", "Sv7Dscf", "Sv7Q1A", "Sv7Q1B", "Sv7Q2A", "Sv7Q2B", "Sv7Q3A", "Sv7Q3B", "Sv7Q4A", "Sv7Q4B", "Sv7Q5A", "Sv7Q5B", "Sv7Q6A", "Sv7Q6B", "Sv7Q7A", "Sv7Q7B",
        "Sv8Scfi1", "Sv8Scfi2", "Sv8Dscf1", "Sv8Dscf2", "Sv8Bands", "Sv8Res1", "Sv8Res2", "Sv8Q1", "Sv8Q2A", "Sv8Q2B", "Sv8Q3", "Sv8Q4", "Sv8Q5A", "Sv8Q5B", "Sv8Q6A", "Sv8Q6B",
        "Sv8Q7A", "Sv8Q7B", "Sv8Q8A", "Sv8Q8B", "Sv8Q9Up",
    };

    [Theory]
    [MemberData(nameof(Canonical))]
    public void The_lookup_gives_what_the_search_of_libmpcdec_gives(string name)
    {
        (int[] triples, sbyte[] symbols) = Read(name);
        var code = new CanonicalCode(triples, symbols);
        int entries = triples.Length / 3;
        Assert.Equal(0, triples[(3 * (entries - 1))]);
        var bits = new MsbBits();
        var data = new byte[16];
        for (int value = 0; value < 65536; value++)
        {
            data[0] = (byte)(value >> 8);
            data[1] = (byte)value;
            bits.Reset(data, 0, 128);
            int entry = 0;
            while (value < triples[3 * entry])
            {
                entry++;
            }

            int length = triples[(3 * entry) + 1];
            int expected = triples[(3 * entry) + 2];
            bool isValid = true;
            if (symbols.Length > 0)
            {
                int index = (expected - (value >> (16 - length))) & 0xFF;
                isValid = index < symbols.Length;
                expected = isValid ? symbols[index] : 0;
            }

            bool decoded = code.TryDecode(bits, out int actual);
            Assert.Equal(isValid, decoded);
            if (isValid)
            {
                Assert.Equal(expected, actual);
                Assert.Equal(length, bits.Position);
            }
        }
    }

    private static (int[], sbyte[]) Read(string name) => name switch
    {
        "Sv7Header" => (MusepackTables.Sv7Header.ToArray(), []),
        "Sv7Scfi" => (MusepackTables.Sv7Scfi.ToArray(), []),
        "Sv7Dscf" => (MusepackTables.Sv7Dscf.ToArray(), []),
        "Sv7Q1A" => (MusepackTables.Sv7Q1A.ToArray(), []),
        "Sv7Q1B" => (MusepackTables.Sv7Q1B.ToArray(), []),
        "Sv7Q2A" => (MusepackTables.Sv7Q2A.ToArray(), []),
        "Sv7Q2B" => (MusepackTables.Sv7Q2B.ToArray(), []),
        "Sv7Q3A" => (MusepackTables.Sv7Q3A.ToArray(), []),
        "Sv7Q3B" => (MusepackTables.Sv7Q3B.ToArray(), []),
        "Sv7Q4A" => (MusepackTables.Sv7Q4A.ToArray(), []),
        "Sv7Q4B" => (MusepackTables.Sv7Q4B.ToArray(), []),
        "Sv7Q5A" => (MusepackTables.Sv7Q5A.ToArray(), []),
        "Sv7Q5B" => (MusepackTables.Sv7Q5B.ToArray(), []),
        "Sv7Q6A" => (MusepackTables.Sv7Q6A.ToArray(), []),
        "Sv7Q6B" => (MusepackTables.Sv7Q6B.ToArray(), []),
        "Sv7Q7A" => (MusepackTables.Sv7Q7A.ToArray(), []),
        "Sv7Q7B" => (MusepackTables.Sv7Q7B.ToArray(), []),
        "Sv8Scfi1" => (MusepackTables.Sv8Scfi1.ToArray(), MusepackTables.Sv8Scfi1Symbols.ToArray()),
        "Sv8Scfi2" => (MusepackTables.Sv8Scfi2.ToArray(), MusepackTables.Sv8Scfi2Symbols.ToArray()),
        "Sv8Dscf1" => (MusepackTables.Sv8Dscf1.ToArray(), MusepackTables.Sv8Dscf1Symbols.ToArray()),
        "Sv8Dscf2" => (MusepackTables.Sv8Dscf2.ToArray(), MusepackTables.Sv8Dscf2Symbols.ToArray()),
        "Sv8Bands" => (MusepackTables.Sv8Bands.ToArray(), MusepackTables.Sv8BandsSymbols.ToArray()),
        "Sv8Res1" => (MusepackTables.Sv8Res1.ToArray(), MusepackTables.Sv8Res1Symbols.ToArray()),
        "Sv8Res2" => (MusepackTables.Sv8Res2.ToArray(), MusepackTables.Sv8Res2Symbols.ToArray()),
        "Sv8Q1" => (MusepackTables.Sv8Q1.ToArray(), MusepackTables.Sv8Q1Symbols.ToArray()),
        "Sv8Q2A" => (MusepackTables.Sv8Q2A.ToArray(), MusepackTables.Sv8Q2ASymbols.ToArray()),
        "Sv8Q2B" => (MusepackTables.Sv8Q2B.ToArray(), MusepackTables.Sv8Q2BSymbols.ToArray()),
        "Sv8Q3" => (MusepackTables.Sv8Q3.ToArray(), MusepackTables.Sv8Q3Symbols.ToArray()),
        "Sv8Q4" => (MusepackTables.Sv8Q4.ToArray(), MusepackTables.Sv8Q4Symbols.ToArray()),
        "Sv8Q5A" => (MusepackTables.Sv8Q5A.ToArray(), MusepackTables.Sv8Q5ASymbols.ToArray()),
        "Sv8Q5B" => (MusepackTables.Sv8Q5B.ToArray(), MusepackTables.Sv8Q5BSymbols.ToArray()),
        "Sv8Q6A" => (MusepackTables.Sv8Q6A.ToArray(), MusepackTables.Sv8Q6ASymbols.ToArray()),
        "Sv8Q6B" => (MusepackTables.Sv8Q6B.ToArray(), MusepackTables.Sv8Q6BSymbols.ToArray()),
        "Sv8Q7A" => (MusepackTables.Sv8Q7A.ToArray(), MusepackTables.Sv8Q7ASymbols.ToArray()),
        "Sv8Q7B" => (MusepackTables.Sv8Q7B.ToArray(), MusepackTables.Sv8Q7BSymbols.ToArray()),
        "Sv8Q8A" => (MusepackTables.Sv8Q8A.ToArray(), MusepackTables.Sv8Q8ASymbols.ToArray()),
        "Sv8Q8B" => (MusepackTables.Sv8Q8B.ToArray(), MusepackTables.Sv8Q8BSymbols.ToArray()),
        _ => (MusepackTables.Sv8Q9Up.ToArray(), MusepackTables.Sv8Q9UpSymbols.ToArray()),
    };
}
