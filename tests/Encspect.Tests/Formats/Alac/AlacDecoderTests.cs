using System.Text.Json;
using Encspect.Core;
using Encspect.Formats.Alac;
using Encspect.Formats.Alac.Decoding;
using Encspect.Host;

namespace Encspect.Tests;

// The ALAC decoder on what the builders write (uncompressed elements, whose samples are known), the walk it runs itself over a frame the parser did not store, and
// the rule that it opens only where a front end asks for the samples (ALAC stores no checksum).
public class AlacDecoderTests
{
    private sealed class CollectingSink : IPcmSink
    {
        public PcmFormat? Format { get; set; }

        public List<(long Position, int Length)> Blocks { get; } = [];

        public List<int>[] Channels { get; set; } = [];

        public void Write(PcmBlock block)
        {
            Channels = Channels.Length == block.Channels ? Channels : [.. Enumerable.Range(0, block.Channels).Select(_ => new List<int>())];
            Blocks.Add((block.Position, block.Length));
            for (int c = 0; c < block.Channels; c++)
            {
                Channels[c].AddRange(block.IntegerChannel(c).ToArray());
            }
        }

        public void End(long samples)
        {
        }
    }

    private static (Report Report, CollectingSink Sink) Decode(byte[] file)
    {
        var sink = new CollectingSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = stream => { sink.Format = stream.Format; return sink; } } };
        return (Analyzer.CreateDefault().Analyze("frames.m4a", new MemoryStream(file), options), sink);
    }

    private static int SignExtend(long value, int bits) => (int)(value << (64 - bits) >> (64 - bits));

    [Theory]
    [InlineData(1, 16)]
    [InlineData(2, 16)]
    [InlineData(1, 20)]
    [InlineData(2, 20)]
    [InlineData(1, 24)]
    [InlineData(2, 24)]
    [InlineData(1, 32)]
    [InlineData(2, 32)]
    public void UncompressedFramesDecodeToTheirSamples(int channels, int bits)
    {
        int[] counts = [64, 64, 30];
        List<byte[]> frames = [.. counts.Select((n, i) => AlacBuilder.EscapeFrame(channels, bits, n, 64, i * 11))];
        byte[] file = AlacBuilder.M4a(frames, AlacBuilder.Cookie(64, bits, channels), channels, bits, 44100, 64);

        (Report report, CollectingSink sink) = Decode(file);

        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("decode.", StringComparison.Ordinal) || f.Code is "parse.failed");
        Assert.Equal(new PcmFormat(44100, channels, bits, SampleFormat.Integer), sink.Format! with { ChannelLayout = null });
        Assert.Equal(sink.Format!.ChannelLayout, report.Streams.Single().ChannelLayout);
        Assert.Equal([(0L, 64), (64L, 64), (128L, 30)], sink.Blocks);
        int position = 0;
        for (int f = 0; f < counts.Length; f++)
        {
            for (int i = 0; i < counts[f]; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    long coded = (f * 11 + (((i * channels) + c) * 37)) & ((1L << bits) - 1);
                    Assert.Equal(SignExtend(coded, bits), sink.Channels[c][position + i]);
                }
            }

            position += counts[f];
        }

        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.Equal(DecodeStatus.Decoded, verification.Decoding);
        Assert.Equal(158, verification.DecodedSamples);
        Assert.Null(verification.IsVerified);
        Assert.Empty(verification.Checksums);
    }

    // ALAC stores no checksum, so verifying alone reads no sample: the session opens for measurements or output only.
    [Fact]
    public void VerifyingAloneDecodesNothing()
    {
        byte[] file = AlacBuilder.M4a([AlacBuilder.EscapeFrame(2, 16, 64, 64)], AlacBuilder.Cookie());
        Report report = Analyzer.CreateDefault().Analyze("frames.m4a", new MemoryStream(file), new AnalysisOptions { Decoding = new DecodeOptions() });

        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.Equal(DecodeStatus.NotRequested, verification.Decoding);
        Assert.Equal(0, verification.DecodedSamples);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("decode.", StringComparison.Ordinal));
        Assert.Null(report.Streams.Single().Measurements);
    }

    [Fact]
    public void AFrameThatCannotBeReadIsNotDecodedAndTheNextOneIsStillDecoded()
    {
        // The middle frame has an element with the tag 5 (a program config element), which ALAC defines no syntax for.
        byte[] broken = AlacBuilder.Frame((5, 3), (0, 29));
        byte[] file = AlacBuilder.M4a([AlacBuilder.EscapeFrame(2, 16, 64, 64, 0), broken, AlacBuilder.EscapeFrame(2, 16, 64, 64, 5)], AlacBuilder.Cookie());

        (Report report, CollectingSink sink) = Decode(file);

        Assert.Equal([(0L, 64), (128L, 64)], sink.Blocks);
        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.Equal(128, verification.DecodedSamples);
        Assert.Contains(report.Findings, f => f.Code == "alac.element-unknown");
        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.failed" or "parse.failed");
    }

    public static TheoryData<string> Undamaged
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string name in ((IEnumerable<object[]>)AlacFixtureTests.Names).Select(d => (string)d[0]))
            {
                JsonElement manifest = Fixtures.LoadMpegManifest(name);
                if (manifest.TryGetProperty("input", out _) && !manifest.GetProperty("findings").EnumerateArray().Any(f => f.GetProperty("code").GetString()!.StartsWith("alac.", StringComparison.Ordinal)))
                {
                    data.Add(name);
                }
            }

            return data;
        }
    }

    // A frame the parser did not store is walked by the decoder itself: the same samples come out, and the reading of the bytes is charged on top.
    [Theory]
    [MemberData(nameof(Undamaged))]
    public void AFrameTheWalkDidNotStoreDecodesToTheSameSamples(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, name));
        Report report = Fixtures.AnalyzeMpeg(name);
        JsonElement manifest = Fixtures.LoadMpegManifest(name);
        Node cookieNode = Find(report.Root, "Magic Cookie")!;
        var cookieBytes = new MappedBytes(file.AsMemory((int)cookieNode.Offset, (int)cookieNode.Length), cookieNode.Offset);
        AlacCookie cookie = AlacCookie.Read(cookieBytes.Span);
        var setup = new DecodeSetup { Stream = report.Streams.Single().Nodes[0], Channels = cookie.Channels, BitsPerSample = cookie.BitDepth, Configuration = cookieBytes };

        // The lent walk's samples and charge, through the host.
        var counting = new CountingDecoder(new AlacDecoder());
        var lentSink = new PcmHashSink();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = stream => { lentSink.Format(stream.Format); return lentSink; } } };
        new Analyzer(DefaultParsers.Tags, DefaultParsers.Formats, DefaultParsers.Codecs, [counting]).Analyze(Path.GetFileName(name), new MemoryStream(file), options);

        // The same frames, handed to the decoder as not walked.
        var directSink = new PcmHashSink();
        long units = 0;
        var context = new DecodeContext(setup, CancellationToken.None, (_, _, _, _, _) => { }, format => { directSink.Format(format); return directSink; }, u => units += u);
        IFrameDecoder decoder = new AlacDecoder().Open(context);
        long position = 0;
        long coded = 0;
        foreach (JsonElement frame in manifest.GetProperty("frames").EnumerateArray())
        {
            var bytes = new MappedBytes(file.AsMemory((int)frame.GetProperty("offset").GetInt64(), frame.GetProperty("size").GetInt32()), frame.GetProperty("offset").GetInt64());
            int samples = frame.GetProperty("samples").GetInt32();
            Assert.Equal(samples, decoder.Decode(new CodedFrame(bytes, "Frame", position, samples)));
            position += samples;
            coded += bytes.Length;
        }

        Assert.Equal(lentSink.Hash(), directSink.Hash());
        Assert.Equal(lentSink.Samples, directSink.Samples);
        Assert.Equal(counting.Units + (DecodeWork.PerCodedByte * coded), units);
    }

    private static Node? Find(Node node, string name)
    {
        if (node.Name == name)
        {
            return node;
        }

        foreach (Node child in node.Children)
        {
            if (Find(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    [Fact]
    public void ACookieTheDecoderCannotUseIsNotDecoded()
    {
        byte[] cookie = AlacBuilder.Cookie(bits: 12);
        byte[] file = AlacBuilder.M4a([AlacBuilder.EscapeFrame(2, 16, 64, 64)], cookie);

        (Report report, CollectingSink sink) = Decode(file);

        Assert.Empty(sink.Blocks);
        Assert.Equal(DecodeStatus.NotPossible, report.Streams.Single().Verification!.Decoding);
        Assert.Contains(report.Findings, f => f.Code == "decode.not-possible");
    }
}
