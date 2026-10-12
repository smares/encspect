using Encspect.Core;

namespace Encspect.Host;

// One line about a file, for batch output and file lists, read from the streams the format parser
// reports and the tags' node kinds, never from node names. Codec, sample rate, channel mode and
// encoder are the first stream's; the duration and samples add up over all streams, and the
// average bitrate is weighted by their durations.
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
            report.Root.Children.Where(c => c.Kind == NodeKind.Tag).Select(c => c.Name).ToList(),
            report.Findings.Count(f => f.Severity == Severity.Error),
            report.Findings.Count(f => f.Severity == Severity.Warning),
            report.Findings.Count(f => f.Severity == Severity.Info));
    }
}
