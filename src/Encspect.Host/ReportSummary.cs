using Encspect.Core;

namespace Encspect.Host;

// One line about a file, for batch output and file lists, read from the streams the format parser
// reports and the tags' node kinds, never from node names. Codec, sample rate, channel mode,
// channel layout, encoder and writer are the first stream's that has one; the duration and samples add up over all streams, except that
// the streams of one playback group (StreamInfo.PlaybackGroup), which play at the same time, count their longest once, and the
// average bitrate is weighted by their durations. Object audio is whether any stream has it, with the
// most JOC objects one reconstructs. Tags are the file's and those its container holds in its chunks,
// in tree order.
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
    IReadOnlyList<Speaker>? ChannelLayout,
    string? Encoder,
    Confidence? EncoderConfidence,
    string? Writer,
    Confidence? WriterConfidence,
    IReadOnlyList<string> Tags,
    int Errors,
    int Warnings,
    int Infos,
    bool HasObjectAudio = false,
    int? JocObjectCount = null)
{
    // The layout's common name, such as "5.1", or null.
    public string? ChannelLayoutName => ChannelLayout is { } layout ? ChannelLayouts.CommonName(layout) : null;

    // The layout as the CSV writes it: "5.1 (front left, front right, front center, LFE, back left,
    // back right)", or the speakers alone where the layout has no common name.
    public string? ChannelLayoutText => ChannelLayout is { } layout ? ChannelLayouts.Describe(layout) : null;

    public static ReportSummary From(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);

        IReadOnlyList<StreamInfo> streams = report.Streams;
        StreamInfo? first = streams.Count > 0 ? streams[0] : null;
        StreamInfo? encoded = streams.FirstOrDefault(s => s.Encoder is not null);
        StreamInfo? written = streams.FirstOrDefault(s => s.Writer is not null);
        List<StreamInfo> timed = streams.Where(s => s.AverageBitrate is not null && s.Duration > 0).ToList();
        double timedDuration = timed.Sum(s => s.Duration);
        List<StreamInfo> counted = [.. streams.Where(s => s.PlaybackGroup is null),
            .. streams.Where(s => s.PlaybackGroup is not null).GroupBy(s => s.PlaybackGroup).Select(g => g.MaxBy(s => s.Duration)!)];
        double duration = counted.Sum(s => s.Duration);
        long samples = counted.Sum(s => s.Samples);

        return new ReportSummary(
            report.Source,
            report.Root.Value?.ToString(),
            streams.Count,
            first?.Codec,
            first is null ? null : duration,
            first is null ? null : samples,
            timedDuration > 0 ? timed.Sum(s => s.AverageBitrate!.Value * s.Duration) / timedDuration : null,
            first?.SampleRate,
            first?.ChannelMode,
            first?.ChannelLayout,
            encoded?.Encoder,
            encoded?.EncoderConfidence,
            written?.Writer,
            written?.WriterConfidence,
            TagNames(report.Root),
            report.Findings.Count(f => f.Severity == Severity.Error),
            report.Findings.Count(f => f.Severity == Severity.Warning),
            report.Findings.Count(f => f.Severity == Severity.Info),
            streams.Any(s => s.HasObjectAudio),
            streams.Where(s => s.JocObjectCount is not null).Select(s => s.JocObjectCount).Max());
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
