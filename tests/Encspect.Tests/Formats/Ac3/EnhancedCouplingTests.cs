using Encspect.Formats.Ac3.Decoding;

namespace Encspect.Tests;

// Enhanced coupling's reconstruction of A/52:2018 Annex E 3.5.5 against what its construction implies: the coupling channel is moved to a complex spectrum and back through
// the lapped transform of the blocks around the current one, so with an amplitude of 1, an angle of 0 and no chaos it returns the current block's coefficients, whatever the
// neighbors hold; an angle of pi turns them over, and the amplitude scales them.
public class EnhancedCouplingTests
{
    private static float[] Random(int seed)
    {
        var rng = new Random(seed);
        var values = new float[256];
        for (int bin = 13; bin < 253; bin++)
        {
            values[bin] = (float)((rng.NextDouble() * 2) - 1) * 0.01f;
        }

        return values;
    }

    private static float[] Transform(InverseTransform transform, float[] coefficients)
    {
        var x = new float[512];
        transform.Long(coefficients, x);
        return x;
    }

    private static float[] Synthesize(int amplitude, int angle, bool isFirst, float[] previous, float[] current, float[] next, int begin = 4, int end = 10)
    {
        var transform = new InverseTransform();
        var enhanced = new EnhancedCoupling();
        enhanced.Analyze(Transform(transform, previous), Transform(transform, current), Transform(transform, next));
        var spectrum = new float[256];
        int[] parameters = new int[22];
        Array.Fill(parameters, amplitude | (angle << 5));
        enhanced.Channel(spectrum, parameters, 0, isFirst, false, false, begin, end, 0);
        return spectrum;
    }

    [Fact]
    public void An_amplitude_of_one_and_no_angle_return_the_current_blocks_coefficients_whatever_the_neighbors_hold()
    {
        float[] current = Random(1);
        float[] result = Synthesize(0, 0, true, Random(2), current, Random(3));

        for (int bin = 37; bin < 109; bin++)
        {
            Assert.True(Math.Abs(result[bin] - current[bin]) < 1e-6, $"bin {bin}: {result[bin]} against {current[bin]}");
        }

        Assert.Equal(0, result[36]);
        Assert.Equal(0, result[109]);
    }

    [Fact]
    public void An_angle_of_pi_turns_the_coefficients_over_and_the_amplitude_scales_them()
    {
        float[] current = Random(4);
        // ecplangle 32 is -1, that is -pi; ecplamp 4 is 0.5.
        float[] turned = Synthesize(4, 32, false, Random(5), current, Random(6));

        for (int bin = 37; bin < 109; bin++)
        {
            Assert.True(Math.Abs(turned[bin] + (0.5f * current[bin])) < 1e-6, $"bin {bin}: {turned[bin]} against {-0.5f * current[bin]}");
        }
    }

    [Fact]
    public void A_missing_neighbor_leaves_the_aliasing_of_its_half_so_only_the_neighbors_themselves_are_needed()
    {
        float[] current = Random(7);
        var transform = new InverseTransform();
        var enhanced = new EnhancedCoupling();
        enhanced.Analyze([], Transform(transform, current), []);
        var spectrum = new float[256];
        int[] parameters = new int[22];
        enhanced.Channel(spectrum, parameters, 0, true, false, false, 4, 10, 0);

        // Without the neighbors the result is not the block's coefficients, but it stays finite and of their order.
        Assert.All(spectrum, v => Assert.True(float.IsFinite(v)));
        double energy = Enumerable.Range(37, 72).Sum(bin => (double)spectrum[bin] * spectrum[bin]);
        double reference = Enumerable.Range(37, 72).Sum(bin => (double)current[bin] * current[bin]);
        Assert.InRange(energy / reference, 0.2, 5.0);
    }
}
