using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The limits the compression level guess draws from the stereo assignments of libFLAC's adaptive levels (State.AdaptiveBreaks), from damaged frames and gaps, and from
// the order allowance of the FFmpeg releases whose levels 6 and 8 name an order one above their highest (docs/formats/flac.md, "Compression level").
public class FlacAdaptiveStereoTests
{
    private const int Lr = 1;
    private const int Ls = 8;
    private const int Ms = 10;

    private static byte[] Comment(string vendor)
    {
        byte[] text = Encoding.UTF8.GetBytes(vendor);
        return [.. BitConverter.GetBytes(text.Length), .. text, 0, 0, 0, 0];
    }

    // A frame of two linear predictor subframes of order 1 (order 9 at the precision 15 where order is given) over a block, with the channel assignment code (channels - 1
    // for left/right, 8 to 10 for the others, whose side channel has one bit more).
    private static byte[] LpcFrame(long number, int block, int rate, int assignmentChannels, int order = 1, int precision = 4)
    {
        return FlacBuilder.Frame(number, block, rate, assignmentChannels + 1, 16, false, body =>
        {
            for (int c = 0; c < 2; c++)
            {
                bool isSide = (assignmentChannels == Ls && c == 1) || (assignmentChannels == Ms && c == 1);
                FlacBuilder.Bits(body, 0, 1);
                FlacBuilder.Bits(body, (uint)(0b100000 | (order - 1)), 6);
                FlacBuilder.Bits(body, 0, 1);
                for (int i = 0; i < order; i++)
                {
                    FlacBuilder.Bits(body, 0, isSide ? 17 : 16);
                }

                FlacBuilder.Bits(body, (uint)(precision - 1), 4);
                FlacBuilder.Bits(body, 0, 5);
                for (int i = 0; i < order; i++)
                {
                    FlacBuilder.Bits(body, 0, precision);
                }

                FlacBuilder.Bits(body, 0, 2);
                FlacBuilder.Bits(body, 0, 4);
                FlacBuilder.Bits(body, 0, 4);
                for (int i = order; i < block; i++)
                {
                    FlacBuilder.Bits(body, 1, 1);
                }
            }
        });
    }

    private static byte[] Stream(string vendor, int rate, int block, IReadOnlyList<int> assignments, int first = 0, int skip = -1, int damage = -1, int order = 1, int precision = 4)
    {
        var frames = new List<byte>();
        for (int i = 0; i < assignments.Count; i++)
        {
            if (i == skip)
            {
                continue;
            }

            byte[] frame = LpcFrame(first + i, block, rate, assignments[i], order, precision);
            if (i == damage)
            {
                frame[9] ^= 0xFF;
                byte[] crc = new byte[2];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(crc, FlacBuilder.Crc16(frame.AsSpan(0, frame.Length - 2)));
                crc.CopyTo(frame, frame.Length - 2);
            }

            frames.AddRange(frame);
        }

        return [.. FlacBuilder.Marker, .. FlacBuilder.Block(0, FlacBuilder.StreamInfo(block, rate, 2, 16, (long)block * assignments.Count)), .. FlacBuilder.Block(4, Comment(vendor), isLast: true), .. frames];
    }

    private static Node? LevelNode(byte[] file) => Find(Analyzer.CreateDefault().Analyze("crafted.flac", new MemoryStream(file)).Root);

    private static Node? Find(Node node) => node.Name == "Compression Level" && node.Children.Count > 0 ? node : node.Children.Select(Find).FirstOrDefault(n => n is not null);

    private static bool HasBreaks(Node? node) => node!.Children.Any(c => c.Name == "Evidence" && c.Value!.ToString()!.Contains("adaptive choice of levels 1 and 4", StringComparison.Ordinal));

    private const string Libflac = "reference libFLAC 1.2.1 20070917";

    // Nine frames of 4,096 samples at 44.1 kHz: the encoder tests all four assignments on frames 0, 4 and 8 and keeps left/right or mid/side between them.
    private static readonly int[] ChangeAtAnOrdinaryFrame = [Lr, Lr, Lr, Lr, Lr, Ms, Ms, Ms, Ms];

    [Fact]
    public void AChangeBetweenLeftRightAndMidSideAtAnOrdinaryFrameRulesOutTheAdaptiveLevels()
    {
        Assert.True(HasBreaks(LevelNode(Stream(Libflac, 44100, 4096, ChangeAtAnOrdinaryFrame))));
        Assert.False(HasBreaks(LevelNode(Stream(Libflac, 44100, 4096, [Lr, Lr, Lr, Lr, Ms, Ms, Ms, Ms, Ls]))));
    }

    // The frames between the tests are 0.4 s apart: at 22.05 kHz that is every second frame, so the change at frame 2 is a test frame there and a break at 44.1 kHz.
    [Theory]
    [InlineData(22050, false)]
    [InlineData(44100, true)]
    public void TheDistanceBetweenTestsFollowsTheRate(int rate, bool isBreak) =>
        Assert.Equal(isBreak, HasBreaks(LevelNode(Stream(Libflac, rate, 4096, [Lr, Lr, Ms, Ms, Ms, Ms, Ms, Ms, Ms]))));

    [Fact]
    public void AFrameWhoseHeaderChecksumFailsIsNotReadForItsAssignment()
    {
        Assert.True(HasBreaks(LevelNode(Stream(Libflac, 44100, 4096, ChangeAtAnOrdinaryFrame))));
        Assert.False(HasBreaks(LevelNode(Stream(Libflac, 44100, 4096, ChangeAtAnOrdinaryFrame, damage: 5))));
    }

    [Fact]
    public void AGapAndAFirstSampleThatIsNotZeroEndTheRule()
    {
        Assert.False(HasBreaks(LevelNode(Stream(Libflac, 44100, 4096, ChangeAtAnOrdinaryFrame, skip: 5))));
        Assert.False(HasBreaks(LevelNode(Stream(Libflac, 44100, 4096, ChangeAtAnOrdinaryFrame, first: 1))));
    }

    // FFmpeg 0.11.5's and 1.0.10's levels 6 and 8 name an order one above their highest; from 1.2.12 the candidates are clipped, so order 9 fits level 6 only in the first two.
    [Theory]
    [InlineData("Lavf54.6.100", "FFmpeg 6, 8, 9, 10, 11 or 12")]
    [InlineData("Lavf54.29.104", "FFmpeg 6, 8, 9, 10, 11 or 12")]
    [InlineData("Lavf54.63.104", "FFmpeg 8 to 12")]
    [InlineData("Lavf61.7.100", "FFmpeg 8 to 12")]
    public void TheOrderAllowanceOfLevels6And8FollowsTheRelease(string vendor, string levels)
    {
        byte[] file = Stream(vendor, 44100, 4608, [Lr, Lr, Lr, Lr, Lr], order: 9, precision: 15);
        Assert.Equal(levels, LevelNode(file)!.Value!.ToString());
    }

    [Fact]
    public void AVersionNoRunConfirmedIsNotNamed()
    {
        Assert.Null(LevelNode(Stream("Lavf61.8.100", 44100, 4608, [Lr, Lr, Lr, Lr, Lr], order: 9, precision: 15)));
        Assert.Null(LevelNode(Stream("reference libFLAC 1.1.1 20050209", 44100, 4608, [Lr, Lr, Lr, Lr, Lr])));
    }

    [Theory]
    [InlineData("flac/external-ogg-ffmpeg-level-5-mono-ffmpeg-0.11.5.ogg")]
    [InlineData("flac/external-ogg-ffmpeg-level-5-mono-ffmpeg-1.0.10.ogg")]
    public void AnOggFileWhoseEncoderCommentNamesTheMuxerIsNamedThroughTheWriter(string name)
    {
        Node node = Find(Fixtures.AnalyzeMpeg(name).Root)!;
        Assert.Equal("FFmpeg 5 to 12", node.Value!.ToString());
    }
}
