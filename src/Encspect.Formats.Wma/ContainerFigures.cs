using Encspect.Core;

namespace Encspect.Formats.Wma;

internal static class ContainerFigures
{
    // The length the container states, in samples, beside the stream's own count; nothing where the container states none.
    public static void AddSamples(Node statistics, Node parent, CodecConfiguration? configuration)
    {
        if (configuration?.DurationSamples is { } samples)
        {
            statistics.Add("Container Samples", parent.Offset, parent.Length, NodeValue.Integer(samples, "samples", "sample"), Explain.StatContainerSamples);
        }
    }
}
