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
    public const int OutputFailed = 4;

    public const string Usage = """
        Usage: encspect [options] [--] <file-or-folder>...
               encspect --learn [<topic>]
               encspect --version

        Analyzes each file, and every file in each folder and its subfolders, hidden ones
        included. A link to a folder is followed only when it leads outside the folder named and
        not above it, so each folder is analyzed once. Anything that is not a regular file, such
        as a pipe or a device, is reported without being read. Standard input is not read.
        After --, every argument is a file or folder, even one that starts with a dash.

        Output (text tree by default):
          --json                One JSON array of reports.
          --ndjson              One compact JSON report per line.
          --csv                 One summary row per file.
          --offsets             Show the byte offset and length of each node and finding in the
                                text tree. Not with --json or --ndjson, which always have them,
                                or --csv.
          --hide <sections>     Leave out sections of the text tree or JSON, comma-separated:
                                tags, frames, side-info, statistics, measurements. Not with --csv.
          --explain             Say what each node is: its description, and the topic that
                                --learn explains, under each node of the text tree, or as
                                "description" and "topic" in JSON. Not with --csv.

        Verification:
          --verify              Decode each file's audio while reading it and compare the
                                checksums that cover decoded audio, for the codings that have a
                                decoder. Slower than the default analysis, which compares only the
                                checksums over coded data. A stream whose coding has no decoder
                                says so. With --csv, fills Is Verified.

        Measurements:
          --measure [<kinds>]   Decode, verify and measure each file's audio, for the codings
                                that have a decoder: the average spectrum and the spectral
                                cutoff, the levels (sample peak, RMS, DC offset, clipping,
                                digital silence, bit depth), the loudness (integrated loudness,
                                loudness range, maximum momentary and short-term loudness) and
                                the true peak. Kinds, comma-separated: spectrum, levels,
                                loudness, true-peak. Costs more than --verify. With --csv, fills
                                Is Verified, Spectral Cutoff, Sample Peak, Maximum True Peak
                                Level, Integrated Loudness and Loudness Range.
          --measure-grids       As --measure, and write the spectrogram and waveform grids into
                                the JSON. The text and CSV never hold them.

        Version:
          --version             Print the version, as the JSON reports give it in tool.version.

        Background:
          --learn [<topic>]     Print the article on a topic, such as mid-side-stereo: why a
                                coding tool exists and what seeing it in a file means. Without
                                a topic, list the topics.

        Selection:
          --only-format <names> Analyze only files of these formats, comma-separated, by the
                                start of the format's name (mpeg: MPEG Audio). Others are skipped.
                                Not with --as.
          --as <format>         Skip format detection and analyze every file as this format, by the
                                start of its name or a name it goes by (ac3, mp3, m4a), for a file
                                detection misses or gets wrong. The report says it was forced and
                                what detection would have chosen.

        Exit code:
          --fail-on <severity>  Exit with 1 when a finding of this severity or worse is found:
                                info, warning or error.
          0 success, 1 findings as --fail-on says, 2 wrong usage, 3 a file or folder could not be
          read or is not a regular file, 4 the output could not be written.
        """;

    private enum Output
    {
        Text,
        Json,
        Ndjson,
        Csv,
    }

    // analyzerOverride: the analyzer to run, for tests; the default one otherwise.
    public static int Run(IReadOnlyList<string> args, Stream stdout, TextWriter stderr, Analyzer? analyzerOverride = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        Output? output = null;
        bool offsets = false;
        bool explain = false;
        bool verify = false;
        bool measure = false;
        bool grids = false;
        MeasurementKinds? kinds = null;
        ReportSections hidden = ReportSections.None;
        var formats = new List<string>();
        string? forcedFormat = null;
        Analyzer analyzer = analyzerOverride ?? Analyzer.CreateDefault();
        Severity? failOn = null;
        var paths = new List<string>();
        bool optionsEnded = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "-")
            {
                return Fail(stderr, "Reading standard input (-) is not supported; name a file, as ./- for a file named -.");
            }

            if (optionsEnded)
            {
                paths.Add(arg);
                continue;
            }

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
                case "--verify" when inline is null:
                    verify = true;
                    break;
                case "--measure":
                    measure = true;

                    // The kinds are the next argument if it names only kinds (and is no existing path); otherwise it is a path.
                    string? kindList = inline ?? (i + 1 < args.Count && !args[i + 1].StartsWith('-') && !IsPath(args[i + 1]) && List(args[i + 1]).All(k => MeasurementNames.ContainsKey(k)) ? args[++i] : null);
                    if (kindList is not null)
                    {
                        MeasurementKinds picked = MeasurementKinds.None;
                        foreach (string name in List(kindList))
                        {
                            if (!MeasurementNames.TryGetValue(name, out MeasurementKinds kind))
                            {
                                return Fail(stderr, $"Unknown measurement: {name}. Measurements: {string.Join(", ", MeasurementNames.Keys)}.");
                            }

                            picked |= kind;
                        }

                        if (picked == MeasurementKinds.None)
                        {
                            return Fail(stderr, "--measure needs a list of measurements, or none.");
                        }

                        kinds = picked;
                    }

                    break;
                case "--measure-grids" when inline is null:
                    measure = true;
                    grids = true;
                    break;
                case "--explain" when inline is null:
                    explain = true;
                    break;
                case "--learn":
                    // The topic is the next argument unless it is another option.
                    string? topic = inline ?? (i + 1 < args.Count && !args[i + 1].StartsWith('-') ? args[++i] : null);
                    if (i + 1 < args.Count || args.Count > (topic is null || inline is not null ? 1 : 2))
                    {
                        return Fail(stderr, "--learn takes a topic and no other arguments.");
                    }

                    return Learn(topic, stdout, stderr);
                case "--":
                    optionsEnded = true;
                    break;
                case "--hide":
                    if (Value(arg) is not { } sections || !List(sections).Any())
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
                    if (Value(arg) is not { } names || !List(names).Any())
                    {
                        return Fail(stderr, "--only-format needs a list of formats.");
                    }

                    foreach (string name in List(names))
                    {
                        IReadOnlyList<string> found = analyzer.MatchFormats(name);
                        if (found.Count == 0)
                        {
                            return Fail(stderr, IsPath(name) ? $"--only-format needs a list of formats before the files and folders; {name} is a path." : $"Unknown format: {name}. Formats: {string.Join(", ", KnownFormats)}.");
                        }

                        formats.AddRange(found);
                    }

                    break;
                case "--as":
                    if (Value(arg) is not { } wanted)
                    {
                        return Fail(stderr, "--as needs a format.");
                    }

                    IReadOnlyList<string> matches = analyzer.MatchFormats(wanted);
                    if (matches.Count != 1)
                    {
                        return Fail(stderr, matches.Count == 0
                            ? IsPath(wanted) ? $"--as needs a format before the files and folders; {wanted} is a path." : $"Unknown format: {wanted}. Formats: {string.Join(", ", KnownFormats)}."
                            : $"Ambiguous format: {wanted} starts {string.Join(", ", matches)}. Name one of them.");
                    }

                    forcedFormat = matches[0];
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
                case "--version" when inline is null:
                    if (args.Count != 1)
                    {
                        return Fail(stderr, "--version takes no other arguments.");
                    }

                    return Write(new GuardedStream(stdout), stderr, stream =>
                    {
                        using var version = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                        version.Write("encspect " + ReportJsonWriter.ToolVersion + "\n");
                    });
                case "--help" or "-h":
                    return Write(new GuardedStream(stdout), stderr, stream =>
                    {
                        using var help = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                        help.Write(Usage);
                    });
                default:
                    if (arg.StartsWith('-') && !File.Exists(arg) && !Directory.Exists(arg))
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

        if (forcedFormat is not null && formats.Count > 0)
        {
            return Fail(stderr, "--as and --only-format cannot be combined: --only-format selects files by the format detection finds, and --as skips detection.");
        }

        Output chosenOutput = output ?? Output.Text;
        if (offsets && chosenOutput != Output.Text)
        {
            return Fail(stderr, chosenOutput == Output.Csv ? "--offsets is for the text tree; CSV has no offsets." : "--offsets is for the text tree; JSON always has offsets.");
        }

        if (hidden != ReportSections.None && chosenOutput == Output.Csv)
        {
            return Fail(stderr, "--hide does not apply to --csv, which writes one summary row per file.");
        }

        if (explain && chosenOutput == Output.Csv)
        {
            return Fail(stderr, "--explain is for the text tree and JSON; CSV has one summary row per file.");
        }

        if (grids && chosenOutput is Output.Text or Output.Csv)
        {
            return Fail(stderr, "--measure-grids is for the JSON; the text and CSV never hold the grids.");
        }

        if (grids && kinds is { } chosenKinds && !chosenKinds.HasFlag(MeasurementKinds.Spectrum))
        {
            return Fail(stderr, "--measure-grids needs the spectrum measurement, which holds the grids; add spectrum to --measure.");
        }

        var filter = new ReportFilter(hidden) { WritesGrids = grids };
        bool unreadable = false;
        Severity? worst = null;

        int written = Write(new GuardedStream(stdout), stderr, stream =>
        {
            switch (chosenOutput)
            {
                case Output.Json:
                    ReportJsonWriter.Write(Reports(), stream, filter, explain);
                    stream.WriteByte((byte)'\n');
                    break;
                case Output.Ndjson:
                    ReportJsonWriter.WriteLines(Reports(), stream, filter, explain);
                    break;
                case Output.Csv:
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true))
                    {
                        ReportCsvWriter.Write(Reports(), writer, verify || measure);
                    }

                    break;
                default:
                    // Console.Out flushes after every write, which costs a system call per node.
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true))
                    {
                        foreach (Report report in Reports())
                        {
                            ReportTextWriter.Write(report, writer, offsets, filter, explain);
                            writer.WriteLine();
                        }
                    }

                    break;
            }
        });

        if (written != Success)
        {
            return written;
        }

        if (unreadable)
        {
            return UnreadableFile;
        }

        return failOn is { } threshold && worst >= threshold ? FindingsFound : Success;

        // Each report is written as soon as its file is analyzed and is then dropped, so memory
        // does not grow with the number of files. Inputs that are not analyzed still get a report,
        // so batch outputs list every one, and a line on stderr.
        IEnumerable<Report> Reports()
        {
            foreach (Input input in InputWalker.Expand(paths))
            {
                if (Analyze(input) is not { } report)
                {
                    continue;
                }

                if (FileReports.IsNotAnalyzed(report))
                {
                    stderr.WriteLine($"{input.Path}: {report.Findings[0].Message}");
                    unreadable = true;
                }

                foreach (Finding finding in report.Findings)
                {
                    worst = worst is null || finding.Severity > worst ? finding.Severity : worst;
                }

                yield return report;
            }
        }

        // The input's report, or null when --only-format skips it.
        Report? Analyze(Input input)
        {
            if (input.FolderError is { } error)
            {
                return FileReports.UnreadableFolder(input.Path, error);
            }

            try
            {
                // What is not a regular file has no format to match and is reported, like a file
                // that cannot be read.
                if (formats.Count > 0 && FileReports.NotRegular(input.Path) is null
                    && !(analyzer.DetectFormat(input.Path) is { } format && formats.Contains(format)))
                {
                    return null;
                }

                return analyzer.AnalyzeFile(input.Path, forcedFormat is null && !verify && !measure ? null : new AnalysisOptions { ForcedFormat = forcedFormat, Decoding = verify || measure ? new DecodeOptions { Measure = measure ? new MeasureOptions { Include = kinds } : null } : null });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return FileReports.Unreadable(input.Path, ex.Message);
            }
        }
    }

    // Runs what writes to stdout. A failed write, such as to a full disk, ends the run with one
    // line on stderr. A closed pipe, as with "| head", never gets here: .NET's console stream
    // ignores EPIPE.
    private static int Write(GuardedStream output, TextWriter stderr, Action<Stream> write)
    {
        try
        {
            write(output);
            output.Flush();
            return Success;
        }
        catch (GuardedStream.WriteFailedException ex)
        {
            stderr.WriteLine($"Could not write the output: {ex.Message}");
            return OutputFailed;
        }
    }

    // Prints one topic's article, or the list of topics.
    private static int Learn(string? id, Stream stdout, TextWriter stderr)
    {
        Topic? topic = null;
        if (id is not null && (topic = TopicLibrary.Find(id)) is null)
        {
            return Fail(stderr, $"Unknown topic: {id}. Run encspect --learn for the list.");
        }

        return Write(new GuardedStream(stdout), stderr, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            if (topic is not null)
            {
                writer.Write(TopicLibrary.ToPlainText(topic).ReplaceLineEndings());
                return;
            }

            writer.WriteLine("Topics (encspect --learn <topic>):");
            int width = TopicLibrary.All.Max(t => t.Id.Length);
            foreach (Topic each in TopicLibrary.All)
            {
                writer.WriteLine($"  {each.Id.PadRight(width)}  {each.Title}");
            }
        });
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine(message);
        stderr.WriteLine("Run encspect --help for the options.");
        return UsageError;
    }

    private static readonly Dictionary<string, MeasurementKinds> MeasurementNames = new(StringComparer.OrdinalIgnoreCase) { ["spectrum"] = MeasurementKinds.Spectrum, ["levels"] = MeasurementKinds.Levels, ["loudness"] = MeasurementKinds.Loudness, ["true-peak"] = MeasurementKinds.TruePeak };

    private static IEnumerable<string> KnownFormats => DefaultParsers.Formats.Select(p => p.Name);

    // A value that is an existing file or folder is the path the option swallowed because its own value was left out.
    private static bool IsPath(string value) => File.Exists(value) || Directory.Exists(value);

    private static IEnumerable<string> List(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
