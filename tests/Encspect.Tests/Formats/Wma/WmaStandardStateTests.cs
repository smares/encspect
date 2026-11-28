using Encspect.Core;
using Encspect.Formats.Wma;
using Encspect.Formats.Wma.Decoding;

namespace Encspect.Tests.Formats.Wma;

// The state the decoder of Windows Media Audio versions 1 and 2 carries from frame to frame (the exponents a block reuses), driven with scripted records of the parser's walk: what
// a lost or failed frame takes with it, and the reuse of exponents that were never received.
public class WmaStandardStateTests
{
    private const int Frame = 2048;
    private const int BothSent = 2 | 4 | StdRecordWriter.ExponentsSentFlag;
    private const int BothReused = 2 | 4;
    private const int FirstSent = 2 | StdRecordWriter.ExponentsSentFlag;

    private sealed class Rig
    {
        private int[] _record = [];

        public Rig(int bytesPerSecond, byte options = 7, int rate = 44_100, bool isVersion1 = false)
        {
            // Version 2, 44.1 kHz stereo with scale factor codes and variable block sizes (version 1: scale factor codes, one block size).
            var format = new AudioFormat(isVersion1 ? 0x0160 : 0x0161, 2, rate, bytesPerSecond, 1000, 16);
            Parameters = StdParameters.Create(format, isVersion1 ? [0, 0, 1, 0] : [0, 0, 0, 0, options, 0]);
            Assert.Null(Parameters.Unsupported);
            var setup = new DecodeSetup { Stream = new Node("Stream", 0, 1), Channels = 2, BitsPerSample = 16, Shared = Parameters };
            var context = new DecodeContext(setup, CancellationToken.None, (_, _, _, _, _) => { }, _ => new Collector(this), _ => { }, () => new SampleBuffers(_record, default, 2, 0),
                (first, length, _) => Withheld.Add((first, length)), (first, length) => LeftOut.Add((first, length)));
            Decoder = new StdFrameDecoder(context, Parameters);
        }

        public StdParameters Parameters { get; }

        public StdFrameDecoder Decoder { get; }

        public List<(long First, long Length)> Withheld { get; } = [];

        public List<(long First, long Length)> LeftOut { get; } = [];

        public bool HasNonFinite { get; private set; }

        public void Decode(int index, int[] record, bool follows = false, bool fails = false)
        {
            _record = record;
            var frame = new CodedFrame(new MappedBytes(Array.Empty<byte>(), 0), "Frame", (long)index * Parameters.FrameLength, Parameters.FrameLength) { IsWalked = true, FollowsGap = follows, FailsChecksum = fails };
            Decoder.Decode(frame);
        }

        public static long Covered(IEnumerable<(long First, long Length)> spans, long from, long length) =>
            Enumerable.Range(0, (int)length).Count(i => spans.Any(s => from + i >= s.First && from + i < s.First + s.Length));

        private sealed class Collector(Rig rig) : IPcmSink
        {
            public void Write(PcmBlock block)
            {
                for (int c = 0; c < block.Channels; c++)
                {
                    foreach (float f in block.Floats.Slice(c * block.Stride, block.Length))
                    {
                        if (!float.IsFinite(f))
                        {
                            rig.HasNonFinite = true;
                        }
                    }
                }
            }

            public void End(long samples)
            {
            }
        }
    }

    // A record of one block of the given size: scale factor exponents of every line when it sends them, and a few coefficients per coded channel.
    private static int[] Block(int size, int flags, int gain = 100, int exponent = 0, int[]? perLine = null, bool hasMasks = false, int frame = Frame)
    {
        int length = frame >> size;
        var r = new List<int> { size, size, size, flags, gain };
        int coded = ((flags & 2) != 0 ? 1 : 0) + ((flags & 4) != 0 ? 1 : 0);
        if (hasMasks)
        {
            r.AddRange(new int[coded]);
        }

        if ((flags & StdRecordWriter.ExponentsSentFlag) != 0)
        {
            for (int c = 0; c < coded; c++)
            {
                r.Add(exponent);
                for (int i = 0; i < length; i++)
                {
                    r.Add(perLine?[i] ?? exponent);
                }
            }
        }

        for (int c = 0; c < coded; c++)
        {
            for (int i = 0; i < length; i++)
            {
                r.Add(i < 40 ? ((i * 7) % 11) - 5 : 0);
            }
        }

        return [.. r];
    }

    private static int[] Record(params int[][] blocks) => [blocks.Length, .. blocks.SelectMany(b => b)];

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(owner)!;

    // The first frame after a gap holds a block that reuses exponents it never received: its window, which reaches into the next frame's output, is withheld too.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABlockThatReusesLostExponentsWithholdsItsWholeWindowAfterAGap(bool fails)
    {
        var rig = new Rig(16_000);
        rig.Decode(0, Record(Block(0, BothSent)));
        rig.Decode(1, Record(Block(0, BothReused)), follows: true, fails: fails);
        rig.Decode(2, Record(Block(0, BothSent)));
        rig.Decode(3, Record(Block(0, BothSent)));

        Assert.Equal(Frame, Rig.Covered(rig.Withheld, 2 * Frame, Frame));
        Assert.Equal(0, Rig.Covered(rig.Withheld, 3 * Frame, Frame));
    }

    // Exponents that were never received (a frame failed after sending them, which the parser counts as received) are not 1: the block is withheld and nothing infinite or NaN is written.
    [Fact]
    public void ReusingExponentsThatWereNeverHeldWritesNoNaNAndWithholdsTheBlock()
    {
        var rig = new Rig(16_000);
        rig.Decode(0, Record(Block(0, FirstSent)));
        rig.Decode(1, Record(Block(0, FirstSent)));
        rig.Decode(2, Record(Block(0, BothReused)));
        rig.Decode(3, Record(Block(0, FirstSent)));

        Assert.False(rig.HasNonFinite);
        Assert.Equal(Frame, Rig.Covered(rig.Withheld, 2 * Frame, Frame));
        Assert.Equal(Frame, Rig.Covered(rig.Withheld, 3 * Frame, Frame));
    }

    // The frame after one that failed its checksum is left out of the measurements; so is a later block that reuses the exponents the failed frame sent, until a good frame sends them again.
    [Fact]
    public void ABlockThatReusesTheExponentsOfAFailedFrameIsLeftOutOfTheMeasurements()
    {
        var rig = new Rig(16_000);
        rig.Decode(0, Record(Block(0, BothSent)));
        rig.Decode(1, Record(Block(0, BothSent)), fails: true);
        rig.Decode(2, Record(Block(0, BothReused)));
        rig.Decode(3, Record(Block(0, BothSent)));
        rig.Decode(4, Record(Block(0, BothReused)));
        rig.Decode(5, Record(Block(0, BothSent)));
        rig.Decode(6, Record(Block(0, BothSent)));

        Assert.Equal(Frame, Rig.Covered(rig.LeftOut, 3 * Frame, Frame));
        Assert.Equal(0, Rig.Covered(rig.LeftOut, 4 * Frame, Frame));
        Assert.Equal(0, Rig.Covered(rig.LeftOut, 5 * Frame, Frame));
        Assert.Empty(rig.Withheld);
    }

    // Version 1 without noise substitution scales line x by the exponent of line x itself, as Microsoft's decoder does (settled by the version 1 file of Microsoft's encoder
    // wandue_project_-_king_of_my_castle.asf: docs/design/wma-standard-decoding.md, "Points to settle", item 2); FFmpeg takes the exponent of line x - 3. The exponents differ
    // between neighboring lines here and one coefficient lies on each of two lines, so the two rules give different ratios.
    [Fact]
    public void Version1WithoutNoiseSubstitutionScalesALineByItsOwnExponent()
    {
        var rig = new Rig(8_000, isVersion1: true);
        Assert.Equal(1, rig.Parameters.Version);
        Assert.False(rig.Parameters.UsesNoiseSubstitution);
        int[] exponents = [.. Enumerable.Range(0, Frame).Select(i => i == 20 ? 16 : 0)];
        int[] block = Block(0, FirstSent, perLine: exponents);
        int coefficients = block.Length - Frame;
        Array.Clear(block, coefficients, Frame);
        block[coefficients + 20] = 3;
        block[coefficients + 23] = 3;
        rig.Decode(0, Record(block));

        float[] spectrum = Field<float[][]>(rig.Decoder, "_spectrum")[0];

        // Line 20 has the exponent 10^1 and line 23 the exponent 1; line x - 3 would swap them.
        Assert.True(spectrum[20] > 9 * spectrum[23], $"{spectrum[20]} against {spectrum[23]}");
    }

    private static float Power(int value) => (float)Math.Pow(10, value / 16.0);

    // A block that sends exponents for the coded channel converts what the uncoded one holds of a longer block to its own size: each band of its list takes the largest held value over the
    // held lines it covers, and a later block that reuses them reads the converted values.
    [Fact]
    public void AnUncodedChannelsHeldExponentsAreConvertedToTheSizeOfABlockThatSendsExponents()
    {
        var rig = new Rig(16_000);
        int[] held = [.. Enumerable.Range(0, Frame).Select(i => ((i * 5) % 13) - 6)];
        rig.Decode(0, Record(Block(0, BothSent, perLine: held)));
        rig.Decode(1, Record(Block(2, FirstSent), Block(2, BothReused), Block(2, BothReused), Block(2, BothReused)));

        float[] converted = Field<float[][]>(rig.Decoder, "_exponents")[1];
        int[] bands = rig.Parameters.ExponentBands[2];
        int line = 0;
        for (int band = 0; line < Frame / 4; band++)
        {
            int width = band < bands.Length && bands[band] > 0 ? bands[band] : 4;
            int to = Math.Min(line + width, Frame / 4);
            int largest = Enumerable.Range(line << 2, ((to - line) << 2)).Max(i => held[i]);
            for (int i = line; i < to; i++)
            {
                Assert.Equal(Power(largest), converted[i], 1e-4f * Power(largest));
            }

            line = to;
        }
    }

    // Lines from the coefficient end on take the exponent that follows the last high band of the shorter block the block reuses, not the one of the line before the end, where the last
    // band's width is not a multiple of the ratio of the sizes (here 29 lines of a block of 256 at 11.025 kHz, held from blocks of 128).
    [Fact]
    public void TheTailOfExponentsReusedFromAShorterBlockTakesTheValueAtTheFinalBase()
    {
        var rig = new Rig(8_000, rate: 11_025);
        StdParameters p = rig.Parameters;
        int frame = p.FrameLength;
        Assert.True(p.UsesNoiseSubstitution);
        int[] high = p.HighBands[1];
        Assert.True(high[^1] % 2 == 1, $"the last high band is {high[^1]} wide");

        int[] held = [.. Enumerable.Range(0, frame / 4).Select(i => ((i * 7) % 23) - 10)];
        rig.Decode(0, Record(Block(2, BothSent, perLine: held, hasMasks: true, frame: frame), Block(2, BothSent, perLine: held, hasMasks: true, frame: frame),
            Block(2, BothSent, perLine: held, hasMasks: true, frame: frame), Block(2, BothSent, perLine: held, hasMasks: true, frame: frame)));
        int noiseAt = Field<int>(rig.Decoder, "_noiseAt");
        rig.Decode(1, Record(Block(1, BothReused, hasMasks: true, frame: frame), Block(1, BothReused, hasMasks: true, frame: frame)));

        int length = frame / 2;
        int end = p.CoefficientEnds[1];
        int highStart = end - high.Sum();
        int finalBase = (highStart >> 1) + high.Sum(w => w >> 1);
        float edge = Power(held[finalBase - 1]);
        float[] spectrum = Field<float[][]>(rig.Decoder, "_spectrum")[0];
        float[] noise = Field<float[]>(rig.Decoder, "_noise");
        double largest = Field<double[]>(rig.Decoder, "_largest")[0];
        double m = Math.Pow(10, 100 / 20.0) / largest * (2.0 / length);
        double expected = noise[(noiseAt + (2 * length) + end) & 8191] * edge * m;
        Assert.Equal(expected, spectrum[end], Math.Abs(expected) * 1e-3);
    }
}
