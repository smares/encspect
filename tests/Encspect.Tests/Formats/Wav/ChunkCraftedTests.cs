using System.Buffers.Binary;
using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The WAV and AIFF findings that no committed fixture can hold, because the tag chain they depend on needs an ID3v2.4 SEEK frame that points at
// offsets of the file, or a combination of claims. The files are built here; the codes are asserted for FindingCoverageTests.
public class ChunkCraftedTests
{
    private static Report Analyze(byte[] data, string name) => Analyzer.CreateDefault().Analyze(name, new MemoryStream(data));

    private static byte[] Syncsafe(int value) => [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];

    private static byte[] Frame(string id, byte[] data) => [.. Encoding.ASCII.GetBytes(id), .. Syncsafe(data.Length), 0, 0, .. data];

    private static byte[] Id3v24(params byte[][] frames) => [.. "ID3"u8, 4, 0, 0, .. Syncsafe(frames.Sum(f => f.Length)), .. frames.SelectMany(f => f)];

    private static byte[] Id3v23(string id, string text)
    {
        byte[] payload = [0, .. Encoding.Latin1.GetBytes(text)];
        byte[] size = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(size, payload.Length);
        byte[] frame = [.. Encoding.ASCII.GetBytes(id), .. size, 0, 0, .. payload];
        return [.. "ID3"u8, 3, 0, 0, .. Syncsafe(frame.Length), .. frame];
    }

    private static byte[] Chunk(string id, byte[] body, bool isBigEndian = false)
    {
        byte[] size = new byte[4];
        if (isBigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)body.Length);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
        }

        return [.. Encoding.ASCII.GetBytes(id), .. size, .. body, .. (body.Length % 2 == 1 ? new byte[] { 0 } : [])];
    }

    private static byte[] Le(params int[] words) => [.. words.SelectMany(w => new[] { (byte)w, (byte)(w >> 8) })];

    // A WAVE file's PCM format chunk: 16 bits, 2 channels, 44100 Hz.
    private static byte[] Format() => Chunk("fmt ", [.. Le(1, 2), 0x44, 0xAC, 0, 0, 0x10, 0xB1, 0x02, 0, .. Le(4, 16)]);

    private static byte[] Riff(uint size, params byte[][] chunks) => [.. "RIFF"u8, .. BitConverter.GetBytes(size), .. "WAVE"u8, .. chunks.SelectMany(c => c)];

    private static byte[] Form(uint size, params byte[][] chunks)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, size);
        return [.. "FORM"u8, .. header, .. "AIFF"u8, .. chunks.SelectMany(c => c)];
    }

    // 2 channels, 50 sample frames, 16 bits, 44100 Hz.
    private static byte[] Common() => Chunk("COMM", [0, 2, 0, 0, 0, 50, 0, 16, 0x40, 0x0E, 0xAC, 0x44, 0, 0, 0, 0, 0, 0], true);

    private static byte[] Sound() => Chunk("SSND", [.. new byte[8], .. new byte[200]], true);

    [Fact]
    public void AnEmptyDataChunkOfARoutedFormatHasNoAudio()
    {
        byte[] format = Chunk("fmt ", [.. Le(0x55, 2), 0x44, 0xAC, 0, 0, 0x80, 0x3E, 0, 0, .. Le(1, 0, 12), .. Le(1, 2, 0, 417, 1, 1393)]);
        byte[] data = Riff(0, format, Chunk("fact", [0, 0, 0, 0]), Chunk("data", []));
        data[4] = (byte)(data.Length - 8);

        Report report = Analyze(data, "empty.wav");

        Assert.Contains(report.Findings, f => f.Code == "wav.no-audio");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    [Fact]
    public void ASoftwareChunkAndAnId3TagThatNameOtherWritersAreAConflict()
    {
        byte[] list = Chunk("LIST", [.. "INFO"u8, .. Chunk("ISFT", [.. "Fixture Tagger 1.2"u8, 0])]);
        byte[] data = Riff(0, Format(), list, Chunk("data", new byte[200]), Chunk("id3 ", Id3v23("TSSE", "Lavf60.16.100")));
        data[4] = (byte)(data.Length - 8);
        data[5] = (byte)((data.Length - 8) >> 8);

        Report report = Analyze(data, "claims.wav");

        Assert.Single(report.Findings, f => f.Code == "wav.writer-claim-differs");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    // A tag that an ID3v2.4 SEEK frame points to is in the data only where the file states no end of its own (the host owns nothing inside a
    // declared one), so the sizes are the placeholders of a writer that could not seek back.
    [Fact]
    public void BytesAfterATagInsideAWaveFileWithoutASizeAreFlagged()
    {
        byte[] inner = [.. Format(), .. Chunk("data", new byte[200])];
        byte[] second = Id3v24(Frame("TIT2", [3, .. "Second tag"u8]));
        byte[] first = Id3v24(Frame("SEEK", [0, 0, (byte)((12 + inner.Length) >> 8), (byte)(12 + inner.Length)]));
        byte[] data = [.. first, .. Riff(uint.MaxValue, inner), .. second, .. new byte[20]];

        Report report = Analyze(data, "tags.wav");

        Assert.Contains(report.Findings, f => f.Code == "wav.data-after-tag");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    [Fact]
    public void BytesAfterATagInsideAnAiffFileWithoutASizeAreFlagged()
    {
        byte[] inner = [.. Common(), .. Sound()];
        byte[] second = Id3v24(Frame("TIT2", [3, .. "Second tag"u8]));
        byte[] first = Id3v24(Frame("SEEK", [0, 0, (byte)((12 + inner.Length) >> 8), (byte)(12 + inner.Length)]));
        byte[] data = [.. first, .. Form(uint.MaxValue, inner), .. second, .. new byte[20]];

        Report report = Analyze(data, "tags.aiff");

        Assert.Contains(report.Findings, f => f.Code == "aiff.data-after-tag");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    [Fact]
    public void AFormatChunkThatDisagreesWithTheRoutedStreamIsFlagged()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "wav/external-mp3-in-wav-stereo.wav"));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(data, 12, 4));
        data[22] = 1;

        Report report = Analyze(data, "channels.wav");
        Assert.Single(report.Findings, f => f.Code == "wav.format-differs-from-stream");

        data[22] = 2;
        data[24] ^= 0x01;
        Assert.Single(Analyze(data, "rate.wav").Findings, f => f.Code == "wav.format-differs-from-stream");
    }

    private static int CountNodes(Node root)
    {
        int count = 0;
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            count++;
            foreach (Node child in node.Children)
            {
                pending.Push(child);
            }
        }

        return count;
    }

    // The tree of a WAVE or AIFF file holds at most 250,000 nodes, as the trees of MP4 and Matroska do; the rest of the level is one node and one
    // finding says so, without the findings about the chunks that were not read.
    [Fact]
    public void AWaveFileOfNestedListsIsCutAtTheNodeBudget()
    {
        Report report = Analyze(LongInputs.WavNestedLists(300, 600), "nested.wav");

        Assert.InRange(CountNodes(report.Root), 250_000, 260_000);
        Finding finding = Assert.Single(report.Findings, f => f.Code == "wav.node-limit");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains(Fixtures.Resolve(report.Root, "RIFF/List Chunk"), l => l.Children.Any(c => c.Name == "Chunks Not Shown"));
        Assert.DoesNotContain(report.Findings, f => f.Code is "wav.no-data-chunk" or "wav.no-format-chunk" or "parse.failed");
    }

    [Fact]
    public void AnAiffFileOfRepeatedMarkerChunksIsCutAtTheNodeBudget()
    {
        Report report = Analyze(LongInputs.AiffMarkerChunks(200, 1024), "markers.aiff");

        Assert.InRange(CountNodes(report.Root), 250_000, 270_000);
        Assert.Equal(Severity.Warning, Assert.Single(report.Findings, f => f.Code == "aiff.node-limit").Severity);
        Assert.Contains(report.Root.Children.Single().Children, c => c.Name == "Chunks Not Shown");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    // The names of the cue points are kept to find a name twice and a label for a cue point the file does not hold, up to 65,536 of them; a file with
    // more is not checked, so a label of a cue point beyond them is not called a label without one.
    [Fact]
    public void LabelsAreNotCheckedAgainstCuePointsBeyondTheNamesKept()
    {
        byte[] label = Chunk("LIST", [.. "adtl"u8, .. Chunk("labl", [0x60, 0x11, 0x01, 0, .. "x "u8])]);
        byte[] many = [.. Riff(0, Format(), LongInputs.WavCueChunk(70_000), label, Chunk("data", new byte[16]))];
        BitConverter.GetBytes(many.Length - 8).CopyTo(many, 4);
        Assert.DoesNotContain(Analyze(many, "many.wav").Findings, f => f.Code == "wav.label-without-cue");

        byte[] few = [.. Riff(0, Format(), LongInputs.WavCueChunk(100), label, Chunk("data", new byte[16]))];
        BitConverter.GetBytes(few.Length - 8).CopyTo(few, 4);
        Assert.Single(Analyze(few, "few.wav").Findings, f => f.Code == "wav.label-without-cue");
    }

    // The DTS stream of a DTS music CD is in the 16-bit stereo samples of a little-endian file; the same bytes behind another format chunk (mono,
    // 24 bits, a RIFX file's big-endian samples) are samples of that format.
    [Theory]
    [InlineData(1, 16, false)]
    [InlineData(2, 24, false)]
    [InlineData(2, 16, true)]
    public void ADtsStreamIsOnlyLookedForInSixteenBitStereoPcm(int channels, int bits, bool isBigEndian)
    {
        byte[] dts = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "dts/core-14le.dts"));
        byte[] format = isBigEndian
            ? Chunk("fmt ", [0, 1, 0, (byte)channels, 0, 0, 0xAC, 0x44, 0, 0x02, 0xB1, 0x10, 0, (byte)(channels * bits / 8), 0, (byte)bits], true)
            : Chunk("fmt ", [.. Le(1, channels), 0x44, 0xAC, 0, 0, 0x10, 0xB1, 0x02, 0, .. Le(channels * bits / 8, bits)]);
        byte[] data = Riff(0, format, Chunk("data", dts, isBigEndian));
        if (isBigEndian)
        {
            data[3] = (byte)'X';
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), (uint)(data.Length - 8));
        }
        else
        {
            BitConverter.GetBytes(data.Length - 8).CopyTo(data, 4);
        }

        Report report = Analyze(data, "carrier.wav");

        Assert.DoesNotContain(report.Findings, f => f.Code == "wav.dts-in-pcm");
        Assert.Equal("PCM", Assert.Single(report.Streams).Codec);
    }

    // A RIFF file that is not a WAVE file, and a FORM chunk that is not AIFF, belong to no parser here.
    [Fact]
    public void OtherRiffAndFormTypesAreNotClaimed()
    {
        byte[] avi = [.. "RIFF"u8, 20, 0, 0, 0, .. "AVI "u8, .. Chunk("LIST", [.. "hdrl"u8, 0, 0, 0, 0])];
        byte[] iff = [.. "FORM"u8, 0, 0, 0, 12, .. "8SVX"u8, .. Chunk("VHDR", new byte[4], true)];

        foreach ((string name, byte[] bytes) in new[] { ("a.avi", avi), ("a.8svx", iff) })
        {
            Report report = Analyze(bytes, name);
            Assert.DoesNotContain(report.Root.Children, c => c.Name is "RIFF" or "FORM");
            Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        }
    }

    // MPEG audio data in a WAVE file is read by the MPEG parser, but the file is a WAV file: the RIFF header with its chunks is class 100, an
    // MPEG chain further in is not.
    [Fact]
    public void AWaveFileHoldingMpegAudioIsWav()
    {
        Report report = Fixtures.AnalyzeMpeg("wav/external-mp3-in-wav-stereo.wav");

        Assert.Equal("WAV", report.Root.Value?.ToString());
        Assert.Null(report.Detection);
    }

    // A WavPack file with a RIFF wrapper starts with the block header, and MPEG audio in an "RMP3" RIFF form is the MPEG parser's.
    [Fact]
    public void WrappedFormatsAreStillDetectedAsTheirOwn()
    {
        byte[] mp3 = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "mpeg/external-lame-cbr128-joint.mp3"));
        byte[] wrapped = [.. "RIFF"u8, .. BitConverter.GetBytes(mp3.Length + 12), .. "RMP3"u8, .. "data"u8, .. BitConverter.GetBytes(mp3.Length), .. mp3];

        Report report = Analyze(wrapped, "wrapped.mp3");

        Assert.NotEqual("WAV", report.Root.Value?.ToString());
        Assert.Contains(report.Streams, s => s.Codec.StartsWith("MPEG", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.NotEqual("WAV", Fixtures.AnalyzeMpeg("wavpack/external-lossless-16-stereo.wv").Root.Value?.ToString());
    }
}
