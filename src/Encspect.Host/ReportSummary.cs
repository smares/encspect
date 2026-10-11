using System.Globalization;
using Encspect.Core;

namespace Encspect.Host;

// One line about a file, for batch output and file lists, read from the nodes the parsers write:
// the first stream's statistics, its first frame's header and its encoder, and the tags. A stream
// is a top-level node named after the file's format; a file can hold several, separated by junk
// or a change of stream parameters, and Streams counts them.
public sealed record ReportSummary(
    string Source,
    string? Format,
    int Streams,
    string? Codec,
    string? Duration,
    string? AverageBitrate,
    string? SampleRate,
    string? ChannelMode,
    string? Encoder,
    string? EncoderConfidence,
    IReadOnlyList<string> Tags,
    int Errors,
    int Warnings,
    int Infos)
{
    private static readonly HashSet<string> TagNames = DefaultParsers.Tags.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    public static ReportSummary From(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);

        Node root = report.Root;
        string? format = root.Value;
        List<Node> streams = format is null ? [] : root.Children.Where(c => c.Name == format).ToList();
        Node? stream = streams.FirstOrDefault();
        Node? statistics = Child(stream, "Statistics");
        Node? header = Child(Child(stream, "Frame"), "Header");
        Node? encoder = Child(stream, "Encoder");
        string? version = Child(header, "Version")?.Value;
        string? layer = Child(header, "Layer")?.Value;

        return new ReportSummary(
            report.Source,
            format,
            streams.Count,
            version is not null && layer is not null ? $"{version} Layer {layer}" : null,
            Number(Child(statistics, "Duration")?.Value),
            Number(Child(statistics, "Average Bitrate")?.Value),
            Number(Child(header, "Sample Rate")?.Value),
            Child(header, "Channel Mode")?.Value,
            encoder?.Value,
            Child(encoder, "Confidence")?.Value,
            root.Children.Where(c => TagNames.Contains(c.Name)).Select(c => c.Name).ToList(),
            report.Findings.Count(f => f.Severity == Severity.Error),
            report.Findings.Count(f => f.Severity == Severity.Warning),
            report.Findings.Count(f => f.Severity == Severity.Info));
    }

    private static Node? Child(Node? node, string name) => node?.Children.FirstOrDefault(c => c.Name == name);

    // "0.549 s", "128.0 kbps" and "44100 Hz" lose their unit, which the column names.
    private static string? Number(string? value)
    {
        string? first = value?.Split(' ')[0];
        return first is not null && decimal.TryParse(first, NumberStyles.Number, CultureInfo.InvariantCulture, out _) ? first : value;
    }
}
