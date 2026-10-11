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
            findings.Add(new Finding(Severity.Warning, "file.empty", "The file is empty.", root));
            return new Report(source, root, findings);
        }

        List<Claim> leading = FindLeadingTags(reader, pending);
        long dataStart = leading.Count > 0 ? leading[^1].Tag.End : 0;
        List<Claim> trailing = FindTrailingTags(reader, dataStart, leading, pending);
        long dataEnd = trailing.Count > 0 ? trailing[0].Tag.Offset : reader.Length;
        List<Claim> embedded = FollowNextTagOffsets(reader, dataStart, dataEnd, leading, trailing, findings);

        var detectionNodes = new HashSet<Node>();
        foreach (Claim claim in leading.Concat(embedded).Concat(trailing))
        {
            AddClaim(root, claim, findings, detectionNodes);
        }

        long embeddedBytes = embedded.Sum(c => c.Tag.Length);
        if (dataEnd - dataStart > embeddedBytes)
        {
            ParseData(reader.Slice(dataStart, dataEnd - dataStart), root, embedded.Select(c => c.Tag).ToList(), findings);
        }
        else
        {
            findings.Add(new Finding(Severity.Warning, "layout.tags-only", "The file holds only tags; no bytes are left for audio data.", root));
        }

        root.SortChildrenByOffset();
        CheckTrailingLayout(trailing, findings);

        foreach (PendingFinding finding in pending)
        {
            Node node = DeepestNodeAt(root, finding.Offset, detectionNodes);
            findings.Add(new Finding(finding.Severity, finding.Code, finding.Message(node, root), node));
        }

        // In file order, so the list reads like the tree; stable for findings on the same bytes.
        List<Finding> ordered = findings.OrderBy(f => f.Node?.Offset ?? 0).ToList();
        return new Report(source, root, ordered);
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

    // Walks backward from the end of the file. At each anchor every parser is probed; among the
    // accepted tags that do not reach into the leading tags, the strongest evidence wins and the
    // parser order breaks ties. The winner's start becomes the next anchor, so every anchor moves
    // strictly toward the start.
    private List<Claim> FindTrailingTags(ByteReader reader, long dataStart, List<Claim> leading, List<PendingFinding> pending)
    {
        var claims = new List<Claim>();
        long anchor = reader.Length;

        while (anchor > 0)
        {
            var candidates = new List<(int Order, Claim Claim)>();
            var others = new List<(ITagParser Parser, TagProbe Probe)>();

            for (int order = 0; order < _tagParsers.Count; order++)
            {
                ITagParser parser = _tagParsers[order];
                TagProbe? probe = Probe(parser, anchor, reader, leading: false);
                if (probe is null || IsLeadingTagAgain(parser, probe, leading))
                {
                    continue;
                }

                if (probe.IsAccepted && probe.Tag!.Offset >= dataStart)
                {
                    candidates.Add((order, Candidate(parser, probe, claims.Count > 0 ? claims[0] : null)));
                }
                else
                {
                    others.Add((parser, probe));
                }
            }

            Claim? winner = candidates
                .OrderByDescending(c => c.Claim.Confidence)
                .ThenBy(c => c.Order)
                .Select(c => c.Claim)
                .FirstOrDefault();

            others.AddRange(candidates.Where(c => c.Claim != winner).Select(c => (c.Claim.Parser, c.Claim.Probe)));

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

    // A tag that its specification places directly before another one (Lyrics3 and the Enhanced
    // TAG before ID3v1) is still reported without it, but with a finding and lower confidence,
    // so a well-placed competitor at the same anchor wins.
    private static Claim Candidate(ITagParser parser, TagProbe probe, Claim? following)
    {
        if (parser.MustPrecede is not { } required || following?.Parser.Name == required)
        {
            return new Claim(parser, probe);
        }

        Node tag = probe.Tag!;
        var finding = new Finding(
            Severity.Warning,
            $"layout.{Slug(parser.Name)}-without-{Slug(required)}",
            $"The {TagLabel(parser.Name)} at 0x{tag.Offset:X} is not directly followed by an {TagLabel(required)}, which its specification requires.",
            tag);
        Confidence lowered = Effective(probe) == Confidence.High ? Confidence.Medium : Confidence.Low;
        return new Claim(parser, probe, lowered, [finding]);
    }

    private static string Slug(string name) => name.ToLowerInvariant().Replace(' ', '-');

    // "Lyrics3 tag", but "Enhanced TAG" rather than "Enhanced TAG tag".
    private static string TagLabel(string name) => name.EndsWith("TAG", StringComparison.Ordinal) ? name : name + " tag";

    private const long MaxNextTagSearch = 1 << 20;

    // A tag may say where the next tag of its kind starts at the earliest (ID3v2.4 SEEK). If the
    // next such tag found at an anchor starts at or after that point, the pointer is satisfied;
    // otherwise the unclaimed data from there on is searched, at most 1 MiB of it. Tags found
    // this way are followed in turn, and each search starts after the previous tag, so it ends.
    private static List<Claim> FollowNextTagOffsets(ByteReader reader, long dataStart, long dataEnd, List<Claim> leading, List<Claim> trailing, List<Finding> findings)
    {
        var embedded = new List<Claim>();
        var queue = new Queue<Claim>(leading.Concat(trailing));

        // Every tag in offset order, overall and per parser, so each step finds its neighbors by
        // binary search; a file with thousands of chained tags would otherwise take minutes.
        var embeddedIndex = new ClaimIndex();
        var byParser = new Dictionary<ITagParser, ClaimIndex>();
        foreach (Claim known in leading.Concat(trailing))
        {
            ParserIndex(known.Parser).Add(known);
        }

        while (queue.Count > 0)
        {
            Claim claim = queue.Dequeue();
            if (claim.Probe.NextTagOffset is not { } target)
            {
                continue;
            }

            Node tag = claim.Tag;
            string label = $"The {claim.Parser.Name} tag at 0x{tag.Offset:X} says the next one starts at 0x{target:X} or later";
            if (target > reader.Length)
            {
                findings.Add(new Finding(Severity.Warning, "tag.next-beyond-file", $"{label}, beyond the end of the file.", tag));
                continue;
            }

            Claim? next = ParserIndex(claim.Parser).FirstFrom(tag.End);
            if (next is not null && next.Tag.Offset < target)
            {
                findings.Add(new Finding(Severity.Warning, "tag.next-too-early", $"{label}, but the next one starts at 0x{next.Tag.Offset:X}.", tag));
                continue;
            }

            long from = Math.Max(target, dataStart);
            long limit = Math.Min(Math.Min(dataEnd, next?.Tag.Offset ?? dataEnd), from + MaxNextTagSearch);
            limit = Math.Min(limit, embeddedIndex.FirstFrom(from)?.Tag.Offset ?? limit);

            TagProbe? found = null;
            if (from < limit)
            {
                try
                {
                    found = claim.Parser.FindFrom(reader, from, limit);
                }
                catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
                {
                    found = null;
                }
            }

            if (found?.Tag is { } foundTag && foundTag.Offset >= from && foundTag.Offset < limit && foundTag.End <= dataEnd)
            {
                var foundClaim = new Claim(claim.Parser, found);
                embedded.Add(foundClaim);
                embeddedIndex.Add(foundClaim);
                ParserIndex(claim.Parser).Add(foundClaim);
                queue.Enqueue(foundClaim);
            }
            else if (next is null)
            {
                findings.Add(new Finding(
                    Severity.Warning,
                    "tag.next-missing",
                    $"{label}, but none was found in the {Math.Max(0, limit - from)} bytes searched from there.",
                    tag));
            }
        }

        embedded.Sort((a, b) => a.Tag.Offset.CompareTo(b.Tag.Offset));
        return embedded;

        ClaimIndex ParserIndex(ITagParser parser)
        {
            if (!byParser.TryGetValue(parser, out ClaimIndex? index))
            {
                byParser[parser] = index = new ClaimIndex();
            }

            return index;
        }
    }

    // Claims sorted by offset.
    private sealed class ClaimIndex
    {
        private readonly List<Claim> _claims = [];

        public void Add(Claim claim) => _claims.Insert(LowerBound(claim.Tag.Offset), claim);

        // The first claim at or after offset.
        public Claim? FirstFrom(long offset)
        {
            int index = LowerBound(offset);
            return index < _claims.Count ? _claims[index] : null;
        }

        private int LowerBound(long offset)
        {
            int low = 0;
            int high = _claims.Count;
            while (low < high)
            {
                int middle = (low + high) / 2;
                (low, high) = _claims[middle].Tag.Offset < offset ? (middle + 1, high) : (low, middle);
            }

            return low;
        }
    }

    // An ID3v2.4 tag with a footer that ends where the leading tags end is found again from the
    // end; it is the same tag, not a near miss.
    private static bool IsLeadingTagAgain(ITagParser parser, TagProbe probe, List<Claim> leading) =>
        probe.Tag is { } tag && leading.Any(c => c.Parser == parser && c.Tag.Offset == tag.Offset && c.Tag.Length == tag.Length);

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

        var detection = new Node("Detection", tag.Offset, tag.Length, claim.Confidence.ToString());
        foreach (Evidence evidence in probe.Evidence)
        {
            detection.Add("Evidence", evidence.Offset, evidence.Length, evidence.Description);
        }

        tag.Add(detection);
        detectionNodes.Add(detection);
        root.Add(tag);
        findings.AddRange(probe.Findings);
        findings.AddRange(claim.LayoutFindings);
    }

    private void ParseData(ByteReader window, Node root, List<Node> embedded, List<Finding> findings)
    {
        IFormatParser? parser = FindParser(window);
        if (parser is null)
        {
            Node? first = null;
            long position = window.Start;
            foreach (Node? tag in embedded.Append<Node?>(null))
            {
                long gapEnd = tag?.Offset ?? window.End;
                if (gapEnd > position)
                {
                    Node data = root.Add("Data", position, gapEnd - position);
                    first ??= data;
                }

                position = tag?.End ?? position;
            }

            findings.Add(new Finding(Severity.Info, "format.unrecognized", "No format parser recognized this file.", first ?? root));
            return;
        }

        root.Value = parser.Name;
        var context = new ParseContext(window, root, embedded);

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

    // ID3v1 belongs in the last 128 bytes; other orders occur in the wild, so they are findings,
    // not rejections. Lyrics3 extended fields must agree with the ID3v1 tag after them.
    private static void CheckTrailingLayout(List<Claim> trailing, List<Finding> findings)
    {
        for (int i = 0; i < trailing.Count; i++)
        {
            Node tag = trailing[i].Tag;
            string? next = i + 1 < trailing.Count ? trailing[i + 1].Tag.Name : null;

            if (tag.Name == "Lyrics3" && next == "ID3v1")
            {
                CheckLyrics3ExtendedFields(tag, trailing[i + 1].Tag, findings);
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

    // Lyrics3 v2.00: "The extended Album, Artist and Track are an extension to the fields in the
    // ID3v1 tag ... make sure their first 30 chars are exactly the same as the ones in the ID3v1
    // tag. If they are the same, display the extended field. If not, display the one from the ID
    // tag." It also recommends "NOT to save extended fields at all, if they are not larger then
    // 30 chars".
    private static void CheckLyrics3ExtendedFields(Node lyrics, Node id3v1, List<Finding> findings)
    {
        foreach ((string field, string v1Field) in new[] { ("EAL", "Album"), ("EAR", "Artist"), ("ETT", "Title") })
        {
            Node? extended = lyrics.Children.FirstOrDefault(c => c.Name == field);
            string? v1 = id3v1.Children.FirstOrDefault(c => c.Name == v1Field)?.Value;
            if (extended?.Value is not { } text || v1 is null)
            {
                continue;
            }

            string prefix = text.Length > 30 ? text[..30] : text;
            if (!string.Equals(prefix.TrimEnd(' '), v1, StringComparison.Ordinal))
            {
                findings.Add(new Finding(
                    Severity.Warning,
                    "lyrics3.extended-mismatch",
                    $"The Lyrics3 {field} field does not start with the ID3v1 {v1Field.ToLowerInvariant()} \"{v1}\", so readers show the ID3v1 value.",
                    extended));
            }
            else if (text.Length <= 30)
            {
                findings.Add(new Finding(
                    Severity.Info,
                    "lyrics3.extended-unneeded",
                    $"The Lyrics3 {field} field is not longer than the 30 characters the ID3v1 {v1Field.ToLowerInvariant()} already holds.",
                    extended));
            }
        }
    }

    private static Node DeepestNodeAt(Node root, long offset, HashSet<Node> skip)
    {
        if (TopLevelAt(root, offset) is not { } current || skip.Contains(current))
        {
            return root;
        }

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

    // The root's children are sorted and do not overlap, and a file can have thousands of them,
    // so they are searched by halving.
    private static Node? TopLevelAt(Node root, long offset)
    {
        IReadOnlyList<Node> top = root.Children;
        int low = 0;
        int high = top.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            (low, high) = top[middle].Offset <= offset ? (middle + 1, high) : (low, middle);
        }

        return low > 0 && offset < top[low - 1].End ? top[low - 1] : null;
    }

    private static string PathTo(Node root, Node target)
    {
        var path = new List<string>();
        if (target == root || TopLevelAt(root, target.Offset) is not { } top || !Contains(top, target))
        {
            return string.Empty;
        }

        path.Add(top.Name);
        Node current = top;
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

    // Full confidence needs everything to check out, so a tag with a warning or error is at most
    // medium, whatever its parser says (plan, rule 2).
    private static Confidence Effective(TagProbe probe) =>
        probe.Confidence == Confidence.High && probe.Findings.Any(f => f.Severity >= Severity.Warning) ? Confidence.Medium : probe.Confidence;

    private sealed record Claim(ITagParser Parser, TagProbe Probe, Confidence Confidence, IReadOnlyList<Finding> LayoutFindings)
    {
        public Claim(ITagParser parser, TagProbe probe)
            : this(parser, probe, Effective(probe), [])
        {
        }

        public Node Tag => Probe.Tag!;
    }

    private sealed record PendingFinding(Severity Severity, string Code, long Offset, Func<Node, Node, string> Message);
}
