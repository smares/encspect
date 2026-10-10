using Encspect.Core;
using Encspect.Host;

namespace Encspect.Cli;

internal static class Program
{
    private const string Usage = "Usage: encspect [--json] [--offsets] <file-or-folder>...";

    private static int Main(string[] args)
    {
        bool json = false;
        bool offsets = false;
        var paths = new List<string>();

        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--json":
                    json = true;
                    break;
                case "--offsets":
                    offsets = true;
                    break;
                case "--help":
                case "-h":
                    Console.Out.WriteLine(Usage);
                    return 0;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"Unknown option: {arg}");
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }

                    paths.Add(arg);
                    break;
            }
        }

        if (paths.Count == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var analyzer = Analyzer.CreateDefault();
        var reports = new List<Report>();
        int exitCode = 0;

        foreach (string file in ExpandPaths(paths))
        {
            try
            {
                reports.Add(analyzer.AnalyzeFile(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"{file}: {ex.Message}");
                exitCode = 1;
            }
        }

        if (json)
        {
            using Stream stdout = Console.OpenStandardOutput();
            ReportJsonWriter.Write(reports, stdout);
            stdout.WriteByte((byte)'\n');
        }
        else
        {
            foreach (Report report in reports)
            {
                ReportTextWriter.Write(report, Console.Out, offsets);
                Console.Out.WriteLine();
            }
        }

        return exitCode;
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                IEnumerable<string> files = Directory.EnumerateFiles(path, "*", options);
                foreach (string file in files.Order(StringComparer.Ordinal))
                {
                    yield return file;
                }
            }
            else
            {
                yield return path;
            }
        }
    }
}
