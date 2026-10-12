using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Gui.ViewModels;

public enum ReportFormat
{
    Text,
    Json,
}

// Writes a report as the CLI does: the text tree with offsets (--offsets), or JSON (--json, an
// array of one report).
public static class ReportExport
{
    public static ReportFormat FormatOf(string? fileName) =>
        string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase) ? ReportFormat.Json : ReportFormat.Text;

    public static void Write(Report report, Stream output, ReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(output);

        if (format == ReportFormat.Json)
        {
            ReportJsonWriter.Write([report], output);
            output.WriteByte((byte)'\n');
        }
        else
        {
            using var writer = new StreamWriter(output, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
            ReportTextWriter.Write(report, writer, showOffsets: true);
        }

        output.Flush();
    }
}
