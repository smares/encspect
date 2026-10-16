using System.Globalization;
using Encspect.Core;

namespace Encspect.Host;

// A header row and one ReportSummary row per file (RFC 4180), written as each file is analyzed.
public static class ReportCsvWriter
{
    private static readonly string[] Columns =
    [
        "Path", "Format", "Streams", "Codec", "Duration (s)", "Samples", "Average Bitrate (kbps)", "Sample Rate (Hz)", "Channel Mode",
        "Channel Layout", "Encoder", "Encoder Confidence", "Writer", "Writer Confidence", "Tags", "Errors", "Warnings", "Infos", "Has Object Audio", "JOC Object Count",
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
                s.Source, s.Format, Count(s.Streams), s.Codec, Fixed(s.Duration, 3), s.Samples?.ToString(CultureInfo.InvariantCulture),
                Fixed(s.AverageBitrate, 1), s.SampleRate?.ToString(CultureInfo.InvariantCulture), s.ChannelMode, s.ChannelLayoutText, s.Encoder,
                s.EncoderConfidence?.ToString(), s.Writer, s.WriterConfidence?.ToString(), string.Join(", ", s.Tags), Count(s.Errors), Count(s.Warnings), Count(s.Infos),
                s.Streams == 0 ? null : s.HasObjectAudio ? "true" : "false", s.JocObjectCount?.ToString(CultureInfo.InvariantCulture),
            ]);
            writer.Flush();
        }
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Rounded half to even, as the statistics in the report are.
    private static string? Fixed(double? value, int decimals) =>
        value is { } number ? Math.Round(number, decimals, MidpointRounding.ToEven).ToString("F" + decimals, CultureInfo.InvariantCulture) : null;

    private static void WriteRow(TextWriter writer, IEnumerable<string?> fields)
    {
        writer.Write(string.Join(',', fields.Select(Field)));
        writer.Write("\r\n");
    }

    // Encoder and writer names and paths come from files. Quoted where they hold a separator, quote or line
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
