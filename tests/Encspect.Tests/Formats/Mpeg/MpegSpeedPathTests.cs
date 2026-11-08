using Encspect.Formats.Mpeg;

namespace Encspect.Tests;

// The side information check of MPEG audio, held to the code it replaced.
public class MpegSpeedPathTests
{
    private static Layer3SideInfo RandomSideInfo(Random rng, MpegHeader header)
    {
        byte[] bytes = new byte[Layer3SideInfo.Size(header)];
        rng.NextBytes(bytes);
        // Mostly well-formed values, so that both outcomes occur: the part 2 and 3 lengths, big values and table selects of real frames.
        if (rng.Next(4) != 0)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] &= (byte)rng.Next(0x7F, 0x100);
            }
        }

        return Layer3SideInfo.Read(bytes, header);
    }

    private static string Frames(int count) => count == 1 ? "1 frame" : $"{count} frames";

    private static string Frames(int count, string singular, string plural) => $"{Frames(count)} {(count == 1 ? singular : plural)}";

    // The check of the side information as 65e7fb5 had it, frozen: an iterator over the granules with the conditions written out, and the
    // findings' texts. The parser's flags must give the same findings in the same order, and HasSideInfoProblems whether there are any.
    private static IEnumerable<(string Code, Encspect.Core.Node Node, Func<int, string> Message)> FrozenSideInfoProblems(Layer3SideInfo info, long position, long offset)
    {
        MpegHeader header = info.Header;
        for (int gr = 0; gr < info.Granules.Length; gr++)
        {
            for (int ch = 0; ch < info.Granules[gr].Length; ch++)
            {
                Layer3Granule g = info.Granules[gr][ch];
                if (g.BigValues.Value > 288)
                {
                    yield return ("mpeg.big-values-too-large", Field("Big Values", g.BigValues), count =>
                        $"{Frames(count, "has", "have")} more than 288 big_values pairs, the first {g.BigValues.Value} at 0x{position:X}.");
                }

                if (g.IsSwitched && g.BlockType.Value == 0)
                {
                    yield return ("mpeg.block-type-reserved", Layer3SideInfo.BlockTypeSpan(offset, g), count =>
                        $"{Frames(count, "switches", "switch")} windows with block type 0, which is reserved, the first at 0x{position:X}.");
                }

                foreach (BitField table in g.TableSelect.Where(t => t.Value is 4 or 14))
                {
                    yield return ("mpeg.huffman-table-undefined", Layer3SideInfo.TableSelectSpan(offset, g), count =>
                        $"{Frames(count, "selects", "select")} a Huffman table that does not exist, the first table {table.Value} at 0x{position:X}.");
                }

                int part2 = info.Part2Bits(gr, ch);
                if (part2 > g.Part23Length.Value)
                {
                    yield return ("mpeg.scalefactors-exceed-granule", Field("Part 2 and 3 Length", g.Part23Length), count =>
                        $"{Frames(count, "has", "have")} scalefactors longer than part2_3_length, the first {part2} of {Encspect.Core.Counts.Of(g.Part23Length.Value, "bit", "bits")} at 0x{position:X}.");
                }

                if (header.IsMpeg1 && g.IsShort && info.Scfsi[ch].Value != 0)
                {
                    yield return ("mpeg.scfsi-with-short-blocks", Field($"Channel {ch}", info.Scfsi[ch]), count =>
                        $"{Frames(count, "sets", "set")} scfsi for a channel with short blocks, which libmad rejects, the first at 0x{position:X}.");
                }
            }
        }

        bool jointStereo = header.ChannelModeBits == 1 && header.ModeExtension != 0;
        foreach (Layer3Granule[] row in info.Granules.Where(row => jointStereo && row.Length == 2))
        {
            if (row[0].BlockTypeValue != row[1].BlockTypeValue || row[0].IsMixed != row[1].IsMixed)
            {
                yield return ("mpeg.stereo-block-types-differ", Layer3SideInfo.BlockTypeSpan(offset, row[1]), count =>
                    $"{Frames(count, "uses", "use")} mid/side or intensity stereo with a different block type in each channel, which libmad rejects, the first at 0x{position:X}.");
            }
        }

        Encspect.Core.Node Field(string name, BitField field) => Layer3SideInfo.Span(name, offset, field.Bit, field.Bit + field.Length);
    }

    [Fact]
    public void MpegSideInfoFindingsEqualTheFrozenIteratorInCodeNodeTextAndOrder()
    {
        var rng = new Random(44);
        var seen = new Dictionary<string, int>();
        int without = 0;
        for (int iteration = 0; iteration < 60000; iteration++)
        {
            uint version = rng.Next(3) == 0 ? 2u : (rng.Next(2) == 0 ? 3u : 0u);
            uint mode = (uint)rng.Next(4);
            uint extension = (uint)rng.Next(4);
            uint raw = 0xFFE00000 | (version << 19) | (1u << 17) | (9u << 12) | (mode << 6) | (extension << 4);
            var header = new MpegHeader(raw);
            Layer3SideInfo info = RandomSideInfo(rng, header);
            long position = rng.Next(0, 1000000);
            long offset = position + 4;
            var expected = FrozenSideInfoProblems(info, position, offset).ToList();
            var actual = MpegAudioParser.SideInfoProblems(info, position, offset).ToList();
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Code, actual[i].Code);
                Assert.Equal((expected[i].Node.Name, expected[i].Node.Offset, expected[i].Node.Length), (actual[i].Node.Name, actual[i].Node.Offset, actual[i].Node.Length));
                Assert.Equal(expected[i].Message(1), actual[i].Message(1));
                Assert.Equal(expected[i].Message(7), actual[i].Message(7));
                seen[expected[i].Code] = seen.GetValueOrDefault(expected[i].Code) + 1;
            }

            Assert.Equal(expected.Count > 0, MpegAudioParser.HasSideInfoProblems(info));
            without += expected.Count == 0 ? 1 : 0;
        }

        foreach (string code in new[] { "mpeg.big-values-too-large", "mpeg.block-type-reserved", "mpeg.huffman-table-undefined", "mpeg.scalefactors-exceed-granule", "mpeg.scfsi-with-short-blocks", "mpeg.stereo-block-types-differ" })
        {
            Assert.True(seen.GetValueOrDefault(code) > 100, $"{code} occurred {seen.GetValueOrDefault(code)} times");
        }

        Assert.True(without > 1000, $"{without} without problems");
    }

    // The scalefactor bits of an MPEG-2 or 2.5 granule as they were summed before, with slen and nr_of_sfb_block (ISO/IEC 13818-3 2.4.3.2) as arrays.
    private static readonly int[][][] ReferenceNrOfSfb =
    [
        [[6, 5, 5, 5], [9, 9, 9, 9], [6, 9, 9, 9]],
        [[6, 5, 7, 3], [9, 9, 12, 6], [6, 9, 12, 6]],
        [[11, 10, 0, 0], [18, 18, 0, 0], [15, 18, 0, 0]],
        [[7, 7, 7, 0], [12, 12, 12, 0], [6, 15, 12, 0]],
        [[6, 6, 6, 3], [12, 9, 9, 6], [6, 12, 9, 6]],
        [[8, 8, 5, 0], [15, 12, 9, 0], [6, 18, 9, 0]],
    ];

    private static int ReferencePart2Bits(int sfc, bool intensityRight, bool isShort, bool isMixed)
    {
        int[] slen;
        int table;
        if (!intensityRight)
        {
            if (sfc < 400)
            {
                slen = [(sfc >> 4) / 5, (sfc >> 4) % 5, (sfc % 16) >> 2, sfc % 4];
                table = 0;
            }
            else if (sfc < 500)
            {
                int x = sfc - 400;
                slen = [(x >> 2) / 5, (x >> 2) % 5, x % 4, 0];
                table = 1;
            }
            else
            {
                int y = sfc - 500;
                slen = [y / 3, y % 3, 0, 0];
                table = 2;
            }
        }
        else
        {
            int i = sfc >> 1;
            if (i < 180)
            {
                slen = [i / 36, i % 36 / 6, i % 36 % 6, 0];
                table = 3;
            }
            else if (i < 244)
            {
                int x = i - 180;
                slen = [(x % 64) >> 4, (x % 16) >> 2, x % 4, 0];
                table = 4;
            }
            else
            {
                int z = i - 244;
                slen = [z / 3, z % 3, 0, 0];
                table = 5;
            }
        }

        return slen.Zip(ReferenceNrOfSfb[table][isShort ? (isMixed ? 2 : 1) : 0], (s, n) => s * n).Sum();
    }

    private sealed class Bits
    {
        public List<byte> Bytes { get; } = [];
        private int _count;

        public void Add(int value, int width)
        {
            for (int i = width - 1; i >= 0; i--)
            {
                if (_count % 8 == 0)
                {
                    Bytes.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    Bytes[^1] |= (byte)(0x80 >> (_count % 8));
                }

                _count++;
            }
        }
    }

    [Fact]
    public void MpegPart2BitsOfLowerSampleRatesEqualsTheSumWithArraysForEveryScalefacCompress()
    {
        // MPEG-2 joint stereo with intensity stereo set (mode extension 1): the second channel reads the intensity tables.
        var header = new MpegHeader(0xFFE00000 | (2u << 19) | (1u << 17) | (9u << 12) | (1u << 6) | (1u << 4));
        for (int sfc = 0; sfc < 512; sfc++)
        {
            foreach ((bool switched, int blockType, bool mixed) in new[] { (false, 0, false), (true, 2, false), (true, 2, true), (true, 1, false) })
            {
                var bits = new Bits();
                bits.Add(0, 8);
                bits.Add(0, 2);
                for (int ch = 0; ch < 2; ch++)
                {
                    bits.Add(100, 12);
                    bits.Add(10, 9);
                    bits.Add(100, 8);
                    bits.Add(sfc, 9);
                    bits.Add(switched ? 1 : 0, 1);
                    if (switched)
                    {
                        bits.Add(blockType, 2);
                        bits.Add(mixed ? 1 : 0, 1);
                        bits.Add(0, 10);
                        bits.Add(0, 9);
                    }
                    else
                    {
                        bits.Add(0, 15);
                        bits.Add(0, 7);
                    }

                    bits.Add(0, 2);
                }

                Layer3SideInfo info = Layer3SideInfo.Read(bits.Bytes.ToArray(), header);
                for (int ch = 0; ch < 2; ch++)
                {
                    Assert.Equal(ReferencePart2Bits(sfc, ch == 1, switched && blockType == 2, switched && blockType == 2 && mixed), info.Part2Bits(0, ch));
                }
            }
        }
    }
}
