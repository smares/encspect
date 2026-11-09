using Encspect.Core;
using Encspect.Formats.Tak;
using Encspect.Host;
using Builder = Encspect.Tests.TakBuilder;

namespace Encspect.Tests;

// What the TAK format parser does with input the fixtures do not hold: how its sniff ranks a stream ID, the end the last frame info declares, the walk's rule for the
// end of the metadata (a frame whose header CRC-24 holds, never a sync word alone), the CRC-24 comparisons, and each finding the walk and the checks raise.
// FindingCoverageTests checks that every code the parser emits is asserted somewhere.
public class TakFindingTests
{
    private static readonly TakParser Parser = new();

    private static SniffResult Sniff(byte[] data) => Parser.Sniff(new SniffContext(new ByteReader(new MemoryStream(data))));

    private static long? DeclaredEnd(byte[] data) => Parser.DeclaredEnd(new SniffContext(new ByteReader(new MemoryStream(data))));

    private static Report Analyze(byte[] data, AnalysisOptions? options = null) => Analyzer.CreateDefault().Analyze("test.tak", new MemoryStream(data), options);

    private static Finding Single(Report report, string code) => Assert.Single(report.Findings, f => f.Code == code);

    private static byte[] File(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    // The objects of a file before the frames, with an End object and two frames after them (the last frame info states the second, from the first's length).
    private static byte[] WithObjects(params byte[][] objects)
    {
        byte[] first = Builder.Frame(0, 300, isInfo: true);
        byte[] last = Builder.Frame(1, 300, isLast: true);
        return File("tBaK"u8.ToArray(), File(objects), Builder.End(), first, last);
    }

    [Fact]
    public void AStandardFileRaisesNoFinding()
    {
        Report report = Analyze(Builder.Standard());

        Assert.Empty(report.Findings);
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal("TAK", stream.Codec);
        Assert.Equal(44100, stream.SampleRate);
        Assert.Equal(2, stream.Channels);
        Assert.Equal(16, stream.BitsPerSample);
        Assert.Equal(44100, stream.Samples);
        Assert.Equal("TAK 2.3.3", stream.Encoder);
        Assert.Equal(Confidence.High, stream.EncoderConfidence);
        Assert.Null(stream.Verification!.IsVerified);
        Assert.Equal(DecodeStatus.NoDecoder, stream.Verification.Decoding);
        ChecksumTally header = stream.Verification.Checksums.Single(c => c.Name == "header CRC-24");
        Assert.Equal((4, 4, 0), (header.Stored, header.Checked, header.Failed));
        // One frame header CRC-24 and one frame data CRC-24 for each frame the stream info's samples and frame length call for: 44,100 samples in frames of 5512.
        Assert.Equal(9, stream.Verification.Checksums.Single(c => c.Name == "frame data CRC-24").Stored);
        Assert.Equal(0, stream.Verification.Checksums.Single(c => c.Name == "frame data CRC-24").Checked);
    }

    // The sniff.

    [Fact]
    public void AStreamInfoWhoseCrcHoldsIsCertain()
    {
        byte[] data = Builder.Standard();

        SniffResult result = Sniff(data);

        Assert.Equal(100, result.Class);
        Assert.Equal(1, result.VerifiedFrames);
    }

    [Fact]
    public void AStreamInfoWhoseCrcFailsIsLikely()
    {
        Assert.Equal(80, Sniff(File("tBaK"u8.ToArray(), Builder.StreamInfo(crc: 0x123456))).Class);
        Assert.Equal(80, Sniff(File("tBaK"u8.ToArray(), Builder.StreamInfo())[..12]).Class);
    }

    [Fact]
    public void TheSignatureAloneOrWithAnotherObjectIsPossible()
    {
        Assert.Equal(70, Sniff("tBaK"u8.ToArray()).Class);
        Assert.Equal(70, Sniff(File("tBaK"u8.ToArray(), Builder.EncoderInfo())).Class);
        Assert.Equal(70, Sniff(File("tBaK"u8.ToArray(), Builder.Object(1, new byte[3]))).Class);
        Assert.Equal(70, Sniff(File("tBaK"u8.ToArray(), Builder.Object(1, Builder.StreamInfoBits(), reserved: true))).Class);
    }

    [Fact]
    public void AStreamIdFurtherInIsNoMatch()
    {
        Assert.False(Sniff(File(new byte[10], Builder.Standard())).IsMatch);
        Assert.False(Sniff("tBaL"u8.ToArray()).IsMatch);
    }

    // The end the last frame info declares.

    [Fact]
    public void TheLastFrameInfoGivesTheEndOfTheAudio()
    {
        byte[] file = Builder.Standard();

        Assert.Equal(file.Length, DeclaredEnd(file));
        Assert.Equal(file.Length, DeclaredEnd([.. file, .. TestFiles.ApeText("Title", "Song")]));
    }

    [Fact]
    public void WithoutAValidLastFrameInfoThereIsNoDeclaredEnd()
    {
        byte[] first = Builder.Frame(0, 300, isInfo: true);
        byte[] last = Builder.Frame(1, 300, isLast: true);
        Assert.Null(DeclaredEnd(WithObjects(Builder.StreamInfo(), Builder.EncoderInfo(), Builder.WaveData())));
        Assert.Null(DeclaredEnd(Builder.Standard(lastFrameInfo: Builder.LastFrameInfo(first.Length, last.Length, crc: 0x010203))));
        Assert.Null(DeclaredEnd(File("tBaK"u8.ToArray())));
        Assert.Null(DeclaredEnd(new byte[8]));
    }

    [Fact]
    public void AFileWithoutTheLastFrameInfoIsReadWholeWithoutAFinding()
    {
        Report report = Analyze(WithObjects(Builder.StreamInfo(), Builder.EncoderInfo(), Builder.WaveData()));

        Assert.Empty(report.Findings);
        Assert.Equal("TAK 2.3.3", report.Streams.Single().Encoder);
    }

    [Fact]
    public void AFileCutInTheAudioIsReported()
    {
        byte[] file = Builder.Standard();

        Report report = Analyze(file[..^100]);

        Finding cut = Single(report, "tak.audio-cut");
        Assert.Equal(Severity.Warning, cut.Severity);
        Assert.Contains("100 bytes", cut.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BytesAfterTheLastFrameThatAreNoTagAreReported()
    {
        byte[] file = Builder.Standard();

        Report report = Analyze([.. file, .. "these bytes are no tag at all"u8]);

        Finding extra = Single(report, "tak.data-after-frames");
        Assert.Equal("Trailing Data", extra.Node?.Name);
        Assert.DoesNotContain(report.Findings, f => f.Code == "tak.audio-cut");
    }

    [Fact]
    public void AnApeTagAfterTheLastFrameIsTheHostsAndNoFinding()
    {
        byte[] tag = TestFiles.ApeText("Title", "Song");

        Report report = Analyze([.. Builder.Standard(), .. tag]);

        Assert.Empty(report.Findings);
        Assert.Contains(report.Root.Children, n => n.Kind == NodeKind.Tag);
    }

    [Fact]
    public void AnId3v2TagBeforeTheStreamIdIsReported()
    {
        byte[] tag = TestFiles.Id3v23(("TIT2", "Song"));

        Report report = Analyze([.. tag, .. Builder.Standard()]);

        Finding finding = Single(report, "tak.leading-tag");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Single(report.Findings);
    }

    [Fact]
    public void ForcingTheParserOnDataWithoutTheStreamIdGivesAFinding()
    {
        Report report = Analyze(new byte[64], new AnalysisOptions { ForcedFormat = "TAK" });

        Single(report, "tak.no-signature");
        Assert.Empty(report.Streams);
    }

    // The end of the metadata.

    [Fact]
    public void AMissingEndObjectEndsTheWalkAtTheFirstFrame()
    {
        byte[] file = Builder.Standard(hasEnd: false);

        Report report = Analyze(file);

        Single(report, "tak.end-missing");
        Assert.DoesNotContain(report.Findings, f => f.Code == "tak.object-unknown");
        Node stream = report.Root.Children.Single(n => n.Name == "TAK");
        Assert.Equal(Builder.AudioStart(Builder.StreamInfo(), Builder.LastFrameInfo(0, 0), Builder.EncoderInfo(), Builder.WaveData()) - 4, stream.Children.Single(c => c.Name == "Audio Data").Offset);
        Assert.DoesNotContain(stream.Children, c => c.Name == "End Object");
    }

    [Fact]
    public void AnObjectThatLooksLikeASyncWordButIsNoFrameIsReadAsAnUnknownObject()
    {
        // ID 127 with the reserved bit set and a size of 160: the bytes FF A0 00 00 are no frame header (its CRC-24 fails).
        byte[] fake = Builder.Object(127, new byte[160], hasCrc: false, reserved: true);
        Assert.Equal([0xFF, 0xA0, 0x00, 0x00], fake[..4]);

        Report report = Analyze(Builder.Standard(extra: fake));

        Assert.Equal(["tak.object-reserved-bit", "tak.object-unknown"], report.Findings.Select(f => f.Code).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(report.Findings, f => f.Code == "tak.end-missing");
        Assert.Equal(Severity.Info, Single(report, "tak.object-unknown").Severity);
    }

    [Fact]
    public void AnUnknownObjectIsSkippedByItsSizeAndCounted()
    {
        byte[] file = Builder.Standard(extra: File(Builder.Object(20, new byte[30], hasCrc: false), Builder.Object(21, new byte[6], hasCrc: false)));

        Report report = Analyze(file);

        Finding unknown = Single(report, "tak.object-unknown");
        Assert.Equal(2, unknown.Count);
        Assert.Contains("1 more object", unknown.Message, StringComparison.Ordinal);
        Assert.Contains(report.Root.Children.Single(n => n.Name == "TAK").Children, c => c.Name == "Unknown Object");
        Assert.DoesNotContain(report.Findings, f => f.Severity > Severity.Info);
    }

    [Fact]
    public void AReservedBitThatIsSetIsReported()
    {
        byte[] file = Builder.Standard(encoderInfo: Builder.Object(4, [3, 3, 2, 2], reserved: true));

        Report report = Analyze(file);

        Assert.Equal(Severity.Info, Single(report, "tak.object-reserved-bit").Severity);
        Assert.Single(report.Findings);
    }

    [Fact]
    public void ASeekTableIsShownAndItsCrcCompared()
    {
        Report report = Analyze(WithObjects(Builder.StreamInfo(), Builder.SeekTable(), Builder.WaveData()));

        Assert.Empty(report.Findings);
        Node table = report.Root.Children.Single(n => n.Name == "TAK").Children.Single(c => c.Name == "Seek Table Object");
        Assert.True(table.Children.Single(c => c.Name == "IsCrc24Valid").Value?.AsBoolean());
        Assert.Equal("TAK 1.0", report.Streams.Single().Encoder);
        Assert.Equal(Confidence.Medium, report.Streams.Single().EncoderConfidence);
    }

    [Fact]
    public void APaddingObjectHasNoCrcAndIsNoFinding()
    {
        Report report = Analyze(Builder.Standard(extra: Builder.Padding(2)));

        Assert.Empty(report.Findings);
        Node padding = report.Root.Children.Single(n => n.Name == "TAK").Children.Single(c => c.Name == "Padding Object");
        Assert.DoesNotContain(padding.Children, c => c.Name == "CRC-24");
        Assert.Equal(4, report.Streams.Single().Verification!.Checksums.Single(c => c.Name == "header CRC-24").Checked);
    }

    [Fact]
    public void AnMd5ObjectIsShownAndNotCompared()
    {
        Report report = Analyze(Builder.Standard(extra: Builder.Md5()));

        Assert.Empty(report.Findings);
        Node md5 = report.Root.Children.Single(n => n.Name == "TAK").Children.Single(c => c.Name == "MD5 Object");
        Assert.Equal("0102030405060708090a0b0c0d0e0f10", md5.Children.Single(c => c.Name == "MD5").Value?.ToString());
        ChecksumTally tally = report.Streams.Single().Verification!.Checksums.Single(c => c.Name == "MD5");
        Assert.Equal((1, 0), (tally.Stored, tally.Checked));
    }

    [Fact]
    public void AFirstObjectThatIsNotTheStreamInfoIsReported()
    {
        Report report = Analyze(WithObjects(Builder.EncoderInfo(), Builder.StreamInfo(), Builder.WaveData()));

        Finding finding = Single(report, "tak.stream-info-not-first");
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal("Encoder Info Object", finding.Node?.Name);
        Assert.NotEmpty(report.Streams);
    }

    [Fact]
    public void AFileWithoutAStreamInfoIsReported()
    {
        Report report = Analyze(WithObjects(Builder.EncoderInfo(), Builder.WaveData()));

        Single(report, "tak.stream-info-missing");
        StreamInfo stream = Assert.Single(report.Streams);
        Assert.Equal(0, stream.Samples);
    }

    // The CRC-24s and the sizes.

    [Theory]
    [InlineData("stream info")]
    [InlineData("encoder info")]
    [InlineData("last frame info")]
    [InlineData("wave data")]
    public void AFailingCrcIsReportedAndCounted(string which)
    {
        byte[] first = Builder.Frame(0, 300, isInfo: true);
        byte[] last = Builder.Frame(1, 300, isLast: true);
        byte[] file = which switch
        {
            "stream info" => Builder.Standard(streamInfo: Builder.StreamInfo(crc: 0x010203)),
            "encoder info" => Builder.Standard(encoderInfo: Builder.EncoderInfo(crc: 0x010203)),
            "last frame info" => Builder.Standard(lastFrameInfo: Builder.LastFrameInfo(first.Length, last.Length, crc: 0x010203)),
            _ => Builder.Standard(waveData: Builder.WaveData(crc: 0x010203)),
        };

        Report report = Analyze(file);

        Finding finding = Single(report, "tak.object-crc-mismatch");
        Assert.Equal("CRC-24", finding.Node?.Name);
        Assert.Single(report.Findings, f => f.Severity >= Severity.Warning);
        ChecksumTally tally = report.Streams.Single().Verification!.Checksums.Single(c => c.Name == "header CRC-24");
        Assert.Equal(1, tally.Failed);
        Assert.False(report.Streams.Single().Verification!.IsVerified);
        Node statistics = report.Root.Children.Single(n => n.Name == "TAK").Children.Single(c => c.Name == "Statistics");
        Assert.Equal(3, statistics.Children.Single(c => c.Name == "Verified Header Checksums").Value?.AsInteger());
        Assert.Equal(1, statistics.Children.Single(c => c.Name == "Failed Header Checksums").Value?.AsInteger());
    }

    [Fact]
    public void SeveralFailingCrcsAreOneFindingWithACount()
    {
        Report report = Analyze(Builder.Standard(streamInfo: Builder.StreamInfo(crc: 1), encoderInfo: Builder.EncoderInfo(crc: 2)));

        Finding finding = Single(report, "tak.object-crc-mismatch");
        Assert.Equal(2, finding.Count);
        Assert.Contains("1 more object", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectsWhoseSizeBreaksTheLayoutAreReported()
    {
        byte[] first = Builder.Frame(0, 300, isInfo: true);
        byte[] last = Builder.Frame(1, 300, isLast: true);
        byte[][] files =
        [
            Builder.Standard(encoderInfo: Builder.Object(4, [3, 3, 2, 2, 0])),
            Builder.Standard(streamInfo: Builder.Object(1, new byte[9])),
            Builder.Standard(extra: Builder.Object(6, new byte[17])),
            Builder.Standard(lastFrameInfo: Builder.Object(7, new byte[9])),
            Builder.Standard(waveData: Builder.Object(3, [.. new byte[6], .. new byte[10]])),
            Builder.Standard(waveData: Builder.Object(3, [0x2C, 0, 0, 0, 0, 0, .. new byte[10]])),
            File("tBaK"u8.ToArray(), Builder.StreamInfo(), [0, 5, 0, 0], first, last),
        ];

        foreach (byte[] file in files)
        {
            Report report = Analyze(file);

            Assert.Equal(Severity.Warning, Single(report, "tak.object-size-invalid").Severity);
        }
    }

    [Fact]
    public void AnObjectThatTheDataCutsShortEndsTheMetadata()
    {
        byte[] file = Builder.Standard();
        int waveAt = 4 + Builder.StreamInfo().Length + Builder.LastFrameInfo(0, 0).Length + Builder.EncoderInfo().Length;

        Report report = Analyze(file[..(waveAt + 30)]);

        Finding cut = Single(report, "tak.metadata-cut");
        Assert.Equal("Wave Data Object", cut.Node?.Name);
        Assert.Contains("26 of them", cut.Message, StringComparison.Ordinal);
        Node stream = report.Root.Children.Single(n => n.Name == "TAK");
        Assert.DoesNotContain(stream.Children, c => c.Name == "Audio Data");
    }

    [Fact]
    public void MetadataThatEndsWithoutAnEndObjectOrAFrameIsReported()
    {
        byte[] objects = File("tBaK"u8.ToArray(), Builder.StreamInfo(), Builder.EncoderInfo());

        Single(Analyze(objects), "tak.metadata-cut");
        Single(Analyze([.. objects, 0, 0]), "tak.metadata-cut");
        Node stream = Analyze([.. objects, 0, 0]).Root.Children.Single(n => n.Name == "TAK");
        Assert.Equal("Unread Data", stream.Children.Last().Name);
    }

    [Fact]
    public void AMoreObjectsThanTheWalkFollowsAreReported()
    {
        byte[] padding = Builder.Padding(0);
        byte[] file = File("tBaK"u8.ToArray(), Builder.StreamInfo(), File(Enumerable.Repeat(padding, Records.MaxObjects + 10).ToArray()), Builder.End(), Builder.Frame(0, 100, isInfo: true));

        Report report = Analyze(file);

        Single(report, "tak.objects-limit");
        Assert.Equal(Records.MaxObjects, report.Root.Children.Single(n => n.Name == "TAK").Children.Count(c => c.Name.EndsWith(" Object", StringComparison.Ordinal)));
    }

    // The values the layout does not define.

    [Theory]
    [InlineData("codec")]
    [InlineData("data-type")]
    [InlineData("sample-bits")]
    [InlineData("sample-rate")]
    [InlineData("valid-bits")]
    [InlineData("evaluation")]
    [InlineData("speaker")]
    public void AValueThatIsNotDefinedIsAnInfo(string which)
    {
        byte[] file = which switch
        {
            "codec" => Builder.Standard(streamInfo: Builder.StreamInfo(codec: 9)),
            "data-type" => Builder.Standard(streamInfo: Builder.StreamInfo(dataType: 3)),
            "sample-bits" => Builder.Standard(streamInfo: Builder.StreamInfo(bitsCode: 20)),
            "sample-rate" => Builder.Standard(streamInfo: Builder.StreamInfo(rate: 265000)),
            "valid-bits" => Builder.Standard(streamInfo: Builder.StreamInfo(validBits: 20)),
            "evaluation" => Builder.Standard(encoderInfo: Builder.EncoderInfo(evaluation: 3)),
            _ => Builder.Standard(streamInfo: Builder.StreamInfo(speakers: [1, 40])),
        };

        Report report = Analyze(file);

        Finding finding = Single(report, "tak.value-undefined");
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.DoesNotContain(report.Findings, f => f.Severity > Severity.Info);
    }

    [Fact]
    public void ACodecThatIsNotListedIsNamedWithoutAClaim()
    {
        Report report = Analyze(Builder.Standard(streamInfo: Builder.StreamInfo(codec: 3, sizeType: 7)));

        Assert.Empty(report.Findings);
        Node statistics = report.Root.Children.Single(n => n.Name == "TAK").Children.Single(c => c.Name == "Statistics");
        Assert.Equal("LossyWav (TAK 2.1 Beta)", statistics.Children.Single(c => c.Name == "Codec").Value?.ToString());
        StreamInfo stream = report.Streams.Single();
        Assert.Null(stream.Processor);
        Assert.Empty(stream.Claims);
    }

    [Fact]
    public void AReservedFrameSizeTypeIsReported()
    {
        Report report = Analyze(Builder.Standard(streamInfo: Builder.StreamInfo(sizeType: 12)));

        Assert.Equal(Severity.Warning, Single(report, "tak.frame-size-type-reserved").Severity);
        Node statistics = report.Root.Children.Single(n => n.Name == "TAK").Children.Single(c => c.Name == "Statistics");
        Assert.DoesNotContain(statistics.Children, c => c.Name == "Frame Length");
    }

    [Theory]
    [InlineData(0, 192000)]
    [InlineData(3, 96000)]
    [InlineData(6, 44100)]
    [InlineData(5, 22050)]
    public void AFrameSizeThatDoesNotFitTheRateIsReported(int sizeType, int rate)
    {
        Report report = Analyze(Builder.Standard(streamInfo: Builder.StreamInfo(sizeType: sizeType, rate: rate)));

        Assert.Equal(Severity.Warning, Single(report, "tak.frame-size-invalid").Severity);
    }

    [Theory]
    [InlineData(0, 44100, 4134)]
    [InlineData(1, 44100, 5512)]
    [InlineData(2, 48000, 9000)]
    [InlineData(3, 44100, 11025)]
    [InlineData(4, 44100, 4096)]
    [InlineData(6, 192000, 16384)]
    [InlineData(7, 8000, 512)]
    public void TheFrameLengthFollowsTheSizeType(int sizeType, int rate, int expected)
    {
        Assert.Equal(expected, Layout.FrameSamples(sizeType, rate));
        Assert.True(Layout.IsFrameSizeValid(sizeType, rate));
    }

    // The stored WAV header.

    [Fact]
    public void ADamagedStoredHeaderIsReported()
    {
        byte[] noFormat = [.. "RIFF"u8, .. new byte[4], .. "WAVE"u8, .. "data"u8, .. new byte[4]];

        Report report = Analyze(Builder.Standard(waveData: Builder.WaveData(noFormat)));

        Assert.Equal(Severity.Warning, Single(report, "tak.wav-header-damaged").Severity);
    }

    [Fact]
    public void AStoredHeaderWithOtherChannelsThanTheStreamInfoIsReported()
    {
        Report report = Analyze(Builder.Standard(waveData: Builder.WaveData(Builder.Wav(channels: 1))));

        Single(report, "tak.wav-channels-differ");
    }

    [Fact]
    public void AChannelMaskThatNamesOtherSpeakersThanTheChannelsIsReported()
    {
        byte[] format = OptimFrogBuilder.Format(2, 44100, tag: 0xFFFE, mask: 0x7);

        Report report = Analyze(Builder.Standard(waveData: Builder.WaveData(Builder.Wav(format: format))));

        Assert.Equal(Severity.Info, Single(report, "tak.channel-mask-mismatch").Severity);
    }

    [Fact]
    public void LossyWavsFactTextNamesTheProcessor()
    {
        byte[] fact = OptimFrogBuilder.Chunk("fact", [.. "lossyWAV 1.4.2 @ 08/11/2026 08:37:19, --quality standard\r\n"u8, 0, 0]);

        Report report = Analyze(Builder.Standard(waveData: Builder.WaveData(Builder.Wav(beforeData: fact))));

        Assert.Empty(report.Findings);
        StreamInfo stream = report.Streams.Single();
        Assert.Equal("lossyWAV 1.4.2", stream.Processor);
        Assert.Equal(Confidence.High, stream.ProcessorConfidence);
    }

    [Fact]
    public void AFactTextOfNoKnownFormIsReported()
    {
        byte[] fact = OptimFrogBuilder.Chunk("fact", [.. "lossyWAV is a program"u8, 0, 0]);

        Report report = Analyze(Builder.Standard(waveData: Builder.WaveData(Builder.Wav(beforeData: fact))));

        Single(report, "tak.header-fact-text-unrecognized");
    }

    [Fact]
    public void TheFooterIsListedByItsChunks()
    {
        byte[] footer = OptimFrogBuilder.Chunk("LIST", [.. "INFO"u8]);

        Report report = Analyze(Builder.Standard(waveData: Builder.WaveData(footer: footer)));

        Assert.Empty(report.Findings);
        Node wave = report.Root.Children.Single(n => n.Name == "TAK").Children.Single(c => c.Name == "Wave Data Object");
        Node chunks = wave.Children.Single(c => c.Name == "WAV Terminating Data").Children.Single(c => c.Name == "Chunks");
        Assert.Equal("LIST Chunk", chunks.Children.Single().Name);
    }

    // The channel layout.

    [Fact]
    public void TheSpeakerAssignmentGivesTheLayoutBeforeTheHeadersMask()
    {
        Report report = Analyze(Builder.Standard(streamInfo: Builder.StreamInfo(channels: 6, speakers: [1, 2, 3, 4, 5, 6], validBits: 16), waveData: Builder.WaveData(Builder.Wav(channels: 6))));

        Assert.Empty(report.Findings);
        StreamInfo stream = report.Streams.Single();
        Assert.Equal(6, stream.Channels);
        Assert.Equal(ChannelLayouts.FromWaveMask(0x3F, 6), stream.ChannelLayout);
    }

    [Fact]
    public void ChannelsWithNoAssignmentAndNoMaskHaveALayoutOnlyForOneOrTwo()
    {
        Assert.Equal(ChannelLayouts.Mono, Analyze(Builder.Standard(streamInfo: Builder.StreamInfo(channels: 1))).Streams.Single().ChannelLayout);
        Assert.Equal(ChannelLayouts.Stereo, Analyze(Builder.Standard()).Streams.Single().ChannelLayout);
        Assert.Null(Analyze(Builder.Standard(streamInfo: Builder.StreamInfo(channels: 6))).Streams.Single().ChannelLayout);
    }

    // The writer's claims from tags.

    [Fact]
    public void ADisagreeingWriterClaimIsReported()
    {
        byte[] file = Builder.Standard();

        Report report = Analyze([.. TestFiles.Id3v23(("TSSE", "Lavf60.16.100")), .. file, .. TestFiles.ApeText("Encoder", "LAME 3.100")]);

        Assert.Contains(report.Findings, f => f.Code == "tak.writer-claim-differs");
    }
}
