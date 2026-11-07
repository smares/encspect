namespace Encspect.Core;

// "Container Samples" and "Container Duration": the length of a stream as its container (or the header of its file) states it, beside the stream's
// own count of samples. A format that has a statement of its length adds the nodes to its statistics with its own descriptions, which cite where the
// statement lies; the names, units and rounding are the same everywhere (the ASF route of Windows Media Audio was the first).
public static class ContainerLength
{
    // The node for a length of samples per channel at a sampling rate (seconds to three decimals); nothing for a rate of 0 gives the duration.
    public static void AddSamples(Node statistics, Func<string, NodeValue?, string, Node> cover, long samples, double sampleRate, string samplesDescription, string durationDescription)
    {
        if (sampleRate > 0)
        {
            statistics.Add(cover("Container Duration", NodeValue.Decimal(samples / sampleRate, 3, "s"), durationDescription));
        }

        statistics.Add(cover("Container Samples", NodeValue.Integer(samples, "samples"), samplesDescription));
    }

    // A length in seconds that the container states; the samples at sampleRate, rounded to the nearest, halves up, where the rate is known.
    public static void AddDuration(Node statistics, Func<string, NodeValue?, string, Node> cover, double seconds, double sampleRate, string samplesDescription, string durationDescription)
    {
        statistics.Add(cover("Container Duration", NodeValue.Decimal(seconds, 3, "s"), durationDescription));
        if (sampleRate > 0 && seconds * sampleRate < long.MaxValue / 2.0)
        {
            statistics.Add(cover("Container Samples", NodeValue.Integer((long)Math.Floor((seconds * sampleRate) + 0.5), "samples"), samplesDescription));
        }
    }
}
