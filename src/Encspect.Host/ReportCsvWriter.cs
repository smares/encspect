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
        "Is Protected", "Is Verified", "Protection Scheme", "DRM Systems", "Spectral Cutoff (Hz)", "Sample Peak (dBFS)",
        "Maximum True Peak Level (dBTP)", "Integrated Loudness (LUFS)", "Loudness Range (LU)",
    ];

    // The columns the writer formats from numbers: counts, durations, rates and the measured figures; they skip the guard against formulas.
    private static readonly int[] NumericColumns = [.. new[] { "Streams", "Duration (s)", "Samples", "Average Bitrate (kbps)", "Sample Rate (Hz)", "Errors", "Warnings", "Infos", "JOC Object Count", "Spectral Cutoff (Hz)", "Sample Peak (dBFS)", "Maximum True Peak Level (dBTP)", "Integrated Loudness (LUFS)", "Loudness Range (LU)" }.Select(n => Array.IndexOf(Columns, n))];

    public static void Write(IEnumerable<Report> reports, TextWriter writer) => Write(reports, writer, isVerifying: false);

    // isVerifying: the analysis asked for decoding (--verify or --measure), so Is Verified holds its verdict; otherwise it is empty for
    // every file, and a folder export never mixes verdicts from coded data alone with empty ones. The measured columns are empty
    // for a file without a figure, and for every file of an analysis that did not measure.
    public static void Write(IEnumerable<Report> reports, TextWriter writer, bool isVerifying)
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
                s.Streams == 0 ? null : s.IsProtected ? "true" : "false", isVerifying && s.IsVerified is { } verified ? (verified ? "true" : "false") : null,
                string.Join("; ", s.ProtectionSchemes), string.Join("; ", s.DrmSystems), s.SpectralCutoff?.ToString(CultureInfo.InvariantCulture),
                s.SamplePeakTenths is { } peak ? MeasurementText.Tenths(peak) : null, s.TruePeakTenths is { } truePeak ? MeasurementText.Tenths(truePeak) : null,
                s.IntegratedLoudnessTenths is { } loudness ? MeasurementText.Tenths(loudness) : null, s.LoudnessRangeTenths is { } range ? MeasurementText.Tenths(range) : null,
            ], numeric: NumericColumns);
            writer.Flush();
        }
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Rounded half to even, as the statistics in the report are.
    private static string? Fixed(double? value, int decimals) =>
        value is { } number ? Math.Round(number, decimals, MidpointRounding.ToEven).ToString("F" + decimals, CultureInfo.InvariantCulture) : null;

    // numeric: the indexes of the fields the writer formats from numbers (counts, Fixed, the measured figures). A number cannot start a
    // formula (its first character is a digit, or a minus sign followed by a digit), so the guard against formulas skips it and a
    // negative figure reads back as a number.
    private static void WriteRow(TextWriter writer, IEnumerable<string?> fields, IReadOnlyCollection<int>? numeric = null)
    {
        writer.Write(string.Join(',', fields.Select((f, i) => Cell(f, numeric is not null && numeric.Contains(i)))));
        writer.Write("\r\n");
    }

    // A field the writer formats from a number is written as it is where it reads as one; every other, or a number-flagged field that does
    // not read as one, goes through the guard.
    internal static string Cell(string? value, bool isNumeric) => isNumeric && IsNumber(value) ? value! : Field(value);

    // An optional minus sign, digits, and optionally a point with digits: nothing a spreadsheet reads as a formula or a date.
    internal static bool IsNumber(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        ReadOnlySpan<char> rest = value.AsSpan(value[0] == '-' ? 1 : 0);
        int point = rest.IndexOf('.');
        ReadOnlySpan<char> whole = point < 0 ? rest : rest[..point];
        ReadOnlySpan<char> fraction = point < 0 ? default : rest[(point + 1)..];
        return whole.Length > 0 && whole.IndexOfAnyExceptInRange('0', '9') < 0 && (point < 0 || (fraction.Length > 0 && fraction.IndexOfAnyExceptInRange('0', '9') < 0));
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
