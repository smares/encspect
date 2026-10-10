using Encspect.Core;

namespace Encspect.Host;

public static class ReportTextWriter
{
    public static void Write(Report report, TextWriter writer, bool showOffsets)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine(report.Source);
        WriteNode(report.Root, writer, showOffsets, 0);

        if (report.Findings.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Findings:");
            foreach (Finding finding in report.Findings)
            {
                writer.WriteLine($"  [{finding.Severity}] {finding.Code}: {finding.Message}");
            }
        }
    }

    private static void WriteNode(Node node, TextWriter writer, bool showOffsets, int depth)
    {
        string indent = new(' ', (depth + 1) * 2);
        string value = node.Value is null ? string.Empty : $": {node.Value}";
        string location = showOffsets ? $" [0x{node.Offset:X}, {node.Length} bytes]" : string.Empty;

        writer.WriteLine($"{indent}{node.Name}{value}{location}");

        foreach (Node child in node.Children)
        {
            WriteNode(child, writer, showOffsets, depth + 1);
        }
    }
}
