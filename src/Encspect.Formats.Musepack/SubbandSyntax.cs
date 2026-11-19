using System.Runtime.CompilerServices;

namespace Encspect.Formats.Musepack;

// What the entropy pass found wrong in a frame. A frame with any of them is damaged: libmpcdec stops decoding at it (mpc_demux_decode_inner fails where the bits a frame
// takes differ from the bits it states) and the analyzer counts its samples as lost.
[Flags]
internal enum WalkProblems
{
    None = 0,

    // The subband resolutions run past the frame's data.
    HeaderOverrun = 1,

    // A subband resolution libmpcdec does not decode: outside -1 to 17.
    BadResolution = 2,

    // The scalefactors and samples run past the frame's data (or past the bytes the walker holds of it).
    DataOverrun = 4,

    // The scalefactors and samples end before the frame's data do.
    DataShort = 8,

    // A code the tables do not hold, a count that names more subbands than there are, or a scalefactor selection libmpcdec leaves unread: the frame cannot be read further.
    Syntax = 16,
}

// The facts of one frame the walk reports, which the parser's records and figures use: the highest used subband (the number of subbands from the lowest that carry data), the
// mid/side flags of the subbands that carry data, how many subbands carry data in either channel, and how many subband channels are noise-substituted.
internal struct WalkFacts
{
    public int UsedBands;

    public uint MidSide;

    public int Flagged;

    public int Noise;

    public WalkProblems Problems;

    // The bits the frame's data took, from the first bit the walk was given.
    public int Bits;

    public readonly bool IsDamaged => Problems != WalkProblems.None;
}

// The state one stream's frames share while they are walked, as libmpcdec's decoder holds it between frames: the resolutions, scalefactor selections and scalefactors the
// next frame's differential coding refers to, and the values of the frame just walked, which the parser lends to the decoder. One instance serves one stream.
internal sealed class SubbandState
{
    public const int Bands = 32;

    public const int SamplesPerBand = 36;

    // The resolution of each subband and channel; the scalefactor indexes (3 per subband, as coded, before the decoder takes their low 8 bits); the scalefactor selection of
    // each; whether the next scalefactors are differences (SV4 to SV6 and SV8); the mid/side flag of each subband.
    public int[][] Resolution { get; } = [new int[Bands], new int[Bands]];

    public int[][] Scalefactor { get; } = [new int[Bands * 3], new int[Bands * 3]];

    public int[][] Selection { get; } = [new int[Bands], new int[Bands]];

    public bool[][] Differential { get; } = [new bool[Bands], new bool[Bands]];

    public bool[] MidSideFlag { get; } = new bool[Bands];

    // The number of subbands the last SV8 frame used, which the next frame's count is coded against.
    public int LastUsedBands { get; set; }

    // The quantized values of the frame just walked, subband by subband (36 each), for the left and the right channel. Noise-substituted subbands (resolution -1) hold
    // nothing: the decoder makes their values (docs/design/lossy-decoders.md).
    public int[][] Quantized { get; } = [new int[Bands * SamplesPerBand], new int[Bands * SamplesPerBand]];

    // The scalefactor value libmpcdec stores where a differential scalefactor runs above 1,024.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Clamp(int scalefactor) => scalefactor > 1024 ? 0x8080 : scalefactor;
}

// What the walks lend the decoder, per channel (docs/design/lossy-decoders.md, "Lending for lossy frames"): the frame's quantized values, subband by subband; the
// resolution of each subband; the scalefactor indexes, three to a subband, in their low 8 bits; and the mid/side flag of each subband (in the left channel's plane only).
internal static class LentLayout
{
    public const int Values = SubbandState.Bands * SubbandState.SamplesPerBand;

    public const int ResolutionOffset = Values;

    public const int ScalefactorOffset = ResolutionOffset + SubbandState.Bands;

    public const int MidSideOffset = ScalefactorOffset + (SubbandState.Bands * 3);

    public const int PerChannel = MidSideOffset + SubbandState.Bands;

    // Copies the walked frame into the plane of one channel.
    public static void Store(SubbandState state, int channel, Span<int> plane)
    {
        state.Quantized[channel].CopyTo(plane);
        for (int band = 0; band < SubbandState.Bands; band++)
        {
            plane[ResolutionOffset + band] = state.Resolution[channel][band];
            for (int part = 0; part < 3; part++)
            {
                plane[ScalefactorOffset + (band * 3) + part] = state.Scalefactor[channel][(band * 3) + part] & 0xFF;
            }

            plane[MidSideOffset + band] = channel == 0 && state.MidSideFlag[band] ? 1 : 0;
        }
    }
}
