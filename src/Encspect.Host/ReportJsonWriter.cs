using System.Text.Json;
using Encspect.Core;

namespace Encspect.Host;

public static class ReportJsonWriter
{
    public static void Write(IEnumerable<Report> reports, Stream output)
    {
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(output);

        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });

        writer.WriteStartArray();
        foreach (Report report in reports)
        {
            WriteReport(writer, report);
        }

        writer.WriteEndArray();
    }

    private static void WriteReport(Utf8JsonWriter writer, Report report)
    {
        writer.WriteStartObject();
        writer.WriteString("source", report.Source);

        writer.WritePropertyName("root");
        WriteNode(writer, report.Root);

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

    private static void WriteNode(Utf8JsonWriter writer, Node node)
    {
        writer.WriteStartObject();
        writer.WriteString("name", node.Name);
        if (node.Value is not null)
        {
            writer.WriteString("value", node.Value);
        }

        writer.WriteNumber("offset", node.Offset);
        writer.WriteNumber("length", node.Length);

        if (node.Children.Count > 0)
        {
            writer.WriteStartArray("children");
            foreach (Node child in node.Children)
            {
                WriteNode(writer, child);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}
