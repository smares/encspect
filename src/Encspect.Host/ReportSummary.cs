using Encspect.Core;

namespace Encspect.Host;

// One line about a file, for batch output and file lists, read from the streams the format parser
// reports and the tags' node kinds, never from node names. Codec, sample rate, channel mode and
// encoder are the first stream's; the duration and samples add up over all streams, and the
// average bitrate is weighted by their durations. Tags are the file's and those its container
// holds in its chunks, in tree order.
public sealed record ReportSummary(
    string Source,
    string? Format,
    int Streams,
    string? Codec,
    double? Duration,
    long? Samples,
    double? AverageBitrate,
    int? SampleRate,
    string? ChannelMode,
    string? Encoder,
    Confidence? EncoderConfidence,
    IReadOnlyList<string> Tags,
    int Errors,
    int Warnings,
    int Infos)
{
    public static ReportSummary From(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);

        IReadOnlyList<StreamInfo> streams = report.Streams;
        StreamInfo? first = streams.Count > 0 ? streams[0] : null;
        StreamInfo? encoded = streams.FirstOrDefault(s => s.Encoder is not null);
        List<StreamInfo> timed = streams.Where(s => s.AverageBitrate is not null && s.Duration > 0).ToList();
        double timedDuration = timed.Sum(s => s.Duration);

        return new ReportSummary(
            report.Source,
            report.Root.Value?.ToString(),
            streams.Count,
            first?.Codec,
            first is null ? null : streams.Sum(s => s.Duration),
            first is null ? null : streams.Sum(s => s.Samples),
            timedDuration > 0 ? timed.Sum(s => s.AverageBitrate!.Value * s.Duration) / timedDuration : null,
            first?.SampleRate,
            first?.ChannelMode,
            encoded?.Encoder,
            encoded?.EncoderConfidence,
            TagNames(report.Root),
            report.Findings.Count(f => f.Severity == Severity.Error),
            report.Findings.Count(f => f.Severity == Severity.Warning),
            report.Findings.Count(f => f.Severity == Severity.Info));
    }

    // Only plain details are searched below, as a container's chunks are, never streams, frames or
    // other sections that parsers build by the thousand, nor a tag's own items.
    private static List<string> TagNames(Node root)
    {
        var names = new List<string>();
        var pending = new Stack<(Node Node, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            (Node node, int depth) = pending.Pop();
            if (node.Kind == NodeKind.Tag)
            {
                names.Add(node.Name);
                continue;
            }

            if ((node == root || node.Kind == NodeKind.Detail) && depth < Node.MaxDepth && node.HasChildren)
            {
                IReadOnlyList<Node> children = node.Children;
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    pending.Push((children[i], depth + 1));
                }
            }
        }

        return names;
    }
}
