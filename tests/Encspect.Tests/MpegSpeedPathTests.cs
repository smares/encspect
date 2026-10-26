using Encspect.Formats.Mpeg;

namespace Encspect.Tests;

// The frame summaries and the side information check of MPEG audio, each held to the code it replaced.
public class MpegSpeedPathTests
{
    [Fact]
    public void MpegSummaryEqualsTheFormulaForEveryHeaderShapeAndIgnoresTheOtherBits()
    {
        var rng = new Random(33);
        for (uint version = 0; version < 4; version++)
        {
            for (uint layer = 1; layer < 4; layer++)
            {
                for (uint bitrate = 0; bitrate < 15; bitrate++)
                {
                    for (uint rate = 0; rate < 3; rate++)
                    {
                        for (uint mode = 0; mode < 4; mode++)
                        {
                            if (version == 1)
                            {
                                continue;
                            }

                            uint raw = 0xFFE00000 | (version << 19) | (layer << 17) | (bitrate << 12) | (rate << 10) | (mode << 6);
                            var header = new MpegHeader(raw);
                            string expected = $"{header.VersionName} Layer {header.LayerName}, {header.BitrateName}, {header.SampleRate} Hz, {header.ChannelMode}";
                            Assert.Equal(expected, header.Summary);
                            uint noise = (uint)rng.Next() & 0x0001030F;
                            Assert.Equal(expected, new MpegHeader(raw | noise).Summary);
                        }
                    }
                }
            }
        }
    }

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

    [Fact]
    public void MpegHasSideInfoProblemsEqualsWhetherTheIteratorYieldsAnything()
    {
        var rng = new Random(44);
        int with = 0;
        int without = 0;
        for (int iteration = 0; iteration < 40000; iteration++)
        {
            uint version = rng.Next(3) == 0 ? 2u : (rng.Next(2) == 0 ? 3u : 0u);
            uint mode = (uint)rng.Next(4);
            uint extension = (uint)rng.Next(4);
            uint raw = 0xFFE00000 | (version << 19) | (1u << 17) | (9u << 12) | (mode << 6) | (extension << 4);
            var header = new MpegHeader(raw);
            Layer3SideInfo info = RandomSideInfo(rng, header);
            bool any = MpegAudioParser.SideInfoProblems(info, 0, 0).Any();
            Assert.Equal(any, MpegAudioParser.HasSideInfoProblems(info));
            if (any)
            {
                with++;
            }
            else
            {
                without++;
            }
        }

        Assert.True(with > 1000 && without > 1000, $"{with} with problems, {without} without");
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
