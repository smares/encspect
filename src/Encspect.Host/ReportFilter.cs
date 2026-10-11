using Encspect.Core;

namespace Encspect.Host;

[Flags]
public enum ReportSections
{
    None = 0,
    Tags = 1,
    Frames = 2,
    SideInfo = 4,
    Statistics = 8,
}

// Which parts of a report the writers leave out. Sections are recognized by the names the parsers
// give their nodes: a tag is a top-level node named after a tag parser, a frame is a "Frame" node,
// side information is Layer III's "Side Info" and Layer I and II's bit allocation, scfsi and
// scalefactors inside a frame, and statistics are a "Statistics" node. Writers ask before they
// visit a node, so a hidden frame's children are never built.
public sealed class ReportFilter
{
    private static readonly HashSet<string> TagNames = DefaultParsers.Tags.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> SideInfoNames = new(["Side Info", "Bit Allocation", "SCFSI", "Scalefactors"], StringComparer.Ordinal);

    public ReportFilter(ReportSections hidden)
    {
        Hidden = hidden;
    }

    public static ReportFilter All { get; } = new(ReportSections.None);

    public ReportSections Hidden { get; }

    // Section names as the command line takes them.
    public static IReadOnlyDictionary<string, ReportSections> Names { get; } = new Dictionary<string, ReportSections>(StringComparer.OrdinalIgnoreCase)
    {
        ["tags"] = ReportSections.Tags,
        ["frames"] = ReportSections.Frames,
        ["side-info"] = ReportSections.SideInfo,
        ["statistics"] = ReportSections.Statistics,
    };

    public bool Includes(Node node, Node? parent)
    {
        ArgumentNullException.ThrowIfNull(node);

        return Hidden switch
        {
            ReportSections.None => true,
            _ when Hidden.HasFlag(ReportSections.Tags) && parent is { Name: "File" } && TagNames.Contains(node.Name) => false,
            _ when Hidden.HasFlag(ReportSections.Frames) && node.Name == "Frame" => false,
            _ when Hidden.HasFlag(ReportSections.SideInfo) && parent is { Name: "Frame" } && SideInfoNames.Contains(node.Name) => false,
            _ when Hidden.HasFlag(ReportSections.Statistics) && node.Name == "Statistics" => false,
            _ => true,
        };
    }
}
