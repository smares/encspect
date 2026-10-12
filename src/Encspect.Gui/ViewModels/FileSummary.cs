using Encspect.Core;
using Encspect.Host;

namespace Encspect.Gui.ViewModels;

// What a row of the file list keeps of a report: the summary the CSV writes, whether the bitrate
// varies, and why the file was not analyzed. The report itself is dropped, so a folder of thousands
// of files does not hold thousands of trees.
public sealed record FileSummary(ReportSummary Summary, bool? IsVbr, string? Problem)
{
    public static FileSummary From(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);

        bool? isVbr = report.Streams.Count > 0 ? report.Streams.Any(s => s.IsVbr) : null;
        string? problem = FileReports.IsNotAnalyzed(report) ? report.Findings[0].Message : null;
        return new FileSummary(ReportSummary.From(report), isVbr, problem);
    }
}
