using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Header stripping (RFC 9559, section 5.1.4.1.31.6) for the codecs that read their frames from the file: the stripped bytes are put back from the ContentCompSettings and the
// frames are read as the unstripped track's are, so the same figures and the same audio come of them, and each frame's nodes lie in the ContentCompSettings element for the bytes
// put back and in the block for the rest. The project's current tools do not strip the headers of audio (mkvmerge 82.0 does only for MPEG-4 part 2 video; old releases do, and their files are tested below), so the other files are hand-built
// (make_matroska_fixtures.py) from the frames of committed files.
public class MatroskaStrippingTests
{
    private static (Report Report, List<PcmHashSink> Sinks) Decode(string name)
    {
        var sinks = new List<PcmHashSink>();
        var options = new AnalysisOptions
        {
            Decoding = new DecodeOptions
            {
                Output = stream =>
                {
                    var sink = new PcmHashSink();
                    sink.Format(stream.Format);
                    sinks.Add(sink);
                    return sink;
                },
            },
        };
        using FileStream stream = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        return (Analyzer.CreateDefault().Analyze(Path.GetFileName(name), stream, options), sinks);
    }

    [Theory]
    [InlineData("matroska/mpeg-audio-header-stripped.mka", "matroska/mpeg-audio-frames.mka")]
    [InlineData("matroska/ac3-header-stripped.mka", "matroska/ac3-frames.mka")]
    [InlineData("matroska/dts-header-stripped.mka", "matroska/dts-frames.mka")]
    [InlineData("matroska/aac-header-stripped.mka", "matroska/aac-frames.mka")]
    public void AStrippedTrackIsReadAsTheUnstrippedOneIs(string stripped, string control)
    {
        (Report report, List<PcmHashSink> sinks) = Decode(stripped);
        (Report plain, List<PcmHashSink> own) = Decode(control);

        StreamInfo stream = Assert.Single(report.Streams);
        StreamInfo expected = Assert.Single(plain.Streams);
        Assert.Equal(expected.Codec, stream.Codec);
        Assert.Equal(expected.Samples, stream.Samples);
        Assert.Equal(expected.SampleRate, stream.SampleRate);
        Assert.Equal(expected.Channels, stream.Channels);
        Assert.Equal(plain.Findings.Select(f => f.Code), report.Findings.Select(f => f.Code));
        Assert.Equal(own.Select(o => o.Hash()), sinks.Select(o => o.Hash()));
        Assert.Equal(plain.Streams.Single().Verification?.Decoding, stream.Verification?.Decoding);

        // The frames' nodes are in the same number, and equal but for where they lie.
        Assert.Equal(Tree(plain.Root.Children.Single(), isShape: true), Tree(report.Root.Children.Single(), isShape: true));
    }

    // The files old releases of mkvmerge wrote (old_mkvmerge_tool.py; 4.5.0 to 5.9.0 strip the headers of MP3 and DTS, 5.4.0 to 5.9.0 also of AC-3) are read as the
    // elementary stream they were made from is: the same figures and the same decoded audio.
    [Theory]
    [InlineData("matroska/external-mkvmerge450-mp3-header-stripped.mka", "mpeg-audio/external-ffmpeg-cbr.mp3")]
    [InlineData("matroska/external-mkvmerge590-mp3-header-stripped.mka", "mpeg-audio/external-ffmpeg-cbr.mp3")]
    [InlineData("matroska/external-mkvmerge450-dts-header-stripped.mka", "dts/external-ffmpeg-5.1-48k-768k.dts")]
    [InlineData("matroska/external-mkvmerge590-dts-header-stripped.mka", "dts/external-ffmpeg-5.1-48k-768k.dts")]
    [InlineData("matroska/external-mkvmerge540-ac3-header-stripped.mka", "ac3/external-ffmpeg-stereo-192k.ac3")]
    [InlineData("matroska/external-mkvmerge590-ac3-header-stripped.mka", "ac3/external-ffmpeg-stereo-192k.ac3")]
    public void AFileAnOldMkvmergeWroteIsReadAsItsSourceIs(string name, string source)
    {
        (Report report, List<PcmHashSink> sinks) = Decode(name);
        (Report plain, List<PcmHashSink> own) = Decode(source);

        StreamInfo stream = Assert.Single(report.Streams);
        StreamInfo expected = Assert.Single(plain.Streams);
        Assert.Equal(expected.Codec, stream.Codec);
        Assert.Equal(expected.Samples, stream.Samples);
        Assert.Equal(expected.Channels, stream.Channels);
        Assert.Equal(own.Select(o => o.Hash()), sinks.Select(o => o.Hash()));
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("parse.", StringComparison.Ordinal) || f.Code is "matroska.track-encoding-not-read" or "mpeg-audio.packets-not-analyzed");
    }

    // The shape of a tree: the names and the number of children, with the track's own elements (which hold the encodings) left out; the values quote offsets, which the
    // two files do not share.
    private static string Tree(Node node, bool isShape) =>
        node.Name is "Segment" or "Tracks" ? string.Empty : $"{node.Name}({string.Concat(node.Children.Select(c => Tree(c, isShape)))})";

    [Theory]
    [InlineData("matroska/mpeg-audio-header-stripped.mka")]
    [InlineData("matroska/ac3-header-stripped.mka")]
    [InlineData("matroska/dts-header-stripped.mka")]
    [InlineData("matroska/aac-header-stripped.mka")]
    [InlineData("matroska/external-mkvmerge450-mp3-header-stripped.mka")]
    [InlineData("matroska/external-mkvmerge590-dts-header-stripped.mka")]
    [InlineData("matroska/external-mkvmerge590-ac3-header-stripped.mka")]
    public void AFramesNodesLieInTheSettingsAndInTheBlock(string name)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        Node settings = Find(report.Root, "ContentCompSettings")!;
        Node frame = FindAll(report.Root, "Frame").Concat(FindAll(report.Root, "Access Unit")).First(f => f.Ranges is not null);

        Assert.Equal(2, frame.Ranges!.Count);

        // The element's data is its last bytes, which are the stripped ones.
        Assert.Equal(settings.End, frame.Ranges[0].End);
        Assert.True(frame.Ranges[0].Offset >= settings.Offset);
        Assert.True(frame.Ranges[1].Offset > settings.End);
        TestFiles.CheckInsideParents(report.Root);
    }

    // A message that quotes where a frame is quotes the file offset of the frame's first byte, which for a frame with stripped bytes is a byte of the ContentCompSettings (the
    // node of the finding starts there too), not a position in the image the frames are read from.
    [Theory]
    [InlineData("matroska/mpeg-audio-header-stripped-cut.mka", "mpeg-audio.frame-truncated")]
    [InlineData("matroska/ac3-header-stripped-cut.mka", "ac-3.frame-truncated")]
    public void AMessageQuotesTheFileOffsetOfTheFramesFirstByte(string name, string code)
    {
        Report report = Fixtures.AnalyzeMpeg(name);
        Finding finding = Assert.Single(report.Findings, f => f.Code == code);
        Node settings = Find(report.Root, "ContentCompSettings")!;

        Assert.Equal(settings.End - 2, finding.Node!.Offset);
        Assert.Contains($"0x{settings.End - 2:X}", finding.Message, StringComparison.Ordinal);
    }

    private static Node? Find(Node node, string name) => FindAll(node, name).FirstOrDefault();

    private static IEnumerable<Node> FindAll(Node node, string name)
    {
        if (node.Name == name)
        {
            yield return node;
        }

        foreach (Node child in node.Children)
        {
            foreach (Node found in FindAll(child, name))
            {
                yield return found;
            }
        }
    }
}
