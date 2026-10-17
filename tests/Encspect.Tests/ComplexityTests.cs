using System.Buffers.Binary;
using System.Diagnostics;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

[CollectionDefinition(nameof(ComplexityTests), DisableParallelization = true)]
public class ComplexityCollection;

// Inputs that once took minutes because some work grew with the square of their size, built in
// memory. Each must finish well within a bound that a slow machine meets with the linear code and
// that the quadratic code missed by far (each took 25 s to several minutes on a fast machine). They
// run alone, as the bound is on wall-clock time: beside the rest of the suite, a 4-core macOS
// runner once took 42 s for what takes 3 s.
[Collection(nameof(ComplexityTests))]
public class ComplexityTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Fact]
    public void ManyTagsWithSeekFrames()
    {
        byte[] tag = Id3v2(Frame("SEEK", [0, 0, 0, 0]));
        Report report = Timed(Repeat(tag, 60_000).Concat(Enumerable.Range(0, 256).Select(i => (byte)i)).ToArray());
        Assert.Equal(60_000, report.Root.Children.Count(c => c.Name == "ID3v2"));
    }

    // Packets of 40 bytes that each start a media object of 16 MiB and never finish it: the buffer of an object
    // grows with its fragments, where it was allocated at the size it stated (299 s for 200,000 of them).
    [Fact]
    public void ManyTinyPacketsEachStartingALargeMediaObject()
    {
        const int packets = 200_000;
        (byte[] properties, _) = AsfBuilder.Take("asf/external-ffmpeg-wmav2-stereo-44k-128k.wma", 0x0161);
        var list = new List<byte[]>(packets);
        for (int i = 0; i < packets; i++)
        {
            list.Add(AsfBuilder.Packet(AsfBuilder.SmallPacket, 1, i & 0xFF, 0, 1 << 24, (uint)i));
        }

        Report report = Timed(AsfBuilder.Assemble(properties, AsfBuilder.SmallPacket, list));
        Assert.Contains(report.Findings, f => f.Code == "asf.object-incomplete");
    }

    // Every "ID3" in the window a SEEK frame points to is a candidate; these declare a tag larger
    // than the file whose body starts with padding.
    [Fact]
    public void SearchWindowFullOfCandidates()
    {
        byte[] lead = Id3v2([.. Frame("TIT2", [0, .. "Title"u8]), .. Frame("SEEK", [0, 0, 0, 100])]);
        byte[] candidate = [.. "ID3"u8, 4, 0, 0, 0x7F, 0x7F, 0x7F, 0x7F, 0];
        byte[] data = [.. lead, .. Enumerable.Repeat((byte)1, 100), .. Repeat(candidate, 95_000), .. Enumerable.Repeat((byte)1, 2_000_000)];
        Report report = Timed(data);
        Assert.Contains(report.Findings, f => f.Code == "tag.next-missing");
    }

    // ID3v1 tags whose comment holds an ID3v2 footer signature that is rejected at every anchor.
    [Fact]
    public void ManyTrailingTagsWithRejectedCandidates()
    {
        var tag = new byte[128];
        "TAG"u8.CopyTo(tag);
        "3DI\u0005"u8.CopyTo(tag.AsSpan(118));
        Report report = Timed([.. Enumerable.Range(0, 1024).Select(i => (byte)i), .. Repeat(tag, 60_000)]);
        Assert.Equal(60_000, report.Root.Children.Count(c => c.Name == "ID3v1"));
    }

    // Every frame is an Info frame whose LAME tag's music CRC covers the rest of the file.
    [Fact]
    public void ManyLameTagsWithOverlappingMusicCrcs()
    {
        Report report = Timed(LameFrames(16_000, toEnd: true, musicCrcs: false));
        Assert.Contains(report.Findings, f => f.Code == "mpeg.music-crc-mismatch");
    }

    [Fact]
    public void ManyVbrHeaderFrames()
    {
        Report report = Timed(LameFrames(80_000, toEnd: false, musicCrcs: true));
        Assert.DoesNotContain(report.Findings, f => f.Code is "mpeg.music-crc-mismatch" or "mpeg.lame-tag-crc-mismatch");
    }

    // The music CRCs of overlapping ranges come from one pass over the file; with the right CRCs
    // in every tag, a wrong combination of running CRCs would show as a mismatch.
    [Fact]
    public void OverlappingMusicCrcsAreVerified()
    {
        Report report = Analyzer.CreateDefault().Analyze("crcs.mp3", new MemoryStream(LameFrames(300, toEnd: true, musicCrcs: true)));
        Assert.DoesNotContain(report.Findings, f => f.Code is "mpeg.music-crc-mismatch" or "mpeg.lame-tag-crc-mismatch" or "mpeg.music-crc-not-verified");
    }

    // Free-format MPEG-1 Layer III stereo frames of 36 bytes, the header and zero side information:
    // no frame adds data to the bit reservoir, which once kept every frame (95 s for these).
    [Fact]
    public void ManyFramesWithoutMainData()
    {
        byte[] frame = [0xFF, 0xFB, 0x00, 0x00, .. new byte[32]];
        Report report = Timed(Repeat(frame, 160_000).ToArray());
        Assert.Equal(NodeValue.Integer(160_000, "frames", "frame"), report.Root.Children.Single(c => c.Name == "MPEG Audio").Value);
    }

    // Block headers every 32 bytes that state a size which ends in no header: each is a candidate the
    // scan for a block rejects after a look a megabyte on; the time must stay linear in the data
    // (a megabyte is 2,000 times the headers' distance).
    [Fact]
    public void ManyWavPackHeadersThatEndInNoHeader()
    {
        byte[] template = new byte[32];
        "wvpk"u8.CopyTo(template);
        template[9] = 4;
        template[8] = 0x10;
        template[20] = 1;
        var data = new byte[24 << 20];
        for (int at = 0; at + 32 <= data.Length; at += 32)
        {
            template.CopyTo(data, at);

            // Sizes of 2 more than a multiple of 32, so no end falls on another header, and a little
            // different for each, so the ends are not all in one cache block.
            uint size = 1_044_002 + (uint)(at / 32 % 100 * 32);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(at + 4), size);
        }

        var watch = Stopwatch.StartNew();
        Report report = new Encspect.Host.Analyzer([new ForcedWavPack()]).Analyze("flood.wv", new MemoryStream(data));
        watch.Stop();
        Assert.True(watch.Elapsed < Bound, $"Took {watch.Elapsed.TotalSeconds:F1} s");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
    }

    // Core frame headers every 96 bytes (the smallest frame) that state 16 KiB frames: each is a candidate whose chain the
    // scan looks for 16 KiB on, and finds none; the time must stay linear in the data.
    [Fact]
    public void ManyCoreHeadersThatChainNowhere()
    {
        byte[] header = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "dts", "core-16be.dts"))[..12];
        SetBits(header, 46, 14, 16383);
        var data = new byte[24 << 20];
        for (int at = 0; at + 96 <= data.Length; at += 96)
        {
            header.CopyTo(data, at);
        }

        TimedDts(data);
    }

    // Sync words one after the other, each a header that is invalid, and extension substream headers that state a megabyte.
    [Fact]
    public void ManySyncWordsWithoutHeaders()
    {
        TimedDts([.. Repeat([0x7F, 0xFE, 0x80, 0x01], 6 << 20)]);
        byte[] exss = [0x64, 0x58, 0x20, 0x25, 0, 0, 0x0F, 0x01, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0];
        TimedDts([.. Repeat(exss, 1 << 20)]);
    }

    // An extension substream sync word every 16 bytes, each header stating a size of 4,096 bytes and a megabyte of substream (so the next
    // header lies where the size points) and failing its CRC16: each is a candidate that costs a 4 KiB read and a CRC if checked in full.
    // The search spends a bounded amount of that work, so the time stays linear in the data.
    [Fact]
    public void ManyExtensionSubstreamHeadersThatFailTheirCrc()
    {
        // Sync word, user bits 0, index 0, long header (1), header size 4095, substream size 0xFFFFF, then the first bit of the next field.
        byte[] header = (((new System.Numerics.BigInteger(0x64582025) << 43) | (System.Numerics.BigInteger.One << 32) | (4095L << 20) | 0xFFFFF) << 5).ToByteArray(isUnsigned: true, isBigEndian: true);
        byte[] template = new byte[16];
        header.CopyTo(template, 0);
        var data = new byte[32 << 20];
        for (int at = 0; at + 16 <= data.Length; at += 16)
        {
            template.CopyTo(data, at);
        }

        TimedDts(data);
    }

    // Short chains of core frames, each followed by 4 KiB of the extension substream headers above: each break in the chain starts a search
    // for the next one, and every search once had a CRC allowance of its own (1 MiB each, 190 times the size of the data in all). One
    // allowance for the whole data keeps the cost per byte as that of the headers alone: the same amount of data of each pattern is
    // timed in turn, and the chains must cost no more than a small multiple of the headers (about 10 times before, 1.5 now).
    [Fact]
    public void ShortChainsFollowedByExtensionSubstreamHeadersThatFailTheirCrc()
    {
        byte[] header = (((new System.Numerics.BigInteger(0x64582025) << 43) | (System.Numerics.BigInteger.One << 32) | (4095L << 20) | 0xFFFFF) << 5).ToByteArray(isUnsigned: true, isBigEndian: true);
        byte[] template = new byte[16];
        header.CopyTo(template, 0);
        byte[] frame = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "dts", "core-16be.dts"))[..512];
        const int size = 16 << 20;
        const int group = (3 * 512) + 4096;
        var flood = new byte[size];
        var groups = new byte[size];
        for (int at = 0; at + 16 <= size; at += 16)
        {
            template.CopyTo(flood, at);
        }

        for (int at = 0; at + group <= size; at += group)
        {
            for (int i = 0; i < 3; i++)
            {
                frame.CopyTo(groups, at + (i * 512));
            }

            for (int junk = at + (3 * 512); junk < at + group; junk += 16)
            {
                template.CopyTo(groups, junk);
            }
        }

        TimeSpan floodTime = TimedDtsAnalysis(flood);
        TimeSpan groupsTime = TimedDtsAnalysis(groups);
        Assert.True(groupsTime < (3 * floodTime) + TimeSpan.FromSeconds(1), $"The chains took {groupsTime.TotalSeconds:F1} s, the headers alone {floodTime.TotalSeconds:F1} s");
    }

    private static TimeSpan TimedDtsAnalysis(byte[] data)
    {
        var watch = Stopwatch.StartNew();
        Report report = new Analyzer([new ForcedDts()]).Analyze("flood.dts", new MemoryStream(data));
        watch.Stop();
        Assert.True(watch.Elapsed < Bound, $"Took {watch.Elapsed.TotalSeconds:F1} s");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        return watch.Elapsed;
    }

    // Core frames whose arrangement and LFE flag change at every frame still chain (they share their form and sampling frequency), and each
    // change once made a stream, a finding and the nodes of both: 24 s for 2 MiB, 548 s for 8 MiB. The streams are limited, the changes
    // counted in one finding, and the time is linear.
    [Fact]
    public void ACoreConfigurationThatChangesAtEveryFrame()
    {
        byte[] frame = File.ReadAllBytes(Path.Combine(Fixtures.FixtureDirectory, "dts", "core-16be.dts"))[..512];
        var data = new byte[8 << 20];
        for (int i = 0; i * 512 < data.Length; i++)
        {
            frame.CopyTo(data, i * 512);
            byte[] header = frame[..12];
            SetBits(header, 60, 6, i % 64);
            SetBits(header, 85, 2, (i / 64) % 2);
            header.CopyTo(data, i * 512);
        }

        var watch = Stopwatch.StartNew();
        Report report = new Analyzer([new ForcedDts()]).Analyze("changes.dts", new MemoryStream(data));
        watch.Stop();
        Assert.True(watch.Elapsed < Bound, $"Took {watch.Elapsed.TotalSeconds:F1} s");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.InRange(report.Streams.Count, 2, 64);
        Finding changed = Assert.Single(report.Findings, f => f.Code == "dts.stream-changed");
        Assert.Equal((data.Length / 512) - 1, changed.Count);
        Assert.Contains("limited to 64", changed.Message, StringComparison.Ordinal);
    }

    private static void SetBits(byte[] data, int start, int width, int value)
    {
        for (int i = 0; i < width; i++)
        {
            int bit = start + i;
            int on = (value >> (width - 1 - i)) & 1;
            data[bit >> 3] = (byte)((data[bit >> 3] & ~(0x80 >> (bit & 7))) | (on << (7 - (bit & 7))));
        }
    }

    private static void TimedDts(byte[] data)
    {
        var watch = Stopwatch.StartNew();
        Report report = new Analyzer([new ForcedDts()]).Analyze("flood.dts", new MemoryStream(data));
        watch.Stop();
        Assert.True(watch.Elapsed < Bound, $"Took {watch.Elapsed.TotalSeconds:F1} s");
        Assert.DoesNotContain(report.Findings, f => f.Code == "parse.failed");
        Assert.All(report.Findings, f => Assert.NotNull(f.Path));
    }

    private sealed class ForcedDts : IFormatParser
    {
        private readonly Encspect.Formats.Dts.DtsParser _parser = new();

        public string Name => _parser.Name;

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context) => _parser.Parse(context);
    }

    private sealed class ForcedWavPack : IFormatParser
    {
        private readonly Encspect.Formats.WavPack.WavPackParser _parser = new();

        public string Name => _parser.Name;

        public SniffResult Sniff(SniffContext context) => new(100);

        public void Parse(ParseContext context) => _parser.Parse(context);
    }

    private static Report Timed(byte[] data)
    {
        var watch = Stopwatch.StartNew();
        Report report = Analyzer.CreateDefault().Analyze("complexity.mp3", new MemoryStream(data));
        watch.Stop();
        Assert.True(watch.Elapsed < Bound, $"Took {watch.Elapsed.TotalSeconds:F1} s");
        return report;
    }

    private static IEnumerable<byte> Repeat(byte[] item, int count) => Enumerable.Repeat(item, count).SelectMany(b => b);

    private static byte[] Syncsafe(int value) => [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];

    private static byte[] Frame(string id, byte[] data) => [.. System.Text.Encoding.ASCII.GetBytes(id), .. Syncsafe(data.Length), 0, 0, .. data];

    private static byte[] Id3v2(byte[] frames) => [.. "ID3"u8, 4, 0, 0, .. Syncsafe(frames.Length), .. frames];

    // MPEG-2 Layer III mono frames at 64 kbps and 24000 Hz, 192 bytes each, every one an Info frame
    // with a LAME tag; its music length reaches the end of the file or only its own frame. Without
    // musicCrcs the music CRC stays zero, since computing it here takes as long as the analyzer
    // once did.
    private static byte[] LameFrames(int count, bool toEnd, bool musicCrcs)
    {
        const int Length = 192;
        const int Lame = 133;
        var data = new byte[Length * count];
        for (int i = count - 1; i >= 0; i--)
        {
            Span<byte> frame = data.AsSpan(i * Length, Length);
            int music = toEnd ? Length * (count - i) : Length;
            new byte[] { 0xFF, 0xF3, 0x84, 0xC0 }.CopyTo(frame);
            "Info"u8.CopyTo(frame[13..]);
            BigEndian(frame[17..], 15);
            BigEndian(frame[21..], count - i);
            BigEndian(frame[25..], music);
            "LAME3.100"u8.CopyTo(frame[Lame..]);
            BigEndian(frame[(Lame + 28)..], music);
            int musicCrc = musicCrcs ? Crc(data.AsSpan((i + 1) * Length, music - Length)) : 0;
            frame[Lame + 32] = (byte)(musicCrc >> 8);
            frame[Lame + 33] = (byte)musicCrc;
            int tagCrc = Crc(frame[..(Lame + 34)]);
            frame[Lame + 34] = (byte)(tagCrc >> 8);
            frame[Lame + 35] = (byte)tagCrc;
        }

        return data;
    }

    private static void BigEndian(Span<byte> target, int value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    // CRC-16 with the reflected polynomial 0xA001 and initial value 0, as LAME computes it, bit by bit.
    private static int Crc(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
            }
        }

        return crc;
    }
}
