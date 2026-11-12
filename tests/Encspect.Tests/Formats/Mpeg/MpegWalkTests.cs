using Encspect.Formats.Mpeg;

namespace Encspect.Tests;

// The entropy pass of Layer III (Layer3Syntax, Layer3Walker): frames written by hand in the test, so that what the walk reads is known.
public class MpegWalkTests
{
    // Bits written most significant first.
    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _bits;

        public void Write(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                if (_bits % 8 == 0)
                {
                    _bytes.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    _bytes[^1] |= (byte)(0x80 >> (_bits % 8));
                }

                _bits++;
            }
        }

        public byte[] ToArray() => [.. _bytes];
    }

    // An MPEG-1 mono Layer III frame at 44,100 Hz and 128 kbps (417 bytes) whose first granule has the given part2_3_length and big_values, the Huffman table 1 and no
    // scalefactors, and whose second granule is empty.
    internal static byte[] MonoFrame(int part23Length, int bigValues)
    {
        var bits = new BitWriter();
        bits.Write(0xFFFB, 16);
        bits.Write(0x90, 8);
        bits.Write(0xC0, 8);
        bits.Write(0, 9);
        bits.Write(0, 5);
        bits.Write(0, 4);
        for (int granule = 0; granule < 2; granule++)
        {
            bits.Write(granule == 0 ? part23Length : 0, 12);
            bits.Write(granule == 0 ? bigValues : 0, 9);
            bits.Write(100, 8);
            bits.Write(0, 4);
            bits.Write(0, 1);
            bits.Write(granule == 0 ? 1 : 0, 5);
            bits.Write(0, 10);
            bits.Write(granule == 0 ? 15 : 0, 4);
            bits.Write(0, 3);
            bits.Write(0, 1);
            bits.Write(0, 1);
            bits.Write(0, 1);
        }

        byte[] head = bits.ToArray();
        var frame = new byte[417];
        head.CopyTo(frame, 0);
        return frame;
    }

    private static MpegHeader HeaderOf(byte[] frame) => new((uint)((frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3]));

    [Fact]
    public void ABigValuesRegionOfOnesIsPairsOfZeros()
    {
        byte[] frame = MonoFrame(part23Length: 40, bigValues: 10);
        byte[] main = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        var walker = new Layer3Walker();

        WalkFacts facts = walker.Walk(HeaderOf(frame), frame, main, [], 0);

        Assert.Equal(WalkProblems.None, facts.Problems);

        // Table 1 codes the pair (0,0) as one bit, so ten pairs take ten bits; table A of the count1 region codes a quadruple of zeros as one bit too, so the thirty
        // bits left are thirty quadruples, and the first granule codes 20 + 120 lines.
        Assert.Equal(140, facts.CodedLines);
    }

    [Fact]
    public void HuffmanDataThatRunsPastPart23LengthIsAnOverrun()
    {
        byte[] frame = MonoFrame(part23Length: 4, bigValues: 10);
        byte[] main = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        var walker = new Layer3Walker();

        WalkFacts facts = walker.Walk(HeaderOf(frame), frame, main, [], 0);

        Assert.True((facts.Problems & WalkProblems.HuffmanOverrun) != 0);
        Assert.Equal(1, facts.Overruns);
    }

    [Fact]
    public void ABigValuesCountAboveTheLimitMakesTheFrameUnreadable()
    {
        byte[] frame = MonoFrame(part23Length: 40, bigValues: 289);
        var walker = new Layer3Walker();

        WalkFacts facts = walker.Walk(HeaderOf(frame), frame, new byte[64], [], 0);

        Assert.True((facts.Problems & WalkProblems.Unreadable) != 0);
    }

    [Fact]
    public void PartLengthsBeyondTheDataAreReported()
    {
        byte[] frame = MonoFrame(part23Length: 400, bigValues: 10);
        var walker = new Layer3Walker();

        WalkFacts facts = walker.Walk(HeaderOf(frame), frame, new byte[8], [], 0);

        Assert.True((facts.Problems & WalkProblems.PartsExceedData) != 0);
    }

    [Fact]
    public void TheWalkFillsTheLentBuffersAndGivesTheSameFactsWithoutThem()
    {
        byte[] frame = MonoFrame(part23Length: 40, bigValues: 10);
        byte[] main = Enumerable.Repeat((byte)0xFF, 16).ToArray();
        var lent = new int[LentLayout.PerChannel];
        Array.Fill(lent, 7);

        WalkFacts withBuffers = new Layer3Walker().Walk(HeaderOf(frame), frame, main, lent, LentLayout.PerChannel);
        WalkFacts without = new Layer3Walker().Walk(HeaderOf(frame), frame, main, [], 0);

        Assert.Equal(without.CodedLines, withBuffers.CodedLines);
        Assert.Equal(without.Problems, withBuffers.Problems);
        Assert.All(lent.Take(LentLayout.Values), v => Assert.Equal(0, v));
        Assert.Equal(0, lent[LentLayout.FlagsIndex]);
    }

    // A granule whose big-values region leaves a count1 region of 574 lines (LAME 3.57 -v wrote one: big_values 1 and 144 quadruples) has a last quadruple of which only
    // two lines lie in the 576. libmad, mpg123 and FFmpeg decode no quadruple that does not fit, so lines 574 and 575 stay 0; where the quadruples fit, the last one is decoded.
    [Theory]
    [InlineData(1, 574, 0)]
    [InlineData(2, 576, 1)]
    public void ACount1QuadrupleThatDoesNotFitIn576LinesIsNotDecoded(int bigValues, int expectedLines, int expectedAt575)
    {
        var bits = new List<bool>();
        void Put(string text) => bits.AddRange(text.Select(c => c == '1'));
        for (int i = 0; i < bigValues; i++)
        {
            Put("1"); // table 1: the pair (0, 0)
        }

        int quadruples = (576 - (2 * bigValues) + 3) / 4;
        for (int i = 0; i < quadruples; i++)
        {
            // Table B: the code is the four flags inverted. The last quadruple has the values at its first, second and fourth line.
            Put(i == quadruples - 1 ? "0010" : "1111");
            if (i == quadruples - 1)
            {
                Put("000");
            }
        }

        var data = new byte[(bits.Count + 7) / 8 + 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                data[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        var granule = new GranuleSyntax { Bands = BandKind.Long, BandRow = 5, LongBandCount = 22, BigValues = bigValues, Table0 = 1, Count1Table = 1 };
        var values = new int[576];
        var source = new BitSource(data);
        int lines = Layer3Syntax.ReadSpectrum(ref source, granule, values, data.Length * 8, out bool isOverrun);

        Assert.False(isOverrun);
        Assert.Equal(expectedLines, lines);
        Assert.Equal(expectedAt575, values[575]);
        Assert.Equal(expectedAt575, values[(2 * bigValues) + ((quadruples - 1) * 4)]);
    }

    [Fact]
    public void LongBlocksCodeLinesInFrequencyOrder()
    {
        var granule = new GranuleSyntax { Bands = BandKind.Long, BandRow = 5, LongBandCount = 22 };

        Assert.Equal(0, Layer3Walker.Top(granule, 0));
        Assert.Equal(140, Layer3Walker.Top(granule, 140));
        Assert.Equal(576, Layer3Walker.Top(granule, 576));
    }

    [Fact]
    public void IntensityStartsAboveTheLastBandTheRightChannelCodesAndSkipsIllegalPositions()
    {
        // 44,100 Hz long bands start at 0, 4, 8, ... 90 (band 13), 110 (band 14), 134 (band 15): a nonzero pair in band 13 puts the first intensity band at 14.
        var granule = new GranuleSyntax { Bands = BandKind.Long, BandRow = 5, LongBandCount = 22 };
        var header = new MpegHeader(0xFFFB1050);
        int[] values = new int[576];
        values[95] = 1;
        byte[] positions = new byte[40];

        Assert.Equal(110, Layer3Walker.IntensityStart(header, granule, granule, values, positions));

        positions[14] = 7;
        Assert.Equal(134, Layer3Walker.IntensityStart(header, granule, granule, values, positions));

        values[500] = 1;
        Assert.Equal(-1, Layer3Walker.IntensityStart(header, granule, granule, values, positions));
    }

    [Fact]
    public void ShortBlocksTakeTheFrequencyOfALineFromItsBand()
    {
        // 44,100 Hz short bands start 4, 4, 4, 4, 4, 4, 6, 8, ... lines wide: the coded lines of the first band's three windows (12) top out at 3 * 4 lines of the 576.
        var granule = new GranuleSyntax { Bands = BandKind.Short, BandRow = 5, ShortBandCount = 39 };

        Assert.Equal(3 * 4, Layer3Walker.Top(granule, 4));
        Assert.Equal(3 * 4, Layer3Walker.Top(granule, 12));
        Assert.Equal(3 * 5, Layer3Walker.Top(granule, 13));
        Assert.Equal(576, Layer3Walker.Top(granule, 576));
    }

    // Every row of every band table covers the 576 lines of a granule, and the mixed table has a row of 40 entries for each of the eight sample rate rows
    // (minimp3's rows hold 36 to 40 initializers, which C pads with zeros; a table written without the padding shifted every row after the first).
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryBandTableRowCovers576Lines(int kindNumber)
    {
        var kind = (BandKind)kindNumber;
        for (int row = 0; row < 8; row++)
        {
            var granule = new GranuleSyntax { Bands = kind, BandRow = row, LongBandCount = kind == BandKind.Short ? 0 : kind == BandKind.Mixed ? 6 : 22, ShortBandCount = kind == BandKind.Long ? 0 : 30 };
            ReadOnlySpan<byte> widths = Layer3Syntax.BandWidths(granule);
            int sum = 0;
            for (int i = 0; i < widths.Length && widths[i] != 0; i++)
            {
                sum += widths[i];
            }

            Assert.True(sum == 576, $"{kind} row {row} covers {sum} lines");
        }
    }
}
