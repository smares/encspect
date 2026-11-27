using Encspect.Formats.Aac.Decoding;

namespace Encspect.Tests.Formats.Aac;

// The forward transform that long-term prediction needs (InverseMdct.Forward) against the sum ISO/IEC 14496-3 4.6.11.3.1 defines the inverse by, written out directly: the
// forward transform is its transpose.
public class AacTransformTests
{
    private static float[] Pattern(int length, int seed)
    {
        var values = new float[length];
        uint state = (uint)seed;
        for (int i = 0; i < length; i++)
        {
            state = (state * 1664525u) + 1013904223u;
            values[i] = ((state >> 8) / (float)(1 << 24)) - 0.5f;
        }

        return values;
    }

    [Theory]
    [InlineData(256)]
    [InlineData(2048)]
    public void TheForwardTransformIsTheTransposeOfTheInverse(int n)
    {
        int m = n / 2;
        float[] z = Pattern(n, 7);
        var transform = new InverseMdct(n, 1f);
        var actual = new float[m];
        transform.Forward(z, actual, 1f);

        for (int k = 0; k < m; k++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += z[i] * Math.Cos(Math.PI / m * (i + 0.5 + (m / 2.0)) * (k + 0.5));
            }

            Assert.Equal(sum, actual[k], 1e-3);
        }
    }

    [Fact]
    public void TheInverseTransformIsTheSumTheStandardDefines()
    {
        const int n = 256;
        int m = n / 2;
        float[] x = Pattern(m, 11);
        var transform = new InverseMdct(n, 1f);
        var actual = new float[n];
        transform.Transform(x, actual);

        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int k = 0; k < m; k++)
            {
                sum += x[k] * Math.Cos(Math.PI / m * (i + 0.5 + (m / 2.0)) * (k + 0.5));
            }

            Assert.Equal(sum, actual[i], 1e-3);
        }
    }
}
