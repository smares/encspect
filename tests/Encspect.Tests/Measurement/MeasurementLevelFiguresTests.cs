using Encspect.Core;
using Encspect.Host;
using Encspect.Host.Measurement;

namespace Encspect.Tests.Measurement;

// Digital silence as stretches of at least 10 ms and clipping at the extremes of the bits in use (docs/design/spectral-analysis.md, "Step 6"): the minimum from
// the rate, stretches across pieces, blocks and gaps against a naive count, the floor of 8 bits in use, runs dropped when the bits in use change, and floats
// on the 16-bit and 24-bit grids.
public class MeasurementLevelFiguresTests
{
    private static int[] Noise(int length, int seed, int amplitude = 1000)
    {
        var random = new Random(seed);
        return [.. Enumerable.Range(0, length).Select(_ => random.Next(1, amplitude + 1) * (random.Next(2) * 2 - 1))];
    }

    [Theory]
    [InlineData(8000, 80)]
    [InlineData(11025, 111)]
    [InlineData(22050, 221)]
    [InlineData(44100, 441)]
    [InlineData(44101, 442)]
    [InlineData(48000, 480)]
    [InlineData(192000, 1920)]
    public void TheMinimumIsTenMillisecondsInSamplesRoundedUp(int rate, int minimum)
    {
        foreach (int length in new[] { minimum - 1, minimum, minimum + 1 })
        {
            int[] samples = [.. Noise(50, 1), .. new int[length], .. Noise(50, 2)];
            LevelFigures levels = MeasurementLevelsTests.Levels(16, [samples], rate: rate);

            Assert.Equal(minimum, levels.MinimumSilentStretch);
            ChannelLevels stream = levels.Stream;
            Assert.Equal(length >= minimum ? (1, length, length) : (0, 0, 0), (stream.SilentStretches, stream.SilentSamples, stream.LongestSilentStretch));
            Assert.Equal(length, stream.ZeroSamples);
        }
    }

    [Fact]
    public void TheStretchAtTheStartAndTheEndCountOnlyFromTheMinimumAndAreStretchesToo()
    {
        // 22,050 Hz: 221 samples.
        int[] samples = [.. new int[220], .. Noise(500, 3), .. new int[221], .. Noise(500, 4), .. new int[300]];
        ChannelLevels stream = MeasurementLevelsTests.Levels(16, [samples], rate: 22050).Stream;

        Assert.Equal((0, 300), (stream.SilentAtStart, stream.SilentAtEnd));
        Assert.Equal((2, 521L, 300L), (stream.SilentStretches, stream.SilentSamples, stream.LongestSilentStretch));
        Assert.Equal(741, stream.ZeroSamples);

        samples = [.. new int[221], .. Noise(500, 3), .. new int[220]];
        stream = MeasurementLevelsTests.Levels(16, [samples], rate: 22050).Stream;
        Assert.Equal((221, 0, 1), (stream.SilentAtStart, stream.SilentAtEnd, stream.SilentStretches));
    }

    [Fact]
    public void AStreamOfZerosIsOneStretchAtTheStartAndTheEndOrNoneBelowTheMinimum()
    {
        ChannelLevels stream = MeasurementLevelsTests.Levels(16, [new int[1000]], rate: 44100).Stream;
        Assert.Equal((1, 1000, 1000, 1000, 1000), (stream.SilentStretches, stream.SilentSamples, stream.LongestSilentStretch, stream.SilentAtStart, stream.SilentAtEnd));

        stream = MeasurementLevelsTests.Levels(16, [new int[440]], rate: 44100).Stream;
        Assert.Equal((0, 0, 0, 0, 0, 440), (stream.SilentStretches, stream.SilentSamples, stream.LongestSilentStretch, stream.SilentAtStart, stream.SilentAtEnd, stream.ZeroSamples));
    }

    [Fact]
    public void AnOpenStretchWhereTheMeasuringStoppedIsCountedAsTheEnd()
    {
        var format = new PcmFormat(8000, 1, 16, SampleFormat.Integer);
        var measurer = new StreamMeasurer(format, new MeasureOptions { Include = MeasurementKinds.Levels }, CancellationToken.None, _ => true);
        int[] block = [.. Noise(10, 5), .. new int[100]];
        measurer.Write(new PcmBlock(0, block.Length, 1, block.Length, block));
        StreamMeasurements m = measurer.Result(MeasurementStatus.Stopped, "test");

        Assert.Equal((100, 1), (m.Levels!.Stream.SilentAtEnd, m.Levels.Stream.SilentStretches));
    }

    // Stretches are found by reading every m-th sample of a piece: lengths around the minimum, at every offset against the pieces of 16,384 and the blocks.
    [Theory]
    [InlineData(7)]
    [InlineData(1000)]
    [InlineData(16384)]
    [InlineData(40000)]
    public void StretchesAcrossPiecesAndBlocksAreCountedOnceWhereverTheyBegin(int blockSize)
    {
        // 800 Hz: the minimum is 8 samples.
        foreach (int offset in new[] { 16384 - 12, 16384 - 8, 16384 - 7, 16384 - 4, 16384 - 1, 16384, 16384 + 3 })
        {
            foreach (int length in new[] { 7, 8, 9, 40 })
            {
                int[] samples = Noise(40000, 6);
                samples.AsSpan(offset, length).Clear();
                samples.AsSpan(30000, 7).Clear();
                samples.AsSpan(31000, 8).Clear();
                ChannelLevels stream = MeasurementLevelsTests.Levels(16, [samples], blockSize, 800).Stream;

                long stretches = (length >= 8 ? 1 : 0) + 1;
                Assert.Equal((stretches, length >= 8 ? length + 8L : 8L), (stream.SilentStretches, stream.SilentSamples));
                Assert.Equal(length + 15L, stream.ZeroSamples);
            }
        }
    }

    // A naive count of the maximal runs of zero frames and of the runs at the extremes, on random streams cut every way.
    [Theory]
    [InlineData(1, 11)]
    [InlineData(1, 12)]
    [InlineData(2, 13)]
    [InlineData(3, 14)]
    public void RandomStreamsGiveTheNaiveCountsWhateverTheBlocks(int channelCount, int seed)
    {
        var random = new Random(seed);
        const int minimum = 8;
        int length = 70000;
        int[][] data = new int[channelCount][];
        for (int c = 0; c < channelCount; c++)
        {
            int[] values = new int[length];
            for (int i = 0; i < length;)
            {
                int run = random.Next(1, 3 * minimum);
                int kind = random.Next(5);
                for (int k = 0; k < run && i < length; k++, i++)
                {
                    values[i] = kind switch
                    {
                        0 or 1 => 0,
                        2 => 32512,
                        3 => -32768,
                        _ => random.Next(-300, 300) & ~255,
                    };
                }
            }

            data[c] = values;
        }

        // Every sample is a multiple of 256, so the bits in use are 8 and the positive extreme is 32,512.
        (long stretches, long samples, long longest, long start, long end) = NaiveStretches(data, minimum);
        (long runs, long clipped, long longestRun) = NaiveRuns(data[0], 32512, -32768);
        foreach (int blockSize in new[] { 1000, 16384, 16385, 70000, 3 })
        {
            LevelFigures levels = MeasurementLevelsTests.Levels(16, data, blockSize, 800);
            ChannelLevels stream = levels.Stream;

            Assert.Equal((stretches, samples, longest, start, end), (stream.SilentStretches, stream.SilentSamples, stream.LongestSilentStretch, stream.SilentAtStart, stream.SilentAtEnd));
            Assert.Equal((runs, clipped, longestRun), (levels.Channels[0].ClippingRuns, levels.Channels[0].ClippedSamples, levels.Channels[0].LongestClippingRun));
            Assert.Equal(8, levels.Stream.LowBitsUnused);
            Assert.Equal(8, levels.ClippingBitsInUse);
        }
    }

    // Streams whose bits in use fall mid-stream (one sample with fewer trailing zeros at a random place and channel, or none), cut by random gaps and blocks:
    // every channel's runs at the extreme of the final bits in use (with the floor), and the stretches of the stream and of each channel, against a naive count.
    [Theory]
    [InlineData(1, 1, 8)]
    [InlineData(1, 2, 8)]
    [InlineData(2, 3, 8)]
    [InlineData(2, 4, 10)]
    [InlineData(3, 5, 8)]
    [InlineData(3, 6, 10)]
    [InlineData(2, 7, 8)]
    [InlineData(2, 8, 8)]
    [InlineData(2, 9, 10)]
    [InlineData(3, 10, 8)]
    public void RandomStreamsWhoseBitsInUseFallAndHaveGapsGiveTheNaiveCounts(int channelCount, int seed, int baseZeros)
    {
        var random = new Random(seed);
        const int minimum = 8;
        const int length = 60000;
        int[][] data = new int[channelCount][];
        int topAtBase = 32768 - (1 << baseZeros);
        for (int c = 0; c < channelCount; c++)
        {
            data[c] = new int[length];
            for (int i = 0; i < length;)
            {
                int run = random.Next(1, 3 * minimum);
                int kind = random.Next(6);
                for (int k = 0; k < run && i < length; k++, i++)
                {
                    data[c][i] = kind switch
                    {
                        0 or 1 => 0,
                        2 => topAtBase,
                        3 => -32768,
                        _ => random.Next(-3, 4) << baseZeros,
                    };
                }
            }
        }

        // The sample that ends the bits in use: not in every stream, and runs of the base extreme are put around it. It stays within 16 bits: at most 63 << 9 | 1 << 9.
        if (random.Next(4) != 0)
        {
            int channel = random.Next(channelCount);
            int at = random.Next(5000, length - 5000);
            data[channel][at] = (random.Next(1, 64) << random.Next(0, baseZeros)) | 1 << random.Next(0, baseZeros);
            for (int c = 0; c < channelCount; c++)
            {
                data[c].AsSpan(at - 4, 4).Fill(topAtBase);
                data[c].AsSpan(at + 1, 5).Fill(topAtBase);
            }
        }

        var cuts = new List<int> { 0 };
        for (int cut = random.Next(2000, 9000); cut < length; cut += random.Next(2000, 9000))
        {
            cuts.Add(cut);
        }

        cuts.Add(length);
        long position = 0;
        var segments = new List<(long Position, int[][] Channels)>();
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            segments.Add((position, [.. data.Select(d => d[cuts[i]..cuts[i + 1]])]));
            position += cuts[i + 1] - cuts[i] + random.Next(5, 50);
        }

        int or = 0;
        foreach (int[] channel in data)
        {
            foreach (int v in channel)
            {
                or |= v;
            }
        }

        int zeros = or == 0 ? 32 : System.Numerics.BitOperations.TrailingZeroCount(or);
        int shift = 16 - zeros >= MeasurementLimits.MinimumBitsInUse ? zeros : 0;
        int high = 32768 - (1 << shift);
        foreach (int blockSize in new[] { 777, 16384, 5 * 16384 })
        {
            LevelFigures levels = MeasurementLevelsTests.Run(16, blockSize, new MeasureOptions { Include = MeasurementKinds.Levels }, 800, [.. segments]).Levels!;

            Assert.Equal(16 - shift, levels.ClippingBitsInUse);
            for (int c = 0; c < channelCount; c++)
            {
                long runs = 0, clipped = 0, longest = 0;
                foreach ((_, int[][] part) in segments)
                {
                    (long r, long k, long l) = NaiveRuns(part[c], high, -32768);
                    runs += r;
                    clipped += k;
                    longest = Math.Max(longest, l);
                }

                ChannelLevels got = levels.Channels[c];
                Assert.Equal((runs, clipped, longest), (got.ClippingRuns, got.ClippedSamples, got.LongestClippingRun));
                (long st, long sa, long lo, long start, long end) = NaiveSegments([.. segments.Select(p => new[] { p.Channels[c] })], minimum);
                Assert.Equal((st, sa, lo, start, end), (got.SilentStretches, got.SilentSamples, got.LongestSilentStretch, got.SilentAtStart, got.SilentAtEnd));
            }

            (long jointStretches, long jointSamples, long jointLongest, long jointStart, long jointEnd) = NaiveSegments([.. segments.Select(p => p.Channels)], minimum);
            ChannelLevels stream = levels.Stream;
            Assert.Equal((jointStretches, jointSamples, jointLongest, jointStart, jointEnd), (stream.SilentStretches, stream.SilentSamples, stream.LongestSilentStretch, stream.SilentAtStart, stream.SilentAtEnd));
        }
    }

    // The stretches of zero frames of the segments between gaps: none crosses a gap, the start is the first segment's leading stretch and the end the last one's trailing.
    private static (long Stretches, long Samples, long Longest, long Start, long End) NaiveSegments(int[][][] segments, int minimum)
    {
        long stretches = 0, samples = 0, longest = 0, start = 0, end = 0;
        for (int s = 0; s < segments.Length; s++)
        {
            (long st, long sa, long lo, long first, long last) = NaiveStretches(segments[s], minimum);
            stretches += st;
            samples += sa;
            longest = Math.Max(longest, lo);
            start = s == 0 ? first : start;
            end = s == segments.Length - 1 ? last : end;
        }

        return (stretches, samples, longest, start, end);
    }

    private static (long Stretches, long Samples, long Longest, long Start, long End) NaiveStretches(int[][] data, int minimum)
    {
        int length = data[0].Length;
        bool[] zero = [.. Enumerable.Range(0, length).Select(i => data.All(c => c[i] == 0))];
        long stretches = 0, samples = 0, longest = 0, start = 0, end = 0;
        for (int i = 0; i < length;)
        {
            if (!zero[i])
            {
                i++;
                continue;
            }

            int j = i;
            while (j < length && zero[j])
            {
                j++;
            }

            if (j - i >= minimum)
            {
                stretches++;
                samples += j - i;
                longest = Math.Max(longest, j - i);
                start = i == 0 ? j - i : start;
                end = j == length ? j - i : end;
            }

            i = j;
        }

        return (stretches, samples, longest, start, end);
    }

    private static (long Runs, long Clipped, long Longest) NaiveRuns(int[] data, int high, int low)
    {
        long runs = 0, clipped = 0, longest = 0;
        for (int i = 0; i < data.Length;)
        {
            if (data[i] != high && data[i] != low)
            {
                i++;
                continue;
            }

            int j = i;
            while (j < data.Length && data[j] == data[i])
            {
                j++;
            }

            if (j - i >= MeasurementLimits.MinimumClippingRun)
            {
                runs++;
                clipped += j - i;
                longest = Math.Max(longest, j - i);
            }

            i = j;
        }

        return (runs, clipped, longest);
    }

    [Fact]
    public void AStretchEndsAtAGapAndEachPartMustReachTheMinimumOnItsOwn()
    {
        // 44.1 kHz: 441 samples. 300 + 300 across a gap are two parts under the minimum; 500 before a gap is a stretch.
        int[] first = [.. Noise(100, 7), .. new int[300]];
        int[] second = [.. new int[300], .. Noise(100, 8), .. new int[500]];
        int[] third = Noise(100, 9);
        LevelFigures levels = MeasurementLevelsTests.Run(16, 64, new MeasureOptions { Include = MeasurementKinds.Levels }, 44100, (0, [first]), (1000, [second]), (3000, [third])).Levels!;

        Assert.Equal((1, 500, 500, 0, 0, 1100), (levels.Stream.SilentStretches, levels.Stream.SilentSamples, levels.Stream.LongestSilentStretch, levels.Stream.SilentAtStart, levels.Stream.SilentAtEnd, levels.Stream.ZeroSamples));

        // A stretch at the start that ends at a gap under the minimum is no stretch.
        levels = MeasurementLevelsTests.Run(16, 64, new MeasureOptions { Include = MeasurementKinds.Levels }, 44100, (0, [new int[300]]), (1000, [Noise(100, 10)])).Levels!;
        Assert.Equal((0, 0, 300), (levels.Stream.SilentStretches, levels.Stream.SilentAtStart, levels.Stream.ZeroSamples));
    }

    [Fact]
    public void ShortZeroRunsInsideQuietAudioAreZeroSamplesButNotSilence()
    {
        // Half the samples are 0 in runs of 1 to 20 (rate 8,000 Hz: the minimum is 80).
        var random = new Random(15);
        int[] samples = new int[60000];
        bool isZero = false;
        for (int i = 0; i < samples.Length; isZero = !isZero)
        {
            int run = random.Next(1, 21);
            for (int k = 0; k < run && i < samples.Length; k++, i++)
            {
                samples[i] = isZero ? 0 : 1;
            }
        }

        ChannelLevels stream = MeasurementLevelsTests.Levels(16, [samples], rate: 8000).Stream;

        Assert.Equal(0, stream.SilentStretches);
        Assert.True(stream.ZeroSamples > 20000);
    }

    [Fact]
    public void TheJointStretchNeedsEveryChannelAndEachChannelCountsItsOwn()
    {
        int[] left = [.. Noise(100, 16), .. new int[2000], .. Noise(100, 17)];
        int[] right = [.. Noise(100, 18), .. new int[500], .. Noise(1600, 19), .. new int[0]];
        LevelFigures levels = MeasurementLevelsTests.Levels(16, [left, right], rate: 22050);

        Assert.Equal((1, 2000), (levels.Channels[0].SilentStretches, levels.Channels[0].SilentSamples));
        Assert.Equal((1, 500), (levels.Channels[1].SilentStretches, levels.Channels[1].SilentSamples));
        Assert.Equal((1, 500), (levels.Stream.SilentStretches, levels.Stream.SilentSamples));
    }

    // Clipping at the extremes of the bits in use.

    [Fact]
    public void SixteenBitAudioIn24BitsClipsAt0x7FFF00AndTheMostNegativeCode()
    {
        int[] samples = [.. Noise(100, 20, 20000).Select(v => v << 8), .. Enumerable.Repeat(0x7FFF00, 3), .. Noise(10, 21, 20000).Select(v => v << 8), .. Enumerable.Repeat(-0x800000, 4), 0x7FFF00, 0x7FFF00, 0x7FFE00, 0x7FFE00, 0x7FFE00];
        LevelFigures levels = MeasurementLevelsTests.Levels(24, [samples]);

        Assert.Equal(16, levels.ClippingBitsInUse);
        Assert.Equal(8, levels.Stream.LowBitsUnused);
        Assert.Equal((2, 7L, 4L), (levels.Stream.ClippingRuns, levels.Stream.ClippedSamples, levels.Stream.LongestClippingRun));
    }

    [Fact]
    public void RunsAtAnExtremeThatTheBitsInUseLeaveAreDroppedWhateverTheOrderOfPiecesAndChannels()
    {
        int[] first = [.. Noise(100, 22, 20000).Select(v => v << 8), .. Enumerable.Repeat(0x7FFF00, 5), .. Enumerable.Repeat(-0x800000, 4)];
        int[] second = [.. Noise(50, 23, 20000).Select(v => v << 4), .. Enumerable.Repeat(0x7FFFF0, 5), .. Enumerable.Repeat(-0x800000, 3)];
        int[] odd = [.. first, .. second, 1];
        int[] twenty = [.. first, .. second];

        // Final bits in use 24: neither positive run counts; 20: only the second; and the same with the odd sample in the other channel.
        foreach (int blockSize in new[] { 1, 17, 4096 })
        {
            LevelFigures atTwentyFour = MeasurementLevelsTests.Levels(24, [odd], blockSize);
            Assert.Equal((24, 2L, 7L, 4L), (atTwentyFour.ClippingBitsInUse, atTwentyFour.Stream.ClippingRuns, atTwentyFour.Stream.ClippedSamples, atTwentyFour.Stream.LongestClippingRun));

            LevelFigures atTwenty = MeasurementLevelsTests.Levels(24, [twenty], blockSize);
            Assert.Equal((20, 3L, 12L, 5L), (atTwenty.ClippingBitsInUse, atTwenty.Stream.ClippingRuns, atTwenty.Stream.ClippedSamples, atTwenty.Stream.LongestClippingRun));

            // The sample that ends the 16 bits in use is in the second channel, in the same piece as the first channel's run: that run goes too.
            int[] second16 = [.. Noise(first.Length - 1, 24, 20000).Select(v => v << 8), 1];
            LevelFigures stereo = MeasurementLevelsTests.Levels(24, [first, second16], blockSize);
            Assert.Equal((24, 1L, 4L), (stereo.ClippingBitsInUse, stereo.Stream.ClippingRuns, stereo.Stream.ClippedSamples));
        }
    }

    [Fact]
    public void TheFloorIsEightBitsInUse()
    {
        // 16 bits, every sample a multiple of 256: 8 bits in use, the extremes are +32,512 and -32,768.
        int[] eight = [.. Enumerable.Repeat(32512, 3), 256, .. Enumerable.Repeat(-32768, 3), 512, .. Enumerable.Repeat(32767 & ~255, 3)];
        LevelFigures levels = MeasurementLevelsTests.Levels(16, [eight]);
        Assert.Equal((8, 8), (levels.ClippingBitsInUse, levels.Stream.LowBitsUnused));
        Assert.Equal((3L, 9L), (levels.Stream.ClippingRuns, levels.Stream.ClippedSamples));

        // A square wave between +16,384 and -16,384 has 14 low bits unused: 2 bits in use, under the floor, so the extremes stay full scale and nothing counts.
        int[] square = [.. Enumerable.Range(0, 1000).Select(i => (i / 50) % 2 == 0 ? 16384 : -16384)];
        levels = MeasurementLevelsTests.Levels(16, [square]);
        Assert.Equal((16, 14, 0L), (levels.ClippingBitsInUse, levels.Stream.LowBitsUnused, levels.Stream.ClippingRuns));

        // 7 bits in use (9 low bits unused) is under the floor: the positive extreme is 32,767, so only the negative run counts.
        int[] seven = [.. Enumerable.Repeat(32768 - 512, 3), .. Enumerable.Repeat(-32768, 3)];
        levels = MeasurementLevelsTests.Levels(16, [seven]);
        Assert.Equal((16, 1L), (levels.ClippingBitsInUse, levels.Stream.ClippingRuns));
    }

    [Fact]
    public void ADeclaredWidthBelowTheFloorKeepsItsOwnExtremes()
    {
        LevelFigures levels = MeasurementLevelsTests.Levels(4, [[7, 7, 7, -8, -8, -8, 2]]);

        Assert.Equal(4, levels.ClippingBitsInUse);
        Assert.Equal(2L, levels.Stream.ClippingRuns);
    }

    // Floats on the 16-bit and 24-bit grids.

    private static float[] FloatRun(float value, int length) => [.. Enumerable.Repeat(value, length)];

    [Fact]
    public void FloatsOnTheSixteenBitGridClipAtTheGridsExtremes()
    {
        const float top = 32767f / 32768f;
        float[] samples = [0.25f, .. FloatRun(top, 3), 0.5f, .. FloatRun(-1f, 4), -0.5f, .. FloatRun(top, 2)];
        LevelFigures levels = MeasurementLevelsTests.Floats([samples]);

        Assert.Equal(16, levels.ClippingBitsInUse);
        Assert.Equal((2L, 7L, 4L), (levels.Stream.ClippingRuns, levels.Stream.ClippedSamples, levels.Stream.LongestClippingRun));
    }

    [Fact]
    public void ATwentyFourBitValueDropsTheRunsAtTheSixteenBitExtreme()
    {
        const float top16 = 32767f / 32768f;
        const float top24 = 8388607f / 8388608f;
        float[] sixteen = [0.25f, .. FloatRun(top16, 3), 0.5f, .. FloatRun(-1f, 3), -0.5f];
        float[] samples = [.. sixteen, 1f / 8388608f, .. FloatRun(top24, 5)];

        foreach (int blockSize in new[] { 1, 4, 4096 })
        {
            LevelFigures levels = MeasurementLevelsTests.Floats([samples], blockSize);
            Assert.Equal(24, levels.ClippingBitsInUse);
            Assert.Equal((2L, 8L, 5L), (levels.Stream.ClippingRuns, levels.Stream.ClippedSamples, levels.Stream.LongestClippingRun));
        }
    }

    [Fact]
    public void AValueOfOneOrMoreEndsTheGridAndTheExtremesAreFullScale()
    {
        const float top16 = 32767f / 32768f;
        float[] samples = [0.25f, .. FloatRun(top16, 3), 0.5f, .. FloatRun(-1f, 3), 0.1f, 1.0f, 1.5f, 2f, 0.3f];
        LevelFigures levels = MeasurementLevelsTests.Floats([samples]);

        Assert.Null(levels.ClippingBitsInUse);
        Assert.Equal((2L, 6L, 3L), (levels.Stream.ClippingRuns, levels.Stream.ClippedSamples, levels.Stream.LongestClippingRun));
    }

    [Fact]
    public void AFloatStreamOffTheGridKeepsFullScale()
    {
        float[] samples = [0.1234567f, .. FloatRun(32767f / 32768f, 5), .. FloatRun(1f, 3)];
        LevelFigures levels = MeasurementLevelsTests.Floats([samples]);

        Assert.Null(levels.ClippingBitsInUse);
        Assert.Equal((1L, 3L), (levels.Stream.ClippingRuns, levels.Stream.ClippedSamples));
    }

    [Fact]
    public void TheGridOfOneChannelMovesTheExtremeOfTheOther()
    {
        const float top16 = 32767f / 32768f;
        float[] left = [0.5f, .. FloatRun(top16, 4), 0.5f];
        float[] right = [0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.123456789f];

        LevelFigures levels = MeasurementLevelsTests.Floats([left, right]);

        Assert.Null(levels.ClippingBitsInUse);
        Assert.Equal(0, levels.Stream.ClippingRuns);
    }

    [Fact]
    public void IntegersAndFloatsReportTheirBitsInUse()
    {
        Assert.Equal(16, MeasurementLevelsTests.Levels(16, [[1, 2, 3]]).ClippingBitsInUse);
        Assert.Equal(24, MeasurementLevelsTests.Floats([[0.5f, 1f / 8388608f]]).ClippingBitsInUse);
    }
}
