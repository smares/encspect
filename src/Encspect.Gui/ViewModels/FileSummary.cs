using Encspect.Core;
using Encspect.Host;

namespace Encspect.Gui.ViewModels;

// What a row of the file list keeps of a report: the summary the CSV writes, whether the bitrate
// varies, and why the file was not analyzed. The report itself is dropped, so a folder of thousands
// of files does not hold thousands of trees.
public sealed record FileSummary(ReportSummary Summary, bool? IsVbr, string? Problem)
{
    // How the first stream that has one stands with its correction file, worded as the command line words it ("Correction file: consistent
    // (x.wvc), lossless with its correction file"); null for a file without the concept.
    public string? Correction { get; init; }

    // The paths of the companion files the analysis looked for, found or not, taken from the report (Report.Companions and the pairings' sources), so the
    // summary cache can key the file by them without knowing any format.
    public IReadOnlyList<string> CompanionPaths { get; init; } = [];

    // For each path of CompanionPaths, the length of the companion as the report read it, or null where the report found none. The cache compares them with the
    // files as they are once the analysis is done, and does not keep a summary a companion that changed meanwhile contradicts.
    public IReadOnlyList<long?> CompanionLengths { get; init; } = [];

    // Whether the analysis found an audio stream: the summary's codec, duration and the other figures of the audio come from the first stream of kind audio and
    // are null without one. The file name never enters.
    public bool HasAudio => Summary.Duration is not null;

    public static FileSummary From(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);

        bool? isVbr = report.Streams.Count > 0 ? report.Streams.Any(s => s.IsVbr) : null;
        string? problem = FileReports.IsNotAnalyzed(report) ? report.Findings[0].Message : null;
        List<(string Path, long? Length)> lookedFor = LookedFor(report);
        CorrectionPairing? pairing = report.Streams.Select(s => s.Correction).FirstOrDefault(c => c is not null);
        return new FileSummary(ReportSummary.From(report), isVbr, problem)
        {
            Correction = pairing is null ? null : CorrectionLine(pairing),
            CompanionPaths = lookedFor.Select(l => l.Path).ToList(),
            CompanionLengths = lookedFor.Select(l => l.Length).ToList(),
        };
    }

    // The line the stream's pairing gets in the command line's text output.
    internal static string CorrectionLine(CorrectionPairing pairing) => "Correction file: " + CorrectionText.Describe(pairing);

    private static List<(string Path, long? Length)> LookedFor(Report report)
    {
        var paths = new List<(string Path, long? Length)>();
        string? folder = Path.GetDirectoryName(report.Source);
        foreach (CompanionReport companion in report.Companions)
        {
            paths.Add((companion.Report.Source, companion.Report.Root.Length));
        }

        // A pairing that is unchecked and not unreadable was not looked for (or was found, and is listed above).
        foreach (CorrectionPairing pairing in report.Streams.Select(s => s.Correction).OfType<CorrectionPairing>())
        {
            if (pairing.Source is { Length: > 0 } source && (pairing.Status != CorrectionStatus.Unchecked || pairing.IsUnreadable))
            {
                paths.Add((string.IsNullOrEmpty(folder) ? source : Path.Combine(folder, source), null));
            }
        }

        return [.. paths.GroupBy(p => p.Path, StringComparer.Ordinal).Select(g => g.First())];
    }
}
