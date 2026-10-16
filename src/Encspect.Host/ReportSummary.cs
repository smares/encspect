using Encspect.Core;

namespace Encspect.Host;

// One line about a file, for batch output and file lists, read from the streams the format parser
// reports and the tags' node kinds, never from node names. Codec, sample rate, channel mode,
// channel layout and encoder are the first stream's; the duration and samples add up over all streams, and the
// average bitrate is weighted by their durations. Object audio is whether any stream has it, with the
// most JOC objects one reconstructs. Protection is whether any stream is protected, with the schemes
// and DRM systems the protected streams name, without repeats, in stream order. Tags are the file's and those its container holds in its chunks,
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
    IReadOnlyList<string> Tags,
    int Errors,
    int Warnings,
    int Infos,
    bool HasObjectAudio = false,
    int? JocObjectCount = null,
    bool IsProtected = false,
    IReadOnlyList<string>? ProtectionSchemes = null,
    IReadOnlyList<string>? DrmSystems = null)
{
    // The schemes and systems as lists even for a summary made without them.
    public IReadOnlyList<string> ProtectionSchemes { get; init; } = ProtectionSchemes ?? [];

    public IReadOnlyList<string> DrmSystems { get; init; } = DrmSystems ?? [];

    // The layout's common name, such as "5.1", or null.
    public string? ChannelLayoutName => ChannelLayout is { } layout ? ChannelLayouts.CommonName(layout) : null;

    // The layout as the CSV writes it: "5.1 (front left, front right, front center, LFE, back left,
    // back right)", or the speakers alone where the layout has no common name.
    public string? ChannelLayoutText => ChannelLayout is { } layout ? ChannelLayouts.Describe(layout) : null;

    public static ReportSummary From(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);

        IReadOnlyList<StreamInfo> streams = report.Streams;
        StreamInfo? first = streams.FirstOrDefault(s => s.IsAudio) ?? (streams.Count > 0 ? streams[0] : null);
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
            first?.ChannelLayout,
            encoded?.Encoder,
            encoded?.EncoderConfidence,
            TagNames(report.Root),
            report.Findings.Count(f => f.Severity == Severity.Error),
            report.Findings.Count(f => f.Severity == Severity.Warning),
            report.Findings.Count(f => f.Severity == Severity.Info),
            streams.Any(s => s.HasObjectAudio),
            streams.Where(s => s.JocObjectCount is not null).Select(s => s.JocObjectCount).Max(),
            streams.Any(s => s.IsProtected),
            [.. streams.Select(s => s.Protection?.Scheme).OfType<string>().Distinct()],
            [.. streams.SelectMany(s => s.Protection?.Systems ?? []).Distinct()]);
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
