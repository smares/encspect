using Encspect.Core;

namespace Encspect.Host;

// Serves the routes of one analysis (AnalysisContext.ParseStream, ParsePackets and ParseTag): finds
// the codec or tag parser, runs it inside the same exception boundary as the format parser, and
// merges its findings and streams into the context that asked (a tag's encoder claims too, but not a codec parser's, which belong to its stream). A failure inside a
// routed parser becomes a finding on the node the container gave it, so the container carries on.
internal sealed class ParserRouter(IReadOnlyList<ICodecParser> codecs, IReadOnlyList<ITagParser> tags, IReadOnlyList<IDecoder>? decoders = null, DecodeRun? run = null) : IParserRouter
{
    private readonly List<DecodeSession> _open = [];

    // Routes nest (a container hands a codec its packets, the codec a tag), but only a few levels;
    // a parser that routes in circles on crafted data must end in a finding, not a stack overflow.
    internal const int MaxDepth = 8;

    private int _depth;

    public bool ParseStream(AnalysisContext caller, string codecId, Node parent, ByteReader payload, CodecConfiguration? configuration, IReadOnlyList<EncoderClaim>? claims) =>
        RunCodec(caller, codecId, parent, () => new CodecContext(parent, payload, ClaimsFor(caller, configuration, claims), this, configuration));

    public bool Recognizes(AnalysisContext caller, string codecId, Node parent, ByteReader payload)
    {
        ICodecParser? codec = codecs.FirstOrDefault(c => c.Id == codecId);
        if (codec is null)
        {
            return false;
        }

        try
        {
            return codec.Recognizes(payload);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            return false;
        }
        catch (Exception ex) when (Analyzer.IsParserBug(ex))
        {
            caller.AddFinding(Severity.Error, "parse.failed", $"The {codecId} codec parser failed recognizing the data at 0x{parent.Offset:X}: {ex.GetType().Name}: {ex.Message}", parent);
            return false;
        }
    }

    public bool ParsePackets(AnalysisContext caller, string codecId, Node parent, IEnumerable<MappedBytes> packets, CodecConfiguration? configuration, IReadOnlyList<EncoderClaim>? claims) =>
        RunCodec(caller, codecId, parent, () => new CodecContext(parent, packets, caller.CancellationToken, ClaimsFor(caller, configuration, claims), this, configuration));

    // The file's claims, then those the container made about this stream alone.
    private static IReadOnlyList<EncoderClaim> ClaimsFor(AnalysisContext caller, CodecConfiguration? configuration, IReadOnlyList<EncoderClaim>? claims) =>
        configuration is { Claims.Count: > 0 } || claims is { Count: > 0 } ? [.. caller.EncoderClaims, .. configuration?.Claims ?? [], .. claims ?? []] : caller.EncoderClaims;

    public Node? ParseTag(AnalysisContext caller, string tagName, ByteReader slice, Node parent) =>
        RunTag(caller, tagName, parent, slice.Start, slice.End, parser => parser.ParseWhole(new TagProbeContext(slice)));

    public Node? ParseTag(AnalysisContext caller, string tagName, MappedBytes tag, Node parent) =>
        RunTag(caller, tagName, parent, tag.FileStart, tag.FileEnd, parser => parser.ParseWhole(tag, caller.CancellationToken));

    // The tag must start at start and end by end, the file range the container gave it.
    private Node? RunTag(AnalysisContext caller, string tagName, Node parent, long start, long end, Func<ITagParser, TagProbe?> parse)
    {
        ITagParser? parser = tags.FirstOrDefault(t => t.Name == tagName);
        if (parser is null || !Enter(caller, $"the {tagName} tag", parent))
        {
            return null;
        }

        TagProbe? probe;
        try
        {
            probe = parse(parser);
            if (probe?.Tag is { } outside && (outside.Offset != start || outside.End > end))
            {
                probe = TagProbe.Reject(probe.SignatureOffset, $"the parser returned a tag at 0x{outside.Offset:X} that does not fit [0x{start:X}, 0x{end:X})");
            }
        }
        catch (EndOfStreamException)
        {
            probe = TagProbe.Reject(start, "the data ends inside the tag");
        }
        catch (InvalidDataException ex)
        {
            probe = TagProbe.Reject(start, ex.Message);
        }
        catch (Exception ex) when (Analyzer.IsParserBug(ex))
        {
            caller.AddFinding(Severity.Error, "parse.failed", $"The {parser.Name} parser failed parsing a tag at 0x{start:X}: {ex.GetType().Name}: {ex.Message}", parent);
            probe = TagProbe.Reject(start, $"the parser failed with {ex.GetType().Name}");
        }
        finally
        {
            _depth--;
        }

        if (probe is null)
        {
            caller.AddFinding(Severity.Warning, "tag.not-found", $"No {parser.Name} tag starts at 0x{start:X}, where the container places one.", parent);
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

    public IDecodeSession StartDecoding(AnalysisContext caller, string codecId, DecodeSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(setup.Stream);

        IDecoder? decoder = decoders?.FirstOrDefault(d => d.Id == codecId);
        if (decoder is null)
        {
            if (run is not null)
            {
                caller.AddFinding(Severity.Info, "decode.no-decoder", $"Decoding was asked for, but no decoder handles {codecId}.", setup.Stream);
            }

            if (run is { IsMeasuring: true })
            {
                run.AddRecord(setup.Stream, new StreamMeasurements { Status = MeasurementStatus.NotPossible, Reason = $"no decoder for {Measurement.MeasurementFindings.DisplayName(codecId)}" });
            }

            return new DecodeSession(this, caller, null, codecId, setup, DecodeStatus.NoDecoder);
        }

        // A decoder with no checksum to compare decodes only for a front end that wants the samples (measurements, output); otherwise the audio is not read.
        if (run is null || (!decoder.DeclaresChecksums && !run.IsMeasuring && run.Options.Output is null))
        {
            return new DecodeSession(this, caller, null, codecId, setup, DecodeStatus.NotRequested);
        }

        // Figures a crafted file can state are not a bug of the parser: a stream that cannot be decoded is a finding.
        var session = new DecodeSession(this, caller, run, codecId, setup, DecodeStatus.Decoded);
        _open.Add(session);
        if (setup.Channels < 1 || setup.BitsPerSample is < 1 or > 64)
        {
            session.RefuseSetup();
        }
        else
        {
            session.Open(decoder);

            // Only a decoder that opened a stream raises the allowance; a refused or failed one does not.
            if (session.IsDecoding)
            {
                run.AllowPerInputByte(decoder.WorkPerInputByte);
            }
        }

        return session;
    }

    internal void Release(DecodeSession session) => _open.Remove(session);

    // Ends the sessions the owner's parser opened and did not finish, so what their decoders count once per stream joins the
    // owner's findings.
    public void EndOpenSessions(AnalysisContext owner)
    {
        foreach (DecodeSession session in _open.Where(s => s.Owner == owner).ToList())
        {
            session.EndUnfinished();
        }
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
            EndOpenSessions(context);
        }

        foreach (Finding finding in context.Findings)
        {
            caller.AddFinding(finding.Severity, finding.Code, finding.Message, finding.Node, finding.Count);
        }

        foreach (StreamInfo stream in context.Streams)
        {
            caller.AddStream(stream);
        }

        // The claims a codec parser makes (the comment of a Vorbis stream, say) are about its stream: they stay with it, so they do not weigh in
        // the codec parsers that run after it in the same container.
        return true;
    }

    // On success the caller must leave through a finally that decrements _depth.
    private bool Enter(AnalysisContext caller, string what, Node parent)
    {
        if (_depth >= MaxDepth)
        {
            caller.AddFinding(Severity.Error, "parse.too-deep", $"Parsers hand data on more than {Counts.Of(MaxDepth, "level", "levels")} deep at 0x{parent.Offset:X}, so {what} was not parsed.", parent);
            return false;
        }

        _depth++;
        return true;
    }
}
