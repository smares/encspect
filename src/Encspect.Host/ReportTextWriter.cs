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

        writer.WriteLine(Escape(report.Source));
        WriteNode(report.Root, writer, showOffsets, filter, 0);

        if (report.Findings.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Findings:");
            foreach (Finding finding in report.Findings)
            {
                writer.Write($"  [{finding.Severity}] {finding.Code}: {Escape(finding.Message)}");
                if (showOffsets && finding.Node is not null)
                {
                    WriteLocation(finding.Node, writer);
                }

                writer.WriteLine();
            }
        }
    }

    // Written piece by piece rather than as one interpolated line per node, because a report can
    // hold hundreds of thousands of nodes.
    // depth: levels below the root. Recursion stops at Node.MaxDepth, where a line says that
    // deeper children are left out.
    private static void WriteNode(Node node, TextWriter writer, bool showOffsets, ReportFilter filter, int depth)
    {
        Indent(writer, depth);
        writer.Write(node.Name);
        if (node.Value is not null)
        {
            writer.Write(": ");
            writer.Write(Escape(node.Value));
        }

        if (showOffsets)
        {
            WriteLocation(node, writer);
        }

        writer.WriteLine();

        if (depth >= Node.MaxDepth)
        {
            if (node.HasChildren && node.Children.Any(c => filter.Includes(c, node)))
            {
                Indent(writer, depth + 1);
                writer.WriteLine($"(children nested deeper than {Node.MaxDepth} levels are not shown)");
            }

            return;
        }

        foreach (Node child in node.Children)
        {
            if (filter.Includes(child, node))
            {
                WriteNode(child, writer, showOffsets, filter, depth + 1);
            }
        }
    }

    private static void Indent(TextWriter writer, int depth)
    {
        for (int indent = (depth + 1) * 2; indent > 0; indent -= Spaces.Length)
        {
            writer.Write(Spaces.AsSpan(0, Math.Min(indent, Spaces.Length)));
        }
    }

    // A field of whole bytes shows "[0x1A, 4 bytes]"; a bit field its first byte, the bit within it
    // counted from the most significant bit, and its width: "[0x1+3, 2 bits]".
    private static void WriteLocation(Node node, TextWriter writer)
    {
        writer.Write(" [0x");
        writer.Write(node.Offset.ToString("X", CultureInfo.InvariantCulture));
        if (node.BitOffset is { } bitOffset && node.BitLength is { } bitLength)
        {
            writer.Write("+");
            writer.Write(bitOffset.ToString(CultureInfo.InvariantCulture));
            writer.Write(", ");
            writer.Write(bitLength.ToString(CultureInfo.InvariantCulture));
            writer.Write(bitLength == 1 ? " bit]" : " bits]");
            return;
        }

        writer.Write(", ");
        writer.Write(node.Length.ToString(CultureInfo.InvariantCulture));
        writer.Write(" bytes]");
    }

    // Values come from the file and paths from the file system; either may hold line breaks or
    // other control characters, which would break the one-line-per-node layout.
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
