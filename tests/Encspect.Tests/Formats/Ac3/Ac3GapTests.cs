using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// After a gap the decoder resets its overlap and its dither generator and withholds the first block (docs/formats/ac3.md, "Decoding"): what follows must equal what a fresh
// decode of the same frames gives, sample for sample, dither included, since the generator starts from its seed at both.
public class Ac3GapTests
{
    private sealed class CollectingSink : IPcmSink
    {
        public List<float[]> Channels { get; } = [];

        public long First { get; private set; } = -1;

        public void Write(PcmBlock block)
        {
            if (First < 0)
            {
                First = block.Position;
            }

            while (Channels.Count < block.Channels)
            {
                Channels.Add([]);
            }

            for (int c = 0; c < block.Channels; c++)
            {
                Channels[c] = [.. Channels[c], .. block.Floats.Slice(c * block.Stride, block.Length)];
            }
        }

        public void End(long samples)
        {
        }
    }

    private static (CollectingSink Sink, StreamInfo Info) Decode(byte[] data)
    {
        var sink = new CollectingSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => sink } };
        Report report = Analyzer.CreateDefault().Analyze("gap.ac3", new MemoryStream(data), options);
        return (sink, report.Streams.Single());
    }

    [Theory]
    [InlineData("external-ffmpeg-stereo-192k.ac3", 768)]
    [InlineData("external-ffmpeg-5.1-448k.ac3", 1792)]
    public void Output_after_a_gap_equals_a_fresh_decode_of_the_frames_after_it(string name, int frameLength)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ac3", name));
        byte[] junk = [.. Enumerable.Repeat((byte)0xAA, 37)];
        const int before = 5;
        byte[] withGap = [.. file[..(before * frameLength)], .. junk, .. file[((before + 1) * frameLength)..]];
        byte[] tail = file[((before + 1) * frameLength)..];

        (CollectingSink gapped, _) = Decode(withGap);
        (CollectingSink fresh, _) = Decode(tail);

        // The sink holds what was written: the 5 frames before the gap, then the frame after it without its first block (256 samples, withheld), then the rest.
        int start = before * 1536;
        for (int c = 0; c < fresh.Channels.Count; c++)
        {
            float[] expected = fresh.Channels[c];
            float[] actual = gapped.Channels[c];
            Assert.Equal(start + expected.Length - 256, actual.Length);
            for (int n = 256; n < expected.Length; n++)
            {
                Assert.True(expected[n] == actual[start + n - 256], $"channel {c}, sample {n} after the gap differs from a fresh decode");
            }
        }
    }
}
