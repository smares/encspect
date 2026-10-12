using System.Globalization;
using Encspect.Core;

namespace Encspect.Host;

public static class ReportTextWriter
{
    private static readonly string Spaces = new(' ', 64);

    // Where --explain wraps the descriptions.
    private const int ExplainWidth = 100;

    public static void Write(Report report, TextWriter writer, bool showOffsets) => Write(report, writer, showOffsets, ReportFilter.All);

    public static void Write(Report report, TextWriter writer, bool showOffsets, ReportFilter filter) => Write(report, writer, showOffsets, filter, explain: false);

    // explain: write each node's description and topic, indented under its line.
    public static void Write(Report report, TextWriter writer, bool showOffsets, ReportFilter filter, bool explain)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(filter);

        writer.WriteLine(Escape(report.Source));
        Dictionary<Node, List<Series>> anchored = new(ReferenceEqualityComparer.Instance);
        foreach (Series series in report.Streams.SelectMany(s => s.Series).Where(s => s.Anchor is not null))
        {
            if (!anchored.TryGetValue(series.Anchor!, out List<Series>? list))
            {
                anchored[series.Anchor!] = list = [];
            }

            list.Add(series);
        }

        WriteNode(report.Root, writer, new Options(showOffsets, explain, filter), 0, anchored);

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
    private static void WriteNode(Node node, TextWriter writer, Options options, int depth, Dictionary<Node, List<Series>> anchored)
    {
        Indent(writer, depth);
        writer.Write(node.Name);
        if (node.Value is { } value)
        {
            writer.Write(": ");
            WriteValue(value, writer);
        }

        if (options.ShowOffsets)
        {
            WriteLocation(node, writer);
        }

        writer.WriteLine();
        if (options.Explain)
        {
            WriteExplanation(node.Description, node.Topic, writer, depth);
        }

        if (depth >= Node.MaxDepth)
        {
            if (node.HasChildren && node.Children.Any(options.Filter.Includes))
            {
                Indent(writer, depth + 1);
                writer.WriteLine($"(children nested deeper than {Node.MaxDepth} levels are not shown)");
            }
        }
        else
        {
            foreach (Node child in node.Children)
            {
                if (options.Filter.Includes(child))
                {
                    WriteNode(child, writer, options, depth + 1, anchored);
                }
            }
        }

        if (anchored.TryGetValue(node, out List<Series>? series))
        {
            foreach (Series item in series)
            {
                WriteSeries(item, writer, depth, options);
            }
        }
    }

    // The description, and the topic to read with --learn, wrapped under the node's line and
    // indented one level deeper than its children, so they do not read as a child.
    private static void WriteExplanation(string? description, string? topic, TextWriter writer, int depth)
    {
        if (description is null && topic is null)
        {
            return;
        }

        string text = topic is null ? description! : $"{description} [topic:{topic}]".TrimStart();
        string indent = new(' ', (depth + 3) * 2);
        var wrapped = new System.Text.StringBuilder();
        TopicLibrary.Wrap(wrapped, Escape(text), Math.Max(ExplainWidth, indent.Length + 40), indent, indent);
        writer.Write(wrapped.ToString().Replace("\n", Environment.NewLine, StringComparison.Ordinal));
    }

    // A series in one line after the node it belongs with, "Bitrate Over Time: 128,160,128", and
    // with offsets that node's location.
    private static void WriteSeries(Series series, TextWriter writer, int depth, Options options)
    {
        Indent(writer, depth);
        writer.Write(series.Name);
        writer.Write(": ");
        ReadOnlySpan<double> values = series.Values.Span;
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            writer.Write(values[i].ToString(CultureInfo.InvariantCulture));
        }

        if (options.ShowOffsets)
        {
            WriteLocation(series.Anchor!, writer);
        }

        writer.WriteLine();
        if (options.Explain)
        {
            WriteExplanation(series.Description, null, writer, depth);
        }
    }

    private sealed record Options(bool ShowOffsets, bool Explain, ReportFilter Filter);

    // Values from the file are text and escaped; numbers are written without a string per node.
    private static void WriteValue(NodeValue value, TextWriter writer)
    {
        if (value.AsText() is { } text)
        {
            writer.Write(Escape(text));
            return;
        }

        Span<char> buffer = stackalloc char[96];
        if (value.TryFormat(buffer, out int written))
        {
            writer.Write(buffer[..written]);
        }
        else
        {
            writer.Write(Escape(value.ToString()));
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
    // counted from the most significant bit, and its width: "[0x1+3, 2 bits]", or counted from the
    // least significant bit where its bits run that way (Vorbis): "[0x1+3, 2 bits, LSB first]". Bytes that are not
    // contiguous add their ranges: "[0x1A, 40 bytes in 0x1A 2 bytes, 0x40 2 bytes]".
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
            writer.Write(bitLength == 1 ? " bit" : " bits");
            if (node.BitOrder == BitOrder.LeastSignificantFirst)
            {
                writer.Write(", LSB first");
            }
        }
        else
        {
            writer.Write(", ");
            writer.Write(node.Length.ToString(CultureInfo.InvariantCulture));
            writer.Write(" bytes");
        }

        if (node.Ranges is { } ranges)
        {
            writer.Write(" in ");
            for (int i = 0; i < ranges.Count; i++)
            {
                writer.Write(i == 0 ? "0x" : ", 0x");
                writer.Write(ranges[i].Offset.ToString("X", CultureInfo.InvariantCulture));
                writer.Write(" ");
                writer.Write(ranges[i].Length.ToString(CultureInfo.InvariantCulture));
                writer.Write(" bytes");
            }
        }

        writer.Write("]");
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
