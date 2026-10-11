using System.Globalization;
using Encspect.Core;

namespace Encspect.Host;

public static class ReportTextWriter
{
    private static readonly string Spaces = new(' ', 64);

    public static void Write(Report report, TextWriter writer, bool showOffsets) => Write(report, writer, showOffsets, ReportFilter.All);

    public static void Write(Report report, TextWriter writer, bool showOffsets, ReportFilter filter)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(filter);

        writer.WriteLine(report.Source);
        WriteNode(report.Root, writer, showOffsets, filter, 0);

        if (report.Findings.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Findings:");
            foreach (Finding finding in report.Findings)
            {
                writer.WriteLine($"  [{finding.Severity}] {finding.Code}: {Escape(finding.Message)}");
            }
        }
    }

    // Written piece by piece rather than as one interpolated line per node, because a report can
    // hold hundreds of thousands of nodes.
    private static void WriteNode(Node node, TextWriter writer, bool showOffsets, ReportFilter filter, int depth)
    {
        for (int indent = (depth + 1) * 2; indent > 0; indent -= Spaces.Length)
        {
            writer.Write(Spaces.AsSpan(0, Math.Min(indent, Spaces.Length)));
        }

        writer.Write(node.Name);
        if (node.Value is not null)
        {
            writer.Write(": ");
            writer.Write(Escape(node.Value));
        }

        if (showOffsets)
        {
            writer.Write(" [0x");
            writer.Write(node.Offset.ToString("X", CultureInfo.InvariantCulture));
            writer.Write(", ");
            writer.Write(node.Length.ToString(CultureInfo.InvariantCulture));
            writer.Write(" bytes]");
        }

        writer.WriteLine();

        foreach (Node child in node.Children)
        {
            if (filter.Includes(child, node))
            {
                WriteNode(child, writer, showOffsets, filter, depth + 1);
            }
        }
    }

    // Values come from the file and may hold line breaks or other control characters, which
    // would break the one-line-per-node layout.
    private static string Escape(string text)
    {
        if (!text.Any(char.IsControl))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            builder.Append(c switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\x{(int)c:X2}",
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }
}
