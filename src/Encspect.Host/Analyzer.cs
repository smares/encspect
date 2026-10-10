using Encspect.Core;

namespace Encspect.Host;

public sealed class Analyzer
{
    private readonly IReadOnlyList<ITagParser> _tagParsers;
    private readonly IReadOnlyList<IFormatParser> _formatParsers;

    public Analyzer(IEnumerable<IFormatParser> formatParsers)
        : this([], formatParsers)
    {
    }

    // Tag parsers are probed in the given order at every anchor, and the first one that
    // validates owns the bytes; see DefaultParsers for the order the plan requires.
    public Analyzer(IEnumerable<ITagParser> tagParsers, IEnumerable<IFormatParser> formatParsers)
    {
        ArgumentNullException.ThrowIfNull(tagParsers);
        ArgumentNullException.ThrowIfNull(formatParsers);

        _tagParsers = tagParsers.ToList();
        _formatParsers = formatParsers.ToList();
    }

    public static Analyzer CreateDefault() => new(DefaultParsers.Tags, DefaultParsers.Formats);

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
        var findings = new List<Finding>();
        var pending = new List<PendingFinding>();

        if (reader.Length == 0)
        {
            findings.Add(new Finding(Severity.Info, "file.empty", "The file is empty.", root));
            return new Report(source, root, findings);
        }

        List<Claim> leading = FindLeadingTags(reader, pending);
        long dataStart = leading.Count > 0 ? leading[^1].Tag.End : 0;
        List<Claim> trailing = FindTrailingTags(reader, dataStart, leading, pending);
        long dataEnd = trailing.Count > 0 ? trailing[0].Tag.Offset : reader.Length;

        var detectionNodes = new HashSet<Node>();
        foreach (Claim claim in leading)
        {
            AddClaim(root, claim, findings, detectionNodes);
        }

        if (dataEnd > dataStart)
        {
            ParseData(reader.Slice(dataStart, dataEnd - dataStart), root, findings);
        }
        else
        {
            findings.Add(new Finding(Severity.Info, "layout.tags-only", "The file holds only tags; no bytes are left for audio data.", root));
        }

        foreach (Claim claim in trailing)
        {
            AddClaim(root, claim, findings, detectionNodes);
        }

        CheckTrailingLayout(trailing, findings);

        foreach (PendingFinding finding in pending)
        {
            Node node = DeepestNodeAt(root, finding.Offset, detectionNodes);
            findings.Add(new Finding(finding.Severity, finding.Code, finding.Message(node, root), node));
        }

        return new Report(source, root, findings);
    }

    private List<Claim> FindLeadingTags(ByteReader reader, List<PendingFinding> pending)
    {
        var claims = new List<Claim>();
        long position = 0;

        while (position < reader.Length)
        {
            Claim? accepted = null;
            foreach (ITagParser parser in _tagParsers)
            {
                TagProbe? probe = Probe(parser, position, reader, leading: true);
                if (probe is null)
                {
                    continue;
                }

                if (probe.IsAccepted)
                {
                    accepted = new Claim(parser, probe);
                    break;
                }

                pending.Add(Rejected(parser, probe, reader));
            }

            if (accepted is null)
            {
                break;
            }

            claims.Add(accepted);
            position = accepted.Tag.End;
        }

        return claims;
    }

    // Walks backward from the end of the file: at each anchor every parser is probed in
    // priority order, the first validated tag that does not reach into the leading tags wins,
    // and its start becomes the next anchor. Every anchor moves strictly toward the start.
    private List<Claim> FindTrailingTags(ByteReader reader, long dataStart, List<Claim> leading, List<PendingFinding> pending)
    {
        var claims = new List<Claim>();
        long anchor = reader.Length;

        while (anchor > 0)
        {
            Claim? winner = null;
            var others = new List<(ITagParser Parser, TagProbe Probe)>();

            foreach (ITagParser parser in _tagParsers)
            {
                TagProbe? probe = Probe(parser, anchor, reader, leading: false);
                if (probe is null)
                {
                    continue;
                }

                if (winner is null && probe.IsAccepted && probe.Tag!.Offset >= dataStart)
                {
                    winner = new Claim(parser, probe);
                }
                else
                {
                    others.Add((parser, probe));
                }
            }

            foreach ((ITagParser parser, TagProbe probe) in others)
            {
                bool overlapsLeading = probe.IsAccepted && probe.Tag!.Offset < dataStart;
                if (winner is not null || overlapsLeading)
                {
                    Claim? owner = winner ?? leading.LastOrDefault(c => c.Tag.Offset <= probe.SignatureOffset && probe.SignatureOffset < c.Tag.End);
                    pending.Add(NearMiss(parser, probe, owner, reader));
                }
                else
                {
                    pending.Add(Rejected(parser, probe, reader));
                }
            }

            if (winner is null)
            {
                break;
            }

            claims.Insert(0, winner);
            anchor = winner.Tag.Offset;
        }

        return claims;
    }

    private static TagProbe? Probe(ITagParser parser, long anchor, ByteReader reader, bool leading)
    {
        try
        {
            TagProbe? probe = leading ? parser.ProbeAt(reader, anchor) : parser.ProbeBefore(reader, anchor);

            // Every claim must touch its anchor, or the anchor walks would not make progress.
            if (probe?.Tag is { } tag && (leading ? tag.Offset != anchor : tag.End != anchor))
            {
                return TagProbe.Reject(probe.SignatureOffset, $"the parser returned a tag at 0x{tag.Offset:X} that is not anchored at 0x{anchor:X}");
            }

            return probe;
        }
        catch (EndOfStreamException)
        {
            return TagProbe.Reject(anchor, "the file ends inside the tag");
        }
        catch (InvalidDataException ex)
        {
            return TagProbe.Reject(anchor, ex.Message);
        }
    }

    private static PendingFinding Rejected(ITagParser parser, TagProbe probe, ByteReader reader)
    {
        string where = Describe(probe.SignatureOffset, reader);
        return new PendingFinding(
            Severity.Warning,
            "tag.rejected",
            probe.SignatureOffset,
            (_, _) => $"{parser.Name} candidate at {where} rejected: {probe.RejectionReason}.");
    }

    private static PendingFinding NearMiss(ITagParser parser, TagProbe probe, Claim? owner, ByteReader reader)
    {
        string where = Describe(probe.SignatureOffset, reader);
        return new PendingFinding(
            Severity.Info,
            "tag.near-miss",
            probe.SignatureOffset,
            (node, root) =>
            {
                if (owner is not null && owner.Tag.Offset <= probe.SignatureOffset && probe.SignatureOffset < owner.Tag.End)
                {
                    return $"{parser.Name} signature at {where} rejected: part of {PathTo(root, node)}.";
                }

                string ownerName = owner is null ? "another tag" : $"the {owner.Parser.Name} tag at 0x{owner.Tag.Offset:X}";
                return $"{parser.Name} signature at {where} rejected: the candidate overlaps {ownerName}.";
            });
    }

    private static string Describe(long offset, ByteReader reader) => $"0x{offset:X} (EOF-{reader.Length - offset})";

    private static void AddClaim(Node root, Claim claim, List<Finding> findings, HashSet<Node> detectionNodes)
    {
        Node tag = claim.Tag;
        TagProbe probe = claim.Probe;

        var detection = new Node("Detection", tag.Offset, tag.Length, probe.Confidence.ToString());
        foreach (Evidence evidence in probe.Evidence)
        {
            detection.Add("Evidence", evidence.Offset, evidence.Length, evidence.Description);
        }

        tag.Add(detection);
        detectionNodes.Add(detection);
        root.Add(tag);
        findings.AddRange(probe.Findings);
    }

    private void ParseData(ByteReader window, Node root, List<Finding> findings)
    {
        IFormatParser? parser = FindParser(window);
        if (parser is null)
        {
            Node data = root.Add("Data", window.Start, window.Length);
            findings.Add(new Finding(Severity.Info, "format.unrecognized", "No format parser recognized this file.", data));
            return;
        }

        root.Value = parser.Name;
        var context = new ParseContext(window, root);

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

        findings.AddRange(context.Findings);
    }

    private IFormatParser? FindParser(ByteReader reader)
    {
        IFormatParser? best = null;
        int bestConfidence = 0;

        foreach (IFormatParser parser in _formatParsers)
        {
            reader.Position = reader.Start;

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

        reader.Position = reader.Start;
        return best;
    }

    // Lyrics3 belongs directly before ID3v1 (Lyrics3 v2.00 specification), and ID3v1 belongs in
    // the last 128 bytes; other orders occur in the wild, so they are findings, not rejections.
    private static void CheckTrailingLayout(List<Claim> trailing, List<Finding> findings)
    {
        for (int i = 0; i < trailing.Count; i++)
        {
            Node tag = trailing[i].Tag;
            string? next = i + 1 < trailing.Count ? trailing[i + 1].Tag.Name : null;

            if (tag.Name == "Lyrics3" && next != "ID3v1")
            {
                findings.Add(new Finding(
                    Severity.Warning,
                    "layout.lyrics3-without-id3v1",
                    $"The Lyrics3 tag at 0x{tag.Offset:X} is not followed by an ID3v1 tag, which its specification requires.",
                    tag));
            }

            if (tag.Name == "ID3v1" && next is not null)
            {
                findings.Add(new Finding(
                    Severity.Warning,
                    "layout.id3v1-not-last",
                    $"The ID3v1 tag at 0x{tag.Offset:X} is followed by a {next} tag; readers expect ID3v1 in the last 128 bytes.",
                    tag));
            }
        }
    }

    private static Node DeepestNodeAt(Node root, long offset, HashSet<Node> skip)
    {
        Node current = root;
        while (true)
        {
            Node? child = current.Children.FirstOrDefault(c => !skip.Contains(c) && c.Offset <= offset && offset < c.End);
            if (child is null)
            {
                return current;
            }

            current = child;
        }
    }

    private static string PathTo(Node root, Node target)
    {
        var path = new List<string>();
        Node current = root;
        while (current != target)
        {
            Node? next = current.Children.FirstOrDefault(c => c.Offset <= target.Offset && target.End <= c.End && Contains(c, target));
            if (next is null)
            {
                break;
            }

            path.Add(next.Name);
            current = next;
        }

        return string.Join(" > ", path);
    }

    private static bool Contains(Node node, Node target) => node == target || node.Children.Any(c => Contains(c, target));

    private sealed record Claim(ITagParser Parser, TagProbe Probe)
    {
        public Node Tag => Probe.Tag!;
    }

    private sealed record PendingFinding(Severity Severity, string Code, long Offset, Func<Node, Node, string> Message);
}
