using Encspect.Core;

namespace Encspect.Formats.Wma;

internal static class ContainerFigures
{
    // The length the container states, in seconds and in samples, beside the stream's own count; nothing where the container states none.
    public static void AddLength(Node statistics, Node parent, CodecConfiguration? configuration)
    {
        if (configuration?.Duration is > 0 and { } seconds)
        {
            statistics.Add("Container Duration", parent.Offset, parent.Length, NodeValue.Decimal(seconds, 3, "s"), Explain.StatContainerDuration);
        }

        if (configuration?.DurationSamples is { } samples)
        {
            statistics.Add("Container Samples", parent.Offset, parent.Length, NodeValue.Integer(samples, "samples"), Explain.StatContainerSamples);
        }
    }
}
