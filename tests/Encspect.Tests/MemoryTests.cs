using System.Runtime;
using System.Text;
using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Formats.WavPack;
using Encspect.Host;
using Xunit.Abstractions;

namespace Encspect.Tests;

[CollectionDefinition(nameof(MemoryTests), DisableParallelization = true)]
public class MemoryCollection;

// A long file's report keeps compact records of its frames, and the passes over them never hold
// every frame's decoded side information or nodes at once (plan, "Large inputs"). Measured over a
// stream built in memory, alone, since the heap is the whole process's: what the report keeps,
// and the most the analysis holds live at once (Measure). The peak bounds are about twice what
// linux-x64 measures, so they hold on other platforms, and below what keeping each packet's bytes
// costs: with that regression the Speex and MP4 peaks rose from about 35 and 56 bytes per packet
// to 125 and 161. The MPEG bounds are far below what the parser took when it kept an array per
// frame and listed every frame for each pass (490 bytes kept and 2.8 KB of heap).
[Collection(nameof(MemoryTests))]
public class MemoryTests(ITestOutputHelper output)
{
    private const int Frames = 100_000;

    private const int SampleInterval = 2;

    [Fact]
    public void ManyFramesStayCompact()
    {
        byte[] data = Layer3Frames(Frames);

        (Report report, long kept, long peak) = Measure("memory.mp3", data, data[..(104 * 100)]);

        Assert.Equal(NodeValue.Integer(Frames, "frames", "frame"), report.Root.Children.Single(c => c.Name == "MPEG Audio").Value);
        output.WriteLine($"{kept / Frames} bytes kept and {peak / Frames} bytes peak per frame");
        Assert.True(kept < Frames * 250L, $"The report keeps {kept / Frames} bytes per frame.");
        Assert.True(peak < Frames * 350L, $"The live heap grew by {peak / Frames} bytes per frame.");

        // The coding series of the stream (docs/design/coding-graphs.md, "Memory budget"): a Layer III stereo frame adds at most 23 bytes,
        // channel modes 1, stereo coding 1, joint stereo modes 1, block usage 4 and channel bit share 16 (a value and its own x); block
        // sizes is a grouping and costs its map. This frame is stereo, not joint stereo, and has a share, so its x values are the bitrate series'.
        StreamInfo stream = Assert.Single(report.Streams);
        CheckCodingBytes(stream, categoryBytesPerFrame: 6, numericBytesPerFrame: 8);
        output.WriteLine($"{stream.CategorySeries.Sum(c => c.StorageBytes) / Frames} bytes of category series per frame");
        GC.KeepAlive(report);
    }

    // The same for MPEG-1 Layer II stereo frames of 417 bytes, whose series are channel modes 1, stereo coding 1, intensity stereo bounds
    // 1, coded bandwidth 8 and channel bit share 16: 27 bytes per frame at most.
    [Fact]
    public void ManyLayer2FramesStayCompact()
    {
        byte[] data = Layer2Frames(Frames);

        (Report report, long kept, long peak) = Measure("memory.mp2", data, data[..(417 * 100)]);

        Assert.Equal(NodeValue.Integer(Frames, "frames", "frame"), report.Root.Children.Single(c => c.Name == "MPEG Audio").Value);
        output.WriteLine($"{kept / Frames} bytes kept and {peak / Frames} bytes peak per frame");
        Assert.True(kept < Frames * 250L, $"The report keeps {kept / Frames} bytes per frame.");
        Assert.True(peak < Frames * 350L, $"The live heap grew by {peak / Frames} bytes per frame.");
        StreamInfo stream = Assert.Single(report.Streams);
        CheckCodingBytes(stream, categoryBytesPerFrame: 2, numericBytesPerFrame: 16);
        output.WriteLine($"{stream.CategorySeries.Sum(c => c.StorageBytes) / Frames} bytes of category series per frame");
        GC.KeepAlive(report);
    }

    // What the stream's coding series keep, from their own sizes: the codes (and the sparse index) of the category series, and 8 bytes
    // for each value of the numeric series that the report did not keep already; a series whose frames all have a value shares the
    // bitrate series' x values.
    private static void CheckCodingBytes(StreamInfo stream, int categoryBytesPerFrame, int numericBytesPerFrame)
    {
        long index = stream.CategorySeries.Count * 4L * ((Frames / 1024) + 1);
        long categories = stream.CategorySeries.Sum(c => c.StorageBytes);
        Assert.True(categories <= (Frames * (long)categoryBytesPerFrame) + index, $"The category series keep {categories / (double)Frames} bytes per frame.");
        Series bitrate = stream.Series.Single(s => s.Name == "Bitrate");
        long numeric = 0;
        foreach (Series series in stream.Series.Where(s => s.Name is "Coded Bandwidth" or "Channel Bit Share"))
        {
            numeric += series.Values.Length * 8L;
            if (series.Values.Length == Frames)
            {
                Assert.True(series.X.Span.SequenceEqual(bitrate.X.Span), $"{series.Name} has its own x values although every frame has a value.");
                Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(series.X, out ArraySegment<double> own)
                    && System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bitrate.X, out ArraySegment<double> shared) && ReferenceEquals(own.Array, shared.Array),
                    $"{series.Name} copies the bitrate series' x values.");
            }
        }

        Assert.True(numeric <= Frames * (long)numericBytesPerFrame, $"The numeric coding series keep {numeric / (double)Frames} bytes per frame.");
    }

    // An MP4 file keeps a compact record per access unit, the arrays of its graphs and each raw data
    // block without its Huffman-coded data, and reads its sample tables into arrays of the entries
    // the boxes hold, never the samples' bytes at once (plan, "Large inputs"): 60 bytes kept per
    // sample on linux-x64 and a peak of 100, and with the coding series (13 bytes a stereo sample) 74 and 113. The bounds leave room for a platform that measures
    // nearly twice that, as macOS on arm64 did for the Ogg records, and stay below what keeping a
    // sample's node or bytes would cost.
    [Fact]
    public void ManySamplesStayCompact()
    {
        const int samples = 100_000;

        (Report report, long kept, long peak) = Measure("samples.m4a", Mp4Samples(samples));

        Assert.Equal(samples, Assert.Single(report.Streams).Samples / 1024);
        output.WriteLine($"{kept / samples} bytes kept and {peak / samples} bytes peak per sample");
        Assert.True(kept < samples * 110L, $"The report keeps {kept / samples} bytes per sample.");
        Assert.True(peak < samples * 180L, $"The live heap grew by {peak / samples} bytes per sample.");
        GC.KeepAlive(report);
    }

    // Chunks that start at the same bytes, with a constant sample size that no table backs: 4,000 chunks of 10,000 samples of 100 bytes
    // from 60 KB of media data were 40 million access units for the codec (2.97 GB of heap for a quarter of that). Samples of a valid file
    // do not overlap, so the walk over the chunks stops where the samples add up to more bytes than the file holds.
    [Fact]
    public void OverlappingChunksOfConstantSizedSamplesStayBounded()
    {
        const int chunks = 4_000;
        const int perChunk = 10_000;
        byte[] unit = Mp4Builder.AacUnit();
        byte[] data = Mp4Builder.FileWithMoov(at => Mp4Builder.Trak(1, Mp4Builder.AacEntry(), Mp4Builder.Stts((long)chunks * perChunk, 1024), Mp4Builder.Stsc(perChunk),
            Mp4Builder.ConstantSizes(unit.Length, (long)chunks * perChunk), Mp4Builder.ChunkOffsets(Enumerable.Repeat(at, chunks))), [.. Enumerable.Repeat(unit, 600).SelectMany(u => u)]);

        (Report report, long kept, long peak) = Measure("overlap.m4a", data);

        output.WriteLine($"{data.Length / 1000} KB: {kept / 1000} KB kept and {peak / 1000} KB peak");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.Contains(report.Findings, f => f.Code == "mp4.samples-overlap");
        Assert.True(Assert.Single(report.Streams).Samples <= 1024L * (data.Length / unit.Length), "More access units than the file has bytes for.");
        Assert.True(peak < 20_000_000, $"The live heap grew by {peak / 1_000_000} MB.");
        GC.KeepAlive(report);
    }

    // A hundred tracks whose track fragments each declare 4 billion samples in a run that states no sizes, over a media data box of 1 MB: with a
    // default size of 0 the samples are counted and none is kept (4 million 16-byte records per track, 6.8 GB for the file); with a default size of 1
    // the bytes they place draw on one budget for the file, which its length bounds, so the records are no more than the file has bytes for
    // (16 MB), where each track kept a million of them (1.6 GB).
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void HugeRunsOfManyTracksStayBounded(uint defaultSize)
    {
        const int tracks = 100;
        byte[] Trex(int id) => Mp4Builder.FullBox("trex", Mp4Builder.U32(id), Mp4Builder.U32(1), Mp4Builder.U32(1024), Mp4Builder.U32(defaultSize), Mp4Builder.U32(0));
        byte[] Traf(int id, long baseOffset) => Mp4Builder.Box("traf", Mp4Builder.Box("tfhd", [0, 0, 0, 1], Mp4Builder.U32(id), Mp4Builder.U32(0), Mp4Builder.U32(baseOffset)),
            Mp4Builder.FullBox("trun", Mp4Builder.U32(0xFFFFFFFF)));
        byte[] Empty(string type) => Mp4Builder.FullBox(type, Mp4Builder.U32(0));
        byte[] moov = Mp4Builder.Box("moov", Mp4Builder.Mvhd(),
            Mp4Builder.Join([.. Enumerable.Range(1, tracks).Select(i => Mp4Builder.Trak(i, Mp4Builder.AlacEntry(), Empty("stts"), Empty("stsc"), Mp4Builder.FullBox("stsz", new byte[8]), Empty("stco")))]),
            Mp4Builder.Box("mvex", [.. Enumerable.Range(1, tracks).Select(Trex)]));
        byte[] Moof(long baseOffset) => Mp4Builder.Box("moof", [Mp4Builder.FullBox("mfhd", Mp4Builder.U32(1)), .. Enumerable.Range(1, tracks).Select(i => Traf(i, baseOffset))]);
        long payload = Mp4Builder.Ftyp().Length + moov.Length + Moof(0).Length + 8;
        byte[] data = Mp4Builder.Join(Mp4Builder.Ftyp(), moov, Moof(payload), Mp4Builder.Box("mdat", new byte[1_000_000]));

        (Report report, long kept, long peak) = Measure("runs.mp4", data);

        output.WriteLine($"{data.Length / 1000} KB: {kept / 1000} KB kept and {peak / 1000} KB peak");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.Contains(report.Findings, f => f.Code == (defaultSize == 0 ? "mp4.empty-samples" : "mp4.samples-overlap"));
        Assert.True(peak < 40_000_000, $"The live heap grew by {peak / 1_000_000} MB.");
        GC.KeepAlive(report);
    }

    // The nodes of a tree are bounded: a million empty boxes (8 MB) cost a node with two fields each, an edit list of two million empty edits (24 MB)
    // a node with three fields for every one it did not list (1.87 GB), a chan box of a million channel descriptions the same with 20 bytes each, and
    // a movie fragment of 5,000 track fragments with a run of 1,000 samples each (20 MB) a node with fields for each of 1,000 samples in every run
    // (2.36 GB). Past the budget the boxes are read into compact records and make no nodes.
    [Fact]
    public void BoxesEditsAndChannelDescriptionsPastTheirLimitsStayBounded()
    {
        byte[] boxes = Mp4Builder.Join(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd()), Mp4Builder.Join([.. Enumerable.Repeat(Mp4Builder.Box("free"), 1_000_000)]));
        Bounded("boxes.m4a", boxes, 120_000_000, "mp4.node-limit");

        byte[] edit = Mp4Builder.Join(Mp4Builder.U32(0), Mp4Builder.U32(0xFFFFFFFF), Mp4Builder.U32(0x10000));
        byte[] edits = Mp4Builder.Join(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd(),
            Mp4Builder.Box("trak", Mp4Builder.Box("edts", Mp4Builder.FullBox("elst", Mp4Builder.U32(2_000_000), Mp4Builder.Join([.. Enumerable.Repeat(edit, 2_000_000)]))))));
        Bounded("edits.m4a", edits, 150_000_000, "mp4.none", expectFinding: false);

        byte[] description = Mp4Builder.Join(Mp4Builder.U32(1), Mp4Builder.U32(0), new byte[12]);
        byte[] chan = Mp4Builder.FullBox("chan", Mp4Builder.U32(0), Mp4Builder.U32(0), Mp4Builder.U32(1_000_000), Mp4Builder.Join([.. Enumerable.Repeat(description, 1_000_000)]));
        byte[] channels = Mp4Builder.Join(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd(),
            Mp4Builder.Trak(1, Mp4Builder.AlacEntry(chan), Mp4Builder.Stts(0, 0), Mp4Builder.Stsc(1), Mp4Builder.ConstantSizes(0, 0), Mp4Builder.ChunkOffsets([]))));
        Bounded("channels.m4a", channels, 20_000_000, "mp4.none", expectFinding: false);

        byte[] sizes = Mp4Builder.Join([.. Enumerable.Range(0, 1000).Select(_ => Mp4Builder.U32(10))]);
        byte[] Traf(int id) => Mp4Builder.Box("traf", Mp4Builder.Box("tfhd", [0, 2, 0, 0], Mp4Builder.U32(id)), Mp4Builder.Box("trun", [0, 0, 2, 0], Mp4Builder.U32(1000), sizes));
        byte[] runs = Mp4Builder.Join(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd()), Mp4Builder.Box("moof", [Mp4Builder.FullBox("mfhd", Mp4Builder.U32(1)), .. Enumerable.Range(1, 5000).Select(Traf)]));
        Bounded("runs.mp4", runs, 120_000_000, "mp4.node-limit");
    }

    // A valid fragmented file with more fragments than the node budget holds (20,000 of one AAC access unit each, as ffmpeg's frag_every_frame
    // writes them, 5 MB) is read in full: the boxes past the budget are read into records and make no nodes, so the stream has all its samples and the
    // checks of their boxes do not report boxes missing that the tree does not hold.
    [Fact]
    public void ValidFragmentsPastTheNodeBudgetAreReadInFull()
    {
        const int fragments = 20_000;
        byte[] unit = Mp4Builder.AacUnit();
        byte[] moov = Mp4Builder.Box("moov", Mp4Builder.Mvhd(), Mp4Builder.Trak(1, Mp4Builder.AacEntry(), Mp4Builder.EmptyTables()),
            Mp4Builder.Box("mvex", Mp4Builder.FullBox("trex", Mp4Builder.U32(1), Mp4Builder.U32(1), Mp4Builder.U32(1024), Mp4Builder.U32(unit.Length), Mp4Builder.U32(0))));
        byte[] Moof(int sequence, long dataOffset) => Mp4Builder.Box("moof", Mp4Builder.FullBox("mfhd", Mp4Builder.U32(sequence)),
            Mp4Builder.Box("traf", Mp4Builder.Box("tfhd", [0, 2, 0, 0], Mp4Builder.U32(1)), Mp4Builder.FullBox("tfdt", Mp4Builder.U32(sequence * 1024L)),
                Mp4Builder.Box("trun", [0, 0, 2, 1], Mp4Builder.U32(1), Mp4Builder.U32(dataOffset), Mp4Builder.U32(unit.Length))));
        long moofLength = Moof(0, 0).Length;
        byte[] data = Mp4Builder.Join(Mp4Builder.Ftyp(), moov, Mp4Builder.Join([.. Enumerable.Range(0, fragments).Select(i => Mp4Builder.Join(Moof(i + 1, moofLength + 8), Mp4Builder.Box("mdat", unit)))]));

        Report report = Analyzer.CreateDefault().Analyze("fragments.mp4", new MemoryStream(data));

        Assert.Contains(report.Findings, f => f.Code == "mp4.node-limit");
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "mp4.sample-outside-mdat" or "mp4.sample-outside-file" || f.Code.StartsWith("mp4.missing-", StringComparison.Ordinal));
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(fragments * 1024L, stream.Samples);
        Assert.Equal(fragments * 1024 / 44100.0, stream.Duration, 3);
    }

    // The evidence that samples are encrypted past the node budget counts: a track whose tenc box says that nothing is protected, 110,000 free boxes (more than the 100,000 boxes an earlier limit allowed)
    // (more nodes than the budget holds) and then a movie fragment with a senc box. Left unread, the box left the ciphertext to the AAC parser.
    [Fact]
    public void ProtectionEvidencePastTheNodeBudgetCounts()
    {
        byte[] moov = Mp4Builder.Box("moov", Mp4Builder.Mvhd(), Mp4Builder.Trak(1, Mp4Builder.AacEntry("enca", Mp4Builder.ClearSinf()), Mp4Builder.EmptyTables()),
            Mp4Builder.Box("mvex", Mp4Builder.FullBox("trex", Mp4Builder.U32(1), Mp4Builder.U32(1), Mp4Builder.U32(1024), Mp4Builder.U32(100), Mp4Builder.U32(0))));
        byte[] Moof(long dataOffset) => Mp4Builder.Box("moof", Mp4Builder.FullBox("mfhd", Mp4Builder.U32(1)),
            Mp4Builder.Box("traf", Mp4Builder.Box("tfhd", [0, 2, 0, 0], Mp4Builder.U32(1)), Mp4Builder.FullBox("senc", Mp4Builder.U32(1), new byte[8]),
                Mp4Builder.Box("trun", [0, 0, 2, 1], Mp4Builder.U32(1), Mp4Builder.U32(dataOffset), Mp4Builder.U32(100))));
        byte[] data = Mp4Builder.Join(Mp4Builder.Ftyp(), moov, Mp4Builder.Join([.. Enumerable.Repeat(Mp4Builder.Box("free"), 110_000)]), Moof(Moof(0).Length + 8), Mp4Builder.Box("mdat", new byte[100]));

        Report report = Analyzer.CreateDefault().Analyze("evidence.m4a", new MemoryStream(data));

        Assert.Contains(report.Findings, f => f.Code == "mp4.node-limit");
        Assert.Contains(report.Findings, f => f.Code == "mp4.samples-encrypted");
        Assert.DoesNotContain(report.Findings, f => f.Code == "mp4.protected-samples-clear");
        Assert.True(Assert.Single(report.Streams).Protection is { IsEncrypted: true });
    }

    // An ADTS stream keeps a record per frame and, per raw data block, what its nodes are rebuilt
    // from: the side information without the Huffman-coded scalefactors and spectral data
    // (UnitStore). Measured over the frames of ffmpeg's encoder at 96 kbit/s, repeated: 116 bytes
    // kept per frame on linux-x64 and a peak of 133, of the 281 a frame holds; the coding series (13 bytes a stereo frame) make it 126
    // and 140. The bounds leave room
    // for a platform that measures nearly twice that and stay below what keeping the frames' bytes
    // would cost.
    [Fact]
    public void ManyAdtsFramesStayCompact()
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "ffmpeg-adts.aac"));
        var stream = new MemoryStream();
        int frames = 0;
        while (frames < 100_000)
        {
            for (int at = 0; at + 7 <= source.Length && frames < 100_000; frames++)
            {
                int length = ((source[at + 3] & 3) << 11) | (source[at + 4] << 3) | (source[at + 5] >> 5);
                stream.Write(source, at, length);
                at += length;
            }
        }

        (Report report, long kept, long peak) = Measure("frames.aac", stream.ToArray());

        Assert.Equal(frames * 1024L, Assert.Single(report.Streams).Samples);
        output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame of {stream.Length / frames} bytes");
        Assert.True(kept < frames * 220L, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * 250L, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    // The coding series of an AAC stream (docs/design/coding-graphs.md, "Memory budget") are recorded while the raw data blocks are read:
    // for a stereo access unit the window sequences of its two channel streams and a count byte, the stereo coding of its pair and a count
    // byte (5 bytes), and the coded bandwidth, which the report keeps as a value of 8 bytes (13 in all). The bound is on the growth between
    // two counts of frames, so that what is kept whatever the count (pools, the records of the first frames) does not count: the series'
    // own bytes grow by at most their budget, and the whole report and the heap the analysis holds above it by less than the bounds of
    // ManyAdtsFramesStayCompact, which stay as they were.
    [Fact]
    public void TheCodingSeriesOfFurtherAdtsFramesStayWithinTheirBudget()
    {
        const int fewer = 50_000;
        const int frames = 100_000;
        byte[] warmUp = AdtsFrames(1_000);

        (Report few, long fewerKept) = MeasureKept("few.aac", AdtsFrames(fewer), warmUp);
        (Report many, long kept) = MeasureKept("many.aac", AdtsFrames(frames), warmUp);

        CheckCodingGrowth(few, many, fewer, frames, categoryBytes: 5, numericBytes: 8);
        double keptPerFrame = (kept - fewerKept) / (double)(frames - fewer);
        output.WriteLine($"{Hundredths(keptPerFrame)} bytes kept per further frame");
        Assert.True(keptPerFrame < 220, $"The report keeps {keptPerFrame:F2} bytes per further frame.");
        GC.KeepAlive(few);
        GC.KeepAlive(many);
    }

    // The same for the access units of an MP4 file, whose bounds are the lower ones of ManySamplesStayCompact.
    [Fact]
    public void TheCodingSeriesOfFurtherMp4SamplesStayWithinTheirBudget()
    {
        const int fewer = 50_000;
        const int samples = 100_000;
        byte[] warmUp = Mp4Samples(1_000);

        (Report few, long fewerKept) = MeasureKept("few.m4a", Mp4Samples(fewer), warmUp);
        (Report many, long kept) = MeasureKept("many.m4a", Mp4Samples(samples), warmUp);

        CheckCodingGrowth(few, many, fewer, samples, categoryBytes: 5, numericBytes: 8);
        double keptPerSample = (kept - fewerKept) / (double)(samples - fewer);
        output.WriteLine($"{Hundredths(keptPerSample)} bytes kept per further sample");
        Assert.True(keptPerSample < 110, $"The report keeps {keptPerSample:F2} bytes per further sample.");
        GC.KeepAlive(few);
        GC.KeepAlive(many);
    }

    // What the further units add to the coding series, from the series' own sizes: the codes and count bytes of the category series and 8 bytes
    // for each point of the numeric series, which shares the bitrate series' x values.
    private static void CheckCodingGrowth(Report few, Report many, int fewer, int units, int categoryBytes, int numericBytes)
    {
        StreamInfo small = Assert.Single(few.Streams);
        StreamInfo large = Assert.Single(many.Streams);
        Assert.Equal(["Window Sequences", "Block Sizes", "Stereo Coding"], large.CategorySeries.Select(c => c.Name));
        long grown = large.CategorySeries.Sum(c => c.StorageBytes) - small.CategorySeries.Sum(c => c.StorageBytes);
        long index = large.CategorySeries.Count * 4L * ((units - fewer) / 1024 + 1);
        Assert.True(grown <= (units - fewer) * (long)categoryBytes + index, $"The category series grow by {grown / (double)(units - fewer):F3} bytes per further unit.");

        Series bandwidth = large.Series.Single(s => s.Name == "Coded Bandwidth");
        Assert.Equal(units, bandwidth.Values.Length);
        long numeric = BackingBytes(bandwidth.Values) - BackingBytes(small.Series.Single(s => s.Name == "Coded Bandwidth").Values);
        Assert.True(numeric <= (units - fewer) * (long)numericBytes, $"The coded bandwidth grows by {numeric / (double)(units - fewer):F2} bytes per further unit.");
        Series bitrate = large.Series.Single(s => s.Name == "Bitrate");
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bandwidth.X, out ArraySegment<double> own)
            && System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bitrate.X, out ArraySegment<double> shared) && ReferenceEquals(own.Array, shared.Array),
            "The coded bandwidth copies the bitrate series' x values although every unit has a value.");
    }

    // The bytes of the array behind a series, not only of the part it shows, so a slack kept at the end counts.
    private static long BackingBytes(ReadOnlyMemory<double> values) =>
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(values, out ArraySegment<double> segment) ? segment.Array!.Length * 8L : values.Length * 8L;

    // Frames of ffmpeg's encoder at 96 kbit/s, repeated.
    private static byte[] AdtsFrames(int count)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "ffmpeg-adts.aac"));
        var stream = new MemoryStream();
        int frames = 0;
        while (frames < count)
        {
            for (int at = 0; at + 7 <= source.Length && frames < count; frames++)
            {
                int length = ((source[at + 3] & 3) << 11) | (source[at + 4] << 3) | (source[at + 5] >> 5);
                stream.Write(source, at, length);
                at += length;
            }
        }

        return stream.ToArray();
    }

    // An Ogg stream of a codec no parser reads keeps a record and a segment table per page, and the
    // scan never holds the pages' nodes or data (56 bytes kept per page and a few more for its
    // segment table and place in its stream, 62 in all on linux-x64, where macOS on arm64
    // measured 120 before the measurement compacted the heap, and a peak of 65 to 85; the bounds
    // leave room for both and stay below what keeping a page's node or bytes would cost).
    [Fact]
    public void ManyPagesStayCompact()
    {
        const int pages = 100_000;
        var builder = new OggBuilder().Page(7, 0, 2, "unknown codec"u8.ToArray());
        byte[] packet = new byte[20];
        for (int i = 1; i < pages; i++)
        {
            builder.Page(7, i, i == pages - 1 ? (byte)4 : (byte)0, packet);
        }

        (Report report, long kept, long peak) = Measure("pages.ogg", builder.Bytes);

        Assert.Equal(NodeValue.Integer(pages, "pages", "page"), report.Root.Children.Single().Value);
        output.WriteLine($"{kept / pages} bytes kept and {peak / pages} bytes peak per page");
        Assert.True(kept < pages * 150L, $"The report keeps {kept / pages} bytes per page.");
        Assert.True(peak < pages * 170L, $"The live heap grew by {peak / pages} bytes per page.");
        GC.KeepAlive(report);
    }

    // A Speex stream keeps an 8-byte record per packet, 4 bytes per frame and its per-packet series
    // (16 bytes, whose x values are also the packets' offsets), plus its share of the page records:
    // 30 bytes in all on linux-x64, where macOS on arm64 measured 67 before the measurement
    // compacted the heap, and a peak of 30 to 50, where macOS on arm64 measured 80. The bounds
    // leave room for both and stay below what keeping a packet's bytes or node would cost (a peak
    // of 125 on linux-x64).
    [Fact]
    public void ManySpeexPacketsStayCompact()
    {
        const int packets = 200_000;
        // A narrowband frame of submode 7 (492 bits, 24.6 kbps) and the terminator, 63 bytes.
        byte[] frame = [0x38, .. new byte[60], 0x07, 0x80];
        var builder = new OggBuilder()
            .Page(9, 0, 2, OggBuilder.SpeexHeader(8000, 0))
            .Page(9, 0, 0, OggBuilder.VorbisComment("Encoded with Speex 1.2.1"));
        for (int done = 0; done < packets; done += 250)
        {
            builder.Page(9, ((done + 250) * 160L) - 40, done + 250 == packets ? (byte)4 : (byte)0, [.. Enumerable.Repeat(frame, 250)]);
        }

        (Report report, long kept, long peak) = Measure("speex.spx", builder.Bytes);

        Assert.Equal(packets * 160L / 8000.0 - (40 / 8000.0), report.Streams.Single().Duration, 9);
        output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * 80L, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * 110L, $"The live heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    // An AC-3 stream keeps a 16-byte record per frame (offset, store position, kept bits and CRC
    // verdicts), the fields its nodes are built from without the exponent groups and mantissas,
    // and its per-frame series, with the coding series (14 bytes for a mono frame): the frames repeat a committed mono fixture of 384-byte frames at
    // 96 kbps, 85 bytes kept per frame on linux-x64 and a peak of 89 to 102. The bounds leave room and
    // stay far below what keeping each frame's bytes would cost.
    [Fact]
    public void ManyAc3FramesStayCompact()
    {
        const int frames = 50_000;
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ac3", "external-ffmpeg-mono-96k.ac3"));
        byte[] data = new byte[source.Length * (frames / (source.Length / 384))];
        for (int at = 0; at < data.Length; at += source.Length)
        {
            source.CopyTo(data, at);
        }

        (Report report, long kept, long peak) = Measure("frames.ac3", data, source);

        Assert.Equal(frames * 1536L, Assert.Single(report.Streams).Samples);
        output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * 220L, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * 300L, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    // The coding series of an AC-3 stream (docs/design/coding-graphs.md, "Memory budget") are recorded while the audio blocks are read: a
    // stereo frame of six blocks holds the three flag series of a fixed layout, 18 bytes with no count byte, and the coded bandwidth, which
    // the report keeps as a value of 8 bytes (26 in all). The bound is on the growth between two counts of frames, so that what is kept
    // whatever the count (pools, the records of the first frames) does not count; the whole report must grow by less than the bound of
    // ManyAc3FramesStayCompact.
    [Fact]
    public void TheCodingSeriesOfFurtherAc3FramesStayWithinTheirBudget()
    {
        const int perCopy = 16;
        const int fewer = 1_562 * perCopy;
        const int frames = 3_125 * perCopy;
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ac3", "external-ffmpeg-stereo-192k.ac3"));

        (Report few, long fewerKept) = MeasureKept("few.ac3", Repeated(source, fewer / perCopy), source);
        (Report many, long kept) = MeasureKept("many.ac3", Repeated(source, frames / perCopy), source);

        CheckAc3CodingGrowth(few, many, fewer, frames, ["Blocks With Block Switching", "Blocks With Coupling", "Blocks With Rematrixing"], categoryBytes: 18, numericBytes: 8);
        double keptPerFrame = (kept - fewerKept) / (double)(frames - fewer);
        output.WriteLine($"{Hundredths(keptPerFrame)} bytes kept per further frame");
        Assert.True(keptPerFrame < 220, $"The report keeps {keptPerFrame:F2} bytes per further frame.");
        GC.KeepAlive(few);
        GC.KeepAlive(many);
    }

    // The E-AC-3 case the design asks for: program frames of an independent frame and a dependent frame of up to six blocks each, a variable
    // layout of four series, whose values are 12 and a count byte at the most for each series (52 bytes), and the coded bandwidth.
    [Fact]
    public void TheCodingSeriesOfFurtherEac3ProgramFramesWithDependentFramesStayWithinTheirBudget()
    {
        const int fewer = 10_000;
        const int units = 20_000;
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ac3", "coding-eac3-dependent.eac3"));
        int perCopy = 4;

        (Report few, long fewerKept) = MeasureKept("few.eac3", Repeated(source, fewer / perCopy), source);
        (Report many, long kept) = MeasureKept("many.eac3", Repeated(source, units / perCopy), source);

        // The budget of this stream, from its own units: for each of the four series the values of every unit and its count byte (a fixed copy of the
        // fixture's 31 blocks in 4 units is 35 bytes a unit), not the worst case of 12 values, which would let 17 bytes a unit of regression pass.
        CategorySeries first = Assert.Single(few.Streams).CategorySeries[0];
        double perUnit = 4.0 * (Enumerable.Range(0, first.Count).Sum(u => first.ValueCount(u)) + first.Count) / first.Count;
        CheckAc3CodingGrowth(few, many, fewer, units, ["Blocks With Block Switching", "Blocks With Coupling", "Blocks With Rematrixing", "Blocks With Spectral Extension"],
            categoryBytes: (int)Math.Ceiling(perUnit) + 1, numericBytes: 8);
        double keptPerUnit = (kept - fewerKept) / (double)(units - fewer);
        output.WriteLine($"{Hundredths(keptPerUnit)} bytes kept per further program frame");
        // 158 bytes measured on linux-x64 (a 16-byte record, the kept bits, the bitrate series' two points and 35 bytes of series for each program frame); the
        // bound leaves a quarter for the other platforms.
        Assert.True(keptPerUnit < 190, $"The report keeps {keptPerUnit:F2} bytes per further program frame.");
        GC.KeepAlive(few);
        GC.KeepAlive(many);
    }

    private static byte[] Repeated(byte[] source, int copies)
    {
        byte[] data = new byte[source.Length * copies];
        for (int at = 0; at < data.Length; at += source.Length)
        {
            source.CopyTo(data, at);
        }

        return data;
    }

    // What the further units add to the coding series, from the series' own sizes: the codes and count bytes of the category series and 8
    // bytes for each point of the numeric series, which shares the bitrate series' x values.
    private static void CheckAc3CodingGrowth(Report few, Report many, int fewer, int units, string[] names, int categoryBytes, int numericBytes)
    {
        StreamInfo small = Assert.Single(few.Streams);
        StreamInfo large = Assert.Single(many.Streams);
        Assert.Equal(names, large.CategorySeries.Select(c => c.Name));
        Assert.Equal(units, large.CategorySeries[0].Count);
        long grown = large.CategorySeries.Sum(c => c.StorageBytes) - small.CategorySeries.Sum(c => c.StorageBytes);
        long index = large.CategorySeries.Count * 4L * ((units - fewer) / 1024 + 1);
        Assert.True(grown <= (units - fewer) * (long)categoryBytes + index, $"The category series grow by {grown / (double)(units - fewer):F3} bytes per further unit.");
        Assert.True(grown > 0, "The category series did not grow with the units.");

        Series bandwidth = large.Series.Single(s => s.Name == "Coded Bandwidth");
        Assert.Equal(units, bandwidth.Values.Length);
        long numeric = BackingBytes(bandwidth.Values) - BackingBytes(small.Series.Single(s => s.Name == "Coded Bandwidth").Values);
        Assert.True(numeric <= (units - fewer) * (long)numericBytes, $"The coded bandwidth grows by {numeric / (double)(units - fewer):F2} bytes per further unit.");
        Series bitrate = large.Series.Single(s => s.Name == "Bitrate");
        Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bandwidth.X, out ArraySegment<double> own)
            && System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bitrate.X, out ArraySegment<double> shared) && ReferenceEquals(own.Array, shared.Array),
            "The coded bandwidth copies the bitrate series' x values although every unit has a value.");
    }

    // A DTS stream keeps a 24-byte record per unit, the fields its nodes are built from (the headers; the subframes are
    // not read), and its per-frame series. The units repeat a committed mono fixture of 256-byte core frames at 11.025 kHz.
    // The bounds stay far below what keeping each frame's bytes or nodes would cost.
    [Fact]
    public void ManyDtsFramesStayCompact()
    {
        const int frames = 50_000;
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "dts", "core-mono-11k.dts"));
        byte[] data = new byte[source.Length * (frames / (source.Length / 256))];
        for (int at = 0; at < data.Length; at += source.Length)
        {
            source.CopyTo(data, at);
        }

        (Report report, long kept, long peak) = Measure("frames.dts", data, source);

        Assert.Equal(frames * 256L, Assert.Single(report.Streams).Samples);
        output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * DtsKeptBound, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * DtsPeakBound, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    // Units with an extension substream keep their header fields and the descriptors' too, and a lossless asset's.
    [Fact]
    public void ManyDtsSubstreamsStayCompact()
    {
        const int units = 30_000;
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "dts", "exss-two-assets.dts"));
        int unit = source.Length / 4;
        byte[] data = new byte[unit * units];
        for (int at = 0; at < data.Length; at += unit)
        {
            source.AsSpan(0, unit).CopyTo(data.AsSpan(at));
        }

        (Report report, long kept, long peak) = Measure("units.dts", data, source);

        Assert.Equal(units * 512L, Assert.Single(report.Streams).Samples);
        output.WriteLine($"{kept / units} bytes kept and {peak / units} bytes peak per unit");
        Assert.True(kept < units * DtsSubstreamKeptBound, $"The report keeps {kept / units} bytes per unit.");
        Assert.True(peak < units * DtsSubstreamPeakBound, $"The live heap grew by {peak / units} bytes per unit.");
        GC.KeepAlive(report);
    }

    // Units whose extension substream header omits the static fields keep a copy of the ones in force (7.5.2) in their record.
    [Fact]
    public void ManyDtsSubstreamsWithoutStaticFieldsStayCompact()
    {
        const int units = 30_000;
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "dts", "exss-static-first-only.dts"));
        const int first = 704;
        const int later = 696;
        byte[] data = new byte[first + (later * (units - 1))];
        source.AsSpan(0, first).CopyTo(data);
        for (int at = first; at < data.Length; at += later)
        {
            source.AsSpan(first, later).CopyTo(data.AsSpan(at));
        }

        (Report report, long kept, long peak) = Measure("units.dts", data, source);

        Assert.Equal(units * 512L, Assert.Single(report.Streams).Samples);
        output.WriteLine($"{kept / units} bytes kept and {peak / units} bytes peak per unit");
        Assert.True(kept < units * DtsSubstreamKeptBound, $"The report keeps {kept / units} bytes per unit.");
        Assert.True(peak < units * DtsSubstreamPeakBound, $"The live heap grew by {peak / units} bytes per unit.");
        GC.KeepAlive(report);
    }

    private const long DtsKeptBound = 160;
    private const long DtsPeakBound = 200;
    private const long DtsSubstreamKeptBound = 450;
    private const long DtsSubstreamPeakBound = 500;

    // The smallest E-AC-3 frames the parser takes, 12 bytes (6 words), each reading up to an
    // overrun: a frame keeps its 16-byte record, the bits its nodes are built from (about 14
    // bytes) and its two values of the per-frame bitrate series, 46 bytes in all on linux-x64, with
    // a peak of 80. The bounds leave about twice that, and for the peak room for macOS on arm64.
    [Fact]
    public void TinyEac3FramesStayCompact()
    {
        const int frames = 200_000;
        byte[] frame = [0x0B, 0x77, 0x00, 0x05, 0x02, 0x86, 0x0F, 0x28, 0, 0, 0, 0];
        int crc = Encspect.Formats.Ac3.Crc16.Compute(frame.AsSpan(2, 8));
        frame[10] = (byte)(crc >> 8);
        frame[11] = (byte)crc;
        byte[] data = [.. Enumerable.Repeat(frame, frames).SelectMany(b => b)];

        (Report report, long kept, long peak) = Measure("tiny.eac3", data);

        Assert.Equal(frames * 256L, Assert.Single(report.Streams).Samples);
        output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * TinyKeptBound, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * TinyPeakBound, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    private const long TinyKeptBound = 90;
    private const long TinyPeakBound = 180;

    // An ASF file keeps a compact record per packet and per payload (the nodes are built when read), and its media
    // objects, which go to the codec parser one at a time and are never kept (plan, "Large inputs"). Measured over
    // a Windows Media Audio stream of ffmpeg's encoder, a 185-byte superframe repeated, and over Professional and
    // PCM payloads: the bounds are about twice what linux-x64 measures and far below what keeping each media
    // object's bytes or nodes would cost: 273 bytes kept per superframe and a peak of 274 for the WMA stream (the
    // ASF packet, payload, superframe, frame, block and series records), 574 and 593 per frame for Professional, and
    // 89 and 86 per packet for PCM payloads.
    [Fact]
    public void ManyAsfPacketsStayCompact()
    {
        const int packets = 50_000;
        (byte[] properties, byte[] mediaObject) = AsfBuilder.Take("asf/external-ffmpeg-wmav2-stereo-44k-128k.wma", 0x0161);
        byte[] data = AsfBuilder.Build(properties, mediaObject, packets, 46);

        (Report report, long kept, long peak) = Measure("packets.wma", data, data[..4096]);

        Assert.Equal("Windows Media Audio 2", Assert.Single(report.Streams).Codec);
        Assert.Equal(packets - 1L, Assert.Single(report.Streams).Samples / 2048);
        output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * AsfWmaKeptBound, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * AsfWmaPeakBound, $"The live heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    private const long AsfWmaKeptBound = 550;
    private const long AsfWmaPeakBound = 550;

    // Objects that state millions of entries, in files of tens of megabytes: each list becomes nodes only up to a
    // count, so the heap stays a small fraction of what a node for every entry would take (gigabytes).
    [Fact]
    public void AsfObjectsOfMillionsOfEntriesStayBounded()
    {
        Bounded("indexes.asf", AsfBuilder.Append("minimal", AsfBuilder.IndexOfBlocks(1_000_000)), 150_000_000, "asf.index-missing", expectFinding: false);
        Bounded("codecs.asf", AsfBuilder.InHeader("minimal", AsfBuilder.CodecListOf(1_500_000)), 150_000_000, "asf.too-many-entries");
        Bounded("encryption.asf", AsfBuilder.InHeader("minimal", AsfBuilder.EncryptionRecords(40_000, 40), extension: true), 150_000_000, "asf.too-many-entries");
        Bounded("objects.asf", AsfBuilder.WithUnknownObjects("minimal", 900_000), 150_000_000, "asf.too-many-objects");
    }

    private Report Bounded(string name, byte[] data, long bound, string code, bool expectFinding = true)
    {
        (Report report, long kept, long peak) = Measure(name, data, data[..Math.Min(data.Length, 4096)]);
        output.WriteLine($"{name}: {kept / 1_000_000} MB kept and {peak / 1_000_000} MB peak for {data.Length / 1_000_000} MB");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        if (expectFinding)
        {
            Assert.Contains(report.Findings, f => f.Code == code);
        }

        Assert.True(peak < bound, $"{name}: the live heap grew by {peak / 1_000_000} MB.");
        GC.KeepAlive(report);
        return report;
    }

    // More packets than are described one by one: the rest are counted, and the heap stays near the cap's.
    [Fact]
    public void PacketsPastTheDetailedOnesAreCounted()
    {
        byte[] data = AsfBuilder.Build(PcmStreamProperties(), new byte[1], (1 << 16) + 5_000, 1);
        Bounded("undetailed.asf", data, 300_000_000, "asf.packets-undetailed");
    }

    // A stream of millions of tiny Windows Media Audio superframes: records are kept up to a count.
    [Fact]
    public void ManyTinyWmaSuperframesAreBounded()
    {
        (byte[] properties, _) = AsfBuilder.Take("asf/external-ffmpeg-wmav2-stereo-44k-128k.wma", 0x0161);
        byte[] data = AsfBuilder.Build(properties, new byte[3], 600_000, 1);
        Bounded("tiny.wma", data, 300_000_000, "wma.records-limit");
    }

    // Professional packets of a Microsoft-encoded file, each with its frames, subframes and channel records.
    [Fact]
    public void ManyProfessionalPacketsStayCompact()
    {
        const int packets = 6_000;
        (byte[] properties, byte[] mediaObject) = AsfBuilder.Take("asf/external-windows-wma10pro-cbr-stereo-44k-64.wma", 0x0162);
        byte[] data = AsfBuilder.Build(properties, mediaObject, packets, 300);

        (Report report, long kept, long peak) = Measure("packets.wma", data, data[..4096]);

        Assert.Equal("Windows Media Audio Professional", Assert.Single(report.Streams).Codec);
        long frames = Assert.IsType<long>(Assert.Single(report.Streams).Samples / 2048);
        output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame ({frames} frames)");
        Assert.True(kept < frames * ProKeptBound, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * ProPeakBound, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    private const long ProKeptBound = 1150;
    private const long ProPeakBound = 1200;

    // Voice packets of a Microsoft-encoded file, repeated: 14 packets of 187 bytes hold 50 superframes of three frames, each kept
    // as a record of 36 bytes, three of 16, and a part of 12 or 24, with the packet record, the tail owner and the series: 175 bytes
    // kept per superframe on linux-x64 and a peak of 195. The bounds are about twice that.
    [Fact]
    public void ManyVoiceSuperframesStayCompact()
    {
        const int cycles = 800;
        const string fixture = "asf/external-windows-wmavoice-cbr-mono-8k-5.wma";
        (byte[] properties, _) = AsfBuilder.Take(fixture, 0x000A);
        List<byte[]> objects = AsfBuilder.Objects(fixture, 0x000A);
        var packets = new List<byte[]>();
        for (int i = 0; i < cycles; i++)
        {
            foreach (byte[] mediaObject in objects)
            {
                byte[] packet = AsfBuilder.Packet(23 + mediaObject.Length, 1, packets.Count & 0xFF, 0, (uint)mediaObject.Length, (uint)(packets.Count * 299));
                mediaObject.CopyTo(packet, 23);
                packets.Add(packet);
            }
        }

        byte[] data = AsfBuilder.Assemble(properties, 23 + objects[0].Length, packets);
        (Report report, long kept, long peak) = Measure("packets.wma", data, data[..4096]);

        Assert.Equal("Windows Media Audio Voice", Assert.Single(report.Streams).Codec);
        long superframes = Assert.Single(report.Streams).Samples / 480;
        Assert.True(superframes > cycles * 45L);
        output.WriteLine($"{kept / superframes} bytes kept and {peak / superframes} bytes peak per superframe ({superframes} superframes)");
        Assert.True(kept < superframes * VoiceKeptBound, $"The report keeps {kept / superframes} bytes per superframe.");
        Assert.True(peak < superframes * VoicePeakBound, $"The live heap grew by {peak / superframes} bytes per superframe.");
        GC.KeepAlive(report);
    }

    private const long VoiceKeptBound = 380;
    private const long VoicePeakBound = 420;

    // Packets of nothing but the smallest superframes (87 bits, 16 to a packet of 187 bytes): more than the stream keeps records for.
    [Fact]
    public void ManyTinyVoiceSuperframesAreBounded()
    {
        const string fixture = "asf/external-windows-wmavoice-cbr-mono-8k-5.wma";
        (byte[] properties, _) = AsfBuilder.Take(fixture, 0x000A);
        var body = new VoiceBitWriter();
        for (int i = 0; i < 16; i++)
        {
            body.Append(VoiceBuilder.Superframe());
        }

        var packets = new List<byte[]>();
        int count = 36_000;
        for (int i = 0; i < count; i++)
        {
            byte[] mediaObject = VoiceBuilder.Packet(187, i & 15, true, 16, 0, body);
            byte[] packet = AsfBuilder.Packet(23 + mediaObject.Length, 1, i & 0xFF, 0, (uint)mediaObject.Length, (uint)(i * 299));
            mediaObject.CopyTo(packet, 23);
            packets.Add(packet);
        }

        byte[] data = AsfBuilder.Assemble(properties, 23 + 187, packets);
        Bounded("tiny.wma", data, 300_000_000, "wma.records-limit");
    }

    // Media objects of nothing but blocks of 2 bytes, each a valid header that counts no superframe: one media object holds
    // thousands of packets, which the count of media objects does not limit. The packets are kept up to a count.
    [Fact]
    public void ManyTinyVoiceBlocksAreBounded()
    {
        const string fixture = "asf/external-windows-wmavoice-cbr-mono-8k-5.wma";
        (byte[] properties, _) = AsfBuilder.Take(fixture, 0x000A);

        // The WAVEFORMATEX starts 78 bytes into the Stream Properties Object; its block alignment is 12 bytes in.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(properties.AsSpan(90), 2);
        var packets = new List<byte[]>();
        const int blocksPerObject = 16_000;
        for (int i = 0; i < 400; i++)
        {
            byte[] mediaObject = new byte[blocksPerObject * 2];
            for (int block = 0; block < blocksPerObject; block++)
            {
                VoiceBuilder.Packet(2, block & 15, true, 0, 0, new VoiceBitWriter()).CopyTo(mediaObject, block * 2);
            }

            byte[] packet = AsfBuilder.Packet(23 + mediaObject.Length, 1, i & 0xFF, 0, (uint)mediaObject.Length, (uint)(i * 299));
            mediaObject.CopyTo(packet, 23);
            packets.Add(packet);
        }

        byte[] data = AsfBuilder.Assemble(properties, 23 + (blocksPerObject * 2), packets);
        Bounded("blocks.wma", data, 120_000_000, "wma.records-limit");
    }

    // A Matroska file keeps a compact record per block and per frame inside Clusters (the nodes are built when read).
    [Fact]
    public void ManyMatroskaBlocksStayCompact()
    {
        const int blocks = 400_000;
        byte[] data = MatroskaBuilder.OpusBlocks(blocks, blocksPerCluster: 1000);

        (Report report, long kept, long peak) = Measure("blocks.mka", data, data[..4096]);

        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        output.WriteLine($"{kept / blocks} bytes kept and {peak / blocks} bytes peak per block");
        Assert.True(kept < blocks * MatroskaKeptBound, $"The report keeps {kept / blocks} bytes per block.");
        Assert.True(peak < blocks * MatroskaPeakBound, $"The live heap grew by {peak / blocks} bytes per block.");
        GC.KeepAlive(report);
    }

    private const long MatroskaKeptBound = 250;
    private const long MatroskaPeakBound = 300;

    // More blocks than the records are kept for, and as many elements outside Clusters as the budget allows.
    [Fact]
    public void MatroskaRecordsAndElementsPastTheirLimitsStayBounded()
    {
        // Once, not four times as Measure does: the cap's records are about 120 bytes per block. The input is built before the baseline and kept
        // alive past the reading, as in Measure, so that its 55 MB are in neither. linux-x64 measures 710 MB; the bound is about twice that.
        byte[] data = MatroskaBuilder.OpusBlocks(6_100_000, blocksPerCluster: 100_000);
        long before = Live();
        Report report = Analyzer.CreateDefault().Analyze("records.mka", new MemoryStream(data));
        Finding limit = Assert.Single(report.Findings, f => f.Code == "matroska.too-many-elements");
        Assert.Equal("Unparsed Data", limit.Node!.Name);
        Assert.NotNull(limit.Path);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        long live = Live() - before;
        GC.KeepAlive(data);
        output.WriteLine($"records.mka: {live / 1_000_000} MB more live");
        Assert.True(live < 1_400_000_000, $"The live heap grew by {live / 1_000_000} MB.");
        GC.KeepAlive(report);
        Bounded("voids.mka", MatroskaBuilder.File([MatroskaBuilder.OpusTracks(), .. Enumerable.Repeat(MatroskaBuilder.Void(0), 1_000_000)]), 110_000_000, "matroska.too-many-elements");
    }

    // Every ContentEncoding of a track hides the next, 16,000 of them in a file of 192 KB: the run that hides each was copied (2 GB live on linux-x64).
    [Fact]
    public void ManyHidingContentEncodingsStayBounded()
    {
        byte[] encodings = MatroskaBuilder.Element(0x6D80, [.. Enumerable.Range(0, 16_000).Select(i => MatroskaBuilder.Element(0x6240, MatroskaBuilder.Unsigned(0x5031, (ulong)i), MatroskaBuilder.Unsigned(0x5032, 5)))]);
        byte[] data = MatroskaBuilder.File(MatroskaBuilder.Info(), MatroskaBuilder.Element(0x1654AE6B, MatroskaBuilder.OpusTrack(1, encodings)));

        Bounded("encodings.mka", data, 150_000_000, "matroska.encoding-hidden");
    }

    // Zlib frames that inflate to 40 MiB each, 20 of them in less than a megabyte: the first is refused at the limit on inflated frames, not inflated.
    [Fact]
    public void InflatedMatroskaFramesStayBounded()
    {
        var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(new byte[40 << 20]);
        }

        byte[] frame = packed.ToArray();
        byte[] encodings = MatroskaBuilder.Element(0x6D80, MatroskaBuilder.Element(0x6240, MatroskaBuilder.Unsigned(0x5031, 0), MatroskaBuilder.Unsigned(0x5032, 1),
            MatroskaBuilder.Unsigned(0x5033, 0), MatroskaBuilder.Element(0x5034, MatroskaBuilder.Unsigned(0x4254, 0))));
        byte[] cluster = MatroskaBuilder.Cluster(0, [.. Enumerable.Range(0, 20).Select(i => MatroskaBuilder.SimpleBlock(1, i * 20, frame))]);
        byte[] data = MatroskaBuilder.File(MatroskaBuilder.Info(), MatroskaBuilder.Element(0x1654AE6B, MatroskaBuilder.OpusTrack(1, encodings)), cluster);

        Bounded("inflate.mka", data, 30_000_000, "matroska.frame-encoding-invalid");
    }

    // Concatenated documents (EBML Header, Segment with an Info element and one Opus track): each routed Segment costs its stream, the codec parser's
    // nodes and the series, about 30 KB, so the node budget charges each audio track for them, and 5,000 of them, past the budget, stay bounded.
    [Fact]
    public void ConcatenatedSegmentsStayBounded()
    {
        byte[] data = MatroskaBuilder.Concat([.. Enumerable.Repeat(MatroskaBuilder.Document(), 5_000)]);

        (Report report, long kept, long peak) = Measure("stream.mka", data, data[..4096]);

        Assert.Contains(report.Findings, f => f.Code == "matroska.too-many-elements");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        int segments = report.Streams.Count;
        output.WriteLine($"{segments} Segments read, {kept / 1_000_000} MB kept and {peak / 1_000_000} MB peak");
        Assert.InRange(segments, 2_000, 4_500);
        Assert.True(peak < 250_000_000, $"The live heap grew by {peak / 1_000_000} MB.");
        GC.KeepAlive(report);
    }

    // A block of seven bytes can state 256 frames of no bytes: the frames of laced blocks are counted against a budget, and fixed-size lacing
    // keeps no sizes, so 100,000 such blocks (700 KB of file, 25.6 million frames) cost a record per block and the frames up to the budget:
    // 120 MB on linux-x64, and the bound about twice that.
    [Fact]
    public void LacedBlocksOfEmptyFramesStayBounded()
    {
        byte[] block = MatroskaBuilder.EmptyLacedBlock();
        byte[] cluster = MatroskaBuilder.Cluster(0, [.. Enumerable.Repeat(block, 100_000)]);

        // Without a track the blocks are only recorded; with an Opus track every frame also goes to the codec parser as an empty packet.
        Bounded("laced.mka", MatroskaBuilder.Concat(MatroskaBuilder.Header(), MatroskaBuilder.Segment(cluster)), 16_000_000, "matroska.too-many-elements");
        byte[] data = MatroskaBuilder.File(MatroskaBuilder.OpusTracks(), cluster);
        long before = Live();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Report report = Analyzer.CreateDefault().Analyze("laced-opus.mka", new MemoryStream(data));
        watch.Stop();
        Assert.Contains(report.Findings, f => f.Code == "matroska.too-many-elements");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        long live = Live() - before;
        GC.KeepAlive(data);
        output.WriteLine($"laced-opus.mka: {live / 1_000_000} MB more live, {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.True(live < 240_000_000, $"The live heap grew by {live / 1_000_000} MB.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120), $"The analysis took {watch.Elapsed.TotalSeconds:0} s.");
        GC.KeepAlive(report);
    }

    // Frames put together from other bytes (header stripping) reach the codec parser as opaque packets, which keep four bytes each beyond
    // the plain ones: the same laced file with a track that strips a byte from every frame stays near the plain one (177 MB on linux-x64, the bound about twice that).
    [Fact]
    public void LacedBlocksOfHeaderStrippedFramesStayBounded()
    {
        byte[] cluster = MatroskaBuilder.Cluster(0, [.. Enumerable.Repeat(MatroskaBuilder.EmptyLacedBlock(), 100_000)]);
        byte[] data = MatroskaBuilder.File(MatroskaBuilder.OpusTracks(isHeaderStripped: true), cluster);
        long before = Live();
        Report report = Analyzer.CreateDefault().Analyze("stripped.mka", new MemoryStream(data));
        Assert.Contains(report.Findings, f => f.Code == "matroska.too-many-elements");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.InRange(report.Streams.Sum(s => s.Samples) / 960, 3_990_000, 4_000_000);
        long live = Live() - before;
        GC.KeepAlive(data);
        output.WriteLine($"stripped.mka: {live / 1_000_000} MB more live");
        Assert.True(live < 350_000_000, $"The live heap grew by {live / 1_000_000} MB.");
        GC.KeepAlive(report);
    }

    // Xiph lacing stores a size per frame, and each costs at least a byte of the file: 8,000 blocks of 255 one-byte frames.
    [Fact]
    public void XiphLacedBlocksStayBounded()
    {
        byte[] header = [0x81, 0x00, 0x00, 0x02, 0xFE];
        byte[] sizes = Enumerable.Repeat((byte)1, 254).ToArray();
        byte[] block = MatroskaBuilder.Element(0xA3, MatroskaBuilder.Concat(header, sizes, new byte[255]));
        byte[] cluster = MatroskaBuilder.Cluster(0, [.. Enumerable.Repeat(block, 8_000)]);
        Bounded("xiph.mka", MatroskaBuilder.File(MatroskaBuilder.OpusTracks(), cluster), 250_000_000, "matroska.too-many-elements", expectFinding: false);
    }

    // PCM payloads, which no codec parser takes: the container's own records.
    [Fact]
    public void ManyAsfPcmPayloadsStayCompact()
    {
        const int packets = 150_000;
        byte[] properties = PcmStreamProperties();
        byte[] data = AsfBuilder.Build(properties, new byte[160], packets, 10);

        (Report report, long kept, long peak) = Measure("packets.asf", data, data[..4096]);

        Assert.Contains(report.Findings, f => f.Code == "asf.codec-not-analyzed");
        output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * AsfPcmKeptBound, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * AsfPcmPeakBound, $"The live heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    private const long AsfPcmKeptBound = 180;
    private const long AsfPcmPeakBound = 180;

    // A Stream Properties Object for mono 16-bit PCM at 8 kHz.
    private static byte[] PcmStreamProperties()
    {
        var stream = new MemoryStream();
        stream.Write(new Guid("B7DC0791-A9B7-11CF-8EE6-00C00C205365").ToByteArray());
        stream.Write(new byte[8]);
        stream.Write(new Guid("F8699E40-5B4D-11CF-A8FD-00805F5C442B").ToByteArray());
        stream.Write(new Guid("20FB5700-5B55-11CF-A8FD-00805F5C442B").ToByteArray());
        stream.Write(new byte[8]);
        stream.Write([18, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0]);
        stream.Write([1, 0, 1, 0, 0x40, 0x1F, 0, 0, 0x80, 0x3E, 0, 0, 2, 0, 16, 0, 0, 0]);
        byte[] bytes = stream.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), (ulong)bytes.Length);
        return bytes;
    }

    // A Musepack SV7 stream keeps a 24-byte record per frame and its per-frame series and figures,
    // 42 bytes in all on linux-x64, with a peak of 59; the frames' nodes are built in blocks when
    // read. The bounds are about twice that and below what keeping a frame's node would cost.
    [Fact]
    public void ManyMusepackFramesStayCompact()
    {
        const int frames = 100_000;

        (Report report, long kept, long peak) = Measure("frames.mpc", Sv7Frames(frames), Sv7Frames(100));

        Assert.Equal(frames * 1152L - 481, report.Streams.Single().Samples);
        output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * MusepackKeptBound, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * MusepackPeakBound, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    // An SV5 stream keeps the same 24-byte record per frame as SV7, so it is held to the same bounds.
    [Fact]
    public void ManyOldMusepackFramesStayCompact()
    {
        const int frames = 100_000;

        (Report report, long kept, long peak) = Measure("frames.mp+", Sv5Frames(frames), Sv5Frames(100));

        Assert.Equal("Musepack SV5", report.Streams.Single().Codec);
        Assert.Equal((frames - 1) * 1152L - 481, report.Streams.Single().Samples);
        output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * MusepackKeptBound, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * MusepackPeakBound, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    // An SV8 audio packet of 4^4 frames in 35 bytes (a bit for each frame at least, which libmpcdec
    // reads) declares about 6.7 s: the per-second figures are kept only for seconds a packet starts
    // in, so 200,000 such packets cost per packet, not per second they declare.
    [Fact]
    public void LongEmptySv8PacketsStayCompact()
    {
        const int packets = 200_000;

        (Report report, long kept, long peak) = Measure("packets.mpc", Sv8EmptyPackets(packets, "AP"u8), Sv8EmptyPackets(100, "AP"u8));

        Assert.Equal("Musepack SV8", report.Streams.Single().Codec);
        output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * MusepackKeptBound, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * MusepackPeakBound, $"The live heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    // Every kind of packet past the detailed ones joins a run, whatever its key: the stream header,
    // ReplayGain, encoder info, seek table offset and seek table packets repeated cost about nothing
    // each too, and the findings they would add are counted once. The bound is the growth from 0.5 to 1.5
    // million packets, as in UnknownSv8PacketsStayCompact.
    [Theory]
    [InlineData("RG", "01000000000000000000")]
    [InlineData("RG", "")]
    [InlineData("RG", "02")]
    [InlineData("EI", "50010000")]
    [InlineData("SO", "00")]
    [InlineData("ST", "010000")]
    [InlineData("SH", "")]
    public void RepeatedSv8StreamPacketsStayCompact(string key, string payloadHex)
    {
        const int fewer = 500_000;
        const int packets = 1_500_000;
        byte[] payload = Convert.FromHexString(payloadHex);
        byte[] warmUp = Sv8RepeatedPackets(5000, Encoding.ASCII.GetBytes(key), payload);

        (_, long fewerKept, long fewerPeak, string fewerWhere) = MeasureExcess("repeated.mpc", Sv8RepeatedPackets(fewer, Encoding.ASCII.GetBytes(key), payload), warmUp);
        (Report report, long kept, long peak, string where) = MeasureExcess("repeated.mpc", Sv8RepeatedPackets(packets, Encoding.ASCII.GetBytes(key), payload), warmUp);

        Assert.Equal("Musepack SV8", report.Streams.Single().Codec);
        Assert.True(report.Findings.Count < 40, $"{report.Findings.Count} findings.");
        double keptPerPacket = (kept - fewerKept) / (double)(packets - fewer);
        double peakPerPacket = (peak - fewerPeak) / (double)(packets - fewer);
        output.WriteLine($"{key} {payloadHex}: {Hundredths(keptPerPacket)} kept and {Hundredths(peakPerPacket)} held per further packet; {fewerWhere} {where}");
        Assert.True(keptPerPacket < UndetailedPacketBound, $"The report keeps {keptPerPacket:F2} bytes per further {key} packet.");
        Assert.True(peakPerPacket < UndetailedPacketBound, $"The analysis held {peakPerPacket:F2} more bytes per further {key} packet than it starts and ends with. {RuntimeSettings()}");
        GC.KeepAlive(report);
    }

    // Packets the analyzer no longer details, once it has detailed 4,096, are kept as one record per
    // run, so empty packets of an unknown key (3 bytes each) keep about nothing per packet. The
    // bound is on the growth from 1 to 4 million packets: what the report keeps whatever the count
    // (the detailed packets, pools the runtime holds on to) differs by platform and would otherwise
    // dominate. The second figure is what the analysis held above the heap it ends with
    // (MeasureExcess), which reads the heap at fixed places in the input and does not depend on the
    // heap before the analysis or on when a sampler thread runs.
    [Fact]
    public void UnknownSv8PacketsStayCompact()
    {
        const int fewer = 1_000_000;
        const int packets = 4_000_000;
        byte[] warmUp = Sv8EmptyPackets(5000, "XX"u8);

        (_, long fewerKept, long fewerPeak, string fewerWhere) = MeasureExcess("unknown.mpc", Sv8EmptyPackets(fewer, "XX"u8), warmUp);
        (Report report, long kept, long peak, string where) = MeasureExcess("unknown.mpc", Sv8EmptyPackets(packets, "XX"u8), warmUp);

        Assert.Equal("Musepack SV8", report.Streams.Single().Codec);
        Assert.Contains(report.Findings, f => f.Code == "musepack.key-unknown" && f.Message.StartsWith("4000000 packets", StringComparison.Ordinal));
        double keptPerPacket = (kept - fewerKept) / (double)(packets - fewer);
        double peakPerPacket = (peak - fewerPeak) / (double)(packets - fewer);
        output.WriteLine($"{fewerKept} and {kept} bytes kept, {fewerPeak} and {peak} bytes held above the start and end: {Hundredths(keptPerPacket)} and {Hundredths(peakPerPacket)} per further packet");
        output.WriteLine($"Highest readings, 1 million packets: {fewerWhere}");
        output.WriteLine($"Highest readings, 4 million packets: {where}");
        output.WriteLine(RuntimeSettings());
        Assert.True(keptPerPacket < UndetailedPacketBound, $"The report keeps {keptPerPacket:F2} bytes per further packet.");
        Assert.True(peakPerPacket < UndetailedPacketBound, $"The analysis held {peakPerPacket:F2} more bytes per further packet than it starts and ends with. Highest readings: 1 million packets {fewerWhere}; 4 million packets {where}. {RuntimeSettings()}");
        GC.KeepAlive(report);
    }

    // What the runtime is configured with, for reading a failure on a runner.
    private static string RuntimeSettings() =>
        $"{Environment.ProcessorCount} processors, server GC {GCSettings.IsServerGC}, " +
        string.Join(", ", GC.GetConfigurationVariables().Where(v => v.Key is "GCServer" or "GCConcurrent" or "GCHeapCount" or "GCgen0size" or "GCgen0MaxBudget" or "GCRegionSize" or "GCConserveMemory" or "GCHeapHardLimit").Select(v => $"{v.Key}={v.Value}"));

    private static string Hundredths(double value) => (Math.Round(value, 2) + 0.0).ToString("F2");

    // A FLAC stream keeps a 56-byte entry per frame and 16 bytes of its bitrate graph, and the subframes of the first 4,096 frames; past them
    // a frame is its entry alone, and its nodes are built in blocks of 1,024 when read. With decoding on, the session holds one frame's buffers
    // and the MD5's state, so the peak is that of the analysis without it plus a constant, however many frames follow.
    [Fact]
    public void ManyFlacFramesStayCompact()
    {
        const int frames = 100_000;
        byte[] data = FlacFrames(frames);

        (Report report, long kept, long peak) = Measure("frames.flac", data, FlacFrames(100));

        Assert.Equal(frames * 4096L, report.Streams.Single().Samples);
        output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * 200L, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * 260L, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    // A WAVE file's samples are never read: the report of a file with 40 MiB of them is as large as that of one with 4 KiB, and what the analysis
    // holds while it runs (the buffer the host reads the ends of the file with) is the same for 8 MiB and for 40 MiB.
    [Fact]
    public void TheSamplesOfAWaveFileCostNothing()
    {
        (Report small, long smallKept, _) = Measure("small.wav", LongInputs.WavWithSamples(4096), LongInputs.WavWithSamples(4096));
        (Report medium, _, long mediumPeak) = Measure("medium.wav", LongInputs.WavWithSamples(8 << 20), LongInputs.WavWithSamples(4096));
        (Report large, long largeKept, long largePeak) = Measure("large.wav", LongInputs.WavWithSamples(40 << 20), LongInputs.WavWithSamples(4096));

        Assert.Equal(40 << 20 >> 2, large.Streams.Single().Samples);
        output.WriteLine($"{smallKept} bytes kept for 4 KiB of samples, {largeKept} for 40 MiB, {mediumPeak} and {largePeak} bytes peak for 8 and 40 MiB");
        Assert.True(largeKept < smallKept + 2_000, $"The report keeps {largeKept - smallKept} bytes more for 40 MiB of samples.");
        Assert.True(largePeak < mediumPeak + 100_000, $"The live heap grew by {largePeak - mediumPeak} bytes more for 40 MiB of samples than for 8 MiB.");
        Assert.True(largePeak < 4_000_000, $"The live heap grew by {largePeak} bytes while the samples were skipped.");
        GC.KeepAlive(small);
        GC.KeepAlive(medium);
        GC.KeepAlive(large);
    }

    [Theory]
    [InlineData("wav")]
    [InlineData("aiff")]
    public void ManyChunksStayCompact(string kind)
    {
        byte[] data = LongInputs.Build(kind);
        (Report report, long kept, long peak) = Measure($"chunks.{kind}", data, LongInputs.Build(kind));

        output.WriteLine($"{kept / 2048} bytes kept and {peak / 2048} bytes peak per listed chunk");
        Assert.True(kept < 2048 * 1_500L, $"The report keeps {kept / 2048} bytes per listed chunk.");
        Assert.True(peak < 2048 * 2_000L, $"The live heap grew by {peak / 2048} bytes per listed chunk.");
        GC.KeepAlive(report);
    }

    // The bound is on the growth, from 50,000 to 200,000 frames, of the live heap that one reading inside the decoding of the last frame
    // sees above the heap the analysis ends with (report alive), so whatever decoding holds per frame for the length of the stream shows,
    // and nothing the analysis does after its last frame can. The reading is taken from the sink's Write of the last frame, on the
    // analysis thread, at the same point of every run, so it needs no sampler and no luck. The first two versions sampled the heap
    // while the analysis ran and compared that with the same measurement of the analysis without decoding; both failed on windows-latest
    // (8.2 MB at 100,000 frames, then 8.04 bytes per further frame). Nothing in the decoding grows: read at fixed points of the
    // analysis (a temporary hook in the parser, linux-x64), decoding adds 83 KB at 25,000, 100,000 and 400,000 frames alike. What
    // grows is the parser's own end, which an analysis without decoding has too: while it builds the stream's series from the figures
    // it read, the figures and the new arrays are alive together (+8.00 bytes per frame once the offsets array exists, +16 with
    // the bitrates', +21 when the series of seconds are in, over the end state at 400,000 frames), and the figures of seconds are
    // larger than their series (+0.8 bytes per frame at the start of the stream's end). A sampler that reads the heap during the
    // analysis finds those only if a reading falls in them: decoding takes seconds after the last read, so the sampler read the
    // stretch before the end state's build-up about every 300 ms, where the analysis alone ends within one reading. The difference of
    // two such samplers is a difference of what they happened to see. Here the parser's own end adds 1.3 MB at every count,
    // growing by about 0.8 bytes per further frame, and a session that keeps one 8-byte value per frame adds 1.2 MB to the growth.
    [Fact]
    public void DecodingManyFlacFramesHoldsNothingPerFrame()
    {
        const int fewer = 50_000;
        const int frames = 200_000;
        byte[] warmUp = FlacFrames(100);
        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(warmUp), new AnalysisOptions { Decoding = new DecodeOptions() });

        (long fewerHeld, long fewerKept) = LastFrameReading("frames.flac", FlacFrames(fewer), fewer, 4096);
        (long held, long kept) = LastFrameReading("frames.flac", FlacFrames(frames), frames, 4096);
        (_, long plainKept) = LastFrameReading("frames.flac", FlacFrames(frames), frames, 4096, isDecoding: false);

        double heldPerFrame = (held - fewerHeld) / (double)(frames - fewer);
        output.WriteLine($"{kept} bytes kept with decoding and {plainKept} without; held above the end state at the last frame: {fewerHeld} at {fewer} frames and {held} at {frames} ({Hundredths(heldPerFrame)} per further frame)");
        Assert.True(kept < plainKept + 1_000_000, $"Decoding made the report keep {(kept - plainKept) / (double)frames:F2} bytes more per frame.");
        Assert.True(heldPerFrame < DecodingFrameBound, $"Decoding held {heldPerFrame:F2} more bytes per further frame at the last frame than the analysis ends with. {RuntimeSettings()}");
    }

    // The live heap read inside the sink's Write of the last frame less the live heap with the report alive after the analysis, and
    // what the report keeps (alive less dropped); each the smaller of two analyses, as for Measure.
    private static (long Held, long Kept) LastFrameReading(string name, byte[] data, int frames, long samplesPerFrame, bool isDecoding = true)
    {
        long held = long.MaxValue;
        long kept = long.MaxValue;
        for (int pass = 0; pass < 2; pass++)
        {
            (long reading, long withReport) = AnalyzeReadingAtLastFrame(name, data, frames, samplesPerFrame, isDecoding);
            long withoutReport = Live();
            held = Math.Min(held, reading - withReport);
            kept = Math.Min(kept, withReport - withoutReport);
        }

        GC.KeepAlive(data);
        return (held, kept);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (long Reading, long WithReport) AnalyzeReadingAtLastFrame(string name, byte[] data, int frames, long samplesPerFrame, bool isDecoding)
    {
        var probe = new LastFrameSink(frames);
        var options = new AnalysisOptions { Decoding = isDecoding ? new DecodeOptions { Output = _ => probe } : null };
        Live();
        Report report = Analyzer.CreateDefault().Analyze(name, new MemoryStream(data), options);
        if (isDecoding)
        {
            Assert.Equal(frames * samplesPerFrame, report.Streams.Single().Verification!.DecodedSamples);
            Assert.True(probe.Reading > 0, "The sink was not written the last frame.");
        }

        long withReport = Live();
        GC.KeepAlive(report);
        return (probe.Reading, withReport);
    }

    private sealed class LastFrameSink(int frames) : IPcmSink
    {
        private int _written;

        public long Reading { get; private set; }

        public void Write(PcmBlock block)
        {
            if (++_written == frames)
            {
                Reading = Live();
            }
        }

        public void End(long samples)
        {
        }
    }

    // Frames of constant subframes, the smallest there are: 4,096 samples of two channels in 14 bytes.
    private static byte[] FlacFrames(int count) => FlacBuilder.Stream(count);

    // A WavPack file keeps a 64-byte entry per block and 16 bytes per set for its graph, and the
    // sub-blocks of the first 4,096 blocks; past them a block is its entry alone, and its nodes are
    // built in blocks of 1,024 when read. The bounds are about twice what linux-x64 measures and below
    // what keeping a block's sub-blocks or nodes would cost.
    [Fact]
    public void ManyWavPackBlocksStayCompact()
    {
        const int blocks = 100_000;

        (Report report, long kept, long peak) = Measure("blocks.wv", WavPackTinyBlocks(blocks), WavPackTinyBlocks(100));

        Assert.Equal(blocks * 128L, report.Streams.Single().Samples);
        output.WriteLine($"{kept / blocks} bytes kept and {peak / blocks} bytes peak per block");
        Assert.True(kept < blocks * WavPackKeptBound, $"The report keeps {kept / blocks} bytes per block.");
        Assert.True(peak < blocks * WavPackPeakBound, $"The live heap grew by {peak / blocks} bytes per block.");
        GC.KeepAlive(report);
    }

    // The decoding of a WavPack file holds its decorrelation and entropy state for the block at hand, the planes of the set, the MD5's state
    // and the findings it counts, so what it holds does not grow with the blocks. Measured as the FLAC test above does: a reading of the live
    // heap inside the sink's Write of the last set, at 50,000 and 200,000 blocks, less the heap the analysis ends with. The blocks are
    // WavPackDecodedBlocks: 128 samples each that are decoded in full, with a CRC that holds. A List<long> that keeps one value per block in
    // StreamDecoder reads 10.30 bytes per further block and fails it (bound DecodingFrameBound, 8).
    [Fact]
    public void DecodingManyWavPackBlocksHoldsNothingPerBlock()
    {
        const int fewer = 50_000;
        const int blocks = 200_000;
        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(WavPackDecodedBlocks(100)), new AnalysisOptions { Decoding = new DecodeOptions() });

        (long fewerHeld, long fewerKept) = LastFrameReading("blocks.wv", WavPackDecodedBlocks(fewer), fewer, 128);
        (long held, long kept) = LastFrameReading("blocks.wv", WavPackDecodedBlocks(blocks), blocks, 128);
        (_, long plainKept) = LastFrameReading("blocks.wv", WavPackDecodedBlocks(blocks), blocks, 128, isDecoding: false);

        double heldPerBlock = (held - fewerHeld) / (double)(blocks - fewer);
        output.WriteLine($"{kept} bytes kept with decoding and {plainKept} without; held above the end state at the last block: {fewerHeld} at {fewer} blocks and {held} at {blocks} ({Hundredths(heldPerBlock)} per further block)");
        Assert.True(kept < plainKept + 1_000_000, $"Decoding made the report keep {(kept - plainKept) / (double)blocks:F2} bytes more per block.");
        Assert.True(heldPerBlock < DecodingFrameBound, $"Decoding held {heldPerBlock:F2} more bytes per further block at the last block than the analysis ends with. {RuntimeSettings()}");
    }

    // A block of up to 1 MiB with a sub-block for every two bytes lists the first 1,024 and counts the rest.
    [Fact]
    public void ManyWavPackSubBlocksAreBounded()
    {
        byte[] block = WavPackTinyBlocks(1);
        byte[] data = WavPackBuilder.Join(block, WavPackBuilder.Parts(block).Take(1).Concat(Enumerable.Repeat(WavPackBuilder.Sub(0, []), 500_000)).Concat(WavPackBuilder.Parts(block).Skip(1)));
        Bounded("subblocks.wv", data, 60_000_000, "wavpack.version-unsupported", expectFinding: false);
    }

    // The caps on what a detailed block lists and keeps are shared by all the detailed blocks, so 4,096 blocks of 1,024 sub-blocks
    // list 64 K of them in all (3 MB measured on linux-x64); per block alone the caps allowed 4 million of them, about 170 MB of records.
    [Fact]
    public void ManySubBlocksInManyDetailedWavPackBlocksShareOneBudget()
    {
        byte[] block = WavPackTinyBlocks(1);
        List<byte[]> parts = WavPackBuilder.Parts(block);
        byte[] full = WavPackBuilder.Join(block, parts.Take(1).Concat(Enumerable.Repeat(WavPackBuilder.Sub(0, []), 1023)).Concat(parts.Skip(1)));
        byte[] data = [.. Enumerable.Repeat(full, 4096).SelectMany(b => b)];

        Report report = Bounded("budget.wv", data, 15_000_000, "wavpack.block-index-overlap");

        List<Node> blocks = [.. report.Root.Children.Single(n => n.Kind == NodeKind.Stream).Children.Where(c => c.Name == "Block")];
        Assert.Equal(4096, blocks.Count);
        Assert.Equal(State.MaxListedSubBlocksInAll, blocks.Sum(c => c.Children.Count(k => k.Children.Any(i => i.Name == "ID"))));
    }

    // Bytes kept of the optional sub-blocks of all the detailed blocks together are capped too: 700 blocks of 64 KiB of them keep 16 MiB
    // (18 MB measured on linux-x64 with the report alive); per block alone the caps allowed 64 KiB of each.
    [Fact]
    public void StoredBytesOfManyDetailedWavPackBlocksShareOneBudget()
    {
        byte[] block = WavPackTinyBlocks(1);
        List<byte[]> parts = WavPackBuilder.Parts(block);
        byte[] big = WavPackBuilder.Sub(0x3E, new byte[State.MaxStoredPerSubBlock]);
        byte[] full = WavPackBuilder.Join(block, parts.Take(1).Concat(Enumerable.Repeat(big, 16)).Concat(parts.Skip(1)));
        byte[] data = [.. Enumerable.Repeat(full, 700).SelectMany(b => b)];

        Report report = Bounded("stored.wv", data, 30_000_000, "wavpack.optional-subblock-unknown");

        List<Node> blocks = [.. report.Root.Children.Single(n => n.Kind == NodeKind.Stream).Children.Where(c => c.Name == "Block")];
        Assert.Equal(700, blocks.Count);
        Assert.Contains(blocks[0].Children, c => c.Name == "Unknown Sub-block");
    }

    // Blocks of 32 bytes that hold nothing, in a file of 64 MB.
    [Fact]
    public void ManyEmptyWavPackBlocksAreBounded()
    {
        byte[] header = WavPackBuilder.Join(WavPackTinyBlocks(1), []);
        Assert.Equal(32, header.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 0);
        byte[] data = [.. Enumerable.Repeat(header, 2_000_000).SelectMany(b => b)];
        Bounded("empty.wv", data, 100_000_000, "wavpack.no-audio");
    }

    private const long WavPackKeptBound = 240;

    private const long WavPackPeakBound = 260;

    // Blocks of mono 16-bit audio at 44.1 kHz, 128 samples each, in one set apiece: a header, a
    // bitstream of 4 bytes and a checksum.
    private static byte[] WavPackTinyBlocks(int count)
    {
        byte[] header = new byte[32];
        "wvpk"u8.CopyTo(header);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 0x410);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 128);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 1 | 4 | 0x800 | 0x1000 | (9u << 23) | 0x10000000);
        byte[][] parts = [WavPackBuilder.Sub(0x0A, new byte[4]), WavPackBuilder.Sub(0x2F, new byte[4])];
        var data = new List<byte>(count * 44);
        for (int i = 0; i < count; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)(count * 128L));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)(i * 128L));
            data.AddRange(WavPackBuilder.Join(header, parts));
        }

        return [.. data];
    }

    // Mono 16-bit blocks of 128 samples, one set each, whose bitstream of 40 zero bytes decodes to 128 samples of 0 (so no block is muted) and
    // whose header CRC is the one those samples give.
    private static byte[] WavPackDecodedBlocks(int count)
    {
        uint crc = uint.MaxValue;
        for (int i = 0; i < 128; i++)
        {
            crc = unchecked(crc * 3);
        }

        byte[] header = new byte[32];
        "wvpk"u8.CopyTo(header);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 0x410);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 128);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 1 | 4 | 0x800 | 0x1000 | (9u << 23) | 0x10000000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), crc);
        byte[][] parts = [WavPackBuilder.Sub(0x0A, new byte[40]), WavPackBuilder.Sub(0x2F, new byte[4])];
        var data = new List<byte>(count * 80);
        for (int i = 0; i < count; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)(count * 128L));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)(i * 128L));
            data.AddRange(WavPackBuilder.Join(header, parts));
        }

        return [.. data];
    }

    // A Vorbis stream keeps an 8-byte record per packet and its per-packet series (16 bytes, whose
    // x values are also the packets' offsets), plus its share of the page records: 26 bytes in all
    // on linux-x64, and a peak of 26 to 27. The bounds leave about twice that, and for the peak
    // room for macOS on arm64, where the Speex peak measured more than twice linux-x64's.
    [Fact]
    public void ManyVorbisPacketsStayCompact()
    {
        const int packets = 200_000;
        // Vorbis I, section 4.2.2: one channel at 44100 Hz, blocks of 256 and 2048 samples.
        byte[] identification = new byte[30];
        "\u0001vorbis"u8.CopyTo(identification);
        identification[11] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(identification.AsSpan(12), 44100);
        identification[28] = 0xB8;
        identification[29] = 1;
        byte[] comment = [0x03, .. "vorbis"u8, .. OggBuilder.VorbisComment("Encspect"), 0x01];
        var builder = new OggBuilder()
            .Page(5, 0, 2, identification)
            .Page(5, 0, 0, comment, MinimalVorbisSetup);
        byte[] block = [0x0E];
        for (int done = 0; done < packets; done += 250)
        {
            builder.Page(5, (done + 249) * 1024L, done + 250 == packets ? (byte)4 : (byte)0, [.. Enumerable.Repeat(block, 250)]);
        }

        (Report report, long kept, long peak) = Measure("vorbis.ogg", builder.Bytes);

        Assert.Equal((packets - 1) * 1024L, report.Streams.Single().Samples);
        output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * 60L, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * 110L, $"The live heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    // An Opus stream keeps a 24-byte record per stream of each packet, 2 bytes per frame and its
    // per-packet series (16 bytes), plus its share of the page records: 40 bytes in all on
    // linux-x64, and a peak of 41. The bounds leave about twice that, and for the peak room for
    // macOS on arm64, as the Vorbis one does.
    [Fact]
    public void ManyOpusPacketsStayCompact()
    {
        const int packets = 200_000;
        byte[] head = [.. "OpusHead"u8, 1, 1, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 0];
        var builder = new OggBuilder()
            .Page(6, 0, 2, head)
            .Page(6, 0, 0, [.. "OpusTags"u8, .. OggBuilder.VorbisComment("Encspect")]);
        byte[] packet = [0xF8, 0, 0, 0];
        for (int done = 0; done < packets; done += 250)
        {
            builder.Page(6, (done + 250) * 960L, done + 250 == packets ? (byte)4 : (byte)0, [.. Enumerable.Repeat(packet, 250)]);
        }

        (Report report, long kept, long peak) = Measure("opus.opus", builder.Bytes);

        Assert.Equal((packets * 960L) - 312, report.Streams.Single().Samples);
        output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * 90L, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * 120L, $"The live heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    // Mapping family 255 with 255 streams (RFC 7845, section 5.1.1.3) and packets of one byte, whose
    // first stream's self-delimited framing breaks: the streams after it are implied as missing, where
    // each once kept a 20-byte record (about 5 KB per packet of 2 bytes in the file).
    [Fact]
    public void ManyBrokenMultistreamOpusPacketsStayCompact()
    {
        const int packets = 20_000;
        byte[] head = [.. "OpusHead"u8, 1, 255, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 255, 255, 0, .. Enumerable.Range(0, 255).Select(i => (byte)i)];
        var builder = new OggBuilder()
            .Page(6, 0, 2, head)
            .Page(6, 0, 0, [.. "OpusTags"u8, .. OggBuilder.VorbisComment("Encspect")]);
        byte[] packet = [0xFC];
        for (int done = 0; done < packets; done += 250)
        {
            builder.Page(6, (done + 250) * 960L, done + 250 == packets ? (byte)4 : (byte)0, [.. Enumerable.Repeat(packet, 250)]);
        }

        (Report report, long kept, long peak) = Measure("opus-multistream.opus", builder.Bytes);

        Assert.Contains(report.Findings, f => f.Code == "opus.self-delimiting");
        output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * 150L, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * 250L, $"The live heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    // An OpusTags packet of half a million comments of four bytes each (a length of 0): each once became three nodes and its own
    // finding, 919 MB for a 2 MB packet. The comments are records of 12 bytes whose nodes are built in blocks when read, and
    // the finding is one with a count.
    [Fact]
    public void ManyEmptyVorbisCommentsStayBounded()
    {
        const int comments = 500_000;
        byte[] tags = new byte[8 + 4 + 4 + (comments * 4)];
        "OpusTags"u8.CopyTo(tags);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(tags.AsSpan(12), comments);
        byte[] head = [.. "OpusHead"u8, 1, 1, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 0];
        var builder = new OggBuilder().Page(6, 0, 2, head).SpanningPacket(6, tags, 0).Page(6, 960, 4, [0xF8, 0, 0, 0]);

        (Report report, long kept, long peak) = Measure("comments.opus", builder.Bytes, builder.Bytes[..4096]);

        Finding finding = report.Findings.SingleOrDefault(f => f.Code == "vorbis-comment.no-separator")
            ?? throw new Xunit.Sdk.XunitException(string.Join("; ", report.Findings.Select(f => $"{f.Code}: {f.Message}")));
        Assert.Equal(comments, finding.Count);
        Assert.Equal(1, report.Findings.Count(f => f.Code.StartsWith("vorbis-comment.", StringComparison.Ordinal)));
        output.WriteLine($"{kept / 1_000_000} MB kept and {peak / 1_000_000} MB peak for {builder.Length / 1_000_000} MB");
        Assert.True(peak < 80_000_000, $"The live heap grew by {peak / 1_000_000} MB.");
        GC.KeepAlive(report);
    }

    // An APEv2 tag of a million items of 14 bytes each (a five-letter key and an empty value): each item once became seven nodes, a
    // duplicate-key finding and a set entry (a plain tag of 4 M items took 58 s and 8.4 GB). The first 1,024 items become nodes, the next
    // ones are listed in blocks from records and the bytes kept up to a budget of 4 MiB, all are checked, and one finding counts the
    // items that are not listed.
    [Fact]
    public void ManyApeItemsStayBounded()
    {
        const int items = 1_000_000;
        byte[] data = ApeTagFile(items);

        (Report report, long kept, long peak) = Measure("items.mp3", data, data[..4096]);

        Node tag = report.Root.Children.Single(c => c.Kind == NodeKind.Tag);
        Node list = tag.Children.Single(c => c.Name == "Items");
        int listed = list.Children.Count;
        Assert.InRange(listed, 1024 + 100_000, 1024 + ((4 << 20) / 14));
        Assert.Equal(ApeKey(1028), list.Children[1028].Name);
        Assert.Equal(items - listed, Assert.Single(report.Findings, f => f.Code == "ape.items-limited").Count);
        Assert.DoesNotContain(report.Findings, f => f.Code == "ape.duplicate-key");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        output.WriteLine($"{kept / 1000} KB kept and {peak / 1000} KB peak for {data.Length / 1_000_000} MB, {listed} items listed");
        Assert.True(kept < 9_000_000, $"The report keeps {kept / 1_000_000} MB.");
        Assert.True(peak < 14_000_000, $"The live heap grew by {peak / 1_000_000} MB.");
        GC.KeepAlive(report);
    }

    internal static string ApeKey(int i) => new([.. Enumerable.Range(0, 5).Reverse().Select(letter => (char)('a' + (i / (int)Math.Pow(26, letter) % 26)))]);

    // A few Layer III frames, then an APEv2 tag of items with an empty value under the keys aaaaa, aaaab, ..., and its footer.
    internal static byte[] ApeTagFile(int items)
    {
        byte[] audio = Layer3Frames(8);
        var tag = new byte[(items * 14) + 32];
        for (int i = 0; i < items; i++)
        {
            Encoding.ASCII.GetBytes(ApeKey(i)).CopyTo(tag, (i * 14) + 8);
        }

        Span<byte> footer = tag.AsSpan(items * 14);
        "APETAGEX"u8.CopyTo(footer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(footer[8..], 2000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(footer[12..], (uint)tag.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(footer[16..], (uint)items);
        return [.. audio, .. tag];
    }

    // Beginning-of-stream pages of 29 bytes, each of a stream of its own that never ends: each stream cost a node of seven
    // children and findings, 10 KB for 29 bytes, so past a count the streams are not followed and one finding counts their pages.
    [Fact]
    public void ManyLogicalStreamsStayBounded()
    {
        const int streams = 100_000;
        var builder = new OggBuilder();
        for (uint serial = 1; serial <= streams; serial++)
        {
            builder.Page(serial, 0, 2, [0xAB]);
        }

        (Report report, long kept, long peak) = Measure("streams.ogg", builder.Bytes, builder.Bytes[..4096]);

        Node ogg = report.Root.Children.Single();
        Assert.True(ogg.Children.Count(c => c.Name == "Logical Stream") < 5_000);
        Finding finding = report.Findings.Single(f => f.Code == "ogg.too-many-streams");
        Assert.True(finding.Count > streams - 5_000);
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
        output.WriteLine($"{kept / 1_000_000} MB kept and {peak / 1_000_000} MB peak for {builder.Length / 1_000_000} MB");
        Assert.True(peak < 150_000_000, $"The live heap grew by {peak / 1_000_000} MB.");
        GC.KeepAlive(report);
    }

    // The setup header of make_vorbis_opus_fixtures.py's minimal codec: one codebook, a floor of
    // type 1 without partitions, a residue without passes, one mapping, a short and a long mode.
    private static readonly byte[] MinimalVorbisSetup =
    [
        0x05, 0x76, 0x6F, 0x72, 0x62, 0x69, 0x73, 0x00, 0x42, 0x43, 0x56, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x80, 0x62, 0x40, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
    ];

    // The heap the report keeps, and the most the analysis held live at once.
    //
    // Kept is the difference of two readings taken one after the other once the analysis is over:
    // with the report alive, and with it dropped (the report is analyzed again for the caller
    // afterwards). The heap before the analysis is no baseline: the process holds the dead large
    // arrays of the tests that ran before, which the collector reports as live until a later
    // collection lets go of them, at a time that varies by run and platform. Taken across an
    // analysis, that read as 47 MB negative (ubuntu CI, and here in half of the runs) or, on
    // macOS, as 4 MB too much. Two readings in a row see the same leftovers.
    //
    // A single pair is still one observation of a heap the process shares with the runner: on
    // ubuntu-24.04-arm one pair read 321 bytes per packet for a stream that keeps 69 (a parser
    // that is deterministic, whose peak read the usual 109 in the same run), and consecutive
    // readings here differ by tens of megabytes while the analysis of the first test after a big
    // one is still holding what its frame held (the ASF tests). So the pair is taken twice, in two
    // separate analyses, and kept is the smaller difference: noise inflates one pair, a real
    // regression inflates both. A planted per-packet allocation in the SV8 record path fails the
    // tests with it (docs/development.md, "Memory tests").
    //
    // Peak: while the analysis runs, a sampler thread forces a blocking full collection every few
    // milliseconds and reads the heap that survived it from the collector's record of that
    // collection. Reading the heap's size afterwards also counts what the analysis allocated
    // since, which on a busy machine, where the sampler waits to run again, tripled the peak
    // (macOS on x64, 125 bytes per Opus packet where linux-x64 measures 41). It is taken from the
    // heap before the analysis, so those leftovers can only lower it, never fail a test. The forced
    // collections slow the analysis down, so the files stay small. The sampler may not get to read
    // the heap once the analysis has built its report, which a short analysis on a loaded machine
    // finishes first: UnknownSv8PacketsStayCompact, whose bound is on the growth of the peak from 1
    // to 4 million packets, read 992 bytes for the first (macOS on x64) where the second read 3.5
    // MB, and failed with 1.17 bytes per further packet. The heap with the report alive, after the
    // analysis, is the least the analysis held at its end, so it counts as a reading too (the
    // smaller of the two pairs' readings). MeasureExcess is the variant for tests whose bound is on
    // a growth between two counts and so must not depend on the heap before the analysis.
    private static (Report Report, long Kept, long Peak) Measure(string name, byte[] data, byte[]? warmUp = null, AnalysisOptions? options = null)
    {
        (Report report, long kept, long before, long sampled, long atEnd) = Run(name, data, warmUp, options);
        return (report, kept, Math.Max(Math.Max(before, sampled), atEnd) - before);
    }

    // What the analysis holds above the heap it starts and ends with (report alive), as the highest of the
    // live heap read at 32 places in the input and by a thread that reads it every 300 ms, all by
    // the compacting collections of Live, so only live objects count and no reading can be too
    // high. The places are fixed fractions of the input, read after the read that reaches them in
    // any pass over it (ProbeStream), so they do not depend on timing; the thread covers the work
    // after the last read and the stretches between places, and its timing only decides whether
    // it catches a transient that exists. docs/development.md, "Memory tests", gives the
    // measurement, what it can and cannot see, and the failures that led to it. The reference is the
    // higher of the heap before the analysis and the heap at its end, so memory that earlier work
    // releases during the analysis lowers the reading instead of raising it. As with kept, two
    // analyses are taken and the smaller excess counts.
    // The report's size, from two analyses with the heap counted after each. The peak while the series are recorded is not asserted here: the
    // sampler sees every 300 ms and the transient lies below the final heap, so it read 0 for any count and could not fail. The recording
    // buffer's 12 bytes per unit at peak are documented in the design.
    private static (Report Report, long Kept) MeasureKept(string name, byte[] data, byte[]? warmUp = null)
    {
        (Report report, long kept, _, _) = MeasureExcess(name, data, warmUp);
        return (report, kept);
    }

    private static (Report Report, long Kept, long Excess, string Where) MeasureExcess(string name, byte[] data, byte[]? warmUp = null, AnalysisOptions? options = null)
    {
        var analyzer = Analyzer.CreateDefault();
        analyzer.Analyze("warm-up", new MemoryStream(warmUp ?? data[..Math.Min(data.Length, 4096)]), options);
        Live();
        (long before, long withReport, long highest, string where) = ProbedAnalysis(analyzer, name, data, options);
        long withoutReport = Live();
        (long secondBefore, long secondWithReport, long secondHighest, string secondWhere) = ProbedAnalysis(analyzer, name, data, options);
        long secondWithoutReport = Live();
        Report report = analyzer.Analyze(name, new MemoryStream(data), options);
        GC.KeepAlive(data);
        long kept = Math.Min(withReport - withoutReport, secondWithReport - secondWithoutReport);
        long excess = Math.Max(0, highest - Math.Max(before, withReport));
        long secondExcess = Math.Max(0, secondHighest - Math.Max(secondBefore, secondWithReport));
        return (report, kept, Math.Min(excess, secondExcess),
            $"[{where}, before {before}, at end {withReport} | {secondWhere}, before {secondBefore}, at end {secondWithReport}]");
    }

    // The live heap before the analysis, with its report alive after it, and the highest live heap
    // read during it; the report is dropped on return. A measurement whose probes did not fire is a failure, not a pass.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (long Before, long WithReport, long Highest, string Where) ProbedAnalysis(Analyzer analyzer, string name, byte[] data, AnalysisOptions? options)
    {
        var stream = new ProbeStream(data);
        long before = Live();
        bool done = false;
        var sampler = new Thread(() =>
        {
            long next = 0;
            while (!Volatile.Read(ref done))
            {
                if (Environment.TickCount64 >= next || stream.Ended)
                {
                    stream.Record(LiveOnce(), stream.Ended ? "thread after the end" : "thread");
                    next = Environment.TickCount64 + 300;
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
        });
        sampler.IsBackground = true;
        sampler.Start();
        Report report;
        try
        {
            report = analyzer.Analyze(name, stream, options);
        }
        finally
        {
            Volatile.Write(ref done, true);
            sampler.Join();
        }

        Assert.True(stream.FullPasses >= 1, $"No pass over the input reached all {ProbeStream.Places.Length} places ({stream.Probes} places reached in all).");
        long live = Live();
        GC.KeepAlive(report);
        return (before, live, stream.Highest, stream.Where);
    }

    private sealed class ProbeStream(byte[] data) : MemoryStream(data)
    {
        // Prime numerators of 137: irregular, fixed fractions of the input.
        public static readonly int[] Places =
            [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41, 43, 47, 53, 59, 61, 67, 71, 73, 79, 83, 89, 97, 101, 103, 107, 109, 113, 127, 131];

        private const int Denominator = 137;

        private long _highest;
        private int _passed;
        private int _probes;
        private int _fullPasses;
        private long _endedUntil;

        public long Highest { get { lock (this) { return _highest; } } }

        public int Probes => Volatile.Read(ref _probes);

        // Passes that reached every place without a read back before one.
        public int FullPasses => Volatile.Read(ref _fullPasses);

        // True for half a second after a read has reached the end of the input, when what the
        // analysis does next (checks, the stream info, the findings) is read back to back.
        public bool Ended => Environment.TickCount64 < Volatile.Read(ref _endedUntil);

        // Which reading was the highest, and what the collector recorded for the collection that made it.
        public string Where { get; private set; } = "none";

        public void Record(long live, string source)
        {
            lock (this)
            {
                if (live <= _highest)
                {
                    return;
                }

                _highest = live;
                GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
                string generations = string.Join("/", info.GenerationInfo.ToArray().Select(g => $"{g.SizeBeforeBytes >> 10}>{g.SizeAfterBytes >> 10}"));
                Where = $"{live} by {source} (position {Position}, places {_passed}), generations KB before>after {generations}, pinned {info.PinnedObjectsCount}, finalization pending {info.FinalizationPendingCount}, promoted {info.PromotedBytes}, fragmented {info.FragmentedBytes}";
            }
        }

        // After a read, once for the places it reached whatever the pass over the input; a read
        // back before a place makes it count again.
        private void Probe()
        {
            long position = Position;
            int passed = _passed;
            while (passed > 0 && At(passed - 1) > position)
            {
                passed--;
            }

            int reached = passed;
            while (reached < Places.Length && At(reached) <= position)
            {
                reached++;
            }

            if (position >= Length)
            {
                Volatile.Write(ref _endedUntil, Environment.TickCount64 + 500);
            }

            _passed = reached;
            if (reached > passed)
            {
                Interlocked.Add(ref _probes, reached - passed);
                if (reached == Places.Length)
                {
                    Interlocked.Increment(ref _fullPasses);
                }

                Record(LiveOnce(), $"place {reached}");
            }
        }

        private long At(int place) => Length * Places[place] / Denominator;

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            Probe();
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = base.Read(buffer);
            Probe();
            return read;
        }

        public override int ReadByte()
        {
            int value = base.ReadByte();
            Probe();
            return value;
        }
    }

    private static (Report Report, long Kept, long Before, long Sampled, long AtEnd) Run(string name, byte[] data, byte[]? warmUp, AnalysisOptions? options = null)
    {
        var analyzer = Analyzer.CreateDefault();
        analyzer.Analyze("warm-up", new MemoryStream(warmUp ?? data[..Math.Min(data.Length, 4096)]), options);
        long before = Live();
        long withReport = SampledAnalysis(analyzer, name, data, out long sampled, options);
        long withoutReport = Live();
        long secondWithReport = AnalysisWithReport(analyzer, name, data, options);
        long secondWithoutReport = Live();
        Report report = analyzer.Analyze(name, new MemoryStream(data), options);
        GC.KeepAlive(data);
        long kept = Math.Min(withReport - withoutReport, secondWithReport - secondWithoutReport);
        return (report, kept, before, sampled, Math.Min(withReport, secondWithReport));
    }

    // The live heap with the report of an analysis alive; the report is dropped on return.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long AnalysisWithReport(Analyzer analyzer, string name, byte[] data, AnalysisOptions? options = null)
    {
        Report report = analyzer.Analyze(name, new MemoryStream(data), options);
        long live = Live();
        GC.KeepAlive(report);
        return live;
    }

    // The live heap with the report of an analysis alive, read while a sampler thread forces
    // collections during it; the report is dropped on return.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long SampledAnalysis(Analyzer analyzer, string name, byte[] data, out long sampled, AnalysisOptions? options = null)
    {
        bool done = false;
        long shared = 0;
        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                Volatile.Write(ref shared, Math.Max(Volatile.Read(ref shared), LastFullCollection()));
                Thread.Sleep(SampleInterval);
            }
        });
        sampler.IsBackground = true;
        sampler.Start();
        Report report;
        try
        {
            report = analyzer.Analyze(name, new MemoryStream(data), options);
        }
        finally
        {
            Volatile.Write(ref done, true);
            sampler.Join();
        }

        long live = Live();
        GC.KeepAlive(report);
        sampled = Volatile.Read(ref shared);
        return live;
    }

    // The heap that survived the last blocking full collection, as it stood when that collection
    // ended.
    private static long LastFullCollection()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return info.HeapSizeBytes - info.FragmentedBytes;
    }

    // A decoder that charges the floor and nothing else, keeping nothing per analysis.
    private sealed class Charging : IDecoder, IFrameDecoder
    {
        private DecodeContext? _context;

        public string Id => "test";

        public IFrameDecoder Open(DecodeContext context) => new Charging { _context = context };

        public int Decode(in CodedFrame frame)
        {
            _context!.AddWork(DecodeRouteTests.Charge(_context.Setup, frame.Samples));
            return frame.Samples;
        }

        public IReadOnlyList<ChecksumResult> Finish(DecodeEnd end) => [];
    }

    // A library caller that reuses one token for every analysis: the decoding's token is linked to it, and a linked source that is not
    // disposed stays registered on it for good (about 100 bytes each, 20,000 analyses would keep megabytes).
    [Fact]
    public void ManyDecodingAnalysesWithOneTokenLeaveNoLinkedSourceRegistered()
    {
        using var cts = new CancellationTokenSource();
        var analyzer = new Analyzer([], [new DecodeRouteTests.FrameParser()], [], [new Charging()]);
        var options = new AnalysisOptions { CancellationToken = cts.Token, Decoding = new DecodeOptions { Stop = cts.Token } };
        byte[] data = new byte[64];

        for (int i = 0; i < 200; i++)
        {
            analyzer.Analyze("sample.bin", new MemoryStream(data), options);
        }

        long before = Live();
        for (int i = 0; i < 20_000; i++)
        {
            analyzer.Analyze("sample.bin", new MemoryStream(data), options);
        }

        long growth = Live() - before;
        output.WriteLine($"The live heap grew by {growth} bytes over 20,000 analyses.");
        Assert.True(growth < 256 * 1024, $"The live heap grew by {growth} bytes over 20,000 analyses with one token.");
    }

    // One such collection: a reading to compare with a bound of MB, taken while the analysis runs,
    // where the repeats of Live (for what finalizers release) would cost seconds.
    private static long LiveOnce()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return LastFullCollection();
    }

    // The live heap after full, compacting collections that also compact the large object heap,
    // where series and record chunks live, so the space their growth left behind does not count.
    // Read from the collector's record of the last collection, as the sampler does, not from
    // GC.GetTotalMemory: that is the collector's running estimate, not a measurement of what
    // survived. With the segments collector (DOTNET_GCName=libclrgc) it reads 94 MB where the
    // record says 49 MB and the gap changes between calls, which showed as 3.5 MB kept after 1
    // million unknown SV8 packets and 9.4 MB after 4 million. Collections repeat until two
    // readings in a row agree to within 64 KB.
    internal static long Live()
    {
        long previous = long.MaxValue;
        long current = 0;
        for (int i = 0; i < 8 && Math.Abs(previous - current) > 64 * 1024; i++)
        {
            previous = current;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            current = LastFullCollection();
        }

        return current;
    }

    // An M4A file of count silent AAC LC stereo access units of 100 bytes (a channel pair element with
    // a common window, max_sfb 0 and a global gain of 100 in each channel, and an END element, then
    // zero bytes no decoder reads, so that keeping a sample's bytes would show), in one chunk after
    // moov (ISO/IEC 14496-12 8.5 to 8.7).
    private static byte[] Mp4Samples(int count, byte[]? config = null, int padding = 94)
    {
        config ??= [0x12, 0x10];
        static byte[] Box(string type, params byte[][] parts)
        {
            byte[] payload = parts.SelectMany(p => p).ToArray();
            return [.. BigEndian(8 + payload.Length), .. System.Text.Encoding.ASCII.GetBytes(type), .. payload];
        }

        static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

        byte[] unit = [0x21, 0x00, 0x03, 0x20, 0x64, 0x1C, .. new byte[padding]];
        byte[] Moov(int mdatPayload)
        {
            byte[] esds = Box("esds", [0, 0, 0, 0, 0x03, (byte)(20 + config.Length), 0, 1, 0, 0x04, (byte)(15 + config.Length), 0x40, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0x05, (byte)config.Length, .. config, 0x06, 1, 2]);
            byte[] mp4a = Box("mp4a", new byte[6], [0, 1], new byte[8], [0, 2, 0, 16, 0, 0, 0, 0, 0xAC, 0x44, 0, 0], esds);
            byte[] stsz = Box("stsz", [0, 0, 0, 0], BigEndian(0), BigEndian(count), Enumerable.Range(0, count).SelectMany(_ => BigEndian(unit.Length)).ToArray());
            byte[] stbl = Box("stbl",
                Box("stsd", [0, 0, 0, 0], BigEndian(1), mp4a),
                Box("stts", [0, 0, 0, 0], BigEndian(1), BigEndian(count), BigEndian(1024)),
                Box("stsc", [0, 0, 0, 0], BigEndian(1), BigEndian(1), BigEndian(count), BigEndian(1)),
                stsz,
                Box("stco", [0, 0, 0, 0], BigEndian(1), BigEndian(mdatPayload)));
            byte[] mdia = Box("mdia",
                Box("mdhd", [0, 0, 0, 0], new byte[8], BigEndian(44100), BigEndian(count * 1024), [0x55, 0xC4, 0, 0]),
                Box("hdlr", [0, 0, 0, 0], new byte[4], System.Text.Encoding.ASCII.GetBytes("soun"), new byte[13]),
                Box("minf", Box("smhd", new byte[8]), Box("stbl", stbl[8..])));
            byte[] tkhd = Box("tkhd", [0, 0, 0, 3], new byte[8], BigEndian(1), new byte[4], BigEndian(count * 1024 / 44100 * 1000), new byte[60]);
            byte[] mvhd = Box("mvhd", [0, 0, 0, 0], new byte[8], BigEndian(1000), BigEndian(count * 1024 / 44100 * 1000), new byte[80]);
            return Box("moov", mvhd, Box("trak", tkhd, mdia));
        }

        byte[] ftyp = Box("ftyp", System.Text.Encoding.ASCII.GetBytes("M4A "), new byte[4], System.Text.Encoding.ASCII.GetBytes("M4A isom"));
        int moovLength = Moov(0).Length;
        byte[] moov = Moov(ftyp.Length + moovLength + 8);
        byte[] mdat = [.. BigEndian(8 + (count * unit.Length)), .. "mdat"u8, .. Enumerable.Repeat(unit, count).SelectMany(u => u)];
        return [.. ftyp, .. moov, .. mdat];
    }

    // Access units of 1 Hz: 1024 seconds each, so the graph of bitrate over time would take a thousand entries per access unit
    // (the reviewer's file of 200,000 units, 1 MB, took 9 GB). It keeps at most StreamFigures.MaxSeconds entries, and a finding says the
    // rate is implausible.
    [Fact]
    public void AnExplicitSampleRateOfOneHertzKeepsTheGraphBounded()
    {
        byte[] config = [0x17, 0x80, 0x00, 0x00, 0x90];
        byte[] data = Mp4Samples(200_000, config, padding: 0);

        Bounded("rate-1-hz.m4a", data, 45_000_000, "aac.implausible-sample-rate");
    }

    // ADIF stores a record for each raw data block, and a block can be one byte (an END element and its alignment): past the cap the
    // blocks are only measured and counted in the stream's figures, with a finding, and the heap stays near the cap's.
    [Fact]
    public void AdifBlocksPastTheCapStayBounded()
    {
        byte[] header = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "adts", "adif.aac"))[..17];
        byte[] data = [.. header, .. Enumerable.Repeat((byte)0xE0, Adif.MaxBlocks + 1_000)];

        Report report = Bounded("end-blocks.aac", data, 320_000_000, "aac.blocks-limit");

        Assert.Equal((Adif.MaxBlocks + 1_000) * 1024L, Assert.Single(report.Streams).Samples);
    }

    // The same for ADTS: frames of 8 bytes, a header and an END element; the frames past the cap are counted from their headers.
    [Fact]
    public void AdtsFramesPastTheCapStayBounded()
    {
        byte[] first = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "ffmpeg-adts.aac"))[..7];
        byte[] frame = [first[0], first[1], first[2], (byte)((first[3] & 0xFC) | 0), 1, (byte)(((8 & 7) << 5) | (first[5] & 0x1F)), first[6], 0xE0];
        int count = RawAacParser.MaxFrames + 1_000;
        byte[] data = new byte[count * 8];
        for (int i = 0; i < count; i++)
        {
            frame.CopyTo(data, i * 8);
        }

        // One frame past the cap is overwritten with zeros: the counting resyncs after the gap and goes on.
        Array.Clear(data, (RawAacParser.MaxFrames + 500) * 8, 8);

        Report report = Bounded("end-frames.aac", data, 360_000_000, "aac.frames-limit");

        Assert.Equal((count - 1) * 1024L, Assert.Single(report.Streams).Samples);
    }

    // ADTS frames that alternate between two fixed headers in runs of three. Every change once made a log of the stream's raw data
    // blocks with a store of 64 KB, 70 KB of heap for 39 bytes of input (6,000 changes took half a gigabyte); now the logs stop at the
    // stream limit and the frames after it are not read.
    [Fact]
    public void AdtsThatChangesItsHeaderStaysBounded()
    {
        const int runs = 6_000;
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "adts", "stream-change.aac"));
        byte[][] frames = [source[..13], source[503..516]];
        var data = new MemoryStream();
        for (int run = 0; run < runs; run++)
        {
            for (int i = 0; i < 3; i++)
            {
                data.Write(frames[run % 2]);
            }
        }

        (Report report, long kept, long peak) = Measure("alternating.aac", data.ToArray());

        Assert.Equal(RawAacParser.MaxStreams, report.Streams.Count);
        Assert.Equal(runs - 1, Assert.Single(report.Findings, f => f.Code == "aac.stream-changed").Count);
        output.WriteLine($"{kept / 1000} KB kept and {peak / 1000} KB peak for {data.Length / 1000} KB");
        Assert.True(kept < 4_000_000, $"The report keeps {kept / 1_000_000} MB.");
        Assert.True(peak < 6_000_000, $"The live heap grew by {peak / 1_000_000} MB.");
        GC.KeepAlive(report);
    }

    // MPEG-1 Layer III stereo frames at 32 kbps and 44100 Hz, 104 bytes each, whose side
    // information gives the first granule's first channel 50 bytes of main data in its own frame
    // (part2_3_length 400 bits, starting at bit 20) and leaves 18 bytes of ancillary data.
    private static byte[] Layer3Frames(int count)
    {
        byte[] frame = [0xFF, 0xFB, 0x10, 0x00, 0x00, 0x00, 0x01, 0x90, .. new byte[96]];
        return Enumerable.Repeat(frame, count).SelectMany(b => b).ToArray();
    }

    // MPEG-1 Layer II stereo frames at 128 kbps and 44100 Hz, 417 bytes each, with 3 in the first allocation field: subband 0 of
    // the first channel is coded, so the frame has a coded bandwidth of 689 Hz, and the first channel its own bits.
    private static byte[] Layer2Frames(int count)
    {
        byte[] frame = [0xFF, 0xFD, 0x80, 0x00, 0x30, .. new byte[412]];
        return Enumerable.Repeat(frame, count).SelectMany(b => b).ToArray();
    }

    private const long MusepackKeptBound = 90;

    private const long MusepackPeakBound = 120;

    // Bytes per further undetailed SV8 packet of 3 bytes: runs are one record each, so close to 0.
    private const long UndetailedPacketBound = 1;

    private const long DecodingFrameBound = 8;

    // An SV7 stream (libmpcdec r495 streaminfo_read_header_sv7): a header with mid/side, max band 1
    // and no true gapless, then frames of 400 zero bits, which read as subband 0 left out and
    // subband 1 filled with noise, in 32-bit little-endian words from their most significant bit.
    private static byte[] Sv7Frames(int frames)
    {
        var words = new List<uint> { 0x072B504D, (uint)frames, (1u << 30) | (1u << 24) | (10u << 20), 0, 0, 0 };
        long bit = 192;
        void Write(uint value, int count)
        {
            for (int i = count - 1; i >= 0; i--, bit++)
            {
                while (words.Count <= bit >> 5)
                {
                    words.Add(0);
                }

                if (((value >> i) & 1) != 0)
                {
                    words[(int)(bit >> 5)] |= 1u << (31 - (int)(bit & 31));
                }
            }
        }

        Write(116, 8);
        for (int i = 0; i < frames; i++)
        {
            Write(400, 20);
            bit += 400;
        }

        Write(0, 11);
        return words.SelectMany(BitConverter.GetBytes).ToArray()[..(int)(((bit + 31) >> 5) << 2)];
    }

    // An SV8 stream (SV8 specification): a stream header with a sample count of 0, 44.1 kHz, 32
    // bands, stereo and 4^7 frames per audio packet and a valid CRC, ReplayGain version 1, then
    // packets of the key, audio packets with one byte, others empty, and the stream end.
    private static byte[] Sv8EmptyPackets(int packets, ReadOnlySpan<byte> key) =>
        Sv8RepeatedPackets(packets, key, key[0] == 'A' ? new byte[32] : []);

    // The same stream with packets of the key and the payload, which the size field counts with itself
    // and the key (the specification's sizes up to 127 here).
    private static byte[] Sv8RepeatedPackets(int packets, ReadOnlySpan<byte> key, byte[] payload)
    {
        byte[] body = [8, 0, 0, 0x1F, 0x1C];
        uint crc = 0xFFFFFFFF;
        foreach (byte b in body)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
        }

        crc ^= 0xFFFFFFFF;
        byte[] sh = [(byte)'S', (byte)'H', 12, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc, .. body];
        byte[] rg = [(byte)'R', (byte)'G', 12, 1, 0, 0, 0, 0, 0, 0, 0, 0];
        // Filled in place: a list that doubles up to 16 MB and is copied out leaves tens of MB of
        // dead large arrays in the heap for the analysis that follows.
        byte[] tail = "SE\x03"u8.ToArray();
        int packetLength = 3 + payload.Length;
        byte[] data = new byte[4 + sh.Length + rg.Length + (packets * packetLength) + tail.Length];
        "MPCK"u8.CopyTo(data);
        sh.CopyTo(data, 4);
        rg.CopyTo(data, 4 + sh.Length);
        int at = 4 + sh.Length + rg.Length;
        for (int i = 0; i < packets; i++, at += packetLength)
        {
            data[at] = key[0];
            data[at + 1] = key[1];
            data[at + 2] = (byte)packetLength;
            payload.CopyTo(data, at + 3);
        }

        tail.CopyTo(data, at);
        return data;
    }

    // An SV5 stream (libmpcdec 1.2.6 streaminfo_read_header_sv6): a header with a variable bitrate,
    // mid/side, max band 0 and one frame per length, then frames of 32 zero bits, whole words only,
    // as the SV5 encoder writes them.
    private static byte[] Sv5Frames(int frames)
    {
        long bits = 64 + (frames * 52L);
        var data = new byte[(int)(bits >> 5) << 2];
        uint[] words = new uint[data.Length / 4];
        words[0] = (1u << 21) | (5u << 11) | 1u;
        words[1] = (uint)frames;
        for (long i = 0, bit = 64; i < frames; i++, bit += 52)
        {
            for (int b = 0; b < 20; b++)
            {
                long position = bit + b;
                if (((32 >> (19 - b)) & 1) != 0 && position >> 5 < words.Length)
                {
                    words[position >> 5] |= 1u << (31 - (int)(position & 31));
                }
            }
        }

        Buffer.BlockCopy(words, 0, data, 0, data.Length);
        return data;
    }
}
