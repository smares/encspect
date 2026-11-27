using Encspect.Core;

namespace Encspect.Formats.Aac.Decoding;

// What a header's frequency tables imply for the reconstruction: the patches that copy low band QMF bands up (ISO/IEC 14496-3 4.6.18.6.3) and the limiter bands (4.6.18.7.5), built the way
// libxaac does (ixheaacd_generate_hf for the patches, ixheaacd_createlimiterbands for the limiter bands; Apache License 2.0, Copyright (C) The Android Open Source Project; the notice is
// in THIRD-PARTY-NOTICES.md).
internal sealed class SbrLayout
{
    public const int MaxPatches = 6;
    private static readonly double[] LimiterBandsPerOctave = [0, 1.2, 2.0, 3.0];

    public SbrLayout(SbrTables tables, int rate)
    {
        Tables = tables;
        Kx = tables.Kx;
        K2 = tables.K2;
        High = tables.High.Length - 1;
        Low = tables.Low.Length - 1;
        Noise = tables.Noise.Length - 1;
        Rate = rate;
        BuildPatches(tables.Master, tables.Kx, rate);
        BuildLimiter(tables.Low);
    }

    public SbrTables Tables { get; }

    public int Kx { get; }

    public int K2 { get; }

    public int High { get; }

    public int Low { get; }

    public int Noise { get; }

    public int Rate { get; }

    public int PatchCount { get; private set; }

    public int[] PatchStart { get; } = new int[MaxPatches + 1];

    public int[] PatchBands { get; } = new int[MaxPatches + 1];

    public int[] PatchStride { get; } = new int[MaxPatches + 1];

    // The limiter bands of limiter_bands 0 to 3: the edges relative to kx, and how many bands.
    public int[][] Limiter { get; } = [new int[14], new int[14], new int[14], new int[14]];

    public int[] LimiterCount { get; } = new int[4];

    private static int Closest(int goal, int[] master, int count)
    {
        if (goal <= master[0])
        {
            return master[0];
        }

        if (goal >= master[count])
        {
            return master[count];
        }

        int index = count;
        while (master[index] > goal)
        {
            index--;
        }

        return master[index];
    }

    private void BuildPatches(int[] master, int kx, int rate)
    {
        int count = master.Length - 1;
        int lsb = master[0];
        int usb = master[count];
        int offset = kx - master[0];
        int goal = (int)((2.048e6f / rate) + 0.5f);
        if (goal < usb)
        {
            int index = 0;
            while (master[index] < goal)
            {
                index++;
            }

            goal = master[index];
        }
        else
        {
            goal = usb;
        }

        int sourceStart = offset + 1;
        int sb = lsb + offset;
        int patch = 0;
        bool broke = false;
        while (sb < usb && patch < MaxPatches)
        {
            PatchStart[patch] = sb;
            int bands = goal - sb;
            if (bands >= lsb - sourceStart)
            {
                int stride = (sb - sourceStart) & ~1;
                bands = lsb - (sb - stride);
                bands = Closest(sb + bands, master, count) - sb;
            }

            int step = (bands + sb - lsb + 1) & ~1;
            sourceStart = 1;
            if (goal - (sb + bands) < 3)
            {
                goal = usb;
            }

            if (bands < 3 && patch > 0 && sb + bands == usb)
            {
                break;
            }

            if (bands < 0 && broke)
            {
                break;
            }

            if (bands < 0)
            {
                broke = true;
                continue;
            }

            broke = false;
            PatchBands[patch] = bands;
            PatchStride[patch] = step;
            sb += bands;
            patch++;
        }

        PatchCount = patch;
        PatchStart[patch] = usb;
    }

    private void BuildLimiter(int[] low)
    {
        int bandCount = low.Length - 1;
        int start = low[0];
        int end = low[bandCount];
        Limiter[0][0] = 0;
        Limiter[0][1] = end - start;
        LimiterCount[0] = 1;
        Span<int> borders = stackalloc int[MaxPatches + 1];
        for (int i = 0; i < PatchCount; i++)
        {
            borders[i] = PatchStart[i] - start;
        }

        borders[PatchCount] = end - start;
        Span<int> temp = stackalloc int[32 + MaxPatches + 1];
        for (int i = 1; i < 4; i++)
        {
            for (int k = 0; k <= bandCount; k++)
            {
                temp[k] = low[k] - start;
            }

            for (int k = 1; k < PatchCount; k++)
            {
                temp[bandCount + k] = borders[k];
            }

            int gate = bandCount + PatchCount - 1;
            temp[..(gate + 1)].Sort();
            for (int j = 1; j <= gate; j++)
            {
                double octaves = DetMath.Log2((double)(temp[j] + start) / (temp[j - 1] + start));
                if (octaves * LimiterBandsPerOctave[i] >= 0.49)
                {
                    continue;
                }

                if (temp[j] == temp[j - 1])
                {
                    temp[j] = end;
                    temp[..(gate + 1)].Sort();
                    gate--;
                    j--;
                    continue;
                }

                bool previousIsBorder = false;
                bool currentIsBorder = false;
                for (int k = 0; k <= PatchCount; k++)
                {
                    previousIsBorder |= temp[j - 1] == borders[k];
                    currentIsBorder |= temp[j] == borders[k];
                }

                if (!currentIsBorder)
                {
                    temp[j] = end;
                    temp[..(gate + 1)].Sort();
                    gate--;
                    j--;
                }
                else if (!previousIsBorder)
                {
                    temp[j - 1] = end;
                    temp[..(gate + 1)].Sort();
                    gate--;
                    j--;
                }
            }

            gate = Math.Min(gate, 12);
            LimiterCount[i] = gate;
            for (int k = 0; k <= gate; k++)
            {
                Limiter[i][k] = temp[k];
            }
        }
    }
}
