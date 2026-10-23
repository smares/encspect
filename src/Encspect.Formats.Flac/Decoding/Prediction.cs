using System.Numerics;

namespace Encspect.Formats.Flac.Decoding;

// Turns the warm-up samples and residual a walk stored back into samples (RFC 9639, section 9.2). Arithmetic wraps, so a stream that breaks
// the bounds of its bit depth decodes to something and its MD5 says it differs.
internal static class Prediction
{
    // d[0, order) are the warm-up samples and d[order, n) the residual; on return all of d[0, n) are samples.
    public static void RestoreFixed(Span<int> d, int order, int n)
    {
        switch (order)
        {
            case 1:
                for (int i = 1; i < n; i++)
                {
                    d[i] += d[i - 1];
                }

                break;
            case 2:
                for (int i = 2; i < n; i++)
                {
                    d[i] += (2 * d[i - 1]) - d[i - 2];
                }

                break;
            case 3:
                for (int i = 3; i < n; i++)
                {
                    d[i] += (3 * (d[i - 1] - d[i - 2])) + d[i - 3];
                }

                break;
            case 4:
                for (int i = 4; i < n; i++)
                {
                    d[i] += (4 * (d[i - 1] + d[i - 3])) - (6 * d[i - 2]) - d[i - 4];
                }

                break;
        }
    }

    public static void RestoreFixed(Span<long> d, int order, int n)
    {
        switch (order)
        {
            case 1:
                for (int i = 1; i < n; i++)
                {
                    d[i] += d[i - 1];
                }

                break;
            case 2:
                for (int i = 2; i < n; i++)
                {
                    d[i] += (2 * d[i - 1]) - d[i - 2];
                }

                break;
            case 3:
                for (int i = 3; i < n; i++)
                {
                    d[i] += (3 * (d[i - 1] - d[i - 2])) + d[i - 3];
                }

                break;
            case 4:
                for (int i = 4; i < n; i++)
                {
                    d[i] += (4 * (d[i - 1] + d[i - 3])) - (6 * d[i - 2]) - d[i - 4];
                }

                break;
        }
    }

    // Whether the prediction sum of this subframe fits 32 bits: the rule libFLAC uses to pick its narrow path.
    public static bool FitsInt(int sampleBits, int precision, int order) => sampleBits + precision + (32 - BitOperations.LeadingZeroCount((uint)(order - 1))) <= 32;

    public static void RestoreLpc(Span<int> d, ReadOnlySpan<int> coefficients, int order, int shift, int n, bool isNarrow)
    {
        if (isNarrow)
        {
            for (int i = order; i < n; i++)
            {
                int sum = 0;
                for (int j = 0; j < order; j++)
                {
                    sum += coefficients[j] * d[i - 1 - j];
                }

                d[i] += sum >> shift;
            }

            return;
        }

        for (int i = order; i < n; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++)
            {
                sum += (long)coefficients[j] * d[i - 1 - j];
            }

            d[i] += (int)(sum >> shift);
        }
    }

    public static void RestoreLpc(Span<long> d, ReadOnlySpan<int> coefficients, int order, int shift, int n)
    {
        for (int i = order; i < n; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++)
            {
                sum += coefficients[j] * d[i - 1 - j];
            }

            d[i] += sum >> shift;
        }
    }
}
