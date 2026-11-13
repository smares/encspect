using Encspect.Core;
using Encspect.Formats.Ac3;
using Encspect.Host;

namespace Encspect.Tests;

// The decoder never writes a value that is not finite or beyond what the specification's ranges allow, whatever the bits of a frame say: the mantissas of every frame of
// some encoders' files are replaced by seeded random bits (so that the grouped codes no quantizer has, the extremes of every asymmetric quantizer and noise in every bin are
// all decoded, with the CRCs failing), and so are bytes of the frames' headers and side information.
public class Ac3DecoderRobustnessTests
{
    public static TheoryData<string, int> Cases
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (string name in new[] { "ac3/external-ffmpeg-stereo-192k.ac3", "ac3/external-ffmpeg-5.1-448k.ac3", "ac3/external-aften-stereo-192k-blocksw.ac3", "ac3/external-ffmpeg-mono-96k.ac3" })
            {
                for (int seed = 1; seed <= 3; seed++)
                {
                    data.Add(name, seed);
                }
            }

            return data;
        }
    }

    private sealed class FiniteSink : IPcmSink
    {
        public long Samples { get; private set; }

        public float Peak { get; private set; }

        public bool IsFinite { get; private set; } = true;

        public void Write(PcmBlock block)
        {
            for (int channel = 0; channel < block.Channels; channel++)
            {
                foreach (float value in block.Floats.Slice(channel * block.Stride, block.Length))
                {
                    IsFinite &= float.IsFinite(value);
                    Peak = Math.Max(Peak, Math.Abs(value));
                }
            }

            Samples += block.Length;
        }

        public void End(long samples)
        {
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RandomMantissasDecodeToFiniteSamples(string name, int seed)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        var random = new Random(seed);
        foreach (Node mantissas in Find(Fixtures.AnalyzeMpeg(name).Root, "Mantissas"))
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

        Decode(name, data);
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
            // A few bytes of the first 40 after the sync word, the bit stream information and the first block's flags.
            for (int i = 0; i < 3; i++)
            {
                data[(frame * frameLength) + 8 + random.Next(32)] = (byte)random.Next(256);
            }
        }

        Decode(name, data);
    }

    private static void Decode(string name, byte[] data)
    {
        var sinks = new List<FiniteSink>();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => { var sink = new FiniteSink(); sinks.Add(sink); return sink; } } };
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), new MemoryStream(data), options);

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed" or "decode.frame-invalid");
        Assert.All(sinks, sink => Assert.True(sink.IsFinite, "The decoder wrote a value that is not finite."));
        Assert.All(sinks, sink => Assert.True(sink.Peak < 1e6f, $"The decoder wrote a sample of {sink.Peak}."));
        foreach (StreamInfo info in report.Streams.Where(s => s.Verification is { Decoding: DecodeStatus.Decoded }))
        {
            Assert.True(info.Verification!.DecodedSamples <= info.Samples - info.LostSamples);
        }
    }

    private static IEnumerable<Node> Find(Node node, string name)
    {
        if (node.Name == name)
        {
            yield return node;
        }

        foreach (Node child in node.Children)
        {
            foreach (Node found in Find(child, name))
            {
                yield return found;
            }
        }
    }
}
