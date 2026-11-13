using Encspect.Formats.Ac3.Decoding;

namespace Encspect.Tests;

// Transient pre-noise processing (A/52:2018 Annex E 3.7.2): the samples before the transient are replaced by a copy of those before the pre-noise, cross-faded at both ends.
public class PreNoiseTests
{
    private static float[] Signal(int count)
    {
        var random = new Random(7);
        var signal = new float[count];
        for (int i = 0; i < count; i++)
        {
            signal[i] = (float)((random.NextDouble() * 2) - 1);
        }

        return signal;
    }

    private static float[] Run(float[] signal, int chunk, (long Location, int Length)[] transients, out int emissions)
    {
        var output = new float[signal.Length];
        int count = 0;
        long next = 0;
        var pre = new PreNoise(1, (first, planar, length) =>
        {
            Assert.Equal(next, first);
            Array.Copy(planar, 0, output, (int)first, length);
            next += length;
            count++;
        }, _ => { });
        foreach ((long location, int length) in transients)
        {
            pre.Add(0, location, length);
        }

        for (int at = 0; at < signal.Length; at += chunk)
        {
            pre.Append(at, signal.AsSpan(at, chunk).ToArray(), chunk);
        }

        pre.Flush();
        Assert.Equal(signal.Length, next);
        emissions = count;
        return output;
    }

    [Theory]
    [InlineData(256)]
    [InlineData(1536)]
    public void A_correction_replaces_the_samples_before_the_transient_with_those_before_the_pre_noise(int chunk)
    {
        float[] signal = Signal(6144);
        const long location = 3000;
        const int length = 50;
        float[] output = Run(signal, chunk, [(location, length)], out _);

        long blockStart = ((location / 256) - 1) * 256;
        long pn = location - blockStart;
        long total = pn + length + 256;
        long start = location - total;
        long source = location - (512 + (2 * pn));
        for (long n = 0; n < signal.Length; n++)
        {
            if (n < start || n >= location)
            {
                Assert.Equal(signal[n], output[n]);
            }
            else if (n >= start + 256 && n < location - 128)
            {
                Assert.Equal(signal[source + (n - start)], output[n]);
            }
        }

        // The fade in is the Hanning window 0.5 * (1 - cos(pi * (n + 0.5) / 256)) between the decoded sample and the copy, the fade out its complement over the last 128 samples.
        const int first = 10;
        float fadeIn = (float)(0.5 * (1.0 - Math.Cos(Math.PI * (first + 0.5) / 256)));
        Assert.Equal((signal[start + first] * (1 - fadeIn)) + (signal[source + first] * fadeIn), output[start + first], 5);
        const int tail = 20;
        float fadeInEnd = (float)(0.5 * (1.0 - Math.Cos(Math.PI * (tail + 0.5) / 128)));
        long last = start + total - 128 + tail;
        Assert.Equal((signal[last] * fadeInEnd) + (signal[source + (last - start)] * (1 - fadeInEnd)), output[last], 5);
    }

    [Fact]
    public void A_transient_in_a_later_frame_waits_for_its_samples_and_the_output_stays_complete()
    {
        float[] signal = Signal(8192);
        float[] early = Run(signal, 1536 / 6, [(6000, 100)], out _);
        float[] late = Run(signal, 8192 / 4, [(6000, 100)], out _);
        Assert.Equal(early, late);
    }

    [Fact]
    public void A_correction_that_needs_samples_that_are_not_there_is_dropped()
    {
        float[] signal = Signal(2048);
        float[] output = Run(signal, 256, [(300, 40), (100000, 40)], out _);
        Assert.Equal(signal, output);
    }
}
