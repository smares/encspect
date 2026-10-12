namespace Encspect.Formats.Ac3;

// The parametric bit allocation of A/52 7.2.2, as a decoder runs it, in the fixed-point integer
// operations the specification defines: exponents to a power spectral density (7.2.2.2), its
// integration into 50 bands (7.2.2.3), the excitation (7.2.2.4) and masking curves (7.2.2.5),
// delta bit allocation (7.2.2.6) and the bit allocation pointers (7.2.2.7). The pointers say how
// many bits each mantissa takes, which is all the analyzer needs to step over the mantissas to
// the next audio block.
internal static class BitAllocation
{
    public const int Bands = 50;

    // The parameters of one channel's allocation.
    public struct Parameters
    {
        public int SlowDecay;
        public int FastDecay;
        public int SlowGain;
        public int DbPerBit;
        public int Floor;
        public int FastGain;
        public int SnrOffset;
        public int FastLeak;
        public int SlowLeak;
        public bool IsCoupling;
        public bool IsLfe;
        public int SampleRateCode;
    }

    // Writes bap[start..end) from exp[start..end). deltaBands is the delta bit allocation per band
    // (the masking curve's offsets, 50 of them), or empty for none.
    public static void Compute(ReadOnlySpan<byte> exp, int start, int end, in Parameters p, ReadOnlySpan<int> deltaBands, Span<byte> bap)
    {
        Span<int> psd = stackalloc int[256];
        Span<int> bandPsd = stackalloc int[Bands];
        Span<int> excite = stackalloc int[Bands];
        Span<int> mask = stackalloc int[Bands];

        // 7.2.2.2.
        for (int bin = start; bin < end; bin++)
        {
            psd[bin] = 3072 - (exp[bin] << 7);
        }

        // 7.2.2.3: log-addition within each band.
        int j = start;
        int k = Tables.MaskTab[start];
        int lastBin;
        do
        {
            lastBin = Math.Min(Tables.BandStart[k + 1], end);
            bandPsd[k] = psd[j];
            j++;
            for (int i = j; i < lastBin; i++)
            {
                bandPsd[k] = LogAdd(bandPsd[k], psd[j]);
                j++;
            }

            k++;
        }
        while (end > lastBin);

        // 7.2.2.4.
        int bandStart = Tables.MaskTab[start];
        int bandEnd = Tables.MaskTab[end - 1] + 1;
        int begin;
        int fastLeak;
        int slowLeak;
        if (bandStart == 0)
        {
            int lowComp = 0;
            lowComp = LowComp(lowComp, bandPsd[0], bandPsd[1], 0);
            excite[0] = bandPsd[0] - p.FastGain - lowComp;
            lowComp = LowComp(lowComp, bandPsd[1], bandPsd[2], 1);
            excite[1] = bandPsd[1] - p.FastGain - lowComp;
            begin = 7;
            fastLeak = 0;
            slowLeak = 0;
            for (int bin = 2; bin < 7; bin++)
            {
                // The last band of the LFE channel (bin 6, its seventh) has no neighbor above.
                bool isLastLfeBand = bandEnd == 7 && bin == 6;
                if (!isLastLfeBand)
                {
                    lowComp = LowComp(lowComp, bandPsd[bin], bandPsd[bin + 1], bin);
                }

                fastLeak = bandPsd[bin] - p.FastGain;
                slowLeak = bandPsd[bin] - p.SlowGain;
                excite[bin] = fastLeak - lowComp;
                if (!isLastLfeBand && bandPsd[bin] <= bandPsd[bin + 1])
                {
                    begin = bin + 1;
                    break;
                }
            }

            for (int bin = begin; bin < Math.Min(bandEnd, 22); bin++)
            {
                if (bandEnd != 7 || bin != 6)
                {
                    lowComp = LowComp(lowComp, bandPsd[bin], bandPsd[bin + 1], bin);
                }

                fastLeak -= p.FastDecay;
                fastLeak = Math.Max(fastLeak, bandPsd[bin] - p.FastGain);
                slowLeak -= p.SlowDecay;
                slowLeak = Math.Max(slowLeak, bandPsd[bin] - p.SlowGain);
                excite[bin] = Math.Max(fastLeak - lowComp, slowLeak);
            }

            begin = 22;
        }
        else
        {
            begin = bandStart;
            fastLeak = p.FastLeak;
            slowLeak = p.SlowLeak;
        }

        for (int bin = begin; bin < bandEnd; bin++)
        {
            fastLeak -= p.FastDecay;
            fastLeak = Math.Max(fastLeak, bandPsd[bin] - p.FastGain);
            slowLeak -= p.SlowDecay;
            slowLeak = Math.Max(slowLeak, bandPsd[bin] - p.SlowGain);
            excite[bin] = Math.Max(fastLeak, slowLeak);
        }

        // 7.2.2.5.
        short[] hearing = p.SampleRateCode switch { 0 => Tables.Hearing48, 1 => Tables.Hearing44, _ => Tables.Hearing32 };
        for (int bin = bandStart; bin < bandEnd; bin++)
        {
            if (bandPsd[bin] < p.DbPerBit)
            {
                excite[bin] += (p.DbPerBit - bandPsd[bin]) >> 2;
            }

            mask[bin] = Math.Max(excite[bin], (int)hearing[bin]);
        }

        // 7.2.2.6.
        if (!deltaBands.IsEmpty)
        {
            for (int band = 0; band < Bands; band++)
            {
                mask[band] += deltaBands[band];
            }
        }

        // 7.2.2.7.
        int i2 = start;
        int j2 = Tables.MaskTab[start];
        do
        {
            lastBin = Math.Min(Tables.BandStart[j2 + 1], end);
            int m = mask[j2] - p.SnrOffset - p.Floor;
            if (m < 0)
            {
                m = 0;
            }

            m &= 0x1FE0;
            m += p.Floor;
            for (; i2 < lastBin; i2++)
            {
                int address = Math.Clamp((psd[i2] - m) >> 5, 0, 63);
                bap[i2] = Tables.BapTab[address];
            }

            j2++;
        }
        while (end > lastBin);
    }

    private static int LogAdd(int a, int b)
    {
        int c = a - b;
        int address = Math.Min(Math.Abs(c) >> 1, 255);
        return c >= 0 ? a + Tables.LogAdd[address] : b + Tables.LogAdd[address];
    }

    private static int LowComp(int a, int b0, int b1, int bin)
    {
        if (bin < 7)
        {
            if (b0 + 256 == b1)
            {
                a = 384;
            }
            else if (b0 > b1)
            {
                a = Math.Max(0, a - 64);
            }
        }
        else if (bin < 20)
        {
            if (b0 + 256 == b1)
            {
                a = 320;
            }
            else if (b0 > b1)
            {
                a = Math.Max(0, a - 64);
            }
        }
        else
        {
            a = Math.Max(0, a - 128);
        }

        return a;
    }
}
