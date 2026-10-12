using System.Reflection;
using Encspect.Host;

namespace Encspect.Gui.Cache;

// Which analyzer made a cached summary. The host's informational version names the commit it was
// built from ("1.0.0+<commit>", which the SDK adds from git), so a build of another commit misses
// the cache. A build of uncommitted changes keeps the commit's version, so the module version IDs
// of the host's and the parsers' assemblies are added: builds are deterministic, so an assembly's
// ID changes exactly when its code does. The GUI's own assembly is among them, since it derives
// parts of the summary (FileSummary) from the report.
public static class AnalyzerVersion
{
    public static string Current { get; } = Compute();

    private static string Compute()
    {
        IEnumerable<Assembly> assemblies = new[] { typeof(Analyzer).Assembly, typeof(Core.Node).Assembly, typeof(ViewModels.FileSummary).Assembly }
            .Concat(DefaultParsers.Tags.Select(p => p.GetType().Assembly))
            .Concat(DefaultParsers.Formats.Select(p => p.GetType().Assembly))
            .Concat(DefaultParsers.Codecs.Select(p => p.GetType().Assembly))
            .Distinct()
            .OrderBy(a => a.GetName().Name, StringComparer.Ordinal);
        return ReportJsonWriter.ToolVersion + " " + string.Join(" ", assemblies.Select(a => a.ManifestModule.ModuleVersionId.ToString("N")));
    }
}
