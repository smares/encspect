namespace Encspect.Core;

// "Compression Ratio" of the lossless formats (docs/design/coding-graphs.md, "Compression ratio and the bitrate's concept"): the coded size of a unit as a share
// of the size of its audio as PCM, in percent. It is the unit's bitrate over the stream's PCM bitrate, the sample rate times the channels times the bits per
// sample, so it is derived at the end from the bitrate series, with the same x values, and needs 8 bytes a unit for its values.
public static class CompressionRatio
{
    public const string Name = "Compression Ratio";

    public const string StatisticDescription = "The coded size of the audio as a share of the size of the same audio as PCM, in percent: the bitrate over the PCM bitrate, which is the sample rate times the channels times the bits per sample of the stream. Under 100% the coding is smaller than PCM.";
    public const string LowestDescription = "The lowest compression ratio of any unit.";
    public const string OverallDescription = "The compression ratio of the stream: its average bitrate over its PCM bitrate.";
    public const string HighestDescription = "The highest compression ratio of any unit.";
    public const string SeriesDescription = "The coded size of each unit as a share of the size of its audio as PCM, in percent: the unit's bitrate over the stream's PCM bitrate (sample rate times channels times bits per sample), so it draws the bitrate on another scale.";
    public const string OverTimeDescription = "The compression ratio of each second of audio: the bitrate of the units that start in it over the PCM bitrate of the stream.";

    // Bits a second of the audio as PCM; 0 where the stream states no rate, channel count or sample size.
    public static long PcmBitsPerSecond(long sampleRate, int channels, int? bitsPerSample) =>
        sampleRate > 0 && channels > 0 && bitsPerSample is > 0 ? sampleRate * channels * bitsPerSample.Value : 0;

    // A percentage with one decimal, half to even.
    public static double Percent(double kbps, long pcm) => Math.Round(kbps * 1000 * 100 / pcm, 1, MidpointRounding.ToEven);

    // The statistics node with the lowest, overall and highest ratio; make creates a node over the stream's bytes with its description. Null where there is nothing to
    // show: no PCM size, no average bitrate or no units.
    public static Node? AddTo(Node statistics, Func<string, NodeValue?, string, Node> make, long pcm, double? averageKbps, double minimumKbps, double maximumKbps)
    {
        if (pcm <= 0 || averageKbps is not { } average || maximumKbps <= 0)
        {
            return null;
        }

        Node node = statistics.Add(make(Name, null, StatisticDescription));
        node.Add(make("Lowest", NodeValue.Decimal(Percent(minimumKbps, pcm), 1, "%"), LowestDescription));
        node.Add(make("Overall", NodeValue.Decimal(Percent(average, pcm), 1, "%"), OverallDescription));
        node.Add(make("Highest", NodeValue.Decimal(Percent(maximumKbps, pcm), 1, "%"), HighestDescription));
        return node;
    }

    // Adds the ratio of each unit and of each second to the series, from "Bitrate" and "Bitrate Over Time" (the x values of the first shared, the runs too), anchored to
    // the statistics node. The concept is the bitrate's.
    public static void AddTo(List<Series> series, long pcm, Node? anchor, CancellationToken cancellation = default)
    {
        if (pcm <= 0 || anchor is null)
        {
            return;
        }

        Series? perUnit = series.Find(s => s is { Name: "Bitrate", Axis: SeriesAxis.FrameOffset });
        Series? perSecond = series.Find(s => s is { Name: "Bitrate Over Time", Axis: SeriesAxis.Seconds });
        if (perUnit is not null)
        {
            series.Add(Of(perUnit, Name, SeriesDescription, null, pcm, cancellation));
        }

        if (perSecond is not null)
        {
            series.Add(Of(perSecond, Name + " Over Time", OverTimeDescription, anchor, pcm, cancellation));
        }
    }

    private static Series Of(Series bitrate, string name, string description, Node? anchor, long pcm, CancellationToken cancellation)
    {
        ReadOnlySpan<double> kbps = bitrate.Values.Span;
        var ratios = new double[kbps.Length];
        for (int i = 0; i < ratios.Length; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            ratios[i] = Percent(kbps[i], pcm);
        }

        return new Series(name, "%", bitrate.Axis, bitrate.X, ratios, anchor)
        {
            Description = description,
            Concept = GraphConcept.BitDistribution,
            Quantity = Name,
            Runs = bitrate.Runs,
        };
    }
}
