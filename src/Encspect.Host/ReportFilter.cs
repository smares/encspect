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
    Measurements = 16,
}

// Which parts of a report the writers leave out, by the kinds the parsers give their nodes, never
// by name, so an APE item named "Frame" stays. Writers ask before they visit a node, so a hidden
// frame's children are never built.
public sealed class ReportFilter
{
    public ReportFilter(ReportSections hidden)
    {
        Hidden = hidden;
    }

    public static ReportFilter All { get; } = new(ReportSections.None);

    public ReportSections Hidden { get; }

    // True for the JSON writers to write the measurements' spectrogram and waveform grids (--measure-grids); the text and CSV
    // never hold them.
    public bool WritesGrids { get; init; }

    public bool ShowsMeasurements => !Hidden.HasFlag(ReportSections.Measurements);

    // Section names as the command line takes them.
    public static IReadOnlyDictionary<string, ReportSections> Names { get; } = new Dictionary<string, ReportSections>(StringComparer.OrdinalIgnoreCase)
    {
        ["tags"] = ReportSections.Tags,
        ["frames"] = ReportSections.Frames,
        ["side-info"] = ReportSections.SideInfo,
        ["statistics"] = ReportSections.Statistics,
        ["measurements"] = ReportSections.Measurements,
    };

    public bool Includes(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);

        ReportSections section = node.Kind switch
        {
            NodeKind.Tag => ReportSections.Tags,
            NodeKind.Frame => ReportSections.Frames,
            NodeKind.SideInfo => ReportSections.SideInfo,
            NodeKind.Statistics => ReportSections.Statistics,
            _ => ReportSections.None,
        };
        return section == ReportSections.None || !Hidden.HasFlag(section);
    }
}
