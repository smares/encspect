using System.Runtime;
using Encspect.Core;
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
        GC.KeepAlive(report);
    }

    // An MP4 file keeps a compact record per access unit, the arrays of its graphs and each raw data
    // block without its Huffman-coded data, and reads its sample tables into arrays of the entries
    // the boxes hold, never the samples' bytes at once (plan, "Large inputs"): 60 bytes kept per
    // sample on linux-x64 and a peak of 100. The bounds leave room for a platform that measures
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

    // An ADTS stream keeps a record per frame and, per raw data block, what its nodes are rebuilt
    // from: the side information without the Huffman-coded scalefactors and spectral data
    // (UnitStore). Measured over the frames of ffmpeg's encoder at 96 kbit/s, repeated: 116 bytes
    // kept per frame on linux-x64 and a peak of 133, of the 281 a frame holds. The bounds leave room
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

    // An Ogg stream of a codec no parser reads keeps a record and a segment table per page, and the
    // scan never holds the pages' nodes or data (64 bytes kept per page and a few more for its
    // segment table and place in its stream, 72 in all on linux-x64, where macOS on arm64
    // measured 120 before the measurement compacted the heap, and a peak of 70 to 85; the bounds
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
    // and its per-frame series: the frames repeat a committed mono fixture of 384-byte frames at
    // 96 kbps, 70 bytes kept per frame on linux-x64 and a peak of 64. The bounds leave room and
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

    private void Bounded(string name, byte[] data, long bound, string code, bool expectFinding = true)
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
    }

    // More packets than are described one by one: the rest are counted, and the heap stays near the cap's.
    [Fact]
    public void PacketsPastTheDetailedOnesAreCounted()
    {
        byte[] data = AsfBuilder.Build(PcmStreamProperties(), new byte[1], (1 << 20) + 5_000, 1);
        Bounded("undetailed.asf", data, 300_000_000, "asf.packets-undetailed");
    }

    // A stream of millions of tiny Windows Media Audio superframes: records are kept up to a count.
    [Fact]
    public void ManyTinyWmaSuperframesAreBounded()
    {
        (byte[] properties, _) = AsfBuilder.Take("asf/external-ffmpeg-wmav2-stereo-44k-128k.wma", 0x0161);
        byte[] data = AsfBuilder.Build(properties, new byte[3], 1_300_000, 1);
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

    // An SV8 audio packet of 4^7 frames in 4 bytes declares about 428 s: the per-second figures are
    // kept only for seconds a packet starts in, so 200,000 such packets cost per packet, not per
    // second they declare.
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

    // Packets the analyzer no longer details, once it has detailed 4,096, are kept as one record per
    // run, so empty packets of an unknown key (3 bytes each) keep about nothing per packet. The
    // bound is on the growth from 1 to 4 million packets: what the report keeps whatever the count
    // (the detailed packets, pools the runtime holds on to) differs by platform and would otherwise
    // dominate.
    [Fact]
    public void UnknownSv8PacketsStayCompact()
    {
        const int fewer = 1_000_000;
        const int packets = 4_000_000;
        byte[] warmUp = Sv8EmptyPackets(5000, "XX"u8);

        (_, long fewerKept, long fewerPeak) = Measure("unknown.mpc", Sv8EmptyPackets(fewer, "XX"u8), warmUp);
        (Report report, long kept, long peak) = Measure("unknown.mpc", Sv8EmptyPackets(packets, "XX"u8), warmUp);

        Assert.Equal("Musepack SV8", report.Streams.Single().Codec);
        Assert.Contains(report.Findings, f => f.Code == "musepack.key-unknown" && f.Message.StartsWith("4000000 packets", StringComparison.Ordinal));
        double keptPerPacket = (kept - fewerKept) / (double)(packets - fewer);
        double peakPerPacket = (peak - fewerPeak) / (double)(packets - fewer);
        output.WriteLine($"{fewerKept} and {kept} bytes kept, {fewerPeak} and {peak} bytes peak: {keptPerPacket:F2} and {peakPerPacket:F2} per further packet");
        Assert.True(keptPerPacket < UndetailedPacketBound, $"The report keeps {keptPerPacket:F2} bytes per further packet.");
        Assert.True(peakPerPacket < UndetailedPacketBound, $"The live heap grew by {peakPerPacket:F2} bytes per further packet.");
        GC.KeepAlive(report);
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

    // An Opus stream keeps a 20-byte record per stream of each packet, 2 bytes per frame and its
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

    // The setup header of make_vorbis_opus_fixtures.py's minimal codec: one codebook, a floor of
    // type 1 without partitions, a residue without passes, one mapping, a short and a long mode.
    private static readonly byte[] MinimalVorbisSetup =
    [
        0x05, 0x76, 0x6F, 0x72, 0x62, 0x69, 0x73, 0x00, 0x42, 0x43, 0x56, 0x01, 0x00, 0x02, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x80, 0x62, 0x40, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
    ];

    // The heap the report keeps, and the most the analysis held live at once: while it runs, a
    // sampler thread forces a blocking full collection every few milliseconds and reads the heap
    // that survived it from the collector's record of that collection. Reading the heap's size
    // afterwards also counts what the analysis allocated since, which on a busy machine, where the
    // sampler waits to run again, tripled the peak (macOS on x64, 125 bytes per Opus packet where
    // linux-x64 measures 41). Reading the collector's last figures without forcing a collection
    // measures nothing where none happens during a short analysis. The forced collections slow the
    // analysis down, so the files stay small.
    private static (Report Report, long Kept, long Peak) Measure(string name, byte[] data, byte[]? warmUp = null)
    {
        var analyzer = Analyzer.CreateDefault();
        analyzer.Analyze("warm-up", new MemoryStream(warmUp ?? data[..Math.Min(data.Length, 4096)]));
        long before = Live();
        long liveBefore = LastFullCollection();
        long peak = liveBefore;
        bool done = false;
        var sampler = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
                Volatile.Write(ref peak, Math.Max(Volatile.Read(ref peak), LastFullCollection()));
                Thread.Sleep(SampleInterval);
            }
        });
        sampler.Start();
        Report report = analyzer.Analyze(name, new MemoryStream(data));
        Volatile.Write(ref done, true);
        sampler.Join();
        long kept = Live() - before;
        GC.KeepAlive(data);
        return (report, kept, Volatile.Read(ref peak) - liveBefore);
    }

    // The heap that survived the last blocking full collection, as it stood when that collection
    // ended.
    private static long LastFullCollection()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        return info.HeapSizeBytes - info.FragmentedBytes;
    }

    // The live heap after a full, compacting collection that also compacts the large object heap,
    // where series and record chunks live, so the space their growth left behind does not count.
    private static long Live()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: false);
    }

    // An M4A file of count silent AAC LC stereo access units of 100 bytes (a channel pair element with
    // a common window, max_sfb 0 and a global gain of 100 in each channel, and an END element, then
    // zero bytes no decoder reads, so that keeping a sample's bytes would show), in one chunk after
    // moov (ISO/IEC 14496-12 8.5 to 8.7).
    private static byte[] Mp4Samples(int count)
    {
        static byte[] Box(string type, params byte[][] parts)
        {
            byte[] payload = parts.SelectMany(p => p).ToArray();
            return [.. BigEndian(8 + payload.Length), .. System.Text.Encoding.ASCII.GetBytes(type), .. payload];
        }

        static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

        byte[] unit = [0x21, 0x00, 0x03, 0x20, 0x64, 0x1C, .. new byte[94]];
        byte[] Moov(int mdatPayload)
        {
            byte[] esds = Box("esds", [0, 0, 0, 0, 0x03, 25, 0, 1, 0, 0x04, 17, 0x40, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x05, 2, 0x12, 0x10, 0x06, 1, 2]);
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

    // MPEG-1 Layer III stereo frames at 32 kbps and 44100 Hz, 104 bytes each, whose side
    // information gives the first granule's first channel 50 bytes of main data in its own frame
    // (part2_3_length 400 bits, starting at bit 20) and leaves 18 bytes of ancillary data.
    private static byte[] Layer3Frames(int count)
    {
        byte[] frame = [0xFF, 0xFB, 0x10, 0x00, 0x00, 0x00, 0x01, 0x90, .. new byte[96]];
        return Enumerable.Repeat(frame, count).SelectMany(b => b).ToArray();
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
        return words.SelectMany(BitConverter.GetBytes).ToArray()[..(int)(((bit + 31) >> 5) << 2)];
    }

    // An SV8 stream (SV8 specification): a stream header with a sample count of 0, 44.1 kHz, 32
    // bands, stereo and 4^7 frames per audio packet and a valid CRC, ReplayGain version 1, then
    // packets of the key, audio packets with one byte, others empty, and the stream end.
    private static byte[] Sv8EmptyPackets(int packets, ReadOnlySpan<byte> key)
    {
        byte[] body = [8, 0, 0, 0x1F, 0x1F];
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
        var data = new List<byte>("MPCK"u8.ToArray());
        data.AddRange(sh);
        data.AddRange(rg);
        for (int i = 0; i < packets; i++)
        {
            data.AddRange(key[0] == 'A' ? [key[0], key[1], 4, 0] : [key[0], key[1], 3]);
        }

        data.AddRange("SE\x03"u8.ToArray());
        return [.. data];
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
