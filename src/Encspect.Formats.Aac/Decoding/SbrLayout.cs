using Encspect.Core;

namespace Encspect.Formats.Aac.Decoding;

// What a header's frequency tables imply for the reconstruction: the patches that copy low band QMF bands up (ISO/IEC 14496-3 4.6.18.6.3) (built as Figure 4.48 gives them, shared with the parser's check of 4.6.18.3.6) and the limiter
// bands (4.6.18.7.5), built the way libxaac does (ixheaacd_createlimiterbands; Apache License 2.0, Copyright (C) The Android Open Source Project; the notice is in THIRD-PARTY-NOTICES.md).
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

    // The patches of Figure 4.48 (4.6.18.6.3), as the parser counts them: patch i fills the QMF bands from kx plus the widths before it, copying from the bands that start at its
    // patchStartSubband; PatchStride is the distance from a band to its source.
    private void BuildPatches(int[] master, int kx, int rate)
    {
        (int count, int[] widths, int[] sources) = Sbr.Patches(master, kx, rate);
        int at = kx;
        PatchCount = Math.Min(count, MaxPatches);
        for (int patch = 0; patch < PatchCount; patch++)
        {
            PatchStart[patch] = at;
            PatchBands[patch] = widths[patch];
            PatchStride[patch] = at - sources[patch];
            at += widths[patch];
        }

        PatchStart[PatchCount] = master[^1];
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
