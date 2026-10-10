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
        int exitCode = 0;

        using Stream stdout = Console.OpenStandardOutput();
        if (json)
        {
            ReportJsonWriter.Write(Reports(), stdout);
            stdout.WriteByte((byte)'\n');
        }
        else
        {
            // Console.Out flushes after every write, which costs a system call per node.
            using var output = new StreamWriter(stdout, Console.OutputEncoding, 1 << 16);
            foreach (Report report in Reports())
            {
                ReportTextWriter.Write(report, output, offsets);
                output.WriteLine();
            }
        }

        return exitCode;

        // Each report is written as soon as its file is analyzed and is then dropped, so memory
        // does not grow with the number of files.
        IEnumerable<Report> Reports()
        {
            foreach (string file in ExpandPaths(paths))
            {
                Report? report = null;
                try
                {
                    report = analyzer.AnalyzeFile(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"{file}: {ex.Message}");
                    exitCode = 1;
                }

                if (report is not null)
                {
                    yield return report;
                }
            }
        }
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
