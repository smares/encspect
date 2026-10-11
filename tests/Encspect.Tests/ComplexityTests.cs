using System.Diagnostics;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Inputs that once took minutes because some work grew with the square of their size, built in
// memory. Each must finish well within a bound that a slow machine meets with the linear code and
// that the quadratic code missed by far (each took 25 s to several minutes on a fast machine).
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
        Assert.Equal("160000 frames", report.Root.Children.Single(c => c.Name == "MPEG Audio").Value);
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
