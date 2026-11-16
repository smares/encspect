using Encspect.Core;
using Encspect.Formats.Dts;
using Encspect.Host;

namespace Encspect.Tests;

// The entropy pass over the subframes of the core (ETSI TS 102 114 5.5, 5.6): the books of Annex D.5 as decoders, and what the walk does with damaged input. What it reads of
// the coded fixtures is asserted by their manifests (DtsFixtureTests), which a second writer (dts_coded.py) produced.
public class DtsSubframeTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "dts", name));

    // Every book is a complete prefix code over its symbols, and the decoder returns each symbol with its length from the bits of its code, whatever follows them.
    [Fact]
    public void EveryBookDecodesEverySymbolOfItsCodeWord()
    {
        foreach (string name in new[] { "A3", "A4", "D4", "A12", "E12", "A17", "G25", "D33", "D65", "SA129", "SE129", "D129" })
        {
            HuffmanBook book = HuffmanBook.Get(name);
            ReadOnlySpan<int> entries = Entries(name);
            double kraft = 0;
            for (int i = 0; i < entries.Length; i += 3)
            {
                int symbol = entries[i];
                int length = entries[i + 1];
                int code = entries[i + 2];
                kraft += Math.Pow(2, -length);
                foreach (uint tail in new uint[] { 0, uint.MaxValue, 0x5A5A5A5A })
                {
                    uint window = ((uint)code << (32 - length)) | (tail >> length);
                    Assert.Equal(symbol, book.Decode(window, out int taken));
                    Assert.Equal(length, taken);
                }
            }

            Assert.Equal(1.0, kraft);
        }
    }

    private static ReadOnlySpan<int> Entries(string name) => name switch
    {
        "A3" => SubframeTables.BookA3,
        "A4" => SubframeTables.BookA4,
        "D4" => SubframeTables.BookD4,
        "A12" => SubframeTables.BookA12,
        "E12" => SubframeTables.BookE12,
        "A17" => SubframeTables.BookA17,
        "G25" => SubframeTables.BookG25,
        "D33" => SubframeTables.BookD33,
        "D65" => SubframeTables.BookD65,
        "SA129" => SubframeTables.BookSA129,
        "SE129" => SubframeTables.BookSE129,
        "D129" => SubframeTables.BookD129,
        _ => ReadOnlySpan<int>.Empty,
    };

    // The books the test reads entries of are a sample of the 62; the prefix and completeness of every book is dts_tables.py's check.
    [Fact]
    public void TheBooksHaveTheEntriesOfTheirLevels()
    {
        Assert.Equal(3, SubframeTables.BookA3.Length / 3);
        Assert.Equal(12, SubframeTables.BookE12.Length / 3);
        Assert.Equal(65, SubframeTables.BookD65.Length / 3);
        Assert.Equal(129, SubframeTables.BookD129.Length / 3);
    }

    // The scale factor tables end with their invalid entries, and the step sizes of the lossy and the lossless streams differ where the specification's do.
    [Fact]
    public void TheScaleFactorAndStepSizeTablesHaveTheirInvalidEntries()
    {
        Assert.Equal(64, SubframeTables.ScaleFactor6.Length);
        Assert.Equal(-1, SubframeTables.ScaleFactor6[63]);
        Assert.Equal(8317638, SubframeTables.ScaleFactor6[62]);
        Assert.Equal(128, SubframeTables.ScaleFactor7.Length);
        Assert.Equal(-1, SubframeTables.ScaleFactor7[125]);
        Assert.Equal(8317638, SubframeTables.ScaleFactor7[124]);
        Assert.Equal(32, SubframeTables.StepSizeLossy.Length);
        Assert.Equal(6710886, SubframeTables.StepSizeLossy[1]);
        Assert.Equal(4194304, SubframeTables.StepSizeLossless[1]);
        Assert.Equal(129, SubframeTables.JointScale.Length);
        Assert.Equal(1.0, SubframeTables.JointScale[64]);
    }

    // A coded frame cut at every length, and with each byte of the first subframe flipped, gives a report and never an unhandled failure (parse.failed).
    [Theory]
    [InlineData("subframes-huffman.dts")]
    [InlineData("subframes-five-one.dts")]
    public void DamagedSubframesGiveFindingsAndNeverAFailure(string name)
    {
        byte[] data = Fixture(name);
        var analyzer = Analyzer.CreateDefault();
        int frame = (((data[5] & 3) << 12) | (data[6] << 4) | (data[7] >> 4)) + 1;
        for (int length = 0; length <= Math.Min(data.Length, frame + 64); length += length < 400 ? 3 : 1)
        {
            Report report = analyzer.Analyze(name, new MemoryStream(data[..length]));
            Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        }

        for (int index = 24; index < Math.Min(frame, 600); index++)
        {
            byte[] copy = [.. data];
            copy[index] ^= 0x55;
            Report report = analyzer.Analyze(name, new MemoryStream(copy));
            Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        }
    }

    // The coded fixtures walk to the end: every subframe ends with its synchronization word, so none of the walk's findings is raised.
    [Theory]
    [InlineData("subframes-huffman.dts")]
    [InlineData("subframes-linear.dts")]
    [InlineData("subframes-five-one.dts")]
    [InlineData("subframes-14le.dts")]
    public void CodedFixturesAreReadToTheEnd(string name)
    {
        Report report = Analyzer.CreateDefault().Analyze(name, new MemoryStream(Fixture(name)));
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("dts.subframe-", StringComparison.Ordinal));
        Assert.Contains(report.Streams[0].Series, s => s.Name == "Coded Bandwidth");
        Assert.Contains(report.Streams[0].CategorySeries, s => s.Name == "Subframes With Block Switching");
    }
}
