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

        public Rig(int bytesPerSecond, byte options = 7)
        {
            // Version 2, 44.1 kHz stereo with scale factor codes and variable block sizes.
            var format = new AudioFormat(0x0161, 2, 44_100, bytesPerSecond, 1000, 16);
            Parameters = StdParameters.Create(format, [0, 0, 0, 0, options, 0]);
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
            var frame = new CodedFrame(new MappedBytes(Array.Empty<byte>(), 0), "Frame", (long)index * Frame, Frame) { IsWalked = true, FollowsGap = follows, FailsChecksum = fails };
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
    private static int[] Block(int size, int flags, int gain = 100, int exponent = 0, int[]? perLine = null)
    {
        int length = Frame >> size;
        var r = new List<int> { 1, size, size, size, flags, gain };
        int coded = ((flags & 2) != 0 ? 1 : 0) + ((flags & 4) != 0 ? 1 : 0);
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

    // The first frame after a gap holds a block that reuses exponents it never received: its window, which reaches into the next frame's output, is withheld too.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABlockThatReusesLostExponentsWithholdsItsWholeWindowAfterAGap(bool fails)
    {
        var rig = new Rig(16_000);
        rig.Decode(0, Block(0, BothSent));
        rig.Decode(1, Block(0, BothReused), follows: true, fails: fails);
        rig.Decode(2, Block(0, BothSent));
        rig.Decode(3, Block(0, BothSent));

        Assert.Equal(Frame, Rig.Covered(rig.Withheld, 2 * Frame, Frame));
        Assert.Equal(0, Rig.Covered(rig.Withheld, 3 * Frame, Frame));
    }

    // Exponents that were never received (a frame failed after sending them, which the parser counts as received) are not 1: the block is withheld and nothing infinite or NaN is written.
    [Fact]
    public void ReusingExponentsThatWereNeverHeldWritesNoNaNAndWithholdsTheBlock()
    {
        var rig = new Rig(16_000);
        rig.Decode(0, Block(0, FirstSent));
        rig.Decode(1, Block(0, FirstSent));
        rig.Decode(2, Block(0, BothReused));
        rig.Decode(3, Block(0, FirstSent));

        Assert.False(rig.HasNonFinite);
        Assert.Equal(Frame, Rig.Covered(rig.Withheld, 2 * Frame, Frame));
        Assert.Equal(Frame, Rig.Covered(rig.Withheld, 3 * Frame, Frame));
    }

    // The frame after one that failed its checksum is left out of the measurements; so is a later block that reuses the exponents the failed frame sent, until a good frame sends them again.
    [Fact]
    public void ABlockThatReusesTheExponentsOfAFailedFrameIsLeftOutOfTheMeasurements()
    {
        var rig = new Rig(16_000);
        rig.Decode(0, Block(0, BothSent));
        rig.Decode(1, Block(0, BothSent), fails: true);
        rig.Decode(2, Block(0, BothReused));
        rig.Decode(3, Block(0, BothSent));
        rig.Decode(4, Block(0, BothReused));
        rig.Decode(5, Block(0, BothSent));
        rig.Decode(6, Block(0, BothSent));

        Assert.Equal(Frame, Rig.Covered(rig.LeftOut, 3 * Frame, Frame));
        Assert.Equal(0, Rig.Covered(rig.LeftOut, 4 * Frame, Frame));
        Assert.Equal(0, Rig.Covered(rig.LeftOut, 5 * Frame, Frame));
        Assert.Empty(rig.Withheld);
    }
}
