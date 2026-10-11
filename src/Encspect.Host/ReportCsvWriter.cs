using System.Globalization;
using Encspect.Core;

namespace Encspect.Host;

// A header row and one ReportSummary row per file (RFC 4180), written as each file is analyzed.
public static class ReportCsvWriter
{
    private static readonly string[] Columns =
    [
        "Path", "Format", "Streams", "Codec", "Duration (s)", "Average Bitrate (kbps)", "Sample Rate (Hz)", "Channel Mode",
        "Encoder", "Encoder Confidence", "Tags", "Errors", "Warnings", "Infos",
    ];

    public static void Write(IEnumerable<Report> reports, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(writer);

        WriteRow(writer, Columns);
        foreach (Report report in reports)
        {
            ReportSummary s = ReportSummary.From(report);
            WriteRow(writer,
            [
                s.Source, s.Format, Count(s.Streams), s.Codec, s.Duration, s.AverageBitrate, s.SampleRate, s.ChannelMode,
                s.Encoder, s.EncoderConfidence, string.Join(", ", s.Tags), Count(s.Errors), Count(s.Warnings), Count(s.Infos),
            ]);
            writer.Flush();
        }
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void WriteRow(TextWriter writer, IEnumerable<string?> fields)
    {
        writer.Write(string.Join(',', fields.Select(Field)));
        writer.Write("\r\n");
    }

    // Encoder names and paths come from files. Quoted where they hold a separator, quote or line
    // break; a leading =, +, -, @ or control character gets an apostrophe, so a spreadsheet shows
    // the text instead of evaluating it as a formula.
    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' || char.IsControl(value[0]))
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
