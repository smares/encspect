using Encspect.Core;

namespace Encspect.Host;

public sealed class Analyzer
{
    private readonly IReadOnlyList<IFormatParser> _parsers;

    public Analyzer(IEnumerable<IFormatParser> parsers)
    {
        ArgumentNullException.ThrowIfNull(parsers);

        _parsers = parsers.ToList();
    }

    public Report AnalyzeFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Analyze(path, stream);
    }

    public Report Analyze(string source, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stream);

        var reader = new ByteReader(stream);
        var root = new Node("File", 0, reader.Length);
        var context = new ParseContext(reader, root);

        IFormatParser? parser = FindParser(reader);
        if (parser is null)
        {
            context.AddFinding(Severity.Info, "format.unrecognized", "No format parser recognized this file.", root);
        }
        else
        {
            root.Value = parser.Name;

            try
            {
                parser.Parse(context);
            }
            catch (EndOfStreamException)
            {
                context.AddFinding(Severity.Error, "parse.truncated", "The file ended in the middle of a structure.", root);
            }
            catch (InvalidDataException ex)
            {
                context.AddFinding(Severity.Error, "parse.invalid", ex.Message, root);
            }
        }

        return new Report(source, root, context.Findings);
    }

    private IFormatParser? FindParser(ByteReader reader)
    {
        IFormatParser? best = null;
        int bestConfidence = 0;

        foreach (IFormatParser parser in _parsers)
        {
            reader.Position = 0;

            int confidence;
            try
            {
                confidence = parser.Sniff(reader);
            }
            catch (EndOfStreamException)
            {
                confidence = 0;
            }

            if (confidence > bestConfidence)
            {
                best = parser;
                bestConfidence = confidence;
            }
        }

        reader.Position = 0;
        return best;
    }
}
