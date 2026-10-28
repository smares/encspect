using System.Buffers.Binary;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Tests;

// The files before version 3.93 that the SDK's old decoder (Old/UnMACOld.cpp) refuses or reads as something else are not decoded, with the reason: each case below
// changes one field of a file of the legacy-* fixtures (make_legacy_monkeys_audio_fixtures.py) and says what the decoder must answer.
public class MonkeysAudioOldDecoderTests
{
    private const string Stereo385 = "legacy-3860-normal-16-stereo-frames.ape";

    public static TheoryData<string, int, int, string> Refused => new()
    {
        { Stereo385, 6, 5000, "did not exist in files of version 3.86" },
        { Stereo385, 6, 1234, "levels 1000 to 4000" },
        { Stereo385, 10, 3, "one or two" },
        { Stereo385, 4, 3700, "takes versions 3.80 to 3.99" },
    };

    private static Report Decode(byte[] file) =>
        Analyzer.CreateDefault().Analyze("old.ape", new MemoryStream(file), new AnalysisOptions { Decoding = new DecodeOptions() });

    [Theory]
    [MemberData(nameof(Refused))]
    public void AFieldTheSdkRefusesLeavesTheFileUndecodedWithTheReason(string fixture, int offset, int value, string reason)
    {
        byte[] file = File.ReadAllBytes(MonkeysAudioBuilder.FixturePath(fixture));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(offset), (ushort)value);

        Report report = Decode(file);

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Finding finding = Assert.Single(report.Findings, f => f.Code == "decode.not-possible");
        Assert.Contains(reason, finding.Message, StringComparison.Ordinal);
        Assert.Equal(DecodeStatus.NotPossible, report.Streams.Single().Verification!.Decoding);
        Assert.NotNull(finding.Path);
    }

    [Fact]
    public void AFileOf390To392WithoutTheCrcFlagIsNotDecodedBecauseTheSdkReadsSilenceFromIt()
    {
        byte[] file = File.ReadAllBytes(MonkeysAudioBuilder.FixturePath("legacy-3900-normal-16-stereo-checksum-not-decoded.ape"));

        Report report = Decode(file);

        Finding finding = Assert.Single(report.Findings, f => f.Code == "decode.not-possible");
        Assert.Contains("gives silence", finding.Message, StringComparison.Ordinal);
        Assert.Equal(0, report.Streams.Single().Verification!.DecodedSamples);
    }

    [Fact]
    public void AFileOf380WhoseTableOfStartBitsIsNotInTheFileIsNotDecoded()
    {
        byte[] file = File.ReadAllBytes(MonkeysAudioBuilder.FixturePath("legacy-3800-normal-16-stereo-frames.ape"));
        // The header is 32 bytes and the peak level and the WAV header data are absent: the seek table of three frames is 12 bytes, and the table of bits follows it.
        byte[] cut = file[..(32 + 12 + 1)];

        Report report = Decode(cut);

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Contains(report.Findings, f => f.Code == "decode.not-possible");
    }

    [Fact]
    public void AFrameThatStatesMoreSamplesThanAFrameOfItsVersionHoldsIsInvalidAndTheRestIsDecoded()
    {
        byte[] file = File.ReadAllBytes(MonkeysAudioBuilder.FixturePath(Stereo385));
        // The header's final frame blocks (offset 28): more than the 9,216 of a frame of the version, which the SDK refuses.
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), 9217);

        Report report = Decode(file);

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed");
        Assert.Contains(report.Findings, f => f.Code == "decode.frame-invalid");
        StreamVerification verification = report.Streams.Single().Verification!;
        Assert.Equal(2 * 9216, verification.DecodedSamples);
        ChecksumTally crc = Assert.Single(verification.Checksums);
        Assert.Equal(2, crc.Checked);
        Assert.Equal(0, crc.Failed);
    }

    [Fact]
    public void AFrameOfTheExtraHighLevelIsDecodedByTheArraysOfItsChannelsWithinTheBudget()
    {
        // A frame of the extra high level of 73,728 samples is decoded by the arrays of its channels and charged by chunks of 4,096 values.
        byte[] file = File.ReadAllBytes(MonkeysAudioBuilder.FixturePath("legacy-3840-extra-high-16-stereo-frames.ape"));

        Report report = Analyzer.CreateDefault().Analyze("extra.ape", new MemoryStream(file), new AnalysisOptions { Decoding = new DecodeOptions { MaxWork = 1L << 30 } });

        Assert.DoesNotContain(report.Findings, f => f.Code is "decode.limit" or "decode.failed" or "parse.failed");
        Assert.True(report.Streams.Single().Verification!.IsVerified);
    }
}
