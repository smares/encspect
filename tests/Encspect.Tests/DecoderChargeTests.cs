using Encspect.Core;
using Encspect.Formats.Flac;
using Encspect.Formats.Flac.Decoding;
using Encspect.Formats.TrueHd;
using Encspect.Formats.WavPack.Decoding;
using Encspect.Host;
using MonkeysAudioDecoder = Encspect.Formats.MonkeysAudio.Decoding.MonkeysAudioDecoder;
using MonkeysAudioFrameDecoder = Encspect.Formats.MonkeysAudio.Decoding.FrameDecoder;

namespace Encspect.Tests;

// What the FLAC and WavPack decoders charge, to the unit, for small inputs built the way DecoderWorkTests builds its large ones
// (docs/design/decoders.md, "What each decoder counts"). DecoderWorkTests holds the weights to the time; these hold the counting to the design.
public class DecoderChargeTests
{
    private static long Units(byte[] data, string name, bool lends = true)
    {
        IDecoder inner = name.EndsWith(".flac", StringComparison.Ordinal) ? new FlacDecoder()
            : name.EndsWith(".thd", StringComparison.Ordinal) ? Formats.TrueHd.Decoding.TrueHdDecoder.TrueHd
            : name.EndsWith(".wav", StringComparison.Ordinal) || name.EndsWith(".aiff", StringComparison.Ordinal) ? new Formats.Wav.Decoding.PcmDecoder() : new WavPackDecoder();
        var counting = new CountingDecoder(inner);
        IEnumerable<IFormatParser> formats = DefaultParsers.Formats.Select(p => !lends && p is FlacParser ? new FlacParser { LendsBuffers = false } : !lends && p is TrueHdParser ? new TrueHdParser { LendsBuffers = false } : p);
        var analyzer = new Analyzer(DefaultParsers.Tags, formats, DefaultParsers.Codecs, [counting]);

        Report report = analyzer.Analyze(name, new MemoryStream(data), new AnalysisOptions { Decoding = new DecodeOptions { MaxWork = long.MaxValue, Output = inner.DeclaresChecksums ? null : _ => new DecoderWorkTests.NullSink() } });

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed" or "decode.limit");
        return counting.Units;
    }

    private const int FlacHeaderBytes = 4 + 4 + 34;

    // PCM.

    [Fact]
    public void PcmChargesTheFixedWorkOfEverySampleOfEachChannelAndNothingMore()
    {
        // 16 units a sample of each channel at 16 bits, 19 at 24 bits, 22 at 32 bits, 34 for 64-bit floats, and 16 for A-law, which is expanded to 16 bits.
        foreach ((byte[] file, string name, long perSample) in new (byte[], string, long)[]
        {
            (DecoderWorkTests.PcmCases.Wav(1, 2, 16, 1L << 22), "pcm16.wav", 16 * 2),
            (DecoderWorkTests.PcmCases.Wav(1, 3, 8, 1L << 22), "pcm8.wav", 13 * 3),
            (DecoderWorkTests.PcmCases.Wav(1, 2, 32, 1L << 22), "pcm32.wav", 22 * 2),
            (DecoderWorkTests.PcmCases.Wav(3, 1, 32, 1L << 22), "float32.wav", 22),
            (DecoderWorkTests.PcmCases.Wav(3, 1, 64, 1L << 22), "float64.wav", 34),
            (DecoderWorkTests.PcmCases.Wav(6, 2, 8, 1L << 22), "alaw.wav", 16 * 2),
            (DecoderWorkTests.PcmCases.Aiff(6, 24, 1L << 22), "pcm24.aiff", 19 * 6),
        })
        {
            long units = Units(file, name);
            Assert.True(units > 0, name);
            Assert.True(units % perSample == 0, $"{name} charged {units}, not a multiple of {perSample}");
        }
    }

    // FLAC.

    [Fact]
    public void FlacChargesTheFixedWorkOfEverySampleAndEachPredictorsOrder()
    {
        // 16 units a sample of each channel and the 8 taps of order 8: the reference's frames of 4,096 samples of 2 channels.
        byte[] reference = DecoderWorkTests.FlacCases.Reference(1L << 24);
        long referenceUnits = Units(reference, "reference.flac");
        Assert.Equal(0, referenceUnits % (4096 * 2 * 24));
        Assert.True(referenceUnits / (4096 * 2 * 24) > 10);

        // Fixed order 4: 16 and 4.
        Assert.Equal(0, Units(DecoderWorkTests.FlacCases.FixedOrder4(1L << 22), "fixed.flac") % (4096 * 2 * 20));

        // Constant subframes of 8 channels at 32 bits: 22 a sample of each channel, no taps.
        Assert.Equal(0, Units(DecoderWorkTests.FlacCases.Constant8Channels32Bits(1L << 26), "constant.flac") % (65535L * 8 * 22));

    }

    [Fact]
    public void FlacChargesTwoUnitsATapInTheWideSumsAndEightUnitsAVerbatimSample()
    {
        // 22 units a sample of each channel and 32 taps of 2 units, in the 64-bit sums (left/side at 32 bits).
        byte[] wide = DecoderWorkTests.FlacCases.Order32In64Bits(1L << 24);
        long units = Units(wide, "wide.flac");
        Assert.NotEqual(0, units);
        Assert.Equal(0, units % (4096L * 2 * (22 + (2 * 32))));

        // 22 and 8 for the walk's store of each sample of a verbatim subframe.
        byte[] verbatim = DecoderWorkTests.FlacCases.Verbatim32Bits(1L << 22);
        Assert.Equal(0, Units(verbatim, "verbatim.flac") % (4096L * 2 * (22 + FrameDecoder.VerbatimSampleUnits)));
    }

    [Fact]
    public void FlacChargesSixteenUnitsAByteForAFrameItReadsItself()
    {
        byte[] data = DecoderWorkTests.FlacCases.Reference(1L << 22);

        long walked = Units(data, "walked.flac");
        long read = Units(data, "read.flac", lends: false);

        // Every byte of the frames: the frame header, the subframes and the CRC-16.
        Assert.Equal(DecodeWork.PerCodedByte * (data.Length - FlacHeaderBytes), read - walked);
    }

    // TrueHD and MLP.

    // The units of the crafted stereo stream at 192 kHz: 160 samples a unit.
    private static (long Units, long Count, int UnitBytes) TrueHdCrafted(int fir, int iir, int matrices, bool lends = true)
    {
        byte[] data = DecoderWorkTests.TrueHdCases.Crafted(fir, iir, matrices);
        int unitBytes = (((data[0] & 0xF) << 8) | data[1]) * 2;
        return (Units(data, "crafted.thd", lends), data.Length / unitBytes, unitBytes);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(8, 0, 0)]
    [InlineData(4, 4, 0)]
    [InlineData(0, 0, 8)]
    [InlineData(4, 4, 8)]
    public void TrueHdChargesTheFixedWorkOfEverySampleTheTapsAndTheMatrixInputs(int fir, int iir, int matrices)
    {
        (long units, long count, int unitBytes) = TrueHdCrafted(fir, iir, matrices);

        // Per sample period: 19 units for each of the 2 channels, the period's own units, one unit for each of the 8 taps of a FIR filter and 4 of an IIR filter, of both channels, and for each
        // matrix its 2 channels and the 2 noise channels as inputs.
        long period = (DecodeWork.ForSample(3) * 2) + Formats.TrueHd.Decoding.StreamDecoder.PeriodUnits + (Formats.TrueHd.Decoding.StreamDecoder.FilterTapUnits * 2 * ((fir > 0 ? 8 : 0) + (iir > 0 ? 4 : 0))) + (Formats.TrueHd.Decoding.StreamDecoder.MatrixTapUnits * matrices * 4);
        // The first unit is the one that starts the decoding session, so its walk has nothing to lend and the decoder reads its 16 units a byte itself.
        Assert.Equal((count * 160 * period) + (DecodeWork.PerCodedByte * unitBytes), units);
    }

    [Fact]
    public void TrueHdChargesSixteenUnitsAByteForAUnitItReadsItself()
    {
        (long walked, long count, int unitBytes) = TrueHdCrafted(4, 4, 8);
        (long read, _, _) = TrueHdCrafted(4, 4, 8, lends: false);

        // The units but the first, which the decoder reads itself in both runs.
        Assert.Equal(DecodeWork.PerCodedByte * (count - 1) * unitBytes, read - walked);
    }

    // WavPack.

    [Fact]
    public void WavPackChargesTheSamplesTheBytesOfTheBitstreamAndEightUnitsAPassForEachSample()
    {
        // Mono blocks of 196,607 samples, 16 passes of term 17 and a bitstream of 8 bytes: 16 + 16 * 8 a sample and 16 a byte.
        const long samples = 196_607;
        byte[] data = DecoderWorkTests.WavPackCases.ZeroRuns([17], 1, 1L << 20);

        long units = Units(data, "passes.wv");

        long blocks = WavPackBuilder.Split(data).Count;
        long bitstreamBytes = DecoderWorkTests.WavPackCases.ZeroRun(samples).Length;
        Assert.True(blocks >= 1);
        Assert.Equal(blocks * ((samples * (DecodeWork.ForSample(2) + StreamDecoder.WorkPerPcmSample + (16 * StreamDecoder.WorkPerPass))) + (DecodeWork.PerCodedByte * bitstreamBytes)), units);
    }

    [Fact]
    public void WavPackChargesAMutedBlockItsSamplesAndTheBlocksOfASetThatNeverCompletesToo()
    {
        // The bitstream of 16 bytes ends after a dozen words: the block is muted, and still charged as it is decoded.
        byte[] muted = DecoderWorkTests.WavPackCases.MutedBlocks(1L << 26);
        Assert.True(Units(muted, "muted.wv") >= 196_607 * 16);

        byte[] never = DecoderWorkTests.WavPackCases.SetsThatNeverComplete(1L << 26);
        long units = Units(never, "never.wv");
        long perBlock = (196_607L * (16 + StreamDecoder.WorkPerPcmSample + (16 * StreamDecoder.WorkPerPass))) + (16 * DecoderWorkTests.WavPackCases.ZeroRun(196_607).Length);
        Assert.Equal(0, units % perBlock);
        Assert.True(units >= 2 * perBlock);
    }

    [Fact]
    public void WavPackChargesTheDsdTablesOfEveryBlockBeforeTheBlockIsDecoded()
    {
        // 100 bytes of DSD a block, mono, in the fast mode with 5 history bits: 13 a byte, 16 a byte of the data, 32 more a byte for the
        // two divisions, and the setup, 32 bins of 256 entries cleared and summed and 1,280 lookup entries each at most.
        byte[] data = DecoderWorkTests.WavPackCases.SmallDsdBlocks(1L << 22);

        long units = Units(data, "dsd.wv");

        const long dataBytes = 4 + 96 + 5 + 300;
        long perBlock = (100 * 13) + (16 * dataBytes) + (100 * DsdBlockDecoder.FastWorkPerByte) + 57_344;
        Assert.Equal(0, units % perBlock);
        Assert.True(units / perBlock > 10);
    }

    [Theory]
    [InlineData(new byte[] { 4, 1, 5, 255 }, 57_344)]
    [InlineData(new byte[] { 4, 1, 0, 255 }, 1_792)]
    [InlineData(new byte[] { 4, 1, 9, 255 }, 57_344)]
    [InlineData(new byte[] { 4, 3, 0, 20 }, 4_096)]
    [InlineData(new byte[] { 4, 0, 0, 0 }, 0)]
    [InlineData(new byte[] { 4, 1 }, 0)]
    public void TheDsdSetupWorkIsWhatTheTablesTake(byte[] data, long expected)
    {
        Assert.Equal(expected, DsdBlockDecoder.SetupWork(data));
    }

    // Monkey's Audio.

    private static long MonkeysAudioFrames(byte[] file) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(MonkeysAudioBuilder.DescriptorLength + 12));

    [Theory]
    [InlineData("external-level-fast-16-stereo.ape", 2, 2, 0)]
    [InlineData("external-level-normal-16-stereo.ape", 2, 2, 16)]
    [InlineData("external-level-high-16-stereo.ape", 2, 2, 64)]
    [InlineData("external-level-extra-high-16-stereo.ape", 2, 2, 288)]
    [InlineData("external-level-insane-16-stereo.ape", 2, 2, 1552)]
    [InlineData("external-insane-8-mono.ape", 1, 1, 1552)]
    [InlineData("external-insane-24-stereo.ape", 3, 2, 1552)]
    [InlineData("external-insane-32-stereo.ape", 4, 2, 1552)]
    public void MonkeysAudioChargesTheFixedWorkOfASampleAndTheRangeDecoderThePredictorAndATapForEachCodedChannel(string fixture, int bytes, int channels, int taps)
    {
        // The frames of 24,000 samples of the fixture, charged by chunks of 4,096 samples, each sample for the width of its bytes in each channel and, in
        // each channel, 192 for the range decoder, 48 for the predictor and a unit for every two taps (three for every four at 32 bits).
        byte[] file = DecoderWorkTests.MonkeysAudioCases.Repeated(fixture, 1L << 24);

        long units = DecoderWorkTests.MonkeysAudioCases.Units(file);

        long perSample = (DecodeWork.ForSample(bytes) * channels) + (channels * (MonkeysAudioFrameDecoder.RangeUnits + MonkeysAudioFrameDecoder.PredictorUnits + MonkeysAudioFrameDecoder.TapUnits(bytes * 8, taps)));
        Assert.Equal(0, units % (24000 * perSample));
        Assert.Equal(MonkeysAudioFrames(file) * 24000 * perSample, units);
    }

    [Fact]
    public void MonkeysAudioChargesAFlaggedSilentFrameItsSamplesAndTheirZeroFillAndNothingElse()
    {
        // 73,728 samples of 2 channels at 16 units and 40 for the zero fill, whatever the level the header states.
        byte[] file = DecoderWorkTests.MonkeysAudioCases.Repeated("external-silent-stereo.ape", 1L << 24, level: 5000);

        long units = DecoderWorkTests.MonkeysAudioCases.Units(file);

        Assert.Equal(MonkeysAudioFrames(file) * 73728 * 2 * (16 + MonkeysAudioFrameDecoder.SilentUnits), units);
    }

    [Fact]
    public void MonkeysAudioChargesAPseudoStereoFrameOneCodedChannelAndBothChannelsTheirFixedWork()
    {
        byte[] file = DecoderWorkTests.MonkeysAudioCases.Repeated("external-pseudo-stereo-insane.ape", 1L << 24);

        long units = DecoderWorkTests.MonkeysAudioCases.Units(file);

        long perSample = (16 * 2) + MonkeysAudioFrameDecoder.RangeUnits + MonkeysAudioFrameDecoder.PredictorUnits + 776;
        Assert.Equal(MonkeysAudioFrames(file) * 24000 * perSample, units);
    }

    // The most a legitimate frame can charge for a byte of coded data: every coded value is the cheapest symbol of the range coder, whose model
    // gives its largest symbol 19,578 of 65,536 (RangeDecoder.RangeWidth), 1.743 bits, and a pseudo-stereo frame codes one channel and charges
    // the fixed work of two. The 32-bit insane case (1,426 units a coded sample, 6,556 a byte; pseudo-stereo 1,448 and 6,657) has 20% of the
    // allowance to spare; a value of 1.4 bits, which the model cannot code, would take 8,148 and 8,274.
    [Theory]
    [InlineData(1, 8, 1, 1)]
    [InlineData(2, 16, 2, 2)]
    [InlineData(3, 24, 2, 2)]
    [InlineData(4, 32, 2, 2)]
    [InlineData(4, 32, 2, 1)]
    [InlineData(3, 24, 2, 1)]
    [InlineData(4, 32, 1, 1)]
    [InlineData(4, 32, 8, 8)]
    public void TheInsaneLevelStaysUnderTheDefaultAllowanceForTheCheapestSymbolAtEveryWidthAndLayout(int bytes, int bits, int channels, int codedChannels)
    {
        long taps = MonkeysAudioFrameDecoder.TapUnits(bits, 1280 + 256 + 16);
        double unitsPerSample = (DecodeWork.ForSample(bytes) * channels) + (codedChannels * (MonkeysAudioFrameDecoder.RangeUnits + MonkeysAudioFrameDecoder.PredictorUnits + taps));
        double bytesPerSample = codedChannels * (-Math.Log2(19578.0 / 65536) / 8);

        Assert.True(unitsPerSample / bytesPerSample < MonkeysAudioDecoder.PerInputByte, $"{unitsPerSample / bytesPerSample:F0} units a byte against {MonkeysAudioDecoder.PerInputByte}.");
    }

    [Fact]
    public void AnInsaneFileOfTheCheapestSymbolStaysUnderTheDefaultAllowanceForItsBytes()
    {
        // Samples the predictors follow exactly cost the range coder about 1.8 bits each (the cheapest symbol is 1.74), so a file of them is the legitimate
        // file that costs the most units for a byte; its charge must stay under what a file of that size is allowed (docs/design/decoders.md, "The default bound").
        byte[] file = DecoderWorkTests.MonkeysAudioCases.Repeated("external-quiet-insane.ape", 1L << 26);

        long units = DecoderWorkTests.MonkeysAudioCases.Units(file);

        Assert.True(units < file.Length * MonkeysAudioDecoder.PerInputByte, $"{units / (double)file.Length:F0} units a byte against {MonkeysAudioDecoder.PerInputByte}.");
    }
}
