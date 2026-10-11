using System.Text;
using Encspect.Core;
using Encspect.Host;

namespace Encspect.Cli;

internal static class CommandLine
{
    public const int Success = 0;
    public const int FindingsFound = 1;
    public const int UsageError = 2;
    public const int UnreadableFile = 3;

    public const string Usage = """
        Usage: encspect [options] <file-or-folder>...

        Analyzes each file, and every file in each folder and its subfolders.

        Output (text tree by default):
          --json                One JSON array of reports.
          --ndjson              One compact JSON report per line.
          --csv                 One summary row per file.
          --offsets             Show each node's byte offset and length in the text tree.
          --hide <sections>     Leave out sections of the text tree or JSON, comma-separated:
                                tags, frames, side-info, statistics.

        Selection:
          --only-format <names> Analyze only files of these formats, comma-separated, by the
                                start of the format's name (mpeg: MPEG Audio). Others are skipped.

        Exit code:
          --fail-on <severity>  Exit with 1 when a finding of this severity or worse is found:
                                info, warning or error.
          0 success, 1 findings as --fail-on says, 2 wrong usage, 3 a file could not be read.
        """;

    private enum Output
    {
        Text,
        Json,
        Ndjson,
        Csv,
    }

    public static int Run(IReadOnlyList<string> args, Stream stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        Output? output = null;
        bool offsets = false;
        ReportSections hidden = ReportSections.None;
        var formats = new List<string>();
        Severity? failOn = null;
        var paths = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string? inline = null;
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=', StringComparison.Ordinal) is > 2 and var equals)
            {
                inline = arg[(equals + 1)..];
                arg = arg[..equals];
            }

            switch (arg)
            {
                case "--json" or "--ndjson" or "--csv" when inline is null:
                    Output chosen = arg switch { "--json" => Output.Json, "--ndjson" => Output.Ndjson, _ => Output.Csv };
                    if (output is not null && output != chosen)
                    {
                        return Fail(stderr, "Choose one of --json, --ndjson and --csv.");
                    }

                    output = chosen;
                    break;
                case "--offsets" when inline is null:
                    offsets = true;
                    break;
                case "--hide":
                    if (Value(arg) is not { } sections)
                    {
                        return Fail(stderr, "--hide needs a list of sections.");
                    }

                    foreach (string name in List(sections))
                    {
                        if (!ReportFilter.Names.TryGetValue(name, out ReportSections section))
                        {
                            return Fail(stderr, $"Unknown section: {name}. Sections: {string.Join(", ", ReportFilter.Names.Keys)}.");
                        }

                        hidden |= section;
                    }

                    break;
                case "--only-format":
                    if (Value(arg) is not { } names)
                    {
                        return Fail(stderr, "--only-format needs a list of formats.");
                    }

                    formats.AddRange(List(names).Select(Normalize));
                    break;
                case "--fail-on":
                    failOn = Value(arg)?.ToLowerInvariant() switch
                    {
                        "info" => Severity.Info,
                        "warning" => Severity.Warning,
                        "error" => Severity.Error,
                        _ => null,
                    };
                    if (failOn is null)
                    {
                        return Fail(stderr, "--fail-on takes info, warning or error.");
                    }

                    break;
                case "--help" or "-h":
                    using (var help = new StreamWriter(stdout, new UTF8Encoding(false), leaveOpen: true))
                    {
                        help.Write(Usage);
                    }

                    return Success;
                default:
                    if (arg.StartsWith('-') && arg != "-" && !File.Exists(arg) && !Directory.Exists(arg))
                    {
                        return Fail(stderr, $"Unknown option: {args[i]}");
                    }

                    paths.Add(args[i]);
                    break;
            }

            // The value of an option, inline after "=" or as the next argument.
            string? Value(string option)
            {
                if (inline is not null)
                {
                    return inline.Length > 0 ? inline : null;
                }

                return i + 1 < args.Count ? args[++i] : null;
            }
        }

        if (paths.Count == 0)
        {
            return Fail(stderr, "Name at least one file or folder.");
        }

        var analyzer = Analyzer.CreateDefault();
        var filter = new ReportFilter(hidden);
        bool unreadable = false;
        Severity? worst = null;

        switch (output ?? Output.Text)
        {
            case Output.Json:
                ReportJsonWriter.Write(Reports(), stdout, filter);
                stdout.WriteByte((byte)'\n');
                break;
            case Output.Ndjson:
                ReportJsonWriter.WriteLines(Reports(), stdout, filter);
                break;
            case Output.Csv:
                using (var writer = new StreamWriter(stdout, new UTF8Encoding(false), 1 << 16, leaveOpen: true))
                {
                    ReportCsvWriter.Write(Reports(), writer);
                }

                break;
            default:
                // Console.Out flushes after every write, which costs a system call per node.
                using (var writer = new StreamWriter(stdout, new UTF8Encoding(false), 1 << 16, leaveOpen: true))
                {
                    foreach (Report report in Reports())
                    {
                        ReportTextWriter.Write(report, writer, offsets, filter);
                        writer.WriteLine();
                    }
                }

                break;
        }

        stdout.Flush();
        if (unreadable)
        {
            return UnreadableFile;
        }

        return failOn is { } threshold && worst >= threshold ? FindingsFound : Success;

        // Each report is written as soon as its file is analyzed and is then dropped, so memory
        // does not grow with the number of files.
        IEnumerable<Report> Reports()
        {
            foreach (string file in ExpandPaths(paths))
            {
                Report? report = null;
                try
                {
                    if (formats.Count > 0 && !(analyzer.DetectFormat(file) is { } format && formats.Any(Normalize(format).StartsWith)))
                    {
                        continue;
                    }

                    report = analyzer.AnalyzeFile(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    stderr.WriteLine($"{file}: {ex.Message}");
                    unreadable = true;
                }

                if (report is not null)
                {
                    foreach (Finding finding in report.Findings)
                    {
                        worst = worst is null || finding.Severity > worst ? finding.Severity : worst;
                    }

                    yield return report;
                }
            }
        }
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine(message);
        stderr.WriteLine("Run encspect --help for the options.");
        return UsageError;
    }

    private static IEnumerable<string> List(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // "MPEG Audio" and "mpeg" compare as "mpegaudio" and "mpeg".
    private static string Normalize(string name) => name.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

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
