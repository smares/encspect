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

[CollectionDefinition(nameof(MemoryTests), DisableParallelization = true)]
public class MemoryCollection;

// A long file's report keeps compact records of its frames, and the passes over them never hold
// every frame's decoded side information or nodes at once (docs/architecture.md, "Large inputs"). Measured over a
// stream built in memory, alone, since the heap is the whole process's: what the report keeps,
// and the most the analysis holds live at once (Measure). The peak bounds are about twice what
// linux-x64 measures, so they hold on other platforms, and below what keeping each packet's bytes
// costs: with that regression the Speex and MP4 peaks rose from about 35 and 56 bytes per packet
// to 125 and 161. The MPEG bounds are far below what the parser took when it kept an array per
// frame and listed every frame for each pass (490 bytes kept and 2.8 KB of heap).
[Collection(nameof(MemoryTests))]
public sealed class MemoryTests(ITestOutputHelper output) : MemoryTestBase(output)
{

    [Fact]
    public void ManyFramesStayCompact()
    {
        byte[] data = Layer3Frames(Frames);

        (Report report, long kept, long peak) = Measure("memory.mp3", data, data[..(104 * 100)]);

        Assert.Equal(NodeValue.Integer(Frames, "frames", "frame"), report.Root.Children.Single(c => c.Name == "MPEG Audio").Value);
        Output.WriteLine($"{kept / Frames} bytes kept and {peak / Frames} bytes peak per frame");
        Assert.True(kept < Frames * 250L, $"The report keeps {kept / Frames} bytes per frame.");
        Assert.True(peak < Frames * 350L, $"The live heap grew by {peak / Frames} bytes per frame.");

        // The coding series of the stream (docs/design/coding-graphs.md, "Memory budget"): a Layer III stereo frame adds at most 23 bytes,
        // channel modes 1, stereo coding 1, joint stereo modes 1, block usage 4 and channel bit share 16 (a value and its own x); block
        // sizes is a grouping and costs its map. This frame is stereo, not joint stereo, and has a share, so its x values are the bitrate series'. The coded bandwidth
        // of the entropy pass adds 8 bytes (its values; its x values are the bitrate series' too), so the numeric series keep 16.
        StreamInfo stream = Assert.Single(report.Streams);
        CheckCodingBytes(stream, categoryBytesPerFrame: 6, numericBytesPerFrame: 16);
        Output.WriteLine($"{stream.CategorySeries.Sum(c => c.StorageBytes) / Frames} bytes of category series per frame");
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
        Output.WriteLine($"{kept / Frames} bytes kept and {peak / Frames} bytes peak per frame");
        Assert.True(kept < Frames * 250L, $"The report keeps {kept / Frames} bytes per frame.");
        Assert.True(peak < Frames * 350L, $"The live heap grew by {peak / Frames} bytes per frame.");
        StreamInfo stream = Assert.Single(report.Streams);
        CheckCodingBytes(stream, categoryBytesPerFrame: 2, numericBytesPerFrame: 16);
        Output.WriteLine($"{stream.CategorySeries.Sum(c => c.StorageBytes) / Frames} bytes of category series per frame");
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
                Assert.True(MemoryMarshal.TryGetArray(series.X, out ArraySegment<double> own)
                    && MemoryMarshal.TryGetArray(bitrate.X, out ArraySegment<double> shared) && ReferenceEquals(own.Array, shared.Array),
                    $"{series.Name} copies the bitrate series' x values.");
            }
        }

        Assert.True(numeric <= Frames * (long)numericBytesPerFrame, $"The numeric coding series keep {numeric / (double)Frames} bytes per frame.");
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
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame of {stream.Length / frames} bytes");
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
        Output.WriteLine($"{Hundredths(keptPerFrame)} bytes kept per further frame");
        Assert.True(keptPerFrame < 220, $"The report keeps {keptPerFrame:F2} bytes per further frame.");
        GC.KeepAlive(few);
        GC.KeepAlive(many);
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
        Output.WriteLine($"{kept / pages} bytes kept and {peak / pages} bytes peak per page");
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
        Output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * 80L, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * 110L, $"The live heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    // Decoding a Speex stream holds the packets it keeps back (the last four, and those before the first granule position), one frame's decoder state and the output block,
    // whatever the packets' count, and keeps nothing per packet: the report keeps what it keeps without decoding and the heap grows by no more per packet. A frame of
    // narrowband submode 7 with zero parameters, 200,000 times.
    [Fact]
    public void DecodingManySpeexPacketsHoldsNothingPerPacket()
    {
        const int packets = 200_000;
        byte[] frame = [0x38, .. new byte[60], 0x07, 0x80];
        static byte[] Stream(int count, byte[] frame)
        {
            var builder = new OggBuilder()
                .Page(9, 0, 2, OggBuilder.SpeexHeader(8000, 0))
                .Page(9, 0, 0, OggBuilder.VorbisComment("Encoded with Speex 1.2.1"));
            for (int done = 0; done < count; done += 250)
            {
                builder.Page(9, ((done + 250) * 160L) - 40, done + 250 == count ? (byte)4 : (byte)0, [.. Enumerable.Repeat(frame, 250)]);
            }

            return builder.Bytes;
        }

        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } };

        (Report report, long kept, long peak) = Measure("speex.spx", Stream(packets, frame), Stream(1_000, frame), options);

        Assert.Equal(packets * 160L, report.Streams.Single().Verification!.DecodedSamples);
        Output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet with decoding");
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
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
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

        (Report few, long fewerKept) = MeasureKept("few.ac3", Unpooled.Repeat(source, fewer / perCopy), source);
        (Report many, long kept) = MeasureKept("many.ac3", Unpooled.Repeat(source, frames / perCopy), source);

        CheckAc3CodingGrowth(few, many, fewer, frames, ["Blocks With Block Switching", "Blocks With Coupling", "Blocks With Rematrixing"], categoryBytes: 18, numericBytes: 8);
        double keptPerFrame = (kept - fewerKept) / (double)(frames - fewer);
        Output.WriteLine($"{Hundredths(keptPerFrame)} bytes kept per further frame");
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
        // The fixture's first program frame (an independent frame and a dependent frame of six blocks): a stream holds the same dependent substreams in all its
        // program frames, so the program frames of the fixture with other configurations would be streams of their own.
        byte[] whole = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ac3", "coding-eac3-dependent.eac3"));
        int firstLength = Encspect.Formats.Ac3.SyncFrame.Read(whole).FrameLength;
        byte[] source = whole[..(firstLength + Encspect.Formats.Ac3.SyncFrame.Read(whole.AsSpan(firstLength)).FrameLength)];
        int perCopy = 1;

        (Report few, long fewerKept) = MeasureKept("few.eac3", Unpooled.Repeat(source, fewer / perCopy), source);
        (Report many, long kept) = MeasureKept("many.eac3", Unpooled.Repeat(source, units / perCopy), source);

        // The budget of this stream, from its own units: for each of the four series the values of every unit and its count byte (a fixed copy of the
        // fixture's 31 blocks in 4 units is 35 bytes a unit), not the worst case of 12 values, which would let 17 bytes a unit of regression pass.
        CategorySeries first = Assert.Single(few.Streams).CategorySeries[0];
        double perUnit = 4.0 * (Enumerable.Range(0, first.Count).Sum(u => first.ValueCount(u)) + first.Count) / first.Count;
        CheckAc3CodingGrowth(few, many, fewer, units, ["Blocks With Block Switching", "Blocks With Coupling", "Blocks With Rematrixing", "Blocks With Spectral Extension"],
            categoryBytes: (int)Math.Ceiling(perUnit) + 1, numericBytes: 8);
        double keptPerUnit = (kept - fewerKept) / (double)(units - fewer);
        Output.WriteLine($"{Hundredths(keptPerUnit)} bytes kept per further program frame");
        // 208 bytes measured on linux-x64 (a 16-byte record, the kept bits, the bitrate series' two points and 52 bytes of series for each program frame of 12
        // blocks); the bound leaves a quarter for the other platforms.
        Assert.True(keptPerUnit < 260, $"The report keeps {keptPerUnit:F2} bytes per further program frame.");
        GC.KeepAlive(few);
        GC.KeepAlive(many);
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
        Assert.True(MemoryMarshal.TryGetArray(bandwidth.X, out ArraySegment<double> own)
            && MemoryMarshal.TryGetArray(bitrate.X, out ArraySegment<double> shared) && ReferenceEquals(own.Array, shared.Array),
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
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * DtsKeptBound, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * DtsPeakBound, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    // Decoding holds one frame's values, the filter banks' history and the output block, whatever the frames' count, and keeps nothing per frame: the report keeps what it keeps
    // without decoding and the heap grows by no more per frame. The stream is a stereo encoder file repeated, about 100,000 frames.
    [Fact]
    public void DecodingManyDtsFramesHoldsNothingPerFrame()
    {
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } };
        byte[] data = DecoderWorkTests.DtsCases.Repeated("external-ffmpeg-stereo-44k-256k.dts", 12_000_000_000);

        (Report report, long kept, long peak) = Measure("frames.dts", data, DecoderWorkTests.DtsCases.Repeated("external-ffmpeg-stereo-44k-256k.dts", 100_000_000), options);

        long frames = report.Streams.Single().Verification!.DecodedSamples / 512;
        Assert.True(frames > 50_000, $"Only {frames} frames were decoded.");
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame with decoding");
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
        Output.WriteLine($"{kept / units} bytes kept and {peak / units} bytes peak per unit");
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
        Output.WriteLine($"{kept / units} bytes kept and {peak / units} bytes peak per unit");
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
        byte[] data = Unpooled.Repeat(frame, frames);

        (Report report, long kept, long peak) = Measure("tiny.eac3", data);

        Assert.Equal(frames * 256L, Assert.Single(report.Streams).Samples);
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * TinyKeptBound, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * TinyPeakBound, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    private const long TinyKeptBound = 90;
    private const long TinyPeakBound = 180;

    // An ASF file keeps a compact record per packet and per payload (the nodes are built when read), and its media
    // objects, which go to the codec parser one at a time and are never kept (docs/architecture.md, "Large inputs"). Measured over
    // a Windows Media Audio stream of ffmpeg's encoder (its frames read), a 743-byte superframe repeated, and over PCM payloads: the
    // bounds are far below what keeping each media object's bytes or nodes would cost.
    [Fact]
    public void ManyAsfPacketsStayCompact()
    {
        const int packets = 50_000;
        (byte[] properties, byte[] mediaObject) = AsfBuilder.Take("asf/external-ffmpeg-wmav2-stereo-44k-128k.wma", 0x0161);
        byte[] data = AsfBuilder.Build(properties, mediaObject, packets, 46);

        (Report report, long kept, long peak) = Measure("packets.wma", data, data[..4096]);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("Windows Media Audio 2", stream.Codec);
        // One frame of 2,048 samples per superframe (the fixture's golden report: 44 superframes, 88,064 samples); the last frame adds none.
        Assert.Equal((packets - 1) * 2048L, stream.Samples);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("parse.", StringComparison.Ordinal));
        Output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * AsfWmaKeptBound, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * AsfWmaPeakBound, $"The live heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    private const long AsfWmaKeptBound = 550;
    private const long AsfWmaPeakBound = 550;

    // A version 2 stream of Microsoft's encoder with variable block lengths (about three blocks to a frame in this fixture) keeps, beside the
    // frame records, one size and one noise flag for each block and a coded bandwidth with its own x for the blocks that have one, recorded
    // while the frames are read. The 15 media objects of a fixture (a stereo stream at 128 kbit/s, 65 frames) repeated with continuing sequence
    // numbers, so the blocks the series count are those of the file, whatever damage the repetition makes. linux-x64 measures 74 bytes kept and
    // 124 peak per block, the frame records included (57 kept before the series); the bounds leave about twice that.
    [Fact]
    public void ManyStandardBlocksStayCompact()
    {
        const int packets = 6_000;
        byte[] data = FileOfCycles("asf/external-windows-wma92-cbr-stereo-44k-128.wma", 0x0161, packets);

        (Report report, long kept, long peak) = Measure("blocks.wma", data, data[..4096]);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("Windows Media Audio 2", stream.Codec);
        CategorySeries sizes = Assert.Single(stream.CategorySeries, s => s.Name == "Block Sizes");
        Series bandwidth = Assert.Single(stream.Series, s => s.Name == "Coded Bandwidth");
        long blocks = sizes.Total;
        Assert.True(blocks > 30_000, $"The stream has {blocks} blocks.");
        Assert.True(bandwidth.X.Length > blocks / 2);
        Output.WriteLine($"{kept / blocks} bytes kept and {peak / blocks} bytes peak per block");
        Assert.True(kept < blocks * StandardBlockKeptBound, $"The report keeps {kept / blocks} bytes per block.");
        Assert.True(peak < blocks * StandardBlockPeakBound, $"The live heap grew by {peak / blocks} bytes per block.");
        GC.KeepAlive(report);
    }

    private const long StandardBlockKeptBound = 150;
    private const long StandardBlockPeakBound = 250;

    // Voice keeps a fixed record per packet and per superframe, and builds the nodes when they are read: a Microsoft-encoded media object of
    // 187 bytes with 4 counted superframes, repeated, gives a packet and 3 to 4 superframes for each repetition, none of them kept as nodes.
    // The bounds leave about twice what linux-x64 measures, and are far below what a node for each superframe would cost.
    [Fact]
    public void ManyVoicePacketsStayCompact()
    {
        const int packets = 20_000;
        (byte[] properties, byte[] mediaObject) = AsfBuilder.Take("asf/external-windows-wmavoice-cbr-mono-8k-5.wma", 0x000A);
        byte[] data = AsfBuilder.Build(properties, mediaObject, packets, 60);

        (Report report, long kept, long peak) = Measure("packets-voice.wma", data, data[..4096]);

        Assert.Equal("Windows Media Audio Voice", Assert.Single(report.Streams).Codec);
        Assert.True(Assert.Single(report.Streams).Samples > 0);
        Output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * VoiceKeptBound, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * VoicePeakBound, $"The live heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    private const long VoiceKeptBound = 900;
    private const long VoicePeakBound = 1_200;

    // Packets of 2 bytes, whose header runs past the packet: no superframe is read and a record is kept for each packet.
    [Fact]
    public void ManyTinyVoiceBlocksAreBounded()
    {
        const int blocks = 400_000;
        (byte[] properties, _) = AsfBuilder.Take("asf/voice-packet-header.asf", 0x000A);
        byte[] mediaObject = new byte[2 * 1_000];
        for (int block = 0; block < 1_000; block++)
        {
            mediaObject[block * 2] = 0x07;
            mediaObject[(block * 2) + 1] = 0xE0;
        }

        byte[] data = AsfBuilder.Build(properties, mediaObject, blocks / 1_000, 10);
        Bounded("tiny-voice.asf", data, 300_000_000, "wma.voice-packet-header");
    }

    // A Professional stream keeps, for each frame, a record with where its bits lie and a trace of the lengths and values its nodes show (a few
    // dozen bytes), and for each packet a record; the packets, frames and subframes become nodes when read. The ten media objects of a fixture
    // (a stereo stream at 128 kbit/s, 66 frames) repeated with continuing sequence numbers.
    [Fact]
    public void ManyProfessionalFramesStayCompact()
    {
        const int packets = 6_000;
        byte[] data = FileOfCycles("asf/external-windows-wma10pro-cbr-stereo-44k-128.wma", 0x0162, packets);

        (Report report, long kept, long peak) = Measure("packets.wma", data, data[..8192]);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("Windows Media Audio Professional", stream.Codec);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("wma.", StringComparison.Ordinal));
        long frames = Assert.Single(stream.Nodes[0].Children, n => n.Name == "Frames").Children.Count;
        Assert.Equal(packets / 10 * 66L, frames);
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * ProfessionalKeptBound, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * ProfessionalPeakBound, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    private const long ProfessionalKeptBound = 600;
    private const long ProfessionalPeakBound = 1200;

    // A Lossless stream keeps the same records, and the bits of the run a packet's frames are read from only while they are read. The fifteen
    // media objects of a fixture (a stereo stream, 44 frames of about 4,000 bytes) repeated with continuing sequence numbers. The peak holds
    // about 10 MB that do not grow with the stream (1,100 frames: 3.9 MB, 4,400: 11.2 MB, 8,800: 12.1 MB), so the stream is long enough for
    // them to be a small part of the bound.
    [Fact]
    public void ManyLosslessFramesStayCompact()
    {
        const int packets = 3_000;
        byte[] data = FileOfCycles("asf/external-windows-wma92lossless-stereo-44k-16.wma", 0x0163, packets);

        (Report report, long kept, long peak) = Measure("packets.wma", data, data[..32768]);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("Windows Media Audio Lossless", stream.Codec);

        // At each seam of the repeated file the last packet's bits after the final frame are read as the end of a run.
        Assert.All(report.Findings.Where(f => f.Code.StartsWith("wma.", StringComparison.Ordinal)), f => Assert.Equal("wma.frame-boundary", f.Code));
        long frames = Assert.Single(stream.Nodes[0].Children, n => n.Name == "Frames").Children.Count;
        Assert.Equal(packets / 15 * 44L, frames);
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * LosslessKeptBound, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * LosslessPeakBound, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    // Frames of 7 bits are valid, so 88 media objects of 30,000 bytes hold 3,000,000 of them (one seekable frame, then frames that code no
    // channel): without a limit the records of the frames take about 130 bytes each and the report hundreds of megabytes. With it the report
    // keeps the first 524,288 frames' records, and the samples of all are counted.
    [Fact]
    public void MinimalLosslessFramesStayBounded()
    {
        const int frames = 3_000_000;
        const int blockAlignment = 30_000;
        List<byte[]> objects = LosslessBuilder.TinyStream(blockAlignment, frames);
        int size = 23 + blockAlignment;
        var packets = new List<byte[]>(objects.Count);
        for (int i = 0; i < objects.Count; i++)
        {
            byte[] packet = AsfBuilder.Packet(size, 1, i & 0xFF, 0, blockAlignment, (uint)(i * 46));
            objects[i].CopyTo(packet, 23);
            packets.Add(packet);
        }

        byte[] properties = AsfBuilder.StreamProperties(1, 0x0163, 1, 44100, 88200, blockAlignment, 16, LosslessBuilder.Data());
        byte[] data = AsfBuilder.Assemble(properties, size, packets);

        (Report report, long kept) = MeasureKept("minimal.wma", data, data[..32768]);

        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(frames * 2048L, stream.Samples);
        long listed = Assert.Single(stream.Nodes[0].Children, n => n.Name == "Frames").Children.Count;
        Assert.Equal(524_288, listed);
        Assert.Contains(report.Findings, f => f.Code == "wma.records-limit");
        Output.WriteLine($"{kept / listed} bytes kept per listed frame");
        Assert.True(kept < listed * 200, $"The report keeps {kept / listed} bytes per listed frame.");
        GC.KeepAlive(report);
    }

    private const long LosslessKeptBound = 600;
    private const long LosslessPeakBound = 2500;

    // A file of a fixture's media objects, one to a packet, repeated until there are the given number of packets, with sequence numbers that
    // continue at every seam (the objects of a Professional or Lossless stream carry theirs in the first four bits).
    private static byte[] FileOfCycles(string fixture, int tag, int count)
    {
        (byte[] properties, _) = AsfBuilder.Take(fixture, tag);
        List<byte[]> objects = AsfBuilder.Objects(fixture, tag);
        int size = 23 + objects[0].Length;
        var packets = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            byte[] media = (byte[])objects[i % objects.Count].Clone();
            media[0] = (byte)((media[0] & 0x0F) | ((i & 15) << 4));
            byte[] packet = AsfBuilder.Packet(size, 1, i & 0xFF, 0, (uint)media.Length, (uint)(i * 46));
            media.CopyTo(packet, 23);
            packets.Add(packet);
        }

        return AsfBuilder.Assemble(properties, size, packets);
    }

    // Objects that state millions of entries, in files of tens of megabytes: each list becomes nodes only up to a
    // count, so the heap stays a small fraction of what a node for every entry would take (gigabytes).
    [Fact]
    public void AsfObjectsOfMillionsOfEntriesStayBounded()
    {
        Bounded("indexes.asf", AsfBuilder.Append("minimal", AsfBuilder.IndexOfBlocks(1_000_000)), 150_000_000, "asf.index-missing", expectFinding: false);
        Bounded("codecs.asf", AsfBuilder.InHeader("minimal", AsfBuilder.CodecListOf(1_500_000)), 150_000_000, "asf.too-many-entries");
        Bounded("markers.asf", AsfBuilder.InHeader("minimal", AsfBuilder.MarkersOf(1_500_000)), 150_000_000, "asf.too-many-entries");
        Bounded("commands.asf", AsfBuilder.InHeader("minimal", AsfBuilder.CommandsOf(60_000)), 150_000_000, "asf.too-many-entries");
        Bounded("encryption.asf", AsfBuilder.InHeader("minimal", AsfBuilder.EncryptionRecords(40_000, 40), extension: true), 150_000_000, "asf.too-many-entries");
        Bounded("objects.asf", AsfBuilder.WithUnknownObjects("minimal", 900_000), 150_000_000, "asf.too-many-objects");
    }

    // More packets than are described one by one: the rest are counted, and the heap stays near the cap's.
    [Fact]
    public void PacketsPastTheDetailedOnesAreCounted()
    {
        byte[] data = AsfBuilder.Build(PcmStreamProperties(), new byte[1], (1 << 16) + 5_000, 1);
        Bounded("undetailed.asf", data, 300_000_000, "asf.packets-undetailed");
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
        Output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
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
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * 200L, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * 260L, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    // An Ogg FLAC stream with many header packets lists the first 1,024 metadata blocks as a native file does and counts the rest (4-byte
    // padding blocks, 255 to a page, were 1.6 GB for 5 MB of file); bytes after each frame cost one node and a count, not a node for each.
    [Fact]
    public void ManyOggFlacHeaderPacketsAndTrailingBytesStayCompact()
    {
        const int headers = 60_000;
        const int frames = 20_000;
        byte[] Build(int headerCount, int frameCount)
        {
            const uint serial = 7;
            var ogg = new OggBuilder();
            byte[] info = FlacBuilder.Block(0, FlacBuilder.StreamInfo(4096, 44100, 2, 16, 4096L * frameCount));
            ogg.Page(serial, 0, 2, [0x7F, .. "FLAC"u8, 1, 0, 0, 0, .. "fLaC"u8, .. info]);
            for (int at = 0; at < headerCount; at += 255)
            {
                int count = Math.Min(255, headerCount - at);
                ogg.Page(serial, 0, 0, [.. Enumerable.Range(at, count).Select(i => FlacBuilder.Block(1, [], isLast: i == headerCount - 1))]);
            }

            byte[] frame = [.. FlacBuilder.ConstantFrame(0, 4096, 44100, 2, 16), 1];
            for (int at = 0; at < frameCount; at += 100)
            {
                int count = Math.Min(100, frameCount - at);
                ogg.Page(serial, 4096L * (at + count), (byte)(at + count == frameCount ? 4 : 0), [.. Enumerable.Repeat(frame, count)]);
            }

            return ogg.Bytes;
        }

        (Report report, long kept, long peak) = Measure("headers.oga", Build(headers, frames), Build(300, 100));

        Output.WriteLine($"{kept} bytes kept and {peak} bytes peak for {headers} header packets and {frames} frames with trailing bytes");
        Assert.True(kept < 8_000_000, $"The report keeps {kept} bytes.");
        Assert.True(peak < 14_000_000, $"The live heap grew by {peak} bytes.");
        GC.KeepAlive(report);
    }

    // A WAVE file's samples are never read: the report of a file with 40 MiB of them is as large as that of one with 4 KiB, nothing the analysis
    // allocates depends on their size, and what it holds while it runs (the blocks the format sniffs of MPEG audio and DTS read from the start
    // of the data, about 1 MiB) stays below a bound far under the 40 MiB.
    // The growth from 8 MiB to 40 MiB is read from the bytes allocated, not from the peak: the sniffed blocks are alive for a few milliseconds
    // at the start of an analysis, so whether a sampler's collection falls into them decides if a peak reads 0.26 or 1.05 MB (ubuntu-26.04 read
    // 263,368 for 8 MiB and 1,049,720 for 40 MiB, and 1,058,752 and 1,050,808 locally), while the allocation of both is the same to a few KB.
    // A per-block cost of 24 bytes per KiB of samples would add 786 KB.
    // The kept bytes are bounded absolutely and not compared with those of the 4 KiB file: a reading of them is a difference of two process-wide
    // heap readings, each good to the 64 KB at which Live() stops collecting, so two readings of about 7 KB can differ by tens of KB in either
    // direction (the small file once read -9,368 and the large one 6,872), which a bound of 2 KB over the small one turns into a failure. The bound
    // is above that resolution and below the 786 KB of a per-block cost; the exact allocation check below is what proves there is none.
    [Fact]
    public void TheSamplesOfAWaveFileCostNothing()
    {
        (Report large, long largeKept, long largePeak) = Measure("large.wav", LongInputs.WavWithSamples(40 << 20), LongInputs.WavWithSamples(4096));

        Assert.Equal(40 << 20 >> 2, large.Streams.Single().Samples);
        long mediumAllocated = Allocated("medium.wav", LongInputs.WavWithSamples(8 << 20));
        long largeAllocated = Allocated("large.wav", LongInputs.WavWithSamples(40 << 20));
        Output.WriteLine($"{largeKept} bytes kept for 40 MiB of samples, {largePeak} bytes peak for 40 MiB, {mediumAllocated} and {largeAllocated} bytes allocated for 8 and 40 MiB");
        Assert.True(largeKept < 128 * 1024, $"The report keeps {largeKept} bytes for 40 MiB of samples.");
        Assert.True(largeAllocated < mediumAllocated + 20_000, $"The analysis allocated {largeAllocated - mediumAllocated} bytes more for 40 MiB of samples than for 8 MiB.");
        Assert.True(largePeak < 4_000_000, $"The live heap grew by {largePeak} bytes while the samples were skipped.");
        GC.KeepAlive(large);
    }

    // The bytes the process allocated during one analysis, after a first one that has run everything once; the smaller of two.
    private static long Allocated(string name, byte[] data)
    {
        var analyzer = Analyzer.CreateDefault();
        analyzer.Analyze(name, new MemoryStream(data));
        long least = long.MaxValue;
        for (int pass = 0; pass < 2; pass++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            GC.KeepAlive(analyzer.Analyze(name, new MemoryStream(data)));
            least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
        }

        return least;
    }

    // Lists inside lists multiply the caps on one level (2,048 chunks) into millions of nodes: this file, of 600 lists of 600 empty chunks, asks
    // for 360,000 chunk nodes and a million with their fields. The tree is held to its node budget whatever the file asks (the file of 1,500
    // lists of 1,500 chunks that showed it took 12 s and 1.9 GB before the budget), and the analysis takes a bounded time.
    [Fact]
    public void NestedListsOfManyChunksStayWithinTheNodeBudget()
    {
        byte[] data = LongInputs.WavNestedLists(600, 600);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        (Report report, long kept, long peak) = Measure("nested.wav", data, LongInputs.WavNestedLists(2, 2));
        watch.Stop();

        int nodes = CountNodes(report.Root);
        Output.WriteLine($"{nodes} nodes, {kept / nodes} bytes kept and {peak / nodes} bytes peak per node, {watch.Elapsed.TotalSeconds:F1} s for three analyses");
        Assert.Contains(report.Findings, f => f.Code == "wav.node-limit");
        Assert.InRange(nodes, 250_000, 260_000);
        Assert.True(kept < nodes * 500L, $"The report keeps {kept / nodes} bytes per node.");
        Assert.True(peak < nodes * 700L, $"The live heap grew by {peak / nodes} bytes per node.");
        GC.KeepAlive(report);
    }

    [Theory]
    [InlineData("cue")]
    [InlineData("aiff")]
    public void RepeatedTableChunksStayWithinTheNodeBudget(string kind)
    {
        byte[] data = kind == "cue" ? LongInputs.WavCueChunks(300, 1024) : LongInputs.AiffMarkerChunks(300, 1024);
        byte[] warm = kind == "cue" ? LongInputs.WavCueChunks(2, 4) : LongInputs.AiffMarkerChunks(2, 4);
        (Report report, long kept, long peak) = Measure($"tables.{kind}", data, warm);

        int nodes = CountNodes(report.Root);
        Output.WriteLine($"{nodes} nodes, {kept / nodes} bytes kept and {peak / nodes} bytes peak per node");
        Assert.Contains(report.Findings, f => f.Code is "wav.node-limit" or "aiff.node-limit");
        Assert.InRange(nodes, 250_000, 270_000);
        Assert.True(kept < nodes * 500L, $"The report keeps {kept / nodes} bytes per node.");
        Assert.True(peak < nodes * 700L, $"The live heap grew by {peak / nodes} bytes per node.");
        GC.KeepAlive(report);
    }

    // A ds64 table of a million entries with distinct IDs (12 MB) and a cue chunk of 2 million cue points (48 MB) cost the names they keep, which
    // are capped: the live heap does not grow with them (431 MB and 180 MB of resident memory before the caps). The report keeps the 1,024 entries it lists.
    [Theory]
    [InlineData("ds64")]
    [InlineData("cue")]
    public void BigTablesCostNoMoreThanTheirCaps(string kind)
    {
        byte[] data = kind == "ds64" ? LongInputs.WavDs64Table(1_000_000) : LongInputs.WavCueChunks(1, 2_000_000);
        byte[] warm = kind == "ds64" ? LongInputs.WavDs64Table(10) : LongInputs.WavCueChunks(1, 10);
        (Report report, long kept, long peak) = Measure($"big.{kind}", data, warm);

        Output.WriteLine($"{kept} bytes kept and {peak} bytes peak for {data.Length} bytes of file");
        Assert.True(kept < 3_000_000, $"The report keeps {kept} bytes.");
        Assert.True(peak < 12_000_000, $"The live heap grew by {peak} bytes.");
        GC.KeepAlive(report);
    }

    // The bursts of IEC 61937 in a WAVE file are walked once and only the first 1,024 are listed: 100,000 bursts (51 MB) cost the figures of the
    // stream and the nodes of the listed ones, not a record for each.
    [Fact]
    public void ManyBurstsCostNothingPerBurst()
    {
        (Report report, long kept, long peak) = Measure("bursts.wav", LongInputs.WavBursts(100_000), LongInputs.WavBursts(10));

        Output.WriteLine($"{kept} bytes kept and {peak} bytes peak");
        Node bursts = Fixtures.Resolve(report.Root, "RIFF/Data Chunk/IEC 61937 Bursts").Single();
        Assert.Equal(100_000, bursts.Value?.AsInteger());
        Assert.Equal(1024, bursts.Children.Count);
        Assert.True(kept < 4_000_000, $"The report keeps {kept} bytes.");
        Assert.True(peak < 12_000_000, $"The live heap grew by {peak} bytes.");
        GC.KeepAlive(report);
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

    [Theory]
    [InlineData("wav")]
    [InlineData("aiff")]
    public void ManyChunksStayCompact(string kind)
    {
        byte[] data = LongInputs.Build(kind);
        (Report report, long kept, long peak) = Measure($"chunks.{kind}", data, LongInputs.Build(kind));

        Output.WriteLine($"{kept / 2048} bytes kept and {peak / 2048} bytes peak per listed chunk");
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
        Output.WriteLine($"{kept} bytes kept with decoding and {plainKept} without; held above the end state at the last frame: {fewerHeld} at {fewer} frames and {held} at {frames} ({Hundredths(heldPerFrame)} per further frame)");
        Assert.True(kept < plainKept + 1_000_000, $"Decoding made the report keep {(kept - plainKept) / (double)frames:F2} bytes more per frame.");
        Assert.True(heldPerFrame < DecodingFrameBound, $"Decoding held {heldPerFrame:F2} more bytes per further frame at the last frame than the analysis ends with. {RuntimeSettings()}");
    }

    // The decoding of an AC-3 file holds one frame's records and the overlap of each channel, so what it holds does not grow with the frames. Measured as the FLAC test does, with one
    // frame of ffmpeg's stereo file (blocks with coupling and rematrixing) repeated 50,000 and 200,000 times.
    [Fact]
    public void DecodingManyAc3FramesHoldsNothingPerFrame()
    {
        const int fewer = 50_000;
        const int frames = 200_000;
        byte[] file = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "ac3", "external-ffmpeg-stereo-192k.ac3"));
        byte[] frame = file[(5 * 768)..(6 * 768)];
        byte[] Repeat(int count)
        {
            var bytes = new byte[count * frame.Length];
            for (int i = 0; i < count; i++)
            {
                frame.CopyTo(bytes, i * frame.Length);
            }

            return bytes;
        }

        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(Repeat(100)), new AnalysisOptions { Decoding = new DecodeOptions() });

        (long fewerHeld, long fewerKept) = LastFrameReading("frames.ac3", Repeat(fewer), fewer, 1536);
        (long held, long kept) = LastFrameReading("frames.ac3", Repeat(frames), frames, 1536);
        (_, long plainKept) = LastFrameReading("frames.ac3", Repeat(frames), frames, 1536, isDecoding: false);

        double heldPerFrame = (held - fewerHeld) / (double)(frames - fewer);
        Output.WriteLine($"{kept} bytes kept with decoding and {plainKept} without; held above the end state at the last frame: {fewerHeld} at {fewer} frames and {held} at {frames} ({Hundredths(heldPerFrame)} per further frame)");
        Assert.True(kept < plainKept + 1_000_000, $"Decoding made the report keep {(kept - plainKept) / (double)frames:F2} bytes more per frame.");
        Assert.True(heldPerFrame < DecodingFrameBound, $"Decoding held {heldPerFrame:F2} more bytes per further frame at the last frame than the analysis ends with. {RuntimeSettings()}");
    }

    // The same reading for ALAC, whose frames are one sample each here (the walk lends one frame's buffers and the decoder keeps nothing per frame).
    [Fact]
    public void DecodingManyAlacFramesHoldsNothingPerFrame()
    {
        const int fewer = 50_000;
        const int frames = 200_000;
        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(AlacBuilder.SmallFrames(100)), new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } });

        (long fewerHeld, long fewerKept) = LastFrameReading("frames.m4a", AlacBuilder.SmallFrames(fewer), fewer, 1);
        (long held, long kept) = LastFrameReading("frames.m4a", AlacBuilder.SmallFrames(frames), frames, 1);
        (_, long plainKept) = LastFrameReading("frames.m4a", AlacBuilder.SmallFrames(frames), frames, 1, isDecoding: false);

        double heldPerFrame = (held - fewerHeld) / (double)(frames - fewer);
        Output.WriteLine($"{kept} bytes kept with decoding and {plainKept} without; held above the end state at the last frame: {fewerHeld} at {fewer} frames and {held} at {frames} ({Hundredths(heldPerFrame)} per further frame)");
        Assert.True(kept < plainKept + 1_000_000, $"Decoding made the report keep {(kept - plainKept) / (double)frames:F2} bytes more per frame.");
        Assert.True(heldPerFrame < DecodingFrameBound, $"Decoding held {heldPerFrame:F2} more bytes per further frame at the last frame than the analysis ends with. {RuntimeSettings()}");
    }

    // The measurer holds a frame of overlap, the grids and the counters whatever the length of the stream: the same reading, taken inside the sink's
    // Write of the last frame, grows by nothing per further frame when the stream is measured (docs/design/spectral-analysis.md, "Memory").
    [Fact]
    public void MeasuringManyFlacFramesHoldsNothingPerFrame()
    {
        const int fewer = 10_000;
        const int frames = 40_000;
        var measure = new MeasureOptions();
        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(FlacFrames(100)), new AnalysisOptions { Decoding = new DecodeOptions { Measure = measure } });

        (long fewerHeld, _) = LastFrameReading("frames.flac", FlacFrames(fewer), fewer, 4096, measure: measure);
        (long held, long kept) = LastFrameReading("frames.flac", FlacFrames(frames), frames, 4096, measure: measure);
        (_, long plainKept) = LastFrameReading("frames.flac", FlacFrames(frames), frames, 4096, isDecoding: false);

        double heldPerFrame = (held - fewerHeld) / (double)(frames - fewer);
        Output.WriteLine($"{kept} bytes kept with measuring; held above the end state at the last frame: {fewerHeld} at {fewer} frames and {held} at {frames} ({Hundredths(heldPerFrame)} per further frame)");
        Assert.True(heldPerFrame < DecodingFrameBound, $"Measuring held {heldPerFrame:F2} more bytes per further frame at the last frame than the analysis ends with. {RuntimeSettings()}");

        // The report keeps the rows and the grids: under 1 MiB for a stream, not a figure for each frame.
        Assert.True(kept < plainKept + 1_000_000, $"Measuring made the report keep {kept - plainKept} bytes more.");
    }

    // A long WAVE file is read in slices of 64 KiB, each unpacked into the same arrays: decoding it and measuring it hold nothing per slice, the same reading as
    // for the FLAC frames, and the report keeps the rows and grids and no more. A slice is 16,384 sample frames of 16-bit stereo.
    //
    // The two lengths are a power of two apart: the bounded structures (the grids that merge their columns when they pass 1,024, the series that merge
    // pairs) are in the same phase at both, which they are not at other ratios (what the state holds at the last slice and what moves into the report
    // differ by up to 350 KB between 40 and 960 slices, the sum is constant). The span is wide because the readings of CI runners differ by up to
    // about 13 KB from one analysis to the next for no length-dependent reason (docs/design/spectral-analysis.md, "As built: step 5"): over 480
    // further slices the bound of 8 bytes is 3.8 KB, below that; over 2,400 it is 19 KB, above it, and a list that gains 8 bytes per slice reads 13 bytes per further slice.
    // The state held at the last slice is also bounded alone, so that a leak the span's allowance would hide is still a bound.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodingAndMeasuringALongWaveFileHoldsNothingPerSlice(bool isMeasuring)
    {
        const int fewer = 160;
        const int slices = 2560;
        const int sliceBytes = 64 * 1024;
        var measure = isMeasuring ? new MeasureOptions() : null;
        var warmUp = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink(), Measure = measure } };
        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(LongInputs.WavWithSamples(sliceBytes * 3)), warmUp);

        (long fewerHeld, _) = LastFrameReading("slices.wav", LongInputs.WavWithSamples(fewer * sliceBytes), fewer, 16384, measure: measure);
        (long held, long kept) = LastFrameReading("slices.wav", LongInputs.WavWithSamples(slices * sliceBytes), slices, 16384, measure: measure);
        (_, long plainKept) = LastFrameReading("slices.wav", LongInputs.WavWithSamples(slices * sliceBytes), slices, 16384, isDecoding: false);

        double heldPerSlice = (held - fewerHeld) / (double)(slices - fewer);
        Output.WriteLine($"{kept} bytes kept {(isMeasuring ? "with measuring" : "with decoding")} and {plainKept} without; held above the end state at the last slice: {fewerHeld} at {fewer} slices and {held} at {slices} ({Hundredths(heldPerSlice)} per further slice)");
        // Exact loudness gating keeps the energy of each gating block and each short-term value, up to LoudnessMeter.MaxExact of each (1 MiB each, an
        // array that doubles; docs/design/spectral-analysis.md, "Loudness and loudness range"): this file's noise gives 16,384 / 4,410 = 3.7 blocks a slice,
        // each a block and a short-term value of 8 bytes, twice for the doubling. The 2,400 further slices stay under the cap (about 9,500 values), where
        // the growth stops (LoudnessMeterTests holds the cap itself); any other retention still shows against DecodingFrameBound.
        double keptByGating = isMeasuring ? 2 * 2 * 8 * 16384 / 4410.0 : 0;
        Assert.True(heldPerSlice < DecodingFrameBound + keptByGating, $"The analysis held {heldPerSlice:F2} more bytes per further slice at the last slice than it ends with ({DecodingFrameBound + keptByGating:F0} allowed). {RuntimeSettings()}");
        // The spectrum of a stereo stream also holds the holes of its combined spectrum (147,456 bytes) and, for its pair, the stored bins, the mid and side
        // rows and their two counter sets (988,160 bytes at this file's 44.1 kHz; StreamMeasurer.EstimateBytes): 1.14 MB over the 5 MB before the mid/side spectra.
        Assert.True(held < (isMeasuring ? 6_150_000 : 500_000), $"The analysis held {held} bytes at the last slice above what it ends with.");
        Assert.True(kept < plainKept + (isMeasuring ? 1_000_000 : 100_000), $"The report keeps {kept - plainKept} bytes more.");
    }

    // The allocations of one analysis do not grow with the slices beyond the little each slice's frame takes (its mapped bytes and the host's bookkeeping):
    // the samples are read into one buffer and unpacked into the same arrays.
    [Fact]
    public void ALongWaveFileAllocatesNoBufferPerSlice()
    {
        const int sliceBytes = 64 * 1024;
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } };
        long Allocate(int slices)
        {
            byte[] data = LongInputs.WavWithSamples(slices * sliceBytes);
            var analyzer = Analyzer.CreateDefault();
            analyzer.Analyze("slices.wav", new MemoryStream(data), options);
            long least = long.MaxValue;
            for (int pass = 0; pass < 2; pass++)
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                GC.KeepAlive(analyzer.Analyze("slices.wav", new MemoryStream(data), options));
                least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
            }

            return least;
        }

        long small = Allocate(100);
        long large = Allocate(600);
        double perSlice = (large - small) / 500.0;
        Output.WriteLine($"{small} bytes allocated for 100 slices and {large} for 600 ({Hundredths(perSlice)} per further slice)");
        Assert.True(perSlice < 2048, $"The analysis allocated {perSlice:F0} bytes per further slice of 64 KiB.");
    }

    // The levels hold a record for each channel and a few counters whatever the length of the stream: the same reading as for the spectrum, with
    // only the levels asked for.
    [Fact]
    public void MeasuringTheLevelsOfManyFlacFramesHoldsNothingPerFrame()
    {
        const int fewer = 10_000;
        const int frames = 40_000;
        var measure = new MeasureOptions { Include = MeasurementKinds.Levels };
        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(FlacFrames(100)), new AnalysisOptions { Decoding = new DecodeOptions { Measure = measure } });

        (long fewerHeld, _) = LastFrameReading("frames.flac", FlacFrames(fewer), fewer, 4096, measure: measure);
        (long held, long kept) = LastFrameReading("frames.flac", FlacFrames(frames), frames, 4096, measure: measure);
        (_, long plainKept) = LastFrameReading("frames.flac", FlacFrames(frames), frames, 4096, isDecoding: false);

        double heldPerFrame = (held - fewerHeld) / (double)(frames - fewer);
        Output.WriteLine($"{kept} bytes kept with the levels; held above the end state at the last frame: {fewerHeld} at {fewer} frames and {held} at {frames} ({Hundredths(heldPerFrame)} per further frame)");
        Assert.True(heldPerFrame < DecodingFrameBound, $"Measuring the levels held {heldPerFrame:F2} more bytes per further frame at the last frame than the analysis ends with. {RuntimeSettings()}");
        Assert.True(kept < plainKept + 100_000, $"Measuring the levels made the report keep {kept - plainKept} bytes more.");
    }

    // The records of the measurements stay in the report, so the bound of 64 MiB (docs/design/spectral-analysis.md, "Memory") holds them: 400
    // chained Ogg FLAC streams of 24 s each would keep 400 grids of 0.25 MiB (100 MiB) without it, and the streams past the bound say so.
    // Two bounds: the report keeps at most 64 MiB of records (and 4 MiB of other state) more than without measuring, and the analysis holds at most 8 MiB
    // of working state above the heap it starts and ends with. A bound on the peak less the peak without measuring could not hold either: each peak is
    // read from the heap before its analysis, which the leftovers of earlier work make differ by more than the bound's margin between runs (the plain
    // peak read 15.3 MB here and 1.1 MB on ubuntu-26.04, below the 14.7 MB its report keeps, so the same 76.8 MB failed there and passed here).
    [Fact]
    public void MeasuringManyChainedStreamsStaysWithinTheBound()
    {
        const int streams = 400;
        const int frames = 65;
        var measure = new MeasureOptions();
        var options = new AnalysisOptions { Decoding = new DecodeOptions { Measure = measure } };
        byte[] data = ChainedOggFlac(streams, frames);

        (_, long plainKept, _) = Measure("chain.oga", data, ChainedOggFlac(2, 4), new AnalysisOptions { Decoding = new DecodeOptions() });
        (Report report, long kept, long excess, string where) = MeasureExcess("chain.oga", data, ChainedOggFlac(2, 4), options);

        int measured = report.Streams.Count(s => s.Measurements?.Status == MeasurementStatus.Complete);
        long records = report.Streams.Sum(s => s.Measurements?.Spectrogram?.Power.Length ?? 0L);
        Output.WriteLine($"{report.Streams.Count} streams, {measured} measured, {records} bytes of grids, {kept} bytes kept ({plainKept} without measuring), {excess} bytes above the heap before and after");
        Assert.Equal(streams, report.Streams.Count);
        Assert.InRange(measured, 100, streams - 1);
        Assert.Equal(streams - measured, report.Findings.Where(f => f.Code == "measure.streams-limited").Sum(f => f.Count));
        Assert.True(records <= MeasureMemoryBound, $"The grids hold {records} bytes.");
        Assert.True(kept - plainKept < MeasureMemoryBound + (4L << 20), $"Measuring made the report keep {kept - plainKept} bytes more.");
        Assert.True(excess < 8L << 20, $"Measuring held {excess} bytes above the heap before and after the analysis, {where}. {RuntimeSettings()}");

        GC.KeepAlive(data);
    }

    private const long MeasureMemoryBound = 64L << 20;

    // Mono streams of 8 kHz (a grid of 513 columns takes 33 s), one after another in one file, each a first page, then a page for each frame of 4,096 samples.
    private static byte[] ChainedOggFlac(int streams, int frames)
    {
        var ogg = new OggBuilder();
        for (uint stream = 0; stream < streams; stream++)
        {
            uint serial = 0x464C4143 + stream;
            byte[] info = FlacBuilder.Block(0, FlacBuilder.StreamInfo(4096, 8000, 1, 16, 4096L * frames), isLast: true);
            ogg.Page(serial, 0, 2, [0x7F, .. "FLAC"u8, 1, 0, 0, 0, .. "fLaC"u8, .. info]);
            for (int i = 0; i < frames; i++)
            {
                ogg.Page(serial, 4096L * (i + 1), (byte)(i == frames - 1 ? 4 : 0), FlacBuilder.ConstantFrame(i, 4096, 8000, 1, 16));
            }
        }

        return ogg.Bytes;
    }

    // The live heap read inside the sink's Write of the last frame less the live heap with the report alive after the analysis, and
    // what the report keeps (alive less dropped); each the smaller of two analyses, as for Measure.
    private static (long Held, long Kept) LastFrameReading(string name, byte[] data, int frames, long samplesPerFrame, bool isDecoding = true, MeasureOptions? measure = null)
    {
        long held = long.MaxValue;
        long kept = long.MaxValue;
        for (int pass = 0; pass < 2; pass++)
        {
            (long reading, long withReport) = AnalyzeReadingAtLastFrame(name, data, frames, samplesPerFrame, isDecoding, measure);
            long withoutReport = Live();
            held = Math.Min(held, reading - withReport);
            kept = Math.Min(kept, withReport - withoutReport);
        }

        GC.KeepAlive(data);
        return (held, kept);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (long Reading, long WithReport) AnalyzeReadingAtLastFrame(string name, byte[] data, int frames, long samplesPerFrame, bool isDecoding, MeasureOptions? measure)
    {
        var probe = new LastFrameSink(frames);
        var options = new AnalysisOptions { Decoding = isDecoding ? new DecodeOptions { Output = _ => probe, Measure = measure } : null };
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

    // MP3 frames in an MP4 track of one chunk each, with 16 bytes of another track's between them: the MPEG audio parser keeps the frames' file ranges, not their bytes, and reads
    // them again from the file, so what it keeps and what it holds at its peak do not grow with the bytes of the frames (a copy of them would).
    private static byte[] Mp3InChunks(int frames, bool isPlainFile = false)
    {
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "mpeg-audio", "external-ffmpeg-cbr.mp3"));
        var sizes = new List<int>();
        int first = source.AsSpan().IndexOf(new byte[] { 0xFF, 0xFB });
        int[] offsets = new int[40];
        for (int at = first; sizes.Count < 40 && at + 4 <= source.Length;)
        {
            int length = new Encspect.Formats.MpegAudio.MpegHeader(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(at))).FrameLength();
            offsets[sizes.Count] = at;
            sizes.Add(length);
            at += length;
        }

        var payload = new List<byte>();
        var chunkOffsets = new List<long>();
        var frameSizes = new List<int>();
        int[] starts = offsets;
        for (int n = 0; n < frames; n++)
        {
            // The first frame holds the Info header, which is no audio.
            int k = 1 + (n % (sizes.Count - 1));
            chunkOffsets.Add(payload.Count);
            payload.AddRange(source.AsSpan(starts[k], sizes[k]).ToArray());
            frameSizes.Add(sizes[k]);
            if (!isPlainFile)
            {
                payload.AddRange(new byte[16]);
            }
        }

        if (isPlainFile)
        {
            return [.. payload];
        }

        byte[] entry = Mp4Builder.AacEntry();
        int oti = Array.IndexOf(entry, (byte)0x40);
        entry[oti] = 0x6B;
        return Mp4Builder.FileWithMoov(offset => Mp4Builder.Trak(1, entry, Mp4Builder.Stts(frames, 1152), Mp4Builder.Stsc(1), Mp4Builder.Sizes(frameSizes),
            Mp4Builder.ChunkOffsets(chunkOffsets.Select(o => o + offset))), [.. payload]);
    }

    [Fact]
    public void TheFramesOfMpegAudioInManyStretchesAreReadAgainFromTheFile()
    {
        (Report smallReport, long smallKept, long smallPeak) = Measure("small.mp4", Mp3InChunks(2_000), Mp3InChunks(200));
        (Report largeReport, long largeKept, long largePeak) = Measure("large.mp4", Mp3InChunks(20_000), Mp3InChunks(200));
        (_, long plainSmallKept, long plainSmallPeak) = Measure("small.mp3", Mp3InChunks(2_000, true), Mp3InChunks(200, true));
        (_, long plainLargeKept, long plainLargePeak) = Measure("large.mp3", Mp3InChunks(20_000, true), Mp3InChunks(200, true));

        Assert.Equal(2_000 * 1152, smallReport.Streams.Single().Samples);
        Assert.Equal(20_000 * 1152, largeReport.Streams.Single().Samples);
        long perFrame = (largeKept - smallKept) / 18_000;
        long plainPerFrame = (plainLargeKept - plainSmallKept) / 18_000;
        long peakGrowth = largePeak - smallPeak;
        long plainPeakGrowth = plainLargePeak - plainSmallPeak;
        Output.WriteLine($"{perFrame} bytes kept for each further frame ({plainPerFrame} in an MP3 file of the same frames), the peak grew by {peakGrowth} bytes ({plainPeakGrowth})");
        // What the parser keeps of a frame (its record and side information) is the same as in a file; the stretches add the ranges (16 bytes each, a few times over, with MP4's own tables)
        // and no byte of the frames, which are about 440 bytes each: a copy of them would add that to every frame.
        Assert.True(perFrame <= plainPerFrame + 100, $"The report keeps {perFrame} bytes for each further frame, {plainPerFrame} in a file.");
        Assert.True(peakGrowth <= plainPeakGrowth + 4_000_000, $"The live heap grew by {peakGrowth} bytes for the stretches, {plainPeakGrowth} for a file of the frames.");
        GC.KeepAlive(largeReport);
    }

    // The frames of Mp3InChunks in an A_MPEG/L3 track of a Matroska file, with the two bytes every header starts with stripped (ContentCompAlgo 3): each frame is the settings' two bytes
    // and the block's rest, put back from the file where they are, so the parser keeps two segments of the image for each frame and no byte of it.
    private static byte[] Mp3InMatroska(int frames, bool isStripped)
    {
        byte[] plain = Mp3InChunks(frames, true);
        byte[] settings = [0xFF, 0xFB];
        byte[] encodings = isStripped ? MatroskaBuilder.Element(0x6D80, MatroskaBuilder.Element(0x6240, MatroskaBuilder.Unsigned(0x5031, 0), MatroskaBuilder.Unsigned(0x5032, 1), MatroskaBuilder.Unsigned(0x5033, 0),
            MatroskaBuilder.Element(0x5034, MatroskaBuilder.Unsigned(0x4254, 3), MatroskaBuilder.Element(0x4255, settings)))) : [];
        byte[] rate = BitConverter.GetBytes(44100.0);
        Array.Reverse(rate);
        byte[] entry = MatroskaBuilder.Element(0xAE, MatroskaBuilder.Unsigned(0xD7, 1), MatroskaBuilder.Unsigned(0x73C5, 1), MatroskaBuilder.Unsigned(0x83, 2), MatroskaBuilder.Text(0x86, "A_MPEG/L3"),
            encodings, MatroskaBuilder.Element(0xE1, MatroskaBuilder.Element(0xB5, rate), MatroskaBuilder.Unsigned(0x9F, 2)));
        var clusters = new List<byte[]>();
        var blocks = new List<byte[]>();
        int at = 0;
        for (int n = 0; n < frames; n++)
        {
            int length = new Encspect.Formats.MpegAudio.MpegHeader(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(at))).FrameLength();
            blocks.Add(MatroskaBuilder.SimpleBlock(1, n % 1000, plain[(isStripped ? at + 2 : at)..(at + length)]));
            at += length;
            if (blocks.Count == 1000 || n == frames - 1)
            {
                clusters.Add(MatroskaBuilder.Cluster((n / 1000) * 1000L, [.. blocks]));
                blocks.Clear();
            }
        }

        return MatroskaBuilder.File([MatroskaBuilder.Info(), MatroskaBuilder.Element(0x1654AE6B, entry), .. clusters]);
    }

    [Fact]
    public void TheFramesOfAHeaderStrippedMpegAudioTrackAreReadAgainFromTheFile()
    {
        (Report smallReport, long smallKept, long smallPeak) = Measure("small.mka", Mp3InMatroska(2_000, true), Mp3InMatroska(200, true));
        (Report largeReport, long largeKept, long largePeak) = Measure("large.mka", Mp3InMatroska(20_000, true), Mp3InMatroska(200, true));
        (_, long plainSmallKept, long plainSmallPeak) = Measure("small-plain.mka", Mp3InMatroska(2_000, false), Mp3InMatroska(200, false));
        (_, long plainLargeKept, long plainLargePeak) = Measure("large-plain.mka", Mp3InMatroska(20_000, false), Mp3InMatroska(200, false));

        Assert.Equal(2_000 * 1152, smallReport.Streams.Single().Samples);
        Assert.Equal(20_000 * 1152, largeReport.Streams.Single().Samples);
        long perFrame = (largeKept - smallKept) / 18_000;
        long plainPerFrame = (plainLargeKept - plainSmallKept) / 18_000;
        long peakGrowth = largePeak - smallPeak;
        long plainPeakGrowth = plainLargePeak - plainSmallPeak;
        Output.WriteLine($"{perFrame} bytes kept for each further frame ({plainPerFrame} in a track of the same frames, not stripped), the peak grew by {peakGrowth} bytes ({plainPeakGrowth})");
        // The segments (24 bytes for each of the frame's two pieces), the stretch (16) and the range the region's node lists for the frame's bytes (16) come on top of what the same
        // track not stripped keeps (the container's own records of the blocks included); a copy of the frames' 440 bytes would add that to every frame.
        Assert.True(perFrame <= plainPerFrame + 130, $"The report keeps {perFrame} bytes for each further frame, {plainPerFrame} in the same track not stripped.");
        Assert.True(peakGrowth <= plainPeakGrowth + 5_000_000, $"The live heap grew by {peakGrowth} bytes for the stripped frames, {plainPeakGrowth} for the same track not stripped.");
        GC.KeepAlive(largeReport);
    }

    // Two MP3 tracks of the same frames, a chunk of one and then a chunk of the other all through the file, so that each track's frames lie in as many stretches as it has frames: the
    // lookups over the stretches are by binary search, so the time follows the frames as an MP3 file's does, and what is kept follows them too.
    private static byte[] TwoMp3TracksInterleaved(int frames)
    {
        byte[] plain = Mp3InChunks(frames, true);
        var sizes = new List<int>();
        for (int at = 0; at < plain.Length;)
        {
            int length = new Encspect.Formats.MpegAudio.MpegHeader(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(at))).FrameLength();
            sizes.Add(length);
            at += length;
        }

        var payload = new List<byte>();
        var offsets = new List<long>[] { [], [] };
        for (int i = 0, at = 0; i < sizes.Count; i++)
        {
            for (int track = 0; track < 2; track++)
            {
                offsets[track].Add(payload.Count);
                payload.AddRange(plain.AsSpan(at, sizes[i]).ToArray());
            }

            at += sizes[i];
        }

        byte[] entry = Mp4Builder.AacEntry();
        entry[Array.IndexOf(entry, (byte)0x40)] = 0x6B;
        byte[] Trak(int id, long offset) => Mp4Builder.Trak(id, entry, Mp4Builder.Stts(frames, 1152), Mp4Builder.Stsc(1), Mp4Builder.Sizes(sizes), Mp4Builder.ChunkOffsets(offsets[id - 1].Select(o => o + offset)));
        byte[] ftyp = Mp4Builder.Ftyp();
        long moovLength = Mp4Builder.Box("moov", Mp4Builder.Mvhd(), Trak(1, 0), Trak(2, 0)).Length;
        long start = ftyp.Length + moovLength + 8;
        return Mp4Builder.Join(ftyp, Mp4Builder.Box("moov", Mp4Builder.Mvhd(), Trak(1, start), Trak(2, start)), Mp4Builder.Box("mdat", [.. payload]));
    }

    [Fact]
    public void TheStretchesOfInterleavedMpegAudioTracksCostTimeAndMemoryByTheirFrames()
    {
        const int frames = 20_000;
        byte[] interleaved = TwoMp3TracksInterleaved(frames);
        byte[] plain = Mp3InChunks(frames, true);
        Analyzer.CreateDefault().Analyze("warm.mp3", new MemoryStream(Mp3InChunks(200, true)));
        Analyzer.CreateDefault().Analyze("warm.mp4", new MemoryStream(TwoMp3TracksInterleaved(200)));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Analyzer.CreateDefault().Analyze("plain.mp3", new MemoryStream(plain));
        TimeSpan plainTime = watch.Elapsed;
        watch.Restart();
        Report report = Analyzer.CreateDefault().Analyze("tracks.mp4", new MemoryStream(interleaved));
        TimeSpan tracksTime = watch.Elapsed;

        Assert.Equal(2, report.Streams.Count);
        Assert.All(report.Streams, st => Assert.Equal(frames * 1152, st.Samples));
        Output.WriteLine($"{plainTime.TotalSeconds:F2} s for the MP3 file, {tracksTime.TotalSeconds:F2} s for two interleaved tracks of the same frames");
        // Two tracks are twice the frames; a scan of every stretch for every node would be a hundred times that.
        Assert.True(tracksTime < (plainTime * 6) + TimeSpan.FromSeconds(2), $"{tracksTime} for the tracks, {plainTime} for the file.");

        (_, long kept, long peak) = Measure("tracks.mp4", interleaved, TwoMp3TracksInterleaved(200));
        (_, long smallKept, long smallPeak) = Measure("tracks-small.mp4", TwoMp3TracksInterleaved(2_000), TwoMp3TracksInterleaved(200));
        (_, long plainKept, long plainPeak) = Measure("plain.mp3", plain, Mp3InChunks(200, true));
        (_, long plainSmallKept, long plainSmallPeak) = Measure("plain-small.mp3", Mp3InChunks(2_000, true), Mp3InChunks(200, true));
        long perFrame = (kept - smallKept) / (2 * 18_000);
        long plainPerFrame = (plainKept - plainSmallKept) / 18_000;
        Output.WriteLine($"{perFrame} bytes kept per frame ({plainPerFrame} in a file), the peak grew by {peak - smallPeak} ({plainPeak - plainSmallPeak} in a file)");
        Assert.True(perFrame <= plainPerFrame + 100, $"The report keeps {perFrame} bytes for each further frame, {plainPerFrame} in a file.");
        Assert.True(peak - smallPeak <= 2 * (plainPeak - plainSmallPeak) + 8_000_000, $"The live heap grew by {peak - smallPeak} bytes.");
    }

    // A headerless Matroska TTA track whose Channels element states 2^26 channels: nothing is allocated for them (the parser once took 40 bytes for each).
    [Fact]
    public void ATtaTrackWithManyChannelsAllocatesNothingForThem()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "fuzz", "matroska-tta-track-channels-2-pow-26.bin"));

        (Report report, _, long peak) = Measure("channels.mka", data);

        Output.WriteLine($"{peak} bytes peak");
        Assert.Contains(report.Findings, f => f.Code == "tta.channels-beyond-limit");
        Assert.True(peak < 16_000_000, $"The live heap grew by {peak} bytes.");
        GC.KeepAlive(report);
    }

    // A TTA stream keeps a 24-byte entry per frame and 16 bytes of its bitrate graph (and its per-second figures while they are built); its
    // nodes are built in blocks of 1,024 when read. The frames here are 4 bytes each, the CRC-32 alone, the fewest a file can hold per frame.
    // linux-x64 measures 56 bytes kept and 80 at the peak per frame; the bounds are about twice that.
    [Fact]
    public void ManyTtaFramesStayCompact()
    {
        const int frames = 200_000;
        byte[] data = TtaBuilder.SmallFrames(frames);

        (Report report, long kept, long peak) = Measure("frames.tta", data, TtaBuilder.SmallFrames(100));

        Assert.Equal(frames * TtaBuilder.FrameLength(8000), report.Streams.Single().Samples);
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * 100L, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * 140L, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    // A header of sample rate 1 and 4,294,967,295 samples calls for a seek table of 16 GB: the file holds 8 MB of it, 2,097,152 entries, and every
    // frame lies past the end of the data. Such an entry keeps its 4-byte length for the seek point node and nothing else; a record of 24 bytes
    // for each of them (with the length list beside it) once made the report 7 times the file.
    [Fact]
    public void ATtaSeekTableOfEntriesPastTheEndStaysTheSizeOfTheFile()
    {
        byte[] data = [.. TtaBuilder.Header(1, 1, 16, uint.MaxValue), .. new byte[8 << 20]];

        (Report report, long kept, long peak) = Measure("table.tta", data, data[..4096]);

        Assert.Contains(report.Findings, f => f.Code == "tta.seek-table-truncated");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Output.WriteLine($"{kept / (double)data.Length:F2} times the file kept, {peak / (double)data.Length:F2} at the peak");
        Assert.True(kept < data.Length * 1.5, $"The report keeps {kept / (double)data.Length:F2} times the file.");
        Assert.True(peak < data.Length * 2.5, $"The live heap grew by {peak / (double)data.Length:F2} times the file.");
        GC.KeepAlive(report);
    }

    // Frames of a CRC-32 alone, more than the records keep: the rest are counted, their CRC-32s compared, and the heap stays near the cap's.
    [Fact]
    public void TtaFramesPastTheCapStayBounded()
    {
        int count = Encspect.Formats.Tta.Records.MaxEntries + 1_000;
        byte[] data = TtaBuilder.Stream(100, 1, 16, count, payload: 0);

        Report report = Bounded("capped.tta", data, 450_000_000, "tta.entries-limit");

        Assert.Equal(count * TtaBuilder.FrameLength(100), Assert.Single(report.Streams).Samples);
        Assert.Equal(true, report.Streams.Single().Verification!.IsVerified);
    }

    // A TAK stream keeps a record of about 64 bytes per frame and 16 bytes of its bitrate graph (and its per-second figures while they are built); its nodes are built in
    // blocks of 1,024 when read. The frames here are 11 bytes each, a 5-byte header with its CRC-24 and the CRC-24 of no data, the fewest a frame can hold. The bounds
    // are about twice the 80 bytes kept and at the peak per frame that linux-x64 measures.
    [Fact]
    public void ManyTakFramesStayCompact()
    {
        const int frames = 200_000;
        byte[] data = TakBuilder.SmallFrames(frames);

        (Report report, long kept, long peak) = Measure("frames.tak", data, TakBuilder.SmallFrames(100));

        Assert.Equal(((frames - 1) * 512L) + 100, report.Streams.Single().Samples);
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * KeptPerTakFrame, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * PeakPerTakFrame, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    private const long KeptPerTakFrame = 160;
    private const long PeakPerTakFrame = 160;

    // Frames of a header and a CRC-24 alone, more than the records keep: the rest are counted, their CRC-24s compared, and the heap stays near the cap's.
    [Fact]
    public void TakFramesPastTheCapStayBounded()
    {
        int count = Encspect.Formats.Tak.FrameFacts.MaxEntries + 1_000;
        byte[] data = TakBuilder.SmallFrames(count);

        Report report = Bounded("capped.tak", data, 450_000_000, "tak.frames-limit");

        Assert.Equal(((count - 1) * 512L) + 100, Assert.Single(report.Streams).Samples);
        Assert.Equal(true, report.Streams.Single().Verification!.IsVerified);
    }

    // An ALAC track keeps a record of a few dozen bytes per frame and 16 bytes of its bitrate graph, and nodes only for the first 4,096 frames; the frames
    // here are 6 bytes each, the fewest a frame can hold. The bounds are about twice what linux-x64 measures.
    [Fact]
    public void ManyAlacFramesStayCompact()
    {
        const int frames = 200_000;
        byte[] data = AlacBuilder.SmallFrames(frames);

        (Report report, long kept, long peak) = Measure("frames.m4a", data, AlacBuilder.SmallFrames(100));

        Assert.Equal(frames, report.Streams.Single().Samples);
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * 100L, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * 160L, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    // Frames past the records the report keeps: the rest are walked and counted, and the heap stays near the cap's.
    [Fact]
    public void AlacFramesPastTheCapStayBounded()
    {
        int count = Encspect.Formats.Alac.Records.MaxEntries + 1_000;
        byte[] data = AlacBuilder.SmallFrames(count);

        Report report = Bounded("capped.m4a", data, 600_000_000, "alac.entries-limit");

        Assert.Equal(count, Assert.Single(report.Streams).Samples);
    }

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
        Output.WriteLine($"{kept / blocks} bytes kept and {peak / blocks} bytes peak per block");
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
        Output.WriteLine($"{kept} bytes kept with decoding and {plainKept} without; held above the end state at the last block: {fewerHeld} at {fewer} blocks and {held} at {blocks} ({Hundredths(heldPerBlock)} per further block)");
        Assert.True(kept < plainKept + 1_000_000, $"Decoding made the report keep {(kept - plainKept) / (double)blocks:F2} bytes more per block.");
        Assert.True(heldPerBlock < DecodingFrameBound, $"Decoding held {heldPerBlock:F2} more bytes per further block at the last block than the analysis ends with. {RuntimeSettings()}");
    }

    // A WavPack file of versions 1 to 3 is one bitstream the decoder reads in chunks and decodes in batches of 4,096 samples, so what decoding holds does not grow
    // with the batches. The files hold zero bits, which decode to a stream of the stated length (the CRC does not hold; the finding is one count).
    // linux-x64 reads 1 to 3 bytes per further batch; 3,500 further batches keep a stray 16 KB of heap (seen once on macOS at 600) under the bound.
    [Fact]
    public void DecodingALongWavPack1To3StreamHoldsNothingPerBatch()
    {
        const int fewer = 500;
        const int batches = 4_000;
        byte[] Build(int count) => DecoderWorkTests.WavPackCases.LegacyZeros("external-wv340-high.wv", (count * 4096 / 2) + 4096, (uint)(count * 4096));

        (long fewerHeld, _) = LastFrameReading("legacy.wv", Build(fewer), fewer, 4096);
        (long held, _) = LastFrameReading("legacy.wv", Build(batches), batches, 4096);

        double heldPerBatch = (held - fewerHeld) / (double)(batches - fewer);
        Output.WriteLine($"Held above the end state at the last batch: {fewerHeld} at {fewer} batches and {held} at {batches} ({Hundredths(heldPerBatch)} per further batch)");
        Assert.True(heldPerBatch < DecodingFrameBound, $"Decoding held {heldPerBatch:F2} more bytes per further batch at the last batch than the analysis ends with. {RuntimeSettings()}");
    }

    // A TrueHD file keeps a 16-byte entry per access unit and the figures of its graphs, 60 bytes a unit in all on linux-x64 (69 at peak). The bounds are
    // about twice that.
    [Fact]
    public void ManyTrueHdUnitsStayCompact()
    {
        const int repeats = 4_000;

        (Report report, long kept, long peak) = Measure("units.thd", TrueHdUnits(repeats), TrueHdUnits(10));

        int units = repeats * 12;
        Assert.Equal(units * 40L, report.Streams.Single().Samples);
        Output.WriteLine($"{kept / units} bytes kept and {peak / units} bytes peak per unit");
        Assert.True(kept < units * TrueHdKeptBound, $"The report keeps {kept / units} bytes per unit.");
        Assert.True(peak < units * TrueHdPeakBound, $"The live heap grew by {peak / units} bytes per unit.");
        GC.KeepAlive(report);
    }

    // Past the limit of records (2,097,152 units, about 30 minutes at 48 kHz) a TrueHD unit costs its place in the figures of the graphs and nothing else,
    // so a feature-length file stays bounded: a 2-hour stream has 8.6 million units at 48 kHz and 34.6 million at 192 kHz. The limit is lowered here so that
    // the test can go past it; what a unit costs past it, measured, is projected over the real limit.
    [Fact]
    public void TrueHdUnitsPastTheLimitOfRecordsCostOnlyTheirFigures()
    {
        const int limit = 6_000;
        const double RealRecordLimit = Formats.TrueHd.Analysis.MaxRecords;
        Analyzer limited = new(DefaultParsers.Tags, [.. DefaultParsers.Formats.Select(f => f is TrueHdParser ? new TrueHdParser { RecordLimit = limit } : f)], DefaultParsers.Codecs, DefaultParsers.Decoders);

        // What a unit costs while it has a record, from two sizes under the limit, and what it costs past the limit, from two sizes beyond it.
        (_, long recordedFewer, _) = Measure("units.thd", TrueHdUnits(250), TrueHdUnits(10));
        (_, long recordedMore, _) = Measure("units.thd", TrueHdUnits(500), TrueHdUnits(10));
        (Report fewer, long fewerKept, _) = Measure("units.thd", TrueHdUnits(2_000), TrueHdUnits(10), analyzer: limited);
        (Report more, long moreKept, _) = Measure("units.thd", TrueHdUnits(8_000), TrueHdUnits(10), analyzer: limited);

        const int moreUnits = 8_000 * 12;
        Assert.Equal(moreUnits * 40L, more.Streams.Single().Samples);
        Assert.Equal(limit, more.Root.Children.Single(c => c.Kind == NodeKind.Stream).Children.Count(c => c.Name == "Access Unit"));
        double perRecord = (recordedMore - recordedFewer) / (double)((500 - 250) * 12);
        double slope = (moreKept - fewerKept) / (double)((8_000 - 2_000) * 12);
        double twoHours48 = (RealRecordLimit * perRecord) + ((8_640_000 - RealRecordLimit) * slope);
        double twoHours192 = (RealRecordLimit * perRecord) + ((34_560_000 - RealRecordLimit) * slope);
        Output.WriteLine($"{perRecord:F1} bytes a unit with a record, {slope:F2} bytes a unit past the limit; a 2-hour stream projects to {twoHours48 / 1_000_000:F0} MB at 48 kHz and {twoHours192 / 1_000_000:F0} MB at 192 kHz");
        Assert.True(slope < 4, $"A unit past the limit keeps {slope:F2} bytes.");
        Assert.True(twoHours48 < 200_000_000, $"A 2-hour stream at 48 kHz projects to {twoHours48 / 1_000_000:F0} MB.");
        Assert.True(twoHours192 < 250_000_000, $"A 2-hour stream at 192 kHz projects to {twoHours192 / 1_000_000:F0} MB.");
        GC.KeepAlive(fewer);
        GC.KeepAlive(more);
    }

    // The decoding of a TrueHD file holds the filter histories and planes of the unit at hand, so what it holds does not grow with the units.
    [Fact]
    public void DecodingManyTrueHdUnitsHoldsNothingPerUnit()
    {
        const int fewer = 4_000;
        const int more = 16_000;
        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(TrueHdUnits(10)), new AnalysisOptions { Decoding = new DecodeOptions() });

        (long fewerHeld, _) = LastFrameReading("units.thd", TrueHdUnits(fewer / 12), fewer / 12 * 12, 40);
        (long held, long kept) = LastFrameReading("units.thd", TrueHdUnits(more / 12), more / 12 * 12, 40);
        (_, long plainKept) = LastFrameReading("units.thd", TrueHdUnits(more / 12), more / 12 * 12, 40, isDecoding: false);

        int units = more / 12 * 12;
        double heldPerUnit = (held - fewerHeld) / (double)(units - (fewer / 12 * 12));
        Output.WriteLine($"{kept} bytes kept with decoding and {plainKept} without; {fewerHeld} held at {fewer / 12 * 12} units and {held} at {units} ({Hundredths(heldPerUnit)} per further unit)");
        Assert.True(kept < plainKept + 1_000_000, $"Decoding made the report keep {(kept - plainKept) / (double)units:F2} bytes more per unit.");
        Assert.True(heldPerUnit < DecodingFrameBound, $"Decoding held {heldPerUnit:F2} more bytes per further unit at the last unit than the analysis ends with. {RuntimeSettings()}");
    }

    // A Monkey's Audio file keeps a record of a few words for each of the first 1,048,576 frames, and a frame is its record alone: its nodes are built when
    // read. The frames are MonkeysAudioBuilder.Silence's, 8 bytes each. The bounds are about twice what linux-x64 measures.
    [Fact]
    public void ManyMonkeysAudioFramesStayCompact()
    {
        const int frames = 100_000;

        (Report report, long kept, long peak) = Measure("frames.ape", MonkeysAudioBuilder.Silence(frames), MonkeysAudioBuilder.Silence(100));

        Assert.Equal(frames * 64L, report.Streams.Single().Samples);
        Output.WriteLine($"{kept / frames} bytes kept and {peak / frames} bytes peak per frame");
        Assert.True(kept < frames * MonkeysAudioKeptBound, $"The report keeps {kept / frames} bytes per frame.");
        Assert.True(peak < frames * MonkeysAudioPeakBound, $"The live heap grew by {peak / frames} bytes per frame.");
        GC.KeepAlive(report);
    }

    // The decoding of a Monkey's Audio file holds one frame's bytes and the state of its predictors and filters, so what it holds does not grow with the
    // frames. Measured as the WavPack test above does, at 50,000 and 200,000 frames of 64 samples of silence.
    [Fact]
    public void DecodingManyMonkeysAudioFramesHoldsNothingPerFrame()
    {
        const int fewer = 50_000;
        const int frames = 200_000;
        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(MonkeysAudioBuilder.Silence(100)), new AnalysisOptions { Decoding = new DecodeOptions() });

        (long fewerHeld, long fewerKept) = LastFrameReading("frames.ape", MonkeysAudioBuilder.Silence(fewer), fewer, 64);
        (long held, long kept) = LastFrameReading("frames.ape", MonkeysAudioBuilder.Silence(frames), frames, 64);
        (_, long plainKept) = LastFrameReading("frames.ape", MonkeysAudioBuilder.Silence(frames), frames, 64, isDecoding: false);

        double heldPerFrame = (held - fewerHeld) / (double)(frames - fewer);
        Output.WriteLine($"{kept} bytes kept with decoding and {plainKept} without; held above the end state at the last frame: {fewerHeld} at {fewer} frames and {held} at {frames} ({Hundredths(heldPerFrame)} per further frame)");
        Assert.True(kept < plainKept + 1_000_000, $"Decoding made the report keep {(kept - plainKept) / (double)frames:F2} bytes more per frame.");
        Assert.True(heldPerFrame < DecodingFrameBound, $"Decoding held {heldPerFrame:F2} more bytes per further frame at the last frame than the analysis ends with. {RuntimeSettings()}");
    }

    // The decoding of a TTA file holds one frame's bytes, a chunk of output and the state of its channels, so what it holds does not grow with the frames. Measured
    // as the Monkey's Audio test above does, at 50,000 and 200,000 frames: at a sample rate of 1 Hz a frame is one sample, and of silence 6 bytes with its CRC-32.
    [Fact]
    public void DecodingManyTtaFramesHoldsNothingPerFrame()
    {
        const int fewer = 50_000;
        const int frames = 200_000;
        static byte[] Silence(int count) => TtaEncoder.Encode(1, 16, [new int[count]]).File;
        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(Silence(100)), new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } });

        (long fewerHeld, long fewerKept) = LastFrameReading("frames.tta", Silence(fewer), fewer, 1);
        (long held, long kept) = LastFrameReading("frames.tta", Silence(frames), frames, 1);
        (_, long plainKept) = LastFrameReading("frames.tta", Silence(frames), frames, 1, isDecoding: false);

        double heldPerFrame = (held - fewerHeld) / (double)(frames - fewer);
        Output.WriteLine($"{kept} bytes kept with decoding and {plainKept} without; held above the end state at the last frame: {fewerHeld} at {fewer} frames and {held} at {frames} ({Hundredths(heldPerFrame)} per further frame)");
        Assert.True(kept < plainKept + 1_000_000, $"Decoding made the report keep {(kept - plainKept) / (double)frames:F2} bytes more per frame.");
        Assert.True(heldPerFrame < DecodingFrameBound, $"Decoding held {heldPerFrame:F2} more bytes per further frame at the last frame than the analysis ends with. {RuntimeSettings()}");
    }

    // The decoding of an MPEG audio file holds one frame's bytes and its main data, the buffers of a granule and the state of the filterbank, so what it holds does not grow
    // with the frames. Measured as the TTA test above does, at 50,000 and 200,000 frames of silence in the smallest Layer III frames there are: MPEG-2.5 at 8 kHz, 72 bytes
    // and 576 samples each.
    [Fact]
    public void DecodingManyMpegFramesHoldsNothingPerFrame()
    {
        const int fewer = 50_000;
        const int frames = 200_000;
        static byte[] Silence(int count)
        {
            byte[] frame = new byte[72];
            frame[0] = 0xFF;
            frame[1] = 0xE3;
            frame[2] = 0x18;
            frame[3] = 0xC0;
            return Unpooled.Repeat(frame, count);
        }

        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(Silence(100)), new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } });

        (long fewerHeld, long fewerKept) = LastFrameReading("frames.mp3", Silence(fewer), fewer, 576);
        (long held, long kept) = LastFrameReading("frames.mp3", Silence(frames), frames, 576);
        (_, long plainKept) = LastFrameReading("frames.mp3", Silence(frames), frames, 576, isDecoding: false);

        double heldPerFrame = (held - fewerHeld) / (double)(frames - fewer);
        Output.WriteLine($"{kept} bytes kept with decoding and {plainKept} without; held above the end state at the last frame: {fewerHeld} at {fewer} frames and {held} at {frames} ({Hundredths(heldPerFrame)} per further frame)");
        Assert.True(kept < plainKept + 1_000_000, $"Decoding made the report keep {(kept - plainKept) / (double)frames:F2} bytes more per frame.");
        Assert.True(heldPerFrame < DecodingFrameBound, $"Decoding held {heldPerFrame:F2} more bytes per further frame at the last frame than the analysis ends with. {RuntimeSettings()}");
    }

    // A Layer II stream with the MPEG-2 multichannel extension in 3/2 with an LFE channel: the decoder takes the frame's side information from the parser's feed and holds the
    // buffers of the plan's channels, so what it holds does not grow with the frames. The six frames of the fixture repeated 4,000 and 16,000 times.
    [Fact]
    public void DecodingManyMultichannelMpegFramesHoldsNothingPerFrame()
    {
        const int fewer = 24_000;
        const int frames = 96_000;
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "mpeg-audio", "layer2", "mc-3-2-lfe.mp2"));
        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(source), new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } });

        (long fewerHeld, long fewerKept) = LastFrameReading("frames.mp2", Unpooled.Repeat(source, fewer / 6), fewer, 1152);
        (long held, long kept) = LastFrameReading("frames.mp2", Unpooled.Repeat(source, frames / 6), frames, 1152);
        (_, long plainKept) = LastFrameReading("frames.mp2", Unpooled.Repeat(source, frames / 6), frames, 1152, isDecoding: false);

        double heldPerFrame = (held - fewerHeld) / (double)(frames - fewer);
        Output.WriteLine($"{kept} bytes kept with decoding and {plainKept} without; held above the end state at the last frame: {fewerHeld} at {fewer} frames and {held} at {frames} ({Hundredths(heldPerFrame)} per further frame)");
        Assert.True(kept < plainKept + 1_000_000, $"Decoding made the report keep {(kept - plainKept) / (double)frames:F2} bytes more per frame.");
        Assert.True(heldPerFrame < DecodingFrameBound, $"Decoding held {heldPerFrame:F2} more bytes per further frame at the last frame than the analysis ends with. {RuntimeSettings()}");
    }

    private const long MonkeysAudioKeptBound = 140;

    private const long MonkeysAudioPeakBound = 140;

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
        byte[] data = Unpooled.Repeat(full, 4096);

        Report report = Bounded("budget.wv", data, 15_000_000, "wavpack.block-index-overlap");

        var blocks = new List<Node>(report.Root.Children.Single(n => n.Kind == NodeKind.Stream).Children.Where(c => c.Name == "Block"));
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
        byte[] data = Unpooled.Repeat(full, 700);

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
        byte[] data = Unpooled.Repeat(header, 2_000_000);
        Bounded("empty.wv", data, 100_000_000, "wavpack.no-audio");
    }

    private const long TrueHdKeptBound = 120;

    private const long TrueHdPeakBound = 140;

    // The 12 units of the tiny stereo fixture, repeated: each repetition starts with a major sync that restarts the substream.
    private static byte[] TrueHdUnits(int repeats) => LongInputs.Repeat("truehd/built-tiny-truehd-stereo.thd", repeats);

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

    // A Vorbis stream keeps an 8-byte record per packet, 2 bytes for the end of its highest coded residue partition and its per-packet series (16 bytes, whose
    // x values are also the packets' offsets, and 8 for the coded bandwidth, with the same x values), plus its share of the page records: 36 bytes in all
    // on linux-x64, and a peak of 36 to 37. The bounds leave about twice that, and for the peak
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
        Output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * 60L, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * 110L, $"The live heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    // The decoding of a Vorbis file holds one block's spectrum, overlap and output per channel and the setup's vectors, so what it holds does not grow with the packets. Measured as the
    // MPEG test above does, at 50,000 and 200,000 packets of silence (one byte each, a long block of 1,024 samples).
    [Fact]
    public void DecodingManyVorbisPacketsHoldsNothingPerPacket()
    {
        const int fewer = 50_000;
        const int packets = 200_000;
        static byte[] Silence(int count)
        {
            byte[] identification = new byte[30];
            "\u0001vorbis"u8.CopyTo(identification);
            identification[11] = 1;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(identification.AsSpan(12), 44100);
            identification[28] = 0xB8;
            identification[29] = 1;
            byte[] comment = [0x03, .. "vorbis"u8, .. OggBuilder.VorbisComment("Encspect"), 0x01];
            var builder = new OggBuilder().Page(5, 0, 2, identification).Page(5, 0, 0, comment, MinimalVorbisSetup);
            byte[] block = [0x0E];
            for (int done = 0; done < count; done += 250)
            {
                builder.Page(5, (done + 249) * 1024L, done + 250 == count ? (byte)4 : (byte)0, [.. Enumerable.Repeat(block, 250)]);
            }

            return builder.Bytes;
        }

        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(Silence(500)), new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } });

        // The first packet of a stream returns no samples, so a block is written for each of the others.
        (long fewerHeld, long fewerKept) = LastFrameReading("packets.ogg", Silence(fewer), fewer - 1, 1024);
        (long held, long kept) = LastFrameReading("packets.ogg", Silence(packets), packets - 1, 1024);
        (_, long plainKept) = LastFrameReading("packets.ogg", Silence(packets), packets - 1, 1024, isDecoding: false);

        double heldPerPacket = (held - fewerHeld) / (double)(packets - fewer);
        Output.WriteLine($"{kept} bytes kept with decoding and {plainKept} without; held above the end state at the last packet: {fewerHeld} at {fewer} packets and {held} at {packets} ({Hundredths(heldPerPacket)} per further packet)");
        Assert.True(kept < plainKept + 1_000_000, $"Decoding made the report keep {(kept - plainKept) / (double)packets:F2} bytes more per packet.");
        Assert.True(heldPerPacket < DecodingFrameBound, $"Decoding held {heldPerPacket:F2} more bytes per further packet at the last packet than the analysis ends with. {RuntimeSettings()}");
    }

    // An Opus stream keeps a 24-byte record per stream of each packet, 2 bytes per frame and its
    // per-packet series, including those of the entropy pass, plus its share of the page records:
    // 62 bytes in all on linux-x64, and a peak of 67 (measured 2026-11-15). The bounds leave
    // room for macOS on arm64, as the Vorbis one does.
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
        Output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
        Assert.True(kept < packets * 90L, $"The report keeps {kept / packets} bytes per packet.");
        Assert.True(peak < packets * 120L, $"The live heap grew by {peak / packets} bytes per packet.");
        GC.KeepAlive(report);
    }

    // The decoding of an Opus file holds one packet's frames per stream and the layers' state, so what it holds does not grow with the packets. Measured as the Vorbis
    // test does, at 50,000 and 200,000 packets of CELT silence (a 20 ms frame of three bytes, no pre-skip).
    [Fact]
    public void DecodingManyOpusPacketsHoldsNothingPerPacket()
    {
        const int fewer = 50_000;
        const int packets = 200_000;
        static byte[] Silence(int count)
        {
            byte[] head = [.. "OpusHead"u8, 1, 1, 0, 0, 0x80, 0xBB, 0, 0, 0, 0, 0];
            var builder = new OggBuilder().Page(6, 0, 2, head).Page(6, 0, 0, [.. "OpusTags"u8, .. OggBuilder.VorbisComment("Encspect")]);
            byte[] packet = [0xF8, 0, 0, 0];
            for (int done = 0; done < count; done += 250)
            {
                builder.Page(6, (done + 250) * 960L, done + 250 == count ? (byte)4 : (byte)0, [.. Enumerable.Repeat(packet, 250)]);
            }

            return builder.Bytes;
        }

        Analyzer.CreateDefault().Analyze("warm-up", new MemoryStream(Silence(500)), new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } });

        (long fewerHeld, long fewerKept) = LastFrameReading("packets.opus", Silence(fewer), fewer, 960);
        (long held, long kept) = LastFrameReading("packets.opus", Silence(packets), packets, 960);
        (_, long plainKept) = LastFrameReading("packets.opus", Silence(packets), packets, 960, isDecoding: false);

        double heldPerPacket = (held - fewerHeld) / (double)(packets - fewer);
        Output.WriteLine($"{kept} bytes kept with decoding and {plainKept} without; held above the end state at the last packet: {fewerHeld} at {fewer} packets and {held} at {packets} ({Hundredths(heldPerPacket)} per further packet)");
        Assert.True(kept < plainKept + 1_000_000, $"Decoding made the report keep {(kept - plainKept) / (double)packets:F2} bytes more per packet.");
        Assert.True(heldPerPacket < DecodingFrameBound, $"Decoding held {heldPerPacket:F2} more bytes per further packet at the last packet than the analysis ends with. {RuntimeSettings()}");
    }

    // Decoding holds back at least 120 ms of packets for the end trimming (OpusParser.Decoding.cs, HoldSamples), as their bytes: 64 streams of CELT frames of 2.5 ms (family 255, a packet
    // of 64 self-delimited frames of 3 bytes) are 2,304 packets of about 0.3 KB each; with the 255 streams the format allows the held bytes are about 3 MB. The records of the entropy pass are made when a packet is fed, so the peak stays near the
    // packets' own bytes plus the report (the design's memory table, "Opus").
    [Fact]
    public void HoldingManyStreamsOfShortOpusFramesStaysBounded()
    {
        const int packets = 2_600;
        const int streams = 64;
        byte[] head = [.. "OpusHead"u8, 1, streams, 0, 0, 0x80, 0xBB, 0, 0, 0, 0, 255, streams, 0, .. Enumerable.Range(0, streams).Select(i => (byte)i)];
        var builder = new OggBuilder().Page(7, 0, 2, head).Page(7, 0, 0, [.. "OpusTags"u8, .. OggBuilder.VorbisComment("Encspect")]);
        var packet = new List<byte>();
        for (int s = 0; s < streams; s++)
        {
            packet.Add(0xE0);
            if (s < streams - 1)
            {
                packet.Add(3);
            }

            packet.AddRange([0x11, 0x22, 0x33]);
        }

        byte[] bytes = [.. packet];
        for (int done = 0; done < packets; done += 10)
        {
            builder.Page(7, (done + 10) * 120L, done + 10 == packets ? (byte)4 : (byte)0, [.. Enumerable.Repeat(bytes, 10)]);
        }

        var options = new AnalysisOptions { Decoding = new DecodeOptions { Output = _ => new DecoderWorkTests.NullSink() } };
        (Report report, long kept, long peak) = Measure("holding.opus", builder.Bytes, options: options);

        Output.WriteLine($"{kept / 1_000_000.0:F1} MB kept and {peak / 1_000_000.0:F1} MB peak for {packets} packets of {bytes.Length} bytes");
        Assert.True(peak < 40_000_000, $"The live heap grew by {peak / 1_000_000.0:F1} MB.");
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
        Output.WriteLine($"{kept / packets} bytes kept and {peak / packets} bytes peak per packet");
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
        Output.WriteLine($"{kept / 1_000_000} MB kept and {peak / 1_000_000} MB peak for {builder.Length / 1_000_000} MB");
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
        Output.WriteLine($"{kept / 1000} KB kept and {peak / 1000} KB peak for {data.Length / 1_000_000} MB, {listed} items listed");
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
        Output.WriteLine($"{kept / 1_000_000} MB kept and {peak / 1_000_000} MB peak for {builder.Length / 1_000_000} MB");
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
        Output.WriteLine($"The live heap grew by {growth} bytes over 20,000 analyses.");
        Assert.True(growth < 256 * 1024, $"The live heap grew by {growth} bytes over 20,000 analyses with one token.");
    }

    // ADIF stores a record for each raw data block, and a block can be one byte (an END element and its alignment): past the cap the
    // blocks are only measured and counted in the stream's figures, with a finding, and the heap stays near the cap's.
    [Fact]
    public void AdifBlocksPastTheCapStayBounded()
    {
        byte[] header = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "adts", "adif.aac"))[..17];
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
        byte[] source = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "aac", "adts", "stream-change.aac"));
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
        Output.WriteLine($"{kept / 1000} KB kept and {peak / 1000} KB peak for {data.Length / 1000} KB");
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
        return Unpooled.Repeat(frame, count);
    }

    // MPEG-1 Layer II stereo frames at 128 kbps and 44100 Hz, 417 bytes each, with 3 in the first allocation field: subband 0 of
    // the first channel is coded, so the frame has a coded bandwidth of 689 Hz, and the first channel its own bits.
    private static byte[] Layer2Frames(int count)
    {
        byte[] frame = [0xFF, 0xFD, 0x80, 0x00, 0x30, .. new byte[412]];
        return Unpooled.Repeat(frame, count);
    }

    private const long DecodingFrameBound = 8;
}
