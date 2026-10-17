using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// Findings that no fixture reaches with a count of one, built here: the count reads in the singular, and its verb agrees.
public class SingularFindingTests
{
    [Fact]
    public void AnAsfWaveFormatThatStatesOneByteOfCodecDataReadsInTheSingular()
    {
        byte[] properties = AsfBuilder.StreamProperties(1, 1, 1, 8000, 16000, 2, 16, []);
        properties[^2] = 1;
        properties[^1] = 0;

        Report report = Analyzer.CreateDefault().Analyze("one.asf", new MemoryStream(AsfBuilder.Build(properties, new byte[160], 20, 10)));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "asf.waveformat-extra");
        Assert.Contains("states 1 byte of codec-specific data", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuickTimeTextItemThatStatesOneByteReadsInTheSingular()
    {
        // A ©cmt atom of one text item: size 1, language 0, and nothing after the four bytes.
        byte[] atom = [0, 0, 0, 12, 0xA9, (byte)'c', (byte)'m', (byte)'t', 0, 1, 0, 0];
        byte[] udta = Mp4Builder.Box("udta", atom);
        byte[] data = Mp4Builder.Join(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd(), udta));

        Report report = Analyzer.CreateDefault().Analyze("one.mov", new MemoryStream(data));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "mp4.text-size-invalid");
        Assert.Contains("states 1 byte, but 0 follow", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnMp4BoxHeaderOfOneByteReadsInTheSingular()
    {
        byte[] data = Mp4Builder.Join(Mp4Builder.Ftyp(), Mp4Builder.Box("moov", Mp4Builder.Mvhd(), [0xFF]));

        Report report = Analyzer.CreateDefault().Analyze("one.m4a", new MemoryStream(data));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "mp4.box-header-truncated");
        Assert.Contains("1 byte at 0x", finding.Message, StringComparison.Ordinal);
        Assert.Contains(" is too few", finding.Message, StringComparison.Ordinal);
    }
}
