using System.Text.Json;
using Encspect.Core;
using Encspect.Formats.Asf;
using Encspect.Host;

namespace Encspect.Tests.Formats.Asf;

// The routes of ASF to MPEG audio (format tag 0x0055) and AC-3 (0x2000): the files ffmpeg's muxer wrote from the committed elementary streams
// (make_external_asf_fixtures.py) have the stream of their source in the report and decode to the same samples as the source does, whatever
// the container; the decode and measure goldens of every ASF fixture with a decodable stream are those of the decoders' fixture tests.
public class AsfRouteTests
{
    // The tags WaveFormatRoutes routes that ASF does not take from it, with the codec each goes to in WAV and Matroska's A_MS/ACM.
    private static readonly Dictionary<int, string> LeftOut = new()
    {
        [WaveFormatRoutes.DtsMicrosoft] = "dts",
        [WaveFormatRoutes.Dts] = "dts",
    };

    [Fact]
    public void EveryTagTheSharedRoutesRouteIsInAsfsTableOrInTheListOfThoseItLeavesOut()
    {
        var routed = new SortedSet<int>();
        for (int tag = 0; tag <= 0xFFFF; tag++)
        {
            if (WaveFormatRoutes.RouteOf(tag) is not null)
            {
                routed.Add(tag);
            }
        }

        Assert.Equal([], routed.Where(tag => !AsfParser.DefaultRoutes.ContainsKey(tag) && !LeftOut.ContainsKey(tag)).Select(tag => $"0x{tag:X4}"));
        Assert.Equal([], LeftOut.Keys.Where(tag => AsfParser.DefaultRoutes.ContainsKey(tag) || !routed.Contains(tag)).Select(tag => $"0x{tag:X4}"));
        Assert.Equal([], AsfParser.DefaultRoutes.Keys.Where(tag => !routed.Contains(tag)).Select(tag => $"0x{tag:X4}"));
        Assert.Equal(routed.Where(tag => AsfParser.DefaultRoutes.ContainsKey(tag)).Select(tag => WaveFormatRoutes.RouteOf(tag)), routed.Where(tag => AsfParser.DefaultRoutes.ContainsKey(tag)).Select(tag => AsfParser.DefaultRoutes[tag]));
        Assert.Equal(LeftOut.Keys.Order(), AsfParser.LeftOut.Order());
    }

    public static TheoryData<string> Names
    {
        get
        {
            var data = new TheoryData<string>();
            string directory = Path.Combine(Fixtures.FixtureDirectory, "asf");
            foreach (string path in Directory.EnumerateFiles(directory, "external-ffmpeg-*.asf").Order(StringComparer.Ordinal))
            {
                data.Add($"asf/{Path.GetFileName(path)}");
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void ReportMatchesGoldenFile(string name)
    {
        string actual = Fixtures.ToJson(Fixtures.AnalyzeMpeg(name));
        string path = Fixtures.MpegCompanion(name, ".report.json");
        if (Fixtures.UpdateGolden)
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing golden report {path}; run the tests with ENCSPECT_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static (Report Report, PcmHashingSink Sink) Decode(string name)
    {
        var sink = new PcmHashingSink();
        using FileStream file = File.OpenRead(Path.Combine(Fixtures.FixtureDirectory, name));
        Report report = Analyzer.CreateDefault().Analyze(Path.GetFileName(name), file, new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare } });
        return (report, sink);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheStreamIsTheOneTheManifestNames(string name)
    {
        JsonElement manifest = Fixtures.LoadMpegManifest(name);
        Report report = Fixtures.AnalyzeMpeg(name);

        Assert.Equal(manifest.GetProperty("streams").EnumerateArray().Select(s => $"{s.GetProperty("codec").GetString()}, {s.GetProperty("sample_rate").GetInt32()} Hz, {s.GetProperty("channels").GetInt32()} channels"),
            report.Streams.Select(s => $"{s.Codec}, {s.SampleRate} Hz, {s.Channels} channels"));

        // Whatever the stream's own findings are, the source file has them too: the container adds none.
        string[] sourceCodes = [.. Fixtures.AnalyzeMpeg(manifest.GetProperty("input").GetProperty("file").GetString()!).Findings.Select(f => f.Code)];
        Assert.All(report.Findings, f => Assert.Contains(f.Code, sourceCodes));
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("asf.", StringComparison.Ordinal));
        TestFiles.CheckInsideParents(report.Root);
    }

    // A frame that is a media object put together from fragments in two packets is read whole: the file decodes as the one without the fragmentation does, its frames are the same in
    // number, and the frame's nodes lie in the two pieces of the file it is made of.
    [Theory]
    [InlineData("asf/ac3-object-fragmented.asf", "asf/ac3-in-asf.asf")]
    [InlineData("asf/mpeg-audio-object-fragmented.asf", "asf/mpeg-audio-in-asf.asf")]
    public void AFrameFragmentedAcrossPacketsIsReadWhole(string fragmented, string control)
    {
        (Report report, PcmHashingSink inFragments) = Decode(fragmented);
        (Report whole, PcmHashingSink inPackets) = Decode(control);

        Assert.Empty(report.Findings);
        Assert.Equal(whole.Streams.Single().Samples, report.Streams.Single().Samples);
        Assert.Equal(inPackets.Samples, inFragments.Samples);
        Assert.Equal(inPackets.Hash(), inFragments.Hash());
        TestFiles.CheckInsideParents(report.Root);
        Assert.Contains(Frames(report.Root), f => f.Ranges is { Count: 2 });
    }

    // A message that quotes where a frame in two fragments is quotes the file offset of its first byte, in the first fragment, and the finding's node starts there.
    [Fact]
    public void AMessageQuotesTheFileOffsetOfAFragmentedFramesFirstByte()
    {
        Report report = Fixtures.AnalyzeMpeg("asf/mpeg-audio-object-fragmented-cut.asf");
        Finding finding = Assert.Single(report.Findings, f => f.Code == "mpeg-audio.frame-truncated");
        Node frame = Frames(report.Root).Last(f => f.Ranges is { Count: 2 });

        Assert.Equal(frame.Ranges![0].Offset, finding.Node!.Offset);
        Assert.Contains($"0x{frame.Ranges[0].Offset:X}", finding.Message, StringComparison.Ordinal);
    }

    private static IEnumerable<Node> Frames(Node node)
    {
        if (node.Name is "Frame" or "Syncframe")
        {
            yield return node;
        }

        foreach (Node child in node.Children)
        {
            foreach (Node frame in Frames(child))
            {
                yield return frame;
            }
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheSamplesAreThoseOfTheElementaryStream(string name)
    {
        string source = Fixtures.LoadMpegManifest(name).GetProperty("input").GetProperty("file").GetString()!;
        (Report wrapped, PcmHashingSink inAsf) = Decode(name);
        (Report plain, PcmHashingSink inFile) = Decode(source);

        Assert.DoesNotContain(wrapped.Findings, f => f.Code is "decode.failed" or "parse.failed" or "decode.no-decoder");
        Assert.Equal(DecodeStatus.Decoded, wrapped.Streams.Single().Verification!.Decoding);
        Assert.Equal(inFile.Samples, inAsf.Samples);
        Assert.Equal(inFile.Hash(), inAsf.Hash());
        Assert.Equal(plain.Streams.Single().Channels, wrapped.Streams.Single().Channels);
    }

    private static Report AnalyzeBytes(byte[] data, PcmHashingSink? sink = null) =>
        Analyzer.CreateDefault().Analyze("p.asf", new MemoryStream(data), sink is null ? new AnalysisOptions() : new AnalysisOptions { Decoding = new DecodeOptions { Output = sink.Declare } });

    // The sample frames a PCM stream reports are those its media objects hold: a compressed payload's length bytes, the fragments past an object's size and the dropped
    // fragments are no samples.
    [Theory]
    [InlineData("compressed-payloads.asf", 200L)]
    [InlineData("payload-overrun.asf", 90L)]
    public void ThePcmSamplesAreCountedFromTheBytesTheMediaObjectsHold(string name, long frames)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf", name));
        var sink = new PcmHashingSink();
        Report report = AnalyzeBytes(data, sink);

        Assert.Equal(frames, report.Streams.Single().Samples);
        Assert.Equal(frames, sink.Samples);
        Assert.DoesNotContain(report.Findings, f => f.Code == "pcm.partial-sample-frame");
    }

    // The findings about damaged media objects are made whether or not a decoder walks them.
    [Theory]
    [InlineData("truncated-data.asf")]
    [InlineData("payload-overrun.asf")]
    [InlineData("length-field-short.asf")]
    public void TheDamageFindingsOfAPcmStreamDoNotWaitForTheDecoder(string name)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf", name));

        string[] analyzed = [.. AnalyzeBytes(data).Findings.Select(f => f.Code).Order(StringComparer.Ordinal)];
        string[] decoded = [.. AnalyzeBytes(data, new PcmHashingSink()).Findings.Select(f => f.Code).Where(c => !c.StartsWith("decode.", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

        Assert.Equal(decoded, analyzed);
    }

    [Theory]
    [InlineData(0x0003, "PCM (floating-point)")]
    [InlineData(0x0006, "A-law")]
    [InlineData(0x0007, "Mu-law")]
    public void TheFormatTagsOfTheLinearCodingsHaveTheNamesEveryContainerUses(int tag, string name)
    {
        Assert.Equal(name, AsfParser.FormatTagName(tag));
        Assert.Equal(name, AsfParser.CodecNameOf(tag));
    }

    // A WAVEFORMATEX of PCM that states no sample width cannot be read: a Warning, and the stream is reported from the header.
    [Fact]
    public void APcmLayoutThatCannotBeReadIsAWarningAndTheStreamComesFromTheHeader()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "asf", "minimal.asf"));
        byte[] marker = [0x01, 0x00, 0x01, 0x00, 0x40, 0x1F, 0x00, 0x00];
        int at = data.AsSpan().IndexOf(marker);
        Assert.True(at >= 0);
        data[at + 14] = 0;
        data[at + 15] = 0;

        Report report = AnalyzeBytes(data);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "asf.codec-not-analyzed");
        Assert.Equal(Severity.Warning, finding.Severity);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(8000, stream.SampleRate);
        Assert.Equal(1, stream.Channels);
    }
}
