using System.Text.Encodings.Web;
using System.Text.Json;
using Encspect.Core;

namespace Encspect.Host;

public static class ReportJsonWriter
{
    // Utf8JsonWriter buffers everything until it is flushed, which for a report with a node per
    // frame header field means the whole document in memory. Flushing once this much is pending
    // keeps memory flat however large the report gets.
    private const int FlushThreshold = 1 << 16;

    // Text from files stays readable: non-ASCII characters are written as UTF-8, not as \u escapes.
    // Quotes, backslashes and control characters are still escaped; the output is never embedded in HTML.
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    // Reports are written as they are enumerated, so a lazy sequence is analyzed and written one
    // file at a time.
    public static void Write(IEnumerable<Report> reports, Stream output) => Write(reports, output, ReportFilter.All);

    public static void Write(IEnumerable<Report> reports, Stream output, ReportFilter filter)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(filter);

        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true, Encoder = Encoder });

        writer.WriteStartArray();
        foreach (Report report in reports)
        {
            WriteReport(writer, report, filter);
            writer.Flush();
        }

        writer.WriteEndArray();
    }

    // Newline-delimited JSON: one compact report per line, so every finished line can be used on
    // its own, even when the run stops early, and outputs can be concatenated.
    public static void WriteLines(IEnumerable<Report> reports, Stream output) => WriteLines(reports, output, ReportFilter.All);

    public static void WriteLines(IEnumerable<Report> reports, Stream output, ReportFilter filter)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(filter);

        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = Encoder });

        foreach (Report report in reports)
        {
            WriteReport(writer, report, filter);
            writer.Flush();
            output.WriteByte((byte)'\n');

            // A writer accepts only one top-level value until it is reset.
            writer.Reset(output);
        }
    }

    private static void WriteReport(Utf8JsonWriter writer, Report report, ReportFilter filter)
    {
        writer.WriteStartObject();
        writer.WriteString("source", report.Source);

        writer.WritePropertyName("root");
        WriteNode(writer, report.Root, filter);

        writer.WriteStartArray("findings");
        foreach (Finding finding in report.Findings)
        {
            writer.WriteStartObject();
            writer.WriteString("severity", finding.Severity.ToString());
            writer.WriteString("code", finding.Code);
            writer.WriteString("message", finding.Message);
            if (finding.Node is not null)
            {
                writer.WriteNumber("offset", finding.Node.Offset);
                writer.WriteNumber("length", finding.Node.Length);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter writer, Node node, ReportFilter filter)
    {
        writer.WriteStartObject();
        writer.WriteString("name", node.Name);
        if (node.Value is not null)
        {
            writer.WriteString("value", node.Value);
        }

        writer.WriteNumber("offset", node.Offset);
        writer.WriteNumber("length", node.Length);

        IReadOnlyList<Node> children = filter.Hidden == ReportSections.None ? node.Children : node.Children.Where(c => filter.Includes(c, node)).ToList();
        if (children.Count > 0)
        {
            writer.WriteStartArray("children");
            foreach (Node child in children)
            {
                WriteNode(writer, child, filter);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        if (writer.BytesPending > FlushThreshold)
        {
            writer.Flush();
        }
    }
}
