using System.Runtime.CompilerServices;

namespace Encspect.Formats.Mpeg;

// How the walk of a frame lays what it reads into the buffers a session lends (IDecodeSession.Lend): one plane per channel of PerChannel integers. A Layer III plane
// holds, for each granule, the 576 quantized values in the order the data comes in (short blocks band by band, each band window by window), then the 40 scalefactors as
// coded and the 40 intensity stereo positions of its channel; the plane's last entry holds the frame's flags. A Layer I or II plane holds the sample codes
// (Layer12Scale.ReadSamples). The decoder reads the side information again from the frame's bytes, which is a few dozen bytes a frame.
internal static class LentLayout
{
    public const int Values = 576;
    public const int Scalefactors = 40;
    public const int GranuleStride = Values + Scalefactors + Scalefactors;
    public const int ScalefactorOffset = Values;
    public const int PositionOffset = Values + Scalefactors;

    // Two granules of a Layer III channel, or the 1,152 codes of Layer II, and one entry of flags.
    public const int PerChannel = (2 * GranuleStride) + 4;
    public const int FlagsIndex = 2 * GranuleStride;

    // The frame's main data are not available: they begin before the stream or in a frame that is missing, so the decoder withholds its output.
    public const int MainDataUnavailable = 1;
}

// Walks the granules of one Layer III frame. One instance serves one stream (it holds scratch); nothing of it outlives a call.
internal sealed class Layer3Walker
{
    private readonly GranuleSyntax[] _granules = new GranuleSyntax[4];
    private readonly byte[][] _scalefactors = [new byte[LentLayout.Scalefactors], new byte[LentLayout.Scalefactors]];
    private readonly byte[][] _positions = [new byte[LentLayout.Scalefactors], new byte[LentLayout.Scalefactors]];
    private readonly int[] _scratch = new int[2 * LentLayout.PerChannel];

    // frame starts at the header; mainData holds the frame's main data from main_data_begin bytes before its own data (the reservoir's tail, then the frame's bytes after
    // the side information). planes is the session's lent buffer (Narrow, with its stride) or empty where nothing is decoded: the walk then fills scratch and keeps only
    // its facts. claimedBytes is how much of mainData the placement covers (the rest, bytes of the frame after them, is what a granule whose values overrun reads on into, as in
    // minimp3); -1 where all of it. Returns what the walk found; the Unreadable and PartsExceedData problems leave the planes as far as they got.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public WalkFacts Walk(MpegHeader header, ReadOnlySpan<byte> frame, ReadOnlySpan<byte> mainData, Span<int> planes, int stride, int claimedBytes = -1)
    {
        var facts = new WalkFacts { IntensityLine = -1 };
        int mainDataBegin = Layer3Syntax.ReadSideInfo(frame, header, _granules, out int count, out int partsTotal);
        if (mainDataBegin < 0)
        {
            facts.Problems |= WalkProblems.Unreadable;
            return facts;
        }

        if (planes.IsEmpty)
        {
            planes = _scratch;
            stride = LentLayout.PerChannel;
        }

        int channels = header.IsMono ? 1 : 2;
        bool isIntensity = header.ChannelModeBits == 1 && (header.ModeExtension & 1) != 0;
        if (partsTotal > (claimedBytes >= 0 ? claimedBytes : mainData.Length) * 8)
        {
            facts.Problems |= WalkProblems.PartsExceedData;
        }

        var bits = new BitSource(mainData);
        for (int index = 0; index < count; index++)
        {
            int granule = index / channels;
            int channel = index % channels;
            ref readonly GranuleSyntax gr = ref _granules[index];
            Span<int> plane = planes.Slice(channel * stride, LentLayout.PerChannel);
            Span<int> block = plane.Slice(granule * LentLayout.GranuleStride, LentLayout.GranuleStride);
            Span<int> values = block[..LentLayout.Values];
            values.Clear();
            int limit = bits.Position + gr.Part23Length;
            Layer3Syntax.ReadScalefactors(ref bits, header, gr, channel, isIntensity, _scalefactors[channel], _positions[channel]);

            // Scalefactors that already exceed part2_3_length are the side information's fault (mpeg.scalefactors-exceed-granule), not the Huffman data's.
            bool scalefactorsFit = bits.Position <= limit;
            for (int i = 0; i < LentLayout.Scalefactors; i++)
            {
                block[LentLayout.ScalefactorOffset + i] = _scalefactors[channel][i];
                block[LentLayout.PositionOffset + i] = _positions[channel][i];
            }

            int coded = Layer3Syntax.ReadSpectrum(ref bits, gr, values, limit, out bool isOverrun);
            if (isOverrun && scalefactorsFit)
            {
                facts.Problems |= WalkProblems.HuffmanOverrun;
                facts.Overruns++;
            }

            facts.CodedLines = Math.Max(facts.CodedLines, Top(gr, coded));
            if (isIntensity && channel == 1)
            {
                int line = IntensityStart(header, _granules[index - 1], gr, values, _positions[1]);
                if (line >= 0)
                {
                    facts.IntensityLine = facts.IntensityLine < 0 ? line : Math.Min(facts.IntensityLine, line);
                }
            }
        }

        plane0Flags(planes, stride, channels, 0);
        return facts;

        static void plane0Flags(Span<int> planes, int stride, int channels, int flags)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                planes[(ch * stride) + LentLayout.FlagsIndex] = flags;
            }
        }
    }

    // The lowest line, in the 576-line scale, of the bands from which a granule's right channel codes intensity positions, or -1 where it has none: the bands above the
    // last one in which the right channel has a nonzero pair, whose position is legal (ISO/IEC 11172-3 2.4.3.4.9.3), found as the decoder finds them (minimp3's
    // L3_stereo_top_band and L3_intensity_stereo). left supplies the band layout, as it does there; positions are the right channel's as coded and are not changed.
    internal static int IntensityStart(MpegHeader header, in GranuleSyntax left, in GranuleSyntax right, ReadOnlySpan<int> values, ReadOnlySpan<byte> positions)
    {
        ReadOnlySpan<byte> widths = Layer3Syntax.BandWidths(left);
        int bandCount = left.LongBandCount + left.ShortBandCount;
        int maxBlocks = left.ShortBandCount != 0 ? 3 : 1;
        Span<int> maxBand = stackalloc int[3];
        maxBand[0] = maxBand[1] = maxBand[2] = -1;
        int position = 0;
        for (int i = 0; i < bandCount && position + widths[i] <= values.Length; i++)
        {
            for (int k = 0; k + 1 < widths[i]; k += 2)
            {
                if (values[position + k] != 0 || values[position + k + 1] != 0)
                {
                    maxBand[i % 3] = i;
                    break;
                }
            }

            position += widths[i];
        }

        if (left.LongBandCount != 0)
        {
            maxBand[0] = maxBand[1] = maxBand[2] = Math.Max(Math.Max(maxBand[0], maxBand[1]), maxBand[2]);
        }

        Span<byte> complete = stackalloc byte[LentLayout.Scalefactors];
        positions[..LentLayout.Scalefactors].CopyTo(complete);
        for (int i = 0; i < maxBlocks; i++)
        {
            int top = bandCount - maxBlocks + i;
            int previous = top - maxBlocks;
            complete[top] = maxBand[i] >= previous ? (byte)(header.IsMpeg1 ? 3 : 0) : complete[previous];
        }

        uint maxPosition = header.IsMpeg1 ? 7u : 64u;
        int offset = 0;
        for (int i = 0; i < LentLayout.Scalefactors && widths[i] != 0; i++)
        {
            int groupStart = i >= left.LongBandCount ? offset - ((i - left.LongBandCount) % 3 * widths[i]) : offset;
            if (i > maxBand[i % 3] && complete[i] < maxPosition)
            {
                return groupStart;
            }

            offset += widths[i];
        }

        return -1;
    }

    // The upper edge, in the 576-line scale of a granule, of the first coded lines in the order the data comes in: where a short block's windows each have 192 lines, the
    // band of a line sets its frequency.
    internal static int Top(in GranuleSyntax gr, int coded)
    {
        if (coded <= 0)
        {
            return 0;
        }

        if (gr.Bands == BandKind.Long)
        {
            return Math.Min(coded, LentLayout.Values);
        }

        ReadOnlySpan<byte> widths = Layer3Syntax.BandWidths(gr);
        int longLines = 0;
        for (int i = 0; i < gr.LongBandCount; i++)
        {
            longLines += widths[i];
        }

        if (coded <= longLines)
        {
            return coded;
        }

        int top = longLines;
        int windowStart = longLines / 3;
        int entry = longLines;
        for (int band = gr.LongBandCount; band + 2 < widths.Length && widths[band] != 0; band += 3)
        {
            int width = widths[band];
            for (int window = 0; window < 3; window++)
            {
                int start = entry + (window * width);
                if (coded > start)
                {
                    top = Math.Max(top, 3 * (windowStart + Math.Min(width, coded - start)));
                }
            }

            entry += 3 * width;
            windowStart += width;
        }

        return Math.Min(top, LentLayout.Values);
    }
}
