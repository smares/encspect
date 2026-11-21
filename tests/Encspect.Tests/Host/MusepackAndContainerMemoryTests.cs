using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using Encspect.Core;
using Encspect.Formats.Aac;
using Encspect.Formats.TrueHd;
using Encspect.Formats.WavPack;
using Encspect.Host;
using Xunit.Abstractions;

namespace Encspect.Tests;

// The memory tests of Musepack, Matroska and MP4, split from MemoryTests to halve its time (CI shards, scripts/ci/shards.json), measured the same way.
// The classes share the non-parallel collection, so they never overlap each other or any other test.
[Collection(nameof(MemoryTests))]
public sealed class MusepackAndContainerMemoryTests(ITestOutputHelper output) : MemoryTestBase(output)
{
    // An MP4 file keeps a compact record per access unit, the arrays of its graphs and each raw data
    // block without its Huffman-coded data, and reads its sample tables into arrays of the entries
    // the boxes hold, never the samples' bytes at once (docs/architecture.md, "Large inputs"): 60 bytes kept per
    // sample on linux-x64 and a peak of 100, and with the coding series (13 bytes a stereo sample) 74 and 113. The bounds leave room for a platform that measures
    // nearly twice that, as macOS on arm64 did for the Ogg records, and stay below what keeping a
    // sample's node or bytes would cost.
    [Fact]
    public void ManySamplesStayCompact()
    {
        const int samples = 100_000;

        (Report report, long kept, long peak) = Measure("samples.m4a", Mp4Samples(samples));

        Assert.Equal(samples, Assert.Single(report.Streams).Samples / 1024);
        Output.WriteLine($"{kept / samples} bytes kept and {peak / samples} bytes peak per sample");
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
            Mp4Builder.ConstantSizes(unit.Length, (long)chunks * perChunk), Mp4Builder.ChunkOffsets(Enumerable.Repeat(at, chunks))), Unpooled.Repeat(unit, 600));

        (Report report, long kept, long peak) = Measure("overlap.m4a", data);

        Output.WriteLine($"{data.Length / 1000} KB: {kept / 1000} KB kept and {peak / 1000} KB peak");
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

        Output.WriteLine($"{data.Length / 1000} KB: {kept / 1000} KB kept and {peak / 1000} KB peak");
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
        Output.WriteLine($"{Hundredths(keptPerSample)} bytes kept per further sample");
        Assert.True(keptPerSample < 110, $"The report keeps {keptPerSample:F2} bytes per further sample.");
        GC.KeepAlive(few);
        GC.KeepAlive(many);
    }

    // A Matroska file keeps a compact record per block and per frame inside Clusters (the nodes are built when read).
    [Fact]
    public void ManyMatroskaBlocksStayCompact()
    {
        const int blocks = 400_000;
        byte[] data = MatroskaBuilder.OpusBlocks(blocks, blocksPerCluster: 1000);

        (Report report, long kept, long peak) = Measure("blocks.mka", data, data[..4096]);

        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Output.WriteLine($"{kept / blocks} bytes kept and {peak / blocks} bytes peak per block");
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
        Output.WriteLine($"records.mka: {live / 1_000_000} MB more live");
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
        Output.WriteLine($"{segments} Segments read, {kept / 1_000_000} MB kept and {peak / 1_000_000} MB peak");
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
        Output.WriteLine($"laced-opus.mka: {live / 1_000_000} MB more live, {watch.Elapsed.TotalSeconds:0.0} s");
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
        Output.WriteLine($"stripped.mka: {live / 1_000_000} MB more live");
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

    // A Musepack SV7 stream keeps a 24-byte record per frame and its per-frame series and figures,
    // 42 bytes in all on linux-x64, with a peak of 59; the frames' nodes are built in blocks when
    // read. The bounds are about twice that and below what keeping a frame's node would cost.
    [Fact]
    public void ManyMusepackFramesStayCompact()
    {
        const int frames = 100_000;

        (Report report, long kept, long peak) = Measure("frames.mpc", Sv7Frames(frames), Sv7Frames(100));

        Assert.Equal(frames * 1152L - 481, report.Streams.Single().Samples);
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * MusepackKeptBound, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * MusepackPeakBound, $"The live heap grew by {peak / frames} bytes per frame.");

        GC.KeepAlive(report);
    }

    // Decoding an SV8 stream holds one frame's values, the synthesis history and the output block, whatever the frames' count, and keeps nothing per frame: the report keeps
    // what it keeps without decoding and the heap grows by no more per frame. The stream is the audio packets of an encoder's file, 2,000 times (about 100,000 frames).
    [Fact]
    public void DecodingManyMusepackFramesHoldsNothingPerFrame()
    {
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } };
        byte[] data = DecoderWorkTests.MusepackCases.Repeated("external-mpcenc-standard.mpc", 12_000_000_000);

        (Report report, long kept, long peak) = Measure("frames.mpc", data, DecoderWorkTests.MusepackCases.Repeated("external-mpcenc-standard.mpc", 100_000_000), options);

        long samples = report.Streams.Single().Verification!.DecodedSamples;
        long frames = samples / 1152;
        Assert.True(frames > 50_000, $"Only {frames} frames were decoded.");
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame with decoding");
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
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
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
        Output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
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
        Output.WriteLine($"{key} {payloadHex}: {Hundredths(keptPerPacket)} kept and {Hundredths(peakPerPacket)} held per further packet; {fewerWhere} {where}");
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
        Output.WriteLine($"{fewerKept} and {kept} bytes kept, {fewerPeak} and {peak} bytes held above the start and end: {Hundredths(keptPerPacket)} and {Hundredths(peakPerPacket)} per further packet");
        Output.WriteLine($"Highest readings, 1 million packets: {fewerWhere}");
        Output.WriteLine($"Highest readings, 4 million packets: {where}");
        Output.WriteLine(RuntimeSettings());
        Assert.True(keptPerPacket < UndetailedPacketBound, $"The report keeps {keptPerPacket:F2} bytes per further packet.");
        Assert.True(peakPerPacket < UndetailedPacketBound, $"The analysis held {peakPerPacket:F2} more bytes per further packet than it starts and ends with. Highest readings: 1 million packets {fewerWhere}; 4 million packets {where}. {RuntimeSettings()}");
        GC.KeepAlive(report);
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
            byte[] payload = Unpooled.Join(parts);
            return [.. BigEndian(8 + payload.Length), .. System.Text.Encoding.ASCII.GetBytes(type), .. payload];
        }

        static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

        byte[] unit = [0x21, 0x00, 0x03, 0x20, 0x64, 0x1C, .. new byte[padding]];
        byte[] Moov(int mdatPayload)
        {
            byte[] esds = Box("esds", [0, 0, 0, 0, 0x03, (byte)(20 + config.Length), 0, 1, 0, 0x04, (byte)(15 + config.Length), 0x40, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0x05, (byte)config.Length, .. config, 0x06, 1, 2]);
            byte[] mp4a = Box("mp4a", new byte[6], [0, 1], new byte[8], [0, 2, 0, 16, 0, 0, 0, 0, 0xAC, 0x44, 0, 0], esds);
            byte[] stsz = Box("stsz", [0, 0, 0, 0], BigEndian(0), BigEndian(count), Unpooled.Repeat(BigEndian(unit.Length), count));
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

    private const long MusepackKeptBound = 90;

    private const long MusepackPeakBound = 120;

    // Bytes per further undetailed SV8 packet of 3 bytes: runs are one record each, so close to 0.
    private const long UndetailedPacketBound = 1;

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
        return MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(words))[..(int)(((bit + 31) >> 5) << 2)].ToArray();
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
