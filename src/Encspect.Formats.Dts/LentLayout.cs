using Encspect.Core;

namespace Encspect.Formats.Dts;

// What the walk over a core frame's subframes stores in the buffers a decode session lends (docs/design/lossy-decoders.md, "Lending for lossy frames"), all integers. The
// frame's planes are its primary channels, then the LFE channel where the frame has one (as a plane of its own).
//
// Every plane starts with HeaderInts integers of the frame: [0] primary channels, [1] subframes, [2] nSUBS of the channel, [3] nVQSUB, [4] JOINX, [5] FILTS, [6] SUMF, [7] SUMS,
// [8] AMODE, [9] LFF, [10] HFLAG, [11] SFREQ, [12] RATE, [13] nSUBS of the channel JOINX names (0 where none), [14] 1 where the frame has an LFE plane. Then each subframe has
// SubframeInts integers: the subframe's header ([0] SSC + 1 subsubframes, [1] PSC, [2] the RANGE index or -1, [3] the LFE scale factor index or -1), then Meta integers for each of
// the 32 subbands, then Samples integers for each. A subband's meta: [0] ABITS, or Vq (-1) for a subband of high-frequency VQ samples and Joint (-2) for one that joint intensity
// coding copies, [1] PMODE, [2] the 12-bit index of the ADPCM coefficient vector, [3] TMODE, [4] and [5] the scale factors before and after a transient (the square root
// table's values, or 0 where the index is invalid), [6] the joint scale factor as the bits of a float, [7] the ADJ index (0 to 3). A subband's samples are the quantization
// indexes of the subsubframes in order (8 for each), or the 10-bit index of the VQ vector in the first of them. The LFE plane holds in each subframe's Samples region the
// decimated samples as signed bytes, the first at SubframeHeaderInts + 32 * Meta.
internal static class LentLayout
{
    public const int HeaderInts = 16;
    public const int Subbands = 32;
    public const int Meta = 8;
    public const int Samples = 32;
    public const int SubframeHeaderInts = 4;
    public const int SubframeInts = SubframeHeaderInts + (Subbands * Meta) + (Subbands * Samples);
    public const int Vq = -1;
    public const int Joint = -2;

    public static int Stride(int subframes) => HeaderInts + (subframes * SubframeInts);

    public static int Subframe(int subframe) => HeaderInts + (subframe * SubframeInts);

    public static int MetaAt(int subframe, int subband) => Subframe(subframe) + SubframeHeaderInts + (subband * Meta);

    public static int SamplesAt(int subframe, int subband) => Subframe(subframe) + SubframeHeaderInts + (Subbands * Meta) + (subband * Samples);

    public static int LfeAt(int subframe) => Subframe(subframe) + SubframeHeaderInts + (Subbands * Meta);
}
