using Encspect.Core;
using Encspect.Formats.Ac3;

namespace Encspect.Tests;

// The same holds for E-AC-3: whatever the bits of a frame say, the decoder writes finite samples within the ranges the specification allows, and decodes every frame the parser
// reads. The mantissas of the hand-built files that use the adaptive hybrid transform, spectral extension and enhanced coupling (whose vectors, gains, tags, angles and noise all
// reach their extremes this way) and of an encoder's 5.1 file are replaced by seeded random bits, and so are bytes of the frames' headers and side information.
public class Eac3DecoderRobustnessTests
{
    public static TheoryData<string, int> Cases
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (string name in new[] { "ac3/eac3-signal-aht.eac3", "ac3/eac3-signal-spx-noise.eac3", "ac3/eac3-signal-ecpl.eac3", "ac3/external-ffmpeg-eac3-5.1-256k.eac3" })
            {
                for (int seed = 1; seed <= 3; seed++)
                {
                    data.Add(name, seed);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RandomMantissasDecodeToFiniteSamples(string name, int seed)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        var random = new Random(seed);
        foreach (Node mantissas in Ac3DecoderRobustnessTests.Find(Fixtures.AnalyzeMpeg(name).Root, "Mantissas"))
        {
            long start = (mantissas.Offset * 8) + (mantissas.BitOffset ?? 0);
            long bits = mantissas.Value?.AsInteger() ?? 0;
            for (long bit = start; bit < start + bits; bit++)
            {
                if (random.Next(2) == 0)
                {
                    data[bit >> 3] ^= (byte)(0x80 >> (int)(bit & 7));
                }
            }
        }

        Ac3DecoderRobustnessTests.Decode(name, data);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RandomSideInformationDecodesToFiniteSamples(string name, int seed)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        var random = new Random(seed);
        int frameLength = SyncFrame.Read(data).FrameLength;
        for (int frame = 0; frame * frameLength + frameLength <= data.Length; frame++)
        {
            // A few bytes of the bit stream information and audio frame fields after the sync word, and of the first block's flags.
            for (int i = 0; i < 3; i++)
            {
                data[(frame * frameLength) + 8 + random.Next(40)] = (byte)random.Next(256);
            }
        }

        Ac3DecoderRobustnessTests.Decode(name, data);
    }
}
