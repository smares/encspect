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

        // The coefficients of both filters, zero past their orders and the oldest sample first, so that a sample's sum is one fixed run of
        // multiplications over the 8 outputs and 4 errors before it.
        Span<long> fir = stackalloc long[ChannelState.MaxFirOrder];
        Span<long> iir = stackalloc long[ChannelState.MaxIirOrder];
        for (int k = 0; k < firOrder; k++)
        {
            fir[ChannelState.MaxFirOrder - 1 - k] = channel.FirCoefficients[k];
        }

        for (int k = 0; k < iirOrder; k++)
        {
            iir[ChannelState.MaxIirOrder - 1 - k] = channel.IirCoefficients[k];
        }

        long f0 = fir[0], f1 = fir[1], f2 = fir[2], f3 = fir[3], f4 = fir[4], f5 = fir[5], f6 = fir[6], f7 = fir[7];
        long i0 = iir[0], i1 = iir[1], i2 = iir[2], i3 = iir[3];
        bool hasFir = firOrder > 0;
        bool hasIir = iirOrder > 0;
        for (int i = 0; i < count; i++)
        {
            int position = at + i;
            long sum = 0;
            if (hasFir)
            {
                ReadOnlySpan<int> past = y.Slice(position - ChannelState.MaxFirOrder, ChannelState.MaxFirOrder);
                sum = (f0 * past[0]) + (f1 * past[1]) + (f2 * past[2]) + (f3 * past[3]) + (f4 * past[4]) + (f5 * past[5]) + (f6 * past[6]) + (f7 * past[7]);
            }

            if (hasIir)
            {
                ReadOnlySpan<int> errors = e.Slice(position - ChannelState.MaxIirOrder, ChannelState.MaxIirOrder);
                sum += (i0 * errors[0]) + (i1 * errors[1]) + (i2 * errors[2]) + (i3 * errors[3]);
            }

            int prediction = (int)(sum >> shift);
            int value = unchecked(residuals[i] + prediction) & mask;
            y[position] = value;
            e[position] = unchecked(value - prediction);
        }
    }
}
