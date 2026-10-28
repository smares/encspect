using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The WAV header data a Monkey's Audio file stores: the fmt chunk is read for the channels and the channel mask, and a header data that is cut or malformed
// gives findings and nothing else (the frames do not depend on it, so decoding goes on). The fixtures hold the intact cases and the one cut inside the fmt chunk.
public class MonkeysAudioWavHeaderTests
{
    private const string Stereo = "external-level-normal-16-stereo.ape";

    private static byte[] Chunk(string id, int declared, int present) =>
        [.. System.Text.Encoding.ASCII.GetBytes(id), .. BitConverter.GetBytes(declared), .. new byte[present]];

    private static byte[] Riff(params byte[][] chunks) => [.. "RIFF"u8, 0, 0, 0, 0, .. "WAVE"u8, .. chunks.SelectMany(c => c)];

    private static byte[] Fmt(int channels, int tag = 1, int declared = 16, int present = 16)
    {
        byte[] body = new byte[present];
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(0), (ushort)tag);
        if (present >= 4)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), (ushort)channels);
        }

        return [.. "fmt "u8, .. BitConverter.GetBytes(declared), .. body];
    }

    public static TheoryData<string, string> Damaged => new()
    {
        { "chunk-header", "ends inside a chunk header" },
        { "chunk", "ends inside the LIST chunk" },
        { "no-format-before-data", "holds no fmt chunk before its data chunk" },
        { "no-format-before-end", "holds no fmt chunk before its end" },
        { "format-short", "fewer than the 16 of the common fields" },
        { "many-chunks", "more than 64 chunks" },
    };

    private static byte[] HeaderData(string kind) => kind switch
    {
        "chunk-header" => Riff("fmt "u8.ToArray()[..3]),
        "chunk" => Riff(Chunk("LIST", 100, 10)),
        "no-format-before-data" => Riff(Chunk("data", 1000, 8)),
        "no-format-before-end" => Riff(),
        "format-short" => Riff(Fmt(2, declared: 8, present: 8)),
        "many-chunks" => Riff([.. Enumerable.Range(0, 70).Select(_ => Chunk("JUNK", 0, 0))]),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static Report Analyze(byte[] headerData, bool isDecoded = false)
    {
        (byte[] header, _, List<byte[]> frames) = MonkeysAudioBuilder.Take(Stereo);
        byte[] file = MonkeysAudioBuilder.Build(header, headerData, frames, MonkeysAudioBuilder.BlocksOf(Stereo, 0), MonkeysAudioBuilder.BlocksOf(Stereo, frames.Count - 1));
        return Analyzer.CreateDefault().Analyze("crafted.ape", new MemoryStream(file), isDecoded ? new AnalysisOptions { Decoding = new DecodeOptions() } : AnalysisOptions.Default);
    }

    [Theory]
    [MemberData(nameof(Damaged))]
    public void DamagedHeaderDataGivesAWarningAndTheFileIsStillRead(string kind, string text)
    {
        Report report = Analyze(HeaderData(kind), isDecoded: true);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "monkeys-audio.wav-header-damaged");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains(text, finding.Message, StringComparison.Ordinal);
        Assert.NotNull(finding.Path);
        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Equal(true, report.Streams.Single().Verification?.IsVerified);
        TestFiles.CheckInsideParents(report.Root);
    }

    [Fact]
    public void TheChannelsOfTheFmtChunkAreComparedWithTheHeader()
    {
        Report report = Analyze(Riff(Fmt(6), Chunk("data", 0, 0)));

        Finding finding = Assert.Single(report.Findings, f => f.Code == "monkeys-audio.wav-channels-differ");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("states 6 channels", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(report.Findings, f => f.Code == "monkeys-audio.wav-header-damaged");
    }

    [Fact]
    public void ANonzeroMaskSetsTheLayoutOfAStereoFile()
    {
        // Front center and low frequency in place of left and right.
        byte[] fmt = Fmt(2, tag: 0xFFFE, declared: 40, present: 40);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(8 + 16), 22);
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8 + 20), 0xC);

        Report report = Analyze(Riff(fmt, Chunk("data", 0, 0)));

        Assert.Equal([Speaker.FrontCenter, Speaker.LowFrequency], report.Streams.Single().ChannelLayout);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("monkeys-audio.wav-", StringComparison.Ordinal));
    }

    [Fact]
    public void AMaskInAHeaderThatIsNotARiffWaveHeaderIsNotRead()
    {
        byte[] aiff = [.. "FORM"u8, 0, 0, 0, 0, .. "AIFF"u8, .. new byte[40]];

        Report report = Analyze(aiff);

        Assert.Equal([Speaker.FrontLeft, Speaker.FrontRight], report.Streams.Single().ChannelLayout);
        Assert.DoesNotContain(report.Findings, f => f.Code.StartsWith("monkeys-audio.wav-", StringComparison.Ordinal));
    }
}
