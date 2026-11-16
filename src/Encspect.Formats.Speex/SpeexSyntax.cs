// The submode tables of this file (modes.c, modes_wb.c: the bits of each submode's parameters) and the order the parameters are read in (nb_decode of nb_celp.c,
// sb_decode of sb_celp.c, lsp_unquant_* of quant_lsp.c, pitch_unquant_3tap of ltp.c, split_cb_shape_sign_unquant of cb_search.c) follow libspeex 1.2.1,
// Copyright (c) 2002-2008 Jean-Marc Valin and Xiph.org Foundation and the other holders named in its COPYING, under the BSD 3-clause license.
// The license text is in THIRD-PARTY-NOTICES.md.

namespace Encspect.Formats.Speex;

// How one frame's coded parameters lie in the integers the session lends the decoder: FrameInts of them for each frame, the layers one after the other. The
// layout is the format's own; the parser fills it (SpeexSyntax) and the decoder reads it, so the decoder reads no coded bit.
internal static class SpeexLayout
{
    public const int FrameInts = 200;

    // The stereo request that came before the frame's narrowband layer, the last of them: 1 for a request, then the sign bit at 1, the 5-bit exponent from 2 and the 2-bit
    // energy ratio index from 7.
    public const int Stereo = 0;

    public const int NarrowbandSubmode = 1;

    // The submode of the wideband and ultra-wideband layers, FrameRecord.Absent where the frame has none.
    public const int WidebandSubmode = 2;

    public const int UltraWidebandSubmode = 3;

    public const int Narrowband = 4;

    public const int Wideband = 100;

    public const int UltraWideband = 148;

    // The narrowband layer: the LSP indexes (5, of which a low-rate submode uses 3), the open-loop pitch less nothing (7 bits), the pitch coefficient (4 bits), the global
    // gain (5 bits) and the 4 bits submode 1 carries; then four subframes of NarrowbandSubframe integers.
    public const int NarrowbandLsp = 0;

    public const int OpenLoopPitch = 5;

    public const int PitchCoefficient = 6;

    public const int OpenLoopGain = 7;

    public const int VocoderExtra = 8;

    public const int NarrowbandSubframes = 9;

    public const int NarrowbandSubframe = 19;

    // In a narrowband subframe: the pitch, the pitch gain index, the gain correction, then the shape indexes of the first and of the second codebook (8 of each).
    public const int SubframePitch = 0;

    public const int SubframePitchGain = 1;

    public const int SubframeGain = 2;

    public const int SubframeShapes = 3;

    public const int SubframeSecondShapes = 11;

    // The wideband and ultra-wideband layers: the two LSP indexes, then four subframes of HighSubframe integers: the gain (4 bits, or 5 in submode 1), then the shapes of the first
    // codebook, as the index with the sign bit at 8, and of the second (5 of each).
    public const int HighLsp = 0;

    public const int HighSubframes = 2;

    public const int HighSubframe = 11;

    public const int HighShapes = 1;

    public const int HighSecondShapes = 6;

    public const int SignBit = 8;
}

internal static class SpeexSyntax
{
    // A narrowband submode's parameters (the SpeexSubmode of modes.c): -1 in LbrPitch for a normal mode, 0 for a global pitch with no variation; HaveSubframeGain the bits of
    // the gain correction; LspIndexes 3 for the low-rate quantizer, 5 for the full one; PitchBits and GainBits of the pitch predictor, 0 and 0 for a forced pitch;
    // Subvectors and ShapeBits of the innovation, 0 for noise.
    private readonly record struct Submode(int LbrPitch, bool IsForced, int HaveSubframeGain, bool IsDouble, int LspIndexes, int PitchBits, int GainBits, int Subvectors, int ShapeBits);

    private static readonly Submode[] Narrowbands =
    [
        default,
        new(0, true, 0, false, 3, 0, 0, 0, 0),
        new(0, false, 0, false, 3, 0, 5, 4, 4),
        new(-1, false, 1, false, 3, 7, 5, 4, 5),
        new(-1, false, 1, false, 3, 7, 5, 5, 7),
        new(-1, false, 3, false, 5, 7, 7, 8, 6),
        new(-1, false, 3, false, 5, 7, 7, 8, 8),
        new(-1, false, 3, true, 5, 7, 7, 8, 6),
        new(0, true, 0, false, 3, 0, 0, 2, 5),
    ];

    // A wideband layer's submodes: the subvectors and bits of the shape of each, and whether it has a sign bit and a second codebook (wb_submode1 to 4 of modes_wb.c).
    private readonly record struct HighSubmode(int Subvectors, int ShapeBits, bool HasSign, bool IsDouble);

    private static readonly HighSubmode[] Highs =
    [
        default,
        new(0, 0, false, false),
        new(4, 5, false, false),
        new(5, 7, true, false),
        new(5, 7, true, true),
    ];

    public const int SubframesPerFrame = 4;

    // The narrowband layer's parameters after the 5 bits of its header (nb_decode), into d, the layout's Narrowband part. Reads exactly NarrowbandBits[submode] - 5 bits.
    public static void ReadNarrowband(ref SpeexBits bits, int submode, scoped Span<int> d)
    {
        if (submode == 0)
        {
            return;
        }

        Submode s = Narrowbands[submode];
        for (int i = 0; i < s.LspIndexes; i++)
        {
            d[SpeexLayout.NarrowbandLsp + i] = bits.Read(6);
        }

        if (s.LbrPitch != -1)
        {
            d[SpeexLayout.OpenLoopPitch] = bits.Read(7);
        }

        if (s.IsForced)
        {
            d[SpeexLayout.PitchCoefficient] = bits.Read(4);
        }

        d[SpeexLayout.OpenLoopGain] = bits.Read(5);
        if (submode == 1)
        {
            d[SpeexLayout.VocoderExtra] = bits.Read(4);
        }

        for (int sub = 0; sub < SubframesPerFrame; sub++)
        {
            Span<int> f = d.Slice(SpeexLayout.NarrowbandSubframes + (sub * SpeexLayout.NarrowbandSubframe), SpeexLayout.NarrowbandSubframe);
            if (s.GainBits > 0 || s.PitchBits > 0)
            {
                f[SpeexLayout.SubframePitch] = bits.Read(s.PitchBits);
                f[SpeexLayout.SubframePitchGain] = bits.Read(s.GainBits);
            }

            if (s.HaveSubframeGain != 0)
            {
                f[SpeexLayout.SubframeGain] = bits.Read(s.HaveSubframeGain);
            }

            for (int v = 0; v < s.Subvectors; v++)
            {
                f[SpeexLayout.SubframeShapes + v] = bits.Read(s.ShapeBits);
            }

            if (s.IsDouble)
            {
                for (int v = 0; v < s.Subvectors; v++)
                {
                    f[SpeexLayout.SubframeSecondShapes + v] = bits.Read(s.ShapeBits);
                }
            }
        }
    }

    // A wideband or ultra-wideband layer's parameters after the 4 bits of its header (sb_decode), into d (the layout's Wideband or UltraWideband part). The ultra-wideband layer has
    // submode 1 only. Reads exactly the layer's bits less 4.
    public static void ReadHigh(ref SpeexBits bits, int submode, scoped Span<int> d)
    {
        if (submode == 0)
        {
            return;
        }

        HighSubmode s = Highs[submode];
        d[SpeexLayout.HighLsp] = bits.Read(6);
        d[SpeexLayout.HighLsp + 1] = bits.Read(6);
        for (int sub = 0; sub < SubframesPerFrame; sub++)
        {
            Span<int> f = d.Slice(SpeexLayout.HighSubframes + (sub * SpeexLayout.HighSubframe), SpeexLayout.HighSubframe);
            f[0] = bits.Read(s.Subvectors == 0 ? 5 : 4);
            for (int v = 0; v < s.Subvectors; v++)
            {
                f[SpeexLayout.HighShapes + v] = (s.HasSign ? bits.Read(1) << SpeexLayout.SignBit : 0) | bits.Read(s.ShapeBits);
            }

            if (s.IsDouble)
            {
                for (int v = 0; v < s.Subvectors; v++)
                {
                    f[SpeexLayout.HighSecondShapes + v] = (s.HasSign ? bits.Read(1) << SpeexLayout.SignBit : 0) | bits.Read(s.ShapeBits);
                }
            }
        }
    }
}
