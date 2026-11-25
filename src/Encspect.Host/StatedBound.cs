using Encspect.Core;

namespace Encspect.Host;

// The stream's own series of the intensity stereo bound its parser states, set beside the measured inter-channel coherence as figures
// (docs/design/spectral-analysis.md, Step 8, "Beside the stated bound"): the lower median of the values and how many there are. No verdict follows from it.
internal static class StatedBound
{
    public static StreamMeasurements? Attach(StreamInfo stream, StreamMeasurements? record)
    {
        if (record?.Spectrum?.Coherence is not { IsSearched: true } coherence)
        {
            return record;
        }

        Series? series = stream.Series.FirstOrDefault(s => s.Quantity == SeriesQuantities.IntensityStereoBound && s.Values.Length > 0);
        if (series is null)
        {
            return record;
        }

        double[] values = series.Values.ToArray();
        Array.Sort(values);
        double median = values[(values.Length - 1) / 2];
        int hertz = (int)Math.Floor(median + 0.5);
        var stated = new StatedIntensityBound
        {
            MedianFrequency = hertz,
            Units = values.Length,
            Difference = coherence.IsFound ? coherence.FromFrequency - hertz : null,
        };
        return record with { Spectrum = record.Spectrum with { Coherence = coherence with { Stated = stated } } };
    }
}
