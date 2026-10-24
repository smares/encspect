namespace Encspect.Formats.TrueHd.Decoding;

// The prediction filters of a channel (docs/design/truehd-format.md, "Prediction filters").
internal static class Filters
{
    // The history in front of a unit's samples: the 8 earlier outputs of the FIR filter and the earlier errors of the IIR filter.
    public const int History = 8;

    // Filters the residuals of one block of a channel in place of a separate output. y and e hold History samples of history and then the unit's
    // samples; the block starts at sample first of the unit. For each sample: the prediction is the sum of the FIR coefficients times the
    // outputs before it and the IIR coefficients times the errors before it, shifted right; the output is the residual plus the prediction with the
    // low step bits cleared, and the error is the output less the prediction.
    public static void Block(ReadOnlySpan<int> residuals, Span<int> y, Span<int> e, int first, int count, ChannelState channel, int step)
    {
        int firOrder = channel.FirOrder;
        int iirOrder = channel.IirOrder;
        int mask = ~((1 << step) - 1);
        int at = History + first;
        if (firOrder + iirOrder == 0)
        {
            for (int i = 0; i < count; i++)
            {
                int value = residuals[i] & mask;
                y[at + i] = value;
                e[at + i] = value;
            }

            return;
        }

        int shift = channel.FilterShift;
        int[] fir = channel.FirCoefficients;
        int[] iir = channel.IirCoefficients;
        for (int i = 0; i < count; i++)
        {
            int position = at + i;
            long sum = 0;
            for (int k = 0; k < firOrder; k++)
            {
                sum += (long)fir[k] * y[position - 1 - k];
            }

            for (int k = 0; k < iirOrder; k++)
            {
                sum += (long)iir[k] * e[position - 1 - k];
            }

            int prediction = (int)(sum >> shift);
            int value = unchecked(residuals[i] + prediction) & mask;
            y[position] = value;
            e[position] = unchecked(value - prediction);
        }
    }
}
