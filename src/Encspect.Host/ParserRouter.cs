using Encspect.Core;

namespace Encspect.Host;

// Serves the routes of one analysis (AnalysisContext.ParseStream, ParsePackets and ParseTag): finds
// the codec or tag parser, runs it inside the same exception boundary as the format parser, and
// merges its findings, streams and encoder claims into the context that asked. A failure inside a
// routed parser becomes a finding on the node the container gave it, so the container carries on.
internal sealed class ParserRouter(IReadOnlyList<ICodecParser> codecs, IReadOnlyList<ITagParser> tags) : IParserRouter
{
    // Routes nest (a container hands a codec its packets, the codec a tag), but only a few levels;
    // a parser that routes in circles on crafted data must end in a finding, not a stack overflow.
    internal const int MaxDepth = 8;

    private int _depth;

    public bool ParseStream(AnalysisContext caller, string codecId, Node parent, ByteReader payload, CodecConfiguration? configuration) =>
        RunCodec(caller, codecId, parent, () => new CodecContext(parent, payload, caller.EncoderClaims, this, configuration));

    public bool ParsePackets(AnalysisContext caller, string codecId, Node parent, IEnumerable<MappedBytes> packets, CodecConfiguration? configuration) =>
        RunCodec(caller, codecId, parent, () => new CodecContext(parent, packets, caller.CancellationToken, caller.EncoderClaims, this, configuration));

    public Node? ParseTag(AnalysisContext caller, string tagName, ByteReader slice, Node parent)
    {
        ITagParser? parser = tags.FirstOrDefault(t => t.Name == tagName);
        if (parser is null || !Enter(caller, $"the {tagName} tag", parent))
        {
            return null;
        }

        TagProbe? probe;
        try
        {
            probe = parser.ParseWhole(new TagProbeContext(slice));
            if (probe?.Tag is { } outside && (outside.Offset != slice.Start || outside.End > slice.End))
            {
                probe = TagProbe.Reject(probe.SignatureOffset, $"the parser returned a tag at 0x{outside.Offset:X} that does not fit [0x{slice.Start:X}, 0x{slice.End:X})");
            }
        }
        catch (EndOfStreamException)
        {
            probe = TagProbe.Reject(slice.Start, "the data ends inside the tag");
        }
        catch (InvalidDataException ex)
        {
            probe = TagProbe.Reject(slice.Start, ex.Message);
        }
        catch (Exception ex) when (Analyzer.IsParserBug(ex))
        {
            caller.AddFinding(Severity.Error, "parse.failed", $"The {parser.Name} parser failed parsing a tag at 0x{slice.Start:X}: {ex.GetType().Name}: {ex.Message}", parent);
            probe = TagProbe.Reject(slice.Start, $"the parser failed with {ex.GetType().Name}");
        }
        finally
        {
            _depth--;
        }

        if (probe is null)
        {
            caller.AddFinding(Severity.Warning, "tag.not-found", $"No {parser.Name} tag starts at 0x{slice.Start:X}, where the container places one.", parent);
            return null;
        }

        if (probe.Tag is not { } tag)
        {
            caller.AddFinding(Severity.Warning, "tag.rejected", $"{parser.Name} candidate at 0x{probe.SignatureOffset:X} rejected: {probe.RejectionReason}.", parent);
            return null;
        }

        Node shown = Analyzer.ShowTag(tag, Analyzer.Effective(probe), probe.Evidence, out _);
        parent.Add(shown);
        foreach (Finding finding in probe.Findings)
        {
            caller.AddFinding(finding.Severity, finding.Code, finding.Message, finding.Node == tag ? shown : finding.Node, finding.Count);
        }

        foreach (EncoderClaim claim in probe.EncoderClaims)
        {
            caller.AddEncoderClaim(claim);
        }

        return shown;
    }

    private bool RunCodec(AnalysisContext caller, string codecId, Node parent, Func<CodecContext> create)
    {
        ICodecParser? codec = codecs.FirstOrDefault(c => c.Id == codecId);
        if (codec is null)
        {
            return false;
        }

        CodecContext context = create();
        if (!Enter(caller, $"the {codecId} data", parent))
        {
            return true;
        }

        try
        {
            codec.Parse(context);
        }
        catch (EndOfStreamException)
        {
            context.AddFinding(Severity.Error, "parse.truncated", $"The {codecId} data at 0x{parent.Offset:X} ended in the middle of a structure.", parent);
        }
        catch (InvalidDataException ex)
        {
            context.AddFinding(Severity.Error, "parse.invalid", ex.Message, parent);
        }
        catch (Exception ex) when (Analyzer.IsParserBug(ex))
        {
            context.AddFinding(Severity.Error, "parse.failed", $"The {codecId} codec parser failed parsing the data at 0x{parent.Offset:X}: {ex.GetType().Name}: {ex.Message}", parent);
        }
        finally
        {
            _depth--;
        }

        foreach (Finding finding in context.Findings)
        {
            caller.AddFinding(finding.Severity, finding.Code, finding.Message, finding.Node, finding.Count);
        }

        foreach (StreamInfo stream in context.Streams)
        {
            caller.AddStream(stream);
        }

        foreach (EncoderClaim claim in context.EncoderClaims.Except(caller.EncoderClaims))
        {
            caller.AddEncoderClaim(claim);
        }

        return true;
    }

    // On success the caller must leave through a finally that decrements _depth.
    private bool Enter(AnalysisContext caller, string what, Node parent)
    {
        if (_depth >= MaxDepth)
        {
            caller.AddFinding(Severity.Error, "parse.too-deep", $"Parsers hand data on more than {MaxDepth} levels deep at 0x{parent.Offset:X}, so {what} was not parsed.", parent);
            return false;
        }

        _depth++;
        return true;
    }
}
