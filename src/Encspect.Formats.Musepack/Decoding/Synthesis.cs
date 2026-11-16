// Reading derived from libmpcdec r495 (synth_filter.c, mpc_compute_new_V and mpc_synthese_filter_float_internal), Copyright (c) 2005-2009 The Musepack Development Team, under the
// BSD 3-clause license. The license text is in THIRD-PARTY-NOTICES.md.

using System.Runtime.CompilerServices;

namespace Encspect.Formats.Musepack.Decoding;

// The 32-band polyphase synthesis filterbank (ISO/IEC 11172-3 2.4.3.2.2), as libmpcdec computes it in floating point: for each of a frame's 36 time slots a fast cosine transform
// of the 32 subband samples into 64 values of the V buffer (Byeong Gi Lee's algorithm), then each output sample as the sum of 16 products of V values and window coefficients, in
// that order. Every operation is a single-precision one in libmpcdec's order, so the result is the same on every platform. The V buffer holds 15 slots of history before the
// frame's 36, which move down at the start of each frame.
internal sealed class Synthesis
{
    public const int History = 960;

    private const int Memory = 2304;

    private static readonly float[] Window = Create();

    private readonly float[] _v = new float[Memory + History];

    // The window of libmpcdec's layout: 32 rows of 16 coefficients, each an integer over 65,536.
    private static float[] Create()
    {
        ReadOnlySpan<int> numerators = SynthesisTables.WindowNumerators;
        var window = new float[numerators.Length];
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = numerators[i] / 65536.0f;
        }

        return window;
    }

    public void Reset() => Array.Clear(_v);

    // Synthesizes the frame's 36 slots of 32 subband samples (36 rows of 32 values) into 1,152 samples of one channel.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Run(ReadOnlySpan<float> subbands, Span<float> output)
    {
        float[] v = _v;
        Array.Copy(v, 0, v, Memory, History);
        float[] window = Window;
        int baseIndex = Memory;
        for (int slot = 0; slot < 36; slot++)
        {
            baseIndex -= 64;
            NewV(subbands.Slice(slot * 32, 32), v.AsSpan(baseIndex, 64));
            int pV = baseIndex;
            int pD = 0;
            Span<float> row = output.Slice(slot * 32, 32);
            for (int k = 0; k < 32; k++)
            {
                float sum = (v[pV] * window[pD]) + (v[pV + 96] * window[pD + 1]);
                sum += v[pV + 128] * window[pD + 2];
                sum += v[pV + 224] * window[pD + 3];
                sum += v[pV + 256] * window[pD + 4];
                sum += v[pV + 352] * window[pD + 5];
                sum += v[pV + 384] * window[pD + 6];
                sum += v[pV + 480] * window[pD + 7];
                sum += v[pV + 512] * window[pD + 8];
                sum += v[pV + 608] * window[pD + 9];
                sum += v[pV + 640] * window[pD + 10];
                sum += v[pV + 736] * window[pD + 11];
                sum += v[pV + 768] * window[pD + 12];
                sum += v[pV + 864] * window[pD + 13];
                sum += v[pV + 896] * window[pD + 14];
                sum += v[pV + 992] * window[pD + 15];
                row[k] = sum;
                pV++;
                pD += 16;
            }
        }
    }

    // The 64 new V values of one slot from its 32 subband samples (libmpcdec's mpc_compute_new_V). The constants are the cosine factors of the fast transform as libmpcdec writes
    // them; index 16 of the 64 is never written and stays 0.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void NewV(ReadOnlySpan<float> s, Span<float> v)
    {
        float a00 = s[0] + s[31];
        float a01 = s[1] + s[30];
        float a02 = s[2] + s[29];
        float a03 = s[3] + s[28];
        float a04 = s[4] + s[27];
        float a05 = s[5] + s[26];
        float a06 = s[6] + s[25];
        float a07 = s[7] + s[24];
        float a08 = s[8] + s[23];
        float a09 = s[9] + s[22];
        float a10 = s[10] + s[21];
        float a11 = s[11] + s[20];
        float a12 = s[12] + s[19];
        float a13 = s[13] + s[18];
        float a14 = s[14] + s[17];
        float a15 = s[15] + s[16];

        float b00 = a00 + a15;
        float b01 = a01 + a14;
        float b02 = a02 + a13;
        float b03 = a03 + a12;
        float b04 = a04 + a11;
        float b05 = a05 + a10;
        float b06 = a06 + a09;
        float b07 = a07 + a08;
        float b08 = (a00 - a15) * 0.5024192929f;
        float b09 = (a01 - a14) * 0.5224986076f;
        float b10 = (a02 - a13) * 0.5669440627f;
        float b11 = (a03 - a12) * 0.6468217969f;
        float b12 = (a04 - a11) * 0.7881546021f;
        float b13 = (a05 - a10) * 1.0606776476f;
        float b14 = (a06 - a09) * 1.7224471569f;
        float b15 = (a07 - a08) * 5.1011486053f;

        a00 = b00 + b07;
        a01 = b01 + b06;
        a02 = b02 + b05;
        a03 = b03 + b04;
        a04 = (b00 - b07) * 0.5097956061f;
        a05 = (b01 - b06) * 0.6013448834f;
        a06 = (b02 - b05) * 0.8999761939f;
        a07 = (b03 - b04) * 2.5629155636f;
        a08 = b08 + b15;
        a09 = b09 + b14;
        a10 = b10 + b13;
        a11 = b11 + b12;
        a12 = (b08 - b15) * 0.5097956061f;
        a13 = (b09 - b14) * 0.6013448834f;
        a14 = (b10 - b13) * 0.8999761939f;
        a15 = (b11 - b12) * 2.5629155636f;

        b00 = a00 + a03;
        b01 = a01 + a02;
        b02 = (a00 - a03) * 0.5411961079f;
        b03 = (a01 - a02) * 1.3065630198f;
        b04 = a04 + a07;
        b05 = a05 + a06;
        b06 = (a04 - a07) * 0.5411961079f;
        b07 = (a05 - a06) * 1.3065630198f;
        b08 = a08 + a11;
        b09 = a09 + a10;
        b10 = (a08 - a11) * 0.5411961079f;
        b11 = (a09 - a10) * 1.3065630198f;
        b12 = a12 + a15;
        b13 = a13 + a14;
        b14 = (a12 - a15) * 0.5411961079f;
        b15 = (a13 - a14) * 1.3065630198f;

        a00 = b00 + b01;
        a01 = (b00 - b01) * 0.7071067691f;
        a02 = b02 + b03;
        a03 = (b02 - b03) * 0.7071067691f;
        a04 = b04 + b05;
        a05 = (b04 - b05) * 0.7071067691f;
        a06 = b06 + b07;
        a07 = (b06 - b07) * 0.7071067691f;
        a08 = b08 + b09;
        a09 = (b08 - b09) * 0.7071067691f;
        a10 = b10 + b11;
        a11 = (b10 - b11) * 0.7071067691f;
        a12 = b12 + b13;
        a13 = (b12 - b13) * 0.7071067691f;
        a14 = b14 + b15;
        a15 = (b14 - b15) * 0.7071067691f;

        v[48] = -a00;
        v[0] = a01;
        v[8] = a03;
        v[40] = -a02 - v[8];
        v[12] = a07;
        v[4] = a05 + v[12];
        v[36] = -(v[4] + a06);
        v[44] = -a04 - a06 - a07;
        v[14] = a15;
        v[10] = a11 + v[14];
        v[6] = v[10] + a13;
        v[2] = a09 + a13 + a15;
        v[34] = -v[2] - a14;
        v[38] = v[34] + a09 - a10 - a11;
        float tmp = -(a12 + a14 + a15);
        v[46] = tmp - a08;
        v[42] = tmp - a10 - a11;

        a00 = (s[0] - s[31]) * 0.5006030202f;
        a01 = (s[1] - s[30]) * 0.5054709315f;
        a02 = (s[2] - s[29]) * 0.5154473186f;
        a03 = (s[3] - s[28]) * 0.5310425758f;
        a04 = (s[4] - s[27]) * 0.5531039238f;
        a05 = (s[5] - s[26]) * 0.5829349756f;
        a06 = (s[6] - s[25]) * 0.6225041151f;
        a07 = (s[7] - s[24]) * 0.6748083234f;
        a08 = (s[8] - s[23]) * 0.7445362806f;
        a09 = (s[9] - s[22]) * 0.8393496275f;
        a10 = (s[10] - s[21]) * 0.9725682139f;
        a11 = (s[11] - s[20]) * 1.1694399118f;
        a12 = (s[12] - s[19]) * 1.4841645956f;
        a13 = (s[13] - s[18]) * 2.0577809811f;
        a14 = (s[14] - s[17]) * 3.4076085091f;
        a15 = (s[15] - s[16]) * 10.1900081635f;

        b00 = a00 + a15;
        b01 = a01 + a14;
        b02 = a02 + a13;
        b03 = a03 + a12;
        b04 = a04 + a11;
        b05 = a05 + a10;
        b06 = a06 + a09;
        b07 = a07 + a08;
        b08 = (a00 - a15) * 0.5024192929f;
        b09 = (a01 - a14) * 0.5224986076f;
        b10 = (a02 - a13) * 0.5669440627f;
        b11 = (a03 - a12) * 0.6468217969f;
        b12 = (a04 - a11) * 0.7881546021f;
        b13 = (a05 - a10) * 1.0606776476f;
        b14 = (a06 - a09) * 1.7224471569f;
        b15 = (a07 - a08) * 5.1011486053f;

        a00 = b00 + b07;
        a01 = b01 + b06;
        a02 = b02 + b05;
        a03 = b03 + b04;
        a04 = (b00 - b07) * 0.5097956061f;
        a05 = (b01 - b06) * 0.6013448834f;
        a06 = (b02 - b05) * 0.8999761939f;
        a07 = (b03 - b04) * 2.5629155636f;
        a08 = b08 + b15;
        a09 = b09 + b14;
        a10 = b10 + b13;
        a11 = b11 + b12;
        a12 = (b08 - b15) * 0.5097956061f;
        a13 = (b09 - b14) * 0.6013448834f;
        a14 = (b10 - b13) * 0.8999761939f;
        a15 = (b11 - b12) * 2.5629155636f;

        b00 = a00 + a03;
        b01 = a01 + a02;
        b02 = (a00 - a03) * 0.5411961079f;
        b03 = (a01 - a02) * 1.3065630198f;
        b04 = a04 + a07;
        b05 = a05 + a06;
        b06 = (a04 - a07) * 0.5411961079f;
        b07 = (a05 - a06) * 1.3065630198f;
        b08 = a08 + a11;
        b09 = a09 + a10;
        b10 = (a08 - a11) * 0.5411961079f;
        b11 = (a09 - a10) * 1.3065630198f;
        b12 = a12 + a15;
        b13 = a13 + a14;
        b14 = (a12 - a15) * 0.5411961079f;
        b15 = (a13 - a14) * 1.3065630198f;

        a00 = b00 + b01;
        a01 = (b00 - b01) * 0.7071067691f;
        a02 = b02 + b03;
        a03 = (b02 - b03) * 0.7071067691f;
        a04 = b04 + b05;
        a05 = (b04 - b05) * 0.7071067691f;
        a06 = b06 + b07;
        a07 = (b06 - b07) * 0.7071067691f;
        a08 = b08 + b09;
        a09 = (b08 - b09) * 0.7071067691f;
        a10 = b10 + b11;
        a11 = (b10 - b11) * 0.7071067691f;
        a12 = b12 + b13;
        a13 = (b12 - b13) * 0.7071067691f;
        a14 = b14 + b15;
        a15 = (b14 - b15) * 0.7071067691f;

        v[15] = a15;
        v[13] = a07 + v[15];
        v[11] = v[13] + a11;
        v[5] = v[11] + a05 + a13;
        v[9] = a03 + a11 + a15;
        v[7] = v[9] + a13;
        v[1] = a01 + a09 + a13 + a15;
        v[33] = -v[1] - a14;
        v[3] = a05 + a07 + a09 + a13 + a15;
        v[35] = -v[3] - a06 - a14;
        tmp = -(a10 + a11 + a13 + a14 + a15);
        v[37] = tmp - a05 - a06 - a07;
        v[39] = tmp - a02 - a03;
        tmp += a13 - a12;
        v[41] = tmp - a02 - a03;
        v[43] = tmp - a04 - a06 - a07;
        tmp = -(a08 + a12 + a14 + a15);
        v[47] = tmp - a00;
        v[45] = tmp - a04 - a06 - a07;

        for (int i = 0; i < 16; i++)
        {
            v[32 - i] = -v[i];
        }

        for (int i = 33; i < 48; i++)
        {
            v[96 - i] = v[i];
        }
    }
}
