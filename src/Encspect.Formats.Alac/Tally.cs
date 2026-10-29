using Encspect.Core;

namespace Encspect.Formats.Alac;

// The coding statistics of a stream's frames, counted while they are read, so nothing is kept per frame: elements by tag, how audio elements are
// coded, the prediction parameters of every compressed channel, the bytes shifted out of samples, and the runs and long codes of the residuals.
// Count is called from the walk, once for each element it reads, so the statistics and the coding series equal each other whatever the records keep.
internal sealed class CodingTally
{
    public long[] Elements { get; } = new long[8];

    // Audio elements (single channel, LFE and channel pair) by how they are coded.
    public long Compressed { get; private set; }

    public long Uncompressed { get; private set; }

    // Channel pair elements, those that mix their channels (mixResolution not 0) and those that do not, uncompressed pairs included.
    public long Pairs { get; private set; }

    public long MixedPairs { get; private set; }

    // The channels of compressed elements, by the fields of their parameters.
    public long Channels { get; private set; }

    public long[] PredictionTypes { get; } = new long[16];

    public long[] PredictorOrders { get; } = new long[32];

    public long[] PredictorShifts { get; } = new long[16];

    public long[] WeightFactors { get; } = new long[8];

    // Compressed audio elements by the bytes shifted out of their samples.
    public long[] BytesShifted { get; } = new long[3];

    public long ZeroRuns { get; private set; }

    public long LongCodes { get; private set; }

    public long FillBytes { get; private set; }

    public long DataStreamBytes { get; private set; }

    // The elements of the frame being read, as the codes of the coding series, taken and cleared by the state after each frame: one for each
    // audio element, and one for each channel pair.
    public List<byte> ElementCodes { get; } = [];

    public List<byte> PairCodes { get; } = [];

    public void Count(in ElementRecord element)
    {
        Elements[element.Tag & 7]++;
        switch (element.Tag)
        {
            case ElementTags.DataStream:
                DataStreamBytes += element.Samples;
                return;
            case ElementTags.Fill:
                FillBytes += element.Samples;
                return;
        }

        if (!ElementTags.IsAudio(element.Tag) || !element.IsHeaderRead)
        {
            return;
        }

        bool isPair = element.Tag == ElementTags.ChannelPair;
        ElementCodes.Add(element.IsUncompressed ? CodingCategories.CodeUncompressed : CodingCategories.CodeCompressed);
        if (element.IsUncompressed)
        {
            Uncompressed++;
        }
        else
        {
            Compressed++;
            BytesShifted[Math.Min((int)element.BytesShifted, 2)]++;
            if (element.ChannelsDone > 0)
            {
                Channel(in element.First);
            }

            if (element.ChannelsDone > 1)
            {
                Channel(in element.Second);
            }
        }

        if (isPair)
        {
            Pairs++;
            bool isMixed = !element.IsUncompressed && element.MixResolution != 0;
            MixedPairs += isMixed ? 1 : 0;
            PairCodes.Add(isMixed ? CodingCategories.CodeMidSide : CodingCategories.CodeLeftRight);
        }
    }

    private void Channel(in ChannelRecord channel)
    {
        Channels++;
        PredictionTypes[channel.PredictionType & 15]++;
        PredictorOrders[channel.Order & 31]++;
        PredictorShifts[channel.PredictorShift & 15]++;
        WeightFactors[channel.WeightFactor & 7]++;
        ZeroRuns += channel.ZeroRuns;
        LongCodes += channel.LongCodes;
    }
}
