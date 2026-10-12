using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// Layer I audio data (ISO/IEC 11172-3 2.4.1.5 and 2.4.2.5): a 4-bit allocation per subband and
// channel, one per subband above the joint stereo bound, a 6-bit scalefactor per allocated subband
// and channel, then twelve samples of allocation + 1 bits per allocated subband, one set above the
// bound. Allocation 15 is forbidden; libmad rejects the frame, mpg123 reads 16-bit samples, as
// here. Whatever follows the samples is ancillary data.
internal sealed class Layer1AudioData
{
    public const int Subbands = 32;

    public const int ForbiddenAllocation = 15;

    private Layer1AudioData(int channels, int bound)
    {
        Channels = channels;
        Bound = bound;
        Allocation = new int[channels, Subbands];
        Scalefactor = new int[channels, Subbands];
        AllocationBit = new int[channels, Subbands];
        ScalefactorBit = new int[channels, Subbands];
    }

    public int Channels { get; }

    public int Bound { get; }

    // [channel, subband]: allocation, and scalefactor index (-1 without allocation).
    public int[,] Allocation { get; }

    public int[,] Scalefactor { get; }

    // Where each field starts, in bits from the start of the frame.
    public int[,] AllocationBit { get; }

    public int[,] ScalefactorBit { get; }

    public int AllocationStart { get; private set; }

    public int ScalefactorStart { get; private set; }

    public int SampleStart { get; private set; }

    public int End { get; private set; }

    // data starts at the frame header and must reach at least to the end of the scalefactors.
    public static Layer1AudioData Read(byte[] data, MpegHeader header)
    {
        int channels = header.IsMono ? 1 : 2;
        int bound = header.ChannelModeBits == 1 ? (header.ModeExtension + 1) * 4 : Subbands;
        var audio = new Layer1AudioData(channels, bound);
        var bits = new BitReader(data);
        bits.Read(16);
        bits.Read(16);
        if (header.IsProtected)
        {
            bits.Read(16);
        }

        audio.AllocationStart = bits.Position;
        for (int sb = 0; sb < Subbands; sb++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (sb >= bound && ch > 0)
                {
                    audio.Allocation[ch, sb] = audio.Allocation[0, sb];
                    audio.AllocationBit[ch, sb] = audio.AllocationBit[0, sb];
                    continue;
                }

                audio.AllocationBit[ch, sb] = bits.Position;
                audio.Allocation[ch, sb] = bits.Read(4);
            }
        }

        audio.ScalefactorStart = bits.Position;
        for (int sb = 0; sb < Subbands; sb++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                audio.ScalefactorBit[ch, sb] = bits.Position;
                audio.Scalefactor[ch, sb] = audio.Allocation[ch, sb] != 0 ? bits.Read(6) : -1;
            }
        }

        audio.SampleStart = bits.Position;
        int granule = 0;
        for (int sb = 0; sb < Subbands; sb++)
        {
            for (int ch = 0; ch < (sb < bound ? channels : 1); ch++)
            {
                int allocation = audio.Allocation[ch, sb];
                granule += allocation == 0 ? 0 : allocation + 1;
            }
        }

        audio.End = audio.SampleStart + (12 * granule);
        return audio;
    }

    public IEnumerable<(int Channel, int Subband)> Coded()
    {
        for (int sb = 0; sb < Subbands; sb++)
        {
            for (int ch = 0; ch < (sb < Bound ? Channels : 1); ch++)
            {
                yield return (ch, sb);
            }
        }
    }

    // A truncated or overflowing frame cuts the samples off at its last byte.
    public IEnumerable<Node> ToNodes(long frameOffset, long frameLength)
    {
        int end = (int)Math.Min(End, frameLength * 8);
        Node allocation = Layer2AudioData.Span("Bit Allocation", frameOffset, AllocationStart, ScalefactorStart, kind: NodeKind.SideInfo,
            description: Explain.Layer1Allocation, topic: Topics.SubbandCoding);
        Node scalefactors = Layer2AudioData.Span("Scalefactors", frameOffset, ScalefactorStart, SampleStart, kind: NodeKind.SideInfo,
            description: Explain.Layer1Scalefactors, topic: Topics.SubbandCoding);
        for (int sb = 0; sb < Subbands; sb++)
        {
            int coded = sb < Bound ? Channels : 1;
            Node subband = allocation.Add(Layer2AudioData.Span($"Subband {sb}", frameOffset, AllocationBit[0, sb], AllocationBit[coded - 1, sb] + 4, description: Explain.Subband));
            for (int ch = 0; ch < coded; ch++)
            {
                int value = Allocation[ch, sb];
                NodeValue text = value switch
                {
                    0 => "None",
                    ForbiddenAllocation => "Forbidden",
                    _ => NodeValue.Integer(value + 1, "bits per sample"),
                };
                subband.Add(AllocationNode(frameOffset, ch, sb, text));
            }

            int[] allocated = Enumerable.Range(0, Channels).Where(ch => Allocation[ch, sb] != 0).ToArray();
            if (allocated.Length == 0)
            {
                continue;
            }

            Node scalefactor = scalefactors.Add(Layer2AudioData.Span($"Subband {sb}", frameOffset, ScalefactorBit[allocated[0], sb], ScalefactorBit[allocated[^1], sb] + 6,
                description: Explain.Subband));
            foreach (int ch in allocated)
            {
                scalefactor.Add(ScalefactorNode(frameOffset, ch, sb, NodeValue.Integer(Scalefactor[ch, sb])));
            }
        }

        yield return allocation;
        if (scalefactors.Children.Count > 0)
        {
            yield return scalefactors;
        }

        if (end > SampleStart)
        {
            yield return Layer2AudioData.Span("Samples", frameOffset, SampleStart, end, NodeValue.Integer(end - SampleStart, "bits"), description: Explain.Layer1Samples);
        }
    }

    // The node of one channel's allocation in a subband; above the intensity stereo bound one
    // allocation serves both channels.
    public Node AllocationNode(long frameOffset, int ch, int sb, NodeValue? value = null) =>
        sb >= Bound && Channels > 1
            ? Layer2AudioData.Span("Channels 0 and 1", frameOffset, AllocationBit[ch, sb], AllocationBit[ch, sb] + 4, value, description: Explain.SharedAllocation, topic: Topics.IntensityStereo)
            : Layer2AudioData.Span($"Channel {ch}", frameOffset, AllocationBit[ch, sb], AllocationBit[ch, sb] + 4, value, description: Explain.Layer1ChannelAllocation);

    public Node ScalefactorNode(long frameOffset, int ch, int sb, NodeValue? value = null) =>
        Layer2AudioData.Span($"Channel {ch}", frameOffset, ScalefactorBit[ch, sb], ScalefactorBit[ch, sb] + 6, value, description: Explain.Layer1Scalefactor);
}
