using Encspect.Core;
using Encspect.Formats.Flac;
using Encspect.Formats.Flac.Decoding;
using Encspect.Formats.WavPack.Decoding;
using Encspect.Host;

namespace Encspect.Tests;

// What the FLAC and WavPack decoders charge, to the unit, for small inputs built the way DecoderWorkTests builds its large ones
// (docs/design/decoders.md, "What each decoder counts"). DecoderWorkTests holds the weights to the time; these hold the counting to the design.
public class DecoderChargeTests
{
    private static long Units(byte[] data, string name, bool lends = true)
    {
        var counting = new CountingDecoder(name.EndsWith(".flac", StringComparison.Ordinal) ? new FlacDecoder() : new WavPackDecoder());
        IEnumerable<IFormatParser> formats = DefaultParsers.Formats.Select(p => p is FlacParser && !lends ? new FlacParser { LendsBuffers = false } : p);
        var analyzer = new Analyzer(DefaultParsers.Tags, formats, DefaultParsers.Codecs, [counting]);

        Report report = analyzer.Analyze(name, new MemoryStream(data), new AnalysisOptions { Decoding = new DecodeOptions { MaxWork = long.MaxValue } });

        Assert.DoesNotContain(report.Findings, f => f.Code is "parse.failed" or "decode.failed" or "decode.limit");
        return counting.Units;
    }

    private const int FlacHeaderBytes = 4 + 4 + 34;

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
}
