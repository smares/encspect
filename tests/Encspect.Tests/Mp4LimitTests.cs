using Encspect.Core;
using Encspect.Formats.Mp4;
using Encspect.Host;

namespace Encspect.Tests;

public class Mp4LimitTests
{
    // 30 silent AAC access units in one run, with a cap of 10 on the samples kept for the file: the other 20 are counted, with their bytes and
    // durations, so the stream has the samples, duration and bitrate of the whole run (the cap's own value is only for this test).
    [Fact]
    public void SamplesPastTheCapAreCountedInTheStream()
    {
        const int count = 30;
        byte[] unit = Mp4Builder.AacUnit();
        byte[] moov = Mp4Builder.Box("moov", Mp4Builder.Mvhd(), Mp4Builder.Trak(1, Mp4Builder.AacEntry(), Mp4Builder.EmptyTables()),
            Mp4Builder.Box("mvex", Mp4Builder.FullBox("trex", Mp4Builder.U32(1), Mp4Builder.U32(1), Mp4Builder.U32(1024), Mp4Builder.U32(unit.Length), Mp4Builder.U32(0))));
        byte[] Moof(long dataOffset) => Mp4Builder.Box("moof", Mp4Builder.FullBox("mfhd", Mp4Builder.U32(1)),
            Mp4Builder.Box("traf", Mp4Builder.Box("tfhd", [0, 2, 0, 0], Mp4Builder.U32(1)), Mp4Builder.Box("trun", [0, 0, 0, 1], Mp4Builder.U32(count), Mp4Builder.U32(dataOffset))));
        byte[] data = Mp4Builder.Join(Mp4Builder.Ftyp(), moov, Moof(Moof(0).Length + 8), Mp4Builder.Box("mdat", Mp4Builder.Join([.. Enumerable.Repeat(unit, count)])));

        Report whole = new Analyzer(DefaultParsers.Tags, [new Mp4Parser()], DefaultParsers.Codecs).Analyze("whole.mp4", new MemoryStream(data));
        Report capped = new Analyzer(DefaultParsers.Tags, [new Mp4Parser(10)], DefaultParsers.Codecs).Analyze("capped.mp4", new MemoryStream(data));

        Assert.DoesNotContain(whole.Findings, f => f.Code == "mp4.fragment-samples-limit");
        Assert.Contains(capped.Findings, f => f.Code == "mp4.fragment-samples-limit");
        StreamInfo expected = Assert.Single(whole.Streams);
        StreamInfo actual = Assert.Single(capped.Streams);
        Assert.Equal(count * 1024L, expected.Samples);
        Assert.Equal(expected.Samples, actual.Samples);
        Assert.Equal(expected.Duration, actual.Duration, 6);
        Assert.Equal(expected.AverageBitrate!.Value, actual.AverageBitrate!.Value, 3);
    }
}
