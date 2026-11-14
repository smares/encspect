using System.Runtime.CompilerServices;

namespace Encspect.Formats.MpegAudio.Decoding;

// The polyphase synthesis filterbank (ISO/IEC 11172-3 2.4.3.2.2, 2.4.3.3.5 and 2.4.3.4.10, Figure A.2 and Table B.3): a 32-point DCT-II over the subbands of each time slot, then the 512-tap window over the
// last sixteen slots. It follows minimp3's mp3d_DCT_II and mp3d_synth (CC0 1.0, third_party/minimp3), whose window is the standard's scaled to integers and
// whose output is full scale 32768, here scaled by 1 / 32768 to the sample range of +-1.0. Time slots go in pairs, two channels in four lanes, as minimp3 lays them out, so
// the sums of every lane are the ones minimp3's vector code makes. The history is 15 rows of 64 floats.
internal static class Polyphase
{
    public const int HistoryLength = 15 * 64;

    // The scratch a synthesis of nbands slots needs: the history rows and one row per slot.
    public const int ScratchLength = (18 + 15) * 64;

    private const float OutputScale = 1.0f / 32768.0f;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DctII(Span<float> lines, int n)
    {
        ReadOnlySpan<float> sec = SynthesisTables.SecantFactors;
        Span<float> t = stackalloc float[4 * 8];
        for (int k = 0; k < n; k++)
        {
            for (int i = 0; i < 8; i++)
            {
                float x0 = lines[k + (i * 18)];
                float x1 = lines[k + ((15 - i) * 18)];
                float x2 = lines[k + ((16 + i) * 18)];
                float x3 = lines[k + ((31 - i) * 18)];
                float t0 = x0 + x3;
                float t1 = x1 + x2;
                float t2 = (x1 - x2) * sec[(3 * i) + 0];
                float t3 = (x0 - x3) * sec[(3 * i) + 1];
                t[i] = t0 + t1;
                t[8 + i] = (t0 - t1) * sec[(3 * i) + 2];
                t[16 + i] = t3 + t2;
                t[24 + i] = (t3 - t2) * sec[(3 * i) + 2];
            }

            for (int row = 0; row < 4; row++)
            {
                int x = row * 8;
                float x0 = t[x];
                float x1 = t[x + 1];
                float x2 = t[x + 2];
                float x3 = t[x + 3];
                float x4 = t[x + 4];
                float x5 = t[x + 5];
                float x6 = t[x + 6];
                float x7 = t[x + 7];
                float xt = x0 - x7;
                x0 += x7;
                x7 = x1 - x6;
                x1 += x6;
                x6 = x2 - x5;
                x2 += x5;
                x5 = x3 - x4;
                x3 += x4;
                x4 = x0 - x3;
                x0 += x3;
                x3 = x1 - x2;
                x1 += x2;
                t[x] = x0 + x1;
                t[x + 4] = (x0 - x1) * 0.70710677f;
                x5 += x6;
                x6 = (x6 + x7) * 0.70710677f;
                x7 += xt;
                x3 = (x3 + x4) * 0.70710677f;
                x5 -= x7 * 0.198912367f;
                x7 += x5 * 0.382683432f;
                x5 -= x7 * 0.198912367f;
                x0 = xt - x6;
                xt += x6;
                t[x + 1] = (xt + x7) * 0.50979561f;
                t[x + 2] = (x4 + x3) * 0.54119611f;
                t[x + 3] = (x0 - x5) * 0.60134488f;
                t[x + 5] = (x0 + x5) * 0.89997619f;
                t[x + 6] = (x4 - x3) * 1.30656302f;
                t[x + 7] = (xt - x7) * 2.56291556f;
            }

            int y = k;
            for (int i = 0; i < 7; i++, y += 4 * 18)
            {
                float s = t[24 + i] + t[24 + i + 1];
                lines[y] = t[i];
                lines[y + 18] = t[16 + i] + s;
                lines[y + (2 * 18)] = t[8 + i] + t[8 + i + 1];
                lines[y + (3 * 18)] = t[16 + 1 + i] + s;
            }

            lines[y] = t[7];
            lines[y + 18] = t[16 + 7] + t[24 + 7];
            lines[y + (2 * 18)] = t[8 + 7];
            lines[y + (3 * 18)] = t[24 + 7];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Pair(Span<float> pcm, int offset, int channels, ReadOnlySpan<float> z, int zo)
    {
        float a = (z[zo + (14 * 64)] - z[zo]) * 29;
        a += (z[zo + (1 * 64)] + z[zo + (13 * 64)]) * 213;
        a += (z[zo + (12 * 64)] - z[zo + (2 * 64)]) * 459;
        a += (z[zo + (3 * 64)] + z[zo + (11 * 64)]) * 2037;
        a += (z[zo + (10 * 64)] - z[zo + (4 * 64)]) * 5153;
        a += (z[zo + (5 * 64)] + z[zo + (9 * 64)]) * 6574;
        a += (z[zo + (8 * 64)] - z[zo + (6 * 64)]) * 37489;
        a += z[zo + (7 * 64)] * 75038;
        pcm[offset] = a * OutputScale;

        zo += 2;
        a = z[zo + (14 * 64)] * 104;
        a += z[zo + (12 * 64)] * 1567;
        a += z[zo + (10 * 64)] * 9727;
        a += z[zo + (8 * 64)] * 64019;
        a += z[zo + (6 * 64)] * -9975;
        a += z[zo + (4 * 64)] * -45;
        a += z[zo + (2 * 64)] * 146;
        a += z[zo + (0 * 64)] * -5;
        pcm[offset + (16 * channels)] = a * OutputScale;
    }

    // Two time slots (starting at the slot xl points at) of every channel: xl and xr are the DCT-II outputs of the left and right channel (the same for one channel), dst
    // the interleaved output from the slot's first sample, lins the history and the slots' rows.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Synth(ReadOnlySpan<float> lines, int xl, int xr, Span<float> pcm, int dstl, int channels, Span<float> lins)
    {
        int dstr = dstl + (channels - 1);
        int zlin = 15 * 64;
        ReadOnlySpan<int> window = SynthesisTables.Window;

        lins[zlin + (4 * 15)] = lines[xl + (18 * 16)];
        lins[zlin + (4 * 15) + 1] = lines[xr + (18 * 16)];
        lins[zlin + (4 * 15) + 2] = lines[xl];
        lins[zlin + (4 * 15) + 3] = lines[xr];

        lins[zlin + (4 * 31)] = lines[xl + 1 + (18 * 16)];
        lins[zlin + (4 * 31) + 1] = lines[xr + 1 + (18 * 16)];
        lins[zlin + (4 * 31) + 2] = lines[xl + 1];
        lins[zlin + (4 * 31) + 3] = lines[xr + 1];

        Pair(pcm, dstr, channels, lins, (4 * 15) + 1);
        Pair(pcm, dstr + (32 * channels), channels, lins, (4 * 15) + 64 + 1);
        Pair(pcm, dstl, channels, lins, 4 * 15);
        Pair(pcm, dstl + (32 * channels), channels, lins, (4 * 15) + 64);

        Span<float> a = stackalloc float[4];
        Span<float> b = stackalloc float[4];
        int w = 0;
        for (int i = 14; i >= 0; i--)
        {
            lins[zlin + (4 * i)] = lines[xl + (18 * (31 - i))];
            lins[zlin + (4 * i) + 1] = lines[xr + (18 * (31 - i))];
            lins[zlin + (4 * i) + 2] = lines[xl + 1 + (18 * (31 - i))];
            lins[zlin + (4 * i) + 3] = lines[xr + 1 + (18 * (31 - i))];
            lins[zlin + (4 * (i + 16))] = lines[xl + 1 + (18 * (1 + i))];
            lins[zlin + (4 * (i + 16)) + 1] = lines[xr + 1 + (18 * (1 + i))];
            lins[zlin + (4 * (i - 16)) + 2] = lines[xl + (18 * (1 + i))];
            lins[zlin + (4 * (i - 16)) + 3] = lines[xr + (18 * (1 + i))];

            for (int k = 0; k < 8; k++)
            {
                float w0 = window[w++];
                float w1 = window[w++];
                int vz = zlin + (4 * i) - (k * 64);
                int vy = zlin + (4 * i) - ((15 - k) * 64);
                for (int j = 0; j < 4; j++)
                {
                    float zj = lins[vz + j];
                    float yj = lins[vy + j];
                    if (k == 0)
                    {
                        b[j] = (zj * w1) + (yj * w0);
                        a[j] = (zj * w0) - (yj * w1);
                    }
                    else if ((k & 1) == 1)
                    {
                        b[j] += (zj * w1) + (yj * w0);
                        a[j] += (yj * w1) - (zj * w0);
                    }
                    else
                    {
                        b[j] += (zj * w1) + (yj * w0);
                        a[j] += (zj * w0) - (yj * w1);
                    }
                }
            }

            pcm[dstr + ((15 - i) * channels)] = a[1] * OutputScale;
            pcm[dstr + ((17 + i) * channels)] = b[1] * OutputScale;
            pcm[dstl + ((15 - i) * channels)] = a[0] * OutputScale;
            pcm[dstl + ((17 + i) * channels)] = b[0] * OutputScale;
            pcm[dstr + ((47 - i) * channels)] = a[3] * OutputScale;
            pcm[dstr + ((49 + i) * channels)] = b[3] * OutputScale;
            pcm[dstl + ((47 - i) * channels)] = a[2] * OutputScale;
            pcm[dstl + ((49 + i) * channels)] = b[2] * OutputScale;
        }
    }

    // Synthesizes nbands time slots (18 for a Layer III granule, 12 for a Layer I or II part) of all channels: lines holds channel c's slots from 576 * c (32 subbands of 18
    // values, the slot the offset in each), pcm receives nbands * 32 samples of each channel interleaved, and history carries the filter state from call to call.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Granule(Span<float> history, Span<float> lines, int nbands, int channels, Span<float> pcm, Span<float> lins)
    {
        for (int ch = 0; ch < channels; ch++)
        {
            DctII(lines.Slice(576 * ch), nbands);
        }

        history[..HistoryLength].CopyTo(lins);
        for (int i = 0; i < nbands; i += 2)
        {
            Synth(lines, i, i + (576 * (channels - 1)), pcm, 32 * channels * i, channels, lins.Slice(i * 64));
        }

        lins.Slice(nbands * 64, HistoryLength).CopyTo(history);
    }
}
