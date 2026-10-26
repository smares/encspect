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
}
