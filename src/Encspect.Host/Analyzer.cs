using System.Reflection;
using Encspect.Core;

namespace Encspect.Host;

public sealed class Analyzer
{
    private readonly IReadOnlyList<ITagParser> _tagParsers;
    private readonly IReadOnlyList<IFormatParser> _formatParsers;
    private readonly IReadOnlyList<ICodecParser> _codecParsers;
    private readonly bool _hasContainers;

    public Analyzer(IEnumerable<IFormatParser> formatParsers)
        : this([], formatParsers)
    {
    }

    public Analyzer(IEnumerable<ITagParser> tagParsers, IEnumerable<IFormatParser> formatParsers)
        : this(tagParsers, formatParsers, [])
    {
    }

    // Tag parsers are probed in the given order at every anchor, and the first one that
    // validates owns the bytes; see DefaultParsers for the order the plan requires. Codec
    // parsers serve the containers' routes (ParseContext.ParseStream and ParsePackets) by their
    // IDs, and tag parsers the ParseTag route by their names.
    public Analyzer(IEnumerable<ITagParser> tagParsers, IEnumerable<IFormatParser> formatParsers, IEnumerable<ICodecParser> codecParsers)
    {
        ArgumentNullException.ThrowIfNull(tagParsers);
        ArgumentNullException.ThrowIfNull(formatParsers);
        ArgumentNullException.ThrowIfNull(codecParsers);

        _tagParsers = tagParsers.ToList();
        _formatParsers = formatParsers.ToList();
        _codecParsers = codecParsers.ToList();
        _hasContainers = _formatParsers.Any(DeclaresEnd);
        if (_codecParsers.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new ArgumentException($"Two codec parsers have the ID {duplicate.Key}.", nameof(codecParsers));
        }
    }

    public static Analyzer CreateDefault() => new(DefaultParsers.Tags, DefaultParsers.Formats, DefaultParsers.Codecs);

    // Anything that is not a regular file gets a report with a file.not-regular finding without
    // being read, since a FIFO blocks and a device can be read forever. A file that cannot be
    // opened or read throws IOException or UnauthorizedAccessException.
    public Report AnalyzeFile(string path, AnalysisOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using FileStream? stream = FileType.Open(path, out string? notRegular);
        return stream is null ? FileReports.NotRegular(path, notRegular!) : Analyze(path, stream, options);
    }

    // The format parser that would analyze the file, or null: the same tag scan and sniffing as
    // Analyze, without parsing the data, so a batch can skip files of other formats cheaply.
    public string? DetectFormat(string path, AnalysisOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using FileStream? stream = FileType.Open(path, out _);
        if (stream is null)
        {
            return null;
        }

        var reader = new ByteReader(stream, (options ?? AnalysisOptions.Default).CancellationToken);
        if (reader.Length == 0)
        {
            return null;
        }

        var pending = new List<PendingFinding>();
        var failures = new List<Failure>();
        List<Claim> leading = FindLeadingTags(reader, [], failures);
        long dataStart = leading.Count > 0 ? leading[^1].Tag.End : 0;
        Container? container = FindContainer(reader, dataStart, Path.GetExtension(path), failures);
        List<Claim> trailing = FindTrailingTags(reader, dataStart, leading, container, pending, failures);
        long dataEnd = trailing.Count > 0 ? trailing[0].Tag.Offset : reader.Length;
        return dataEnd > dataStart ? FindParser(new SniffContext(reader.Slice(dataStart, dataEnd - dataStart), Path.GetExtension(path)), failures)?.Name : null;
    }

    // Parsers are trusted to report damage as findings, but a parser bug must not end a batch: an
    // exception from any parser becomes a parse.failed error. Only cancellation, and running out of
    // memory, escape.
    public Report Analyze(string source, Stream stream, AnalysisOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stream);
        options ??= AnalysisOptions.Default;
        options.CancellationToken.ThrowIfCancellationRequested();

        if (!stream.CanRead)
        {
            return FileReports.Create(source, FileReports.UnreadableCode, "The stream cannot be read.");
        }

        // Tags are found from both ends of the file and parsers jump to the offsets fields point at.
        if (!stream.CanSeek)
        {
            return FileReports.Create(source, FileReports.NotSeekableCode, "The stream cannot seek, so it was not analyzed.");
        }

        var reader = new ByteReader(stream, options.CancellationToken);
        var root = new Node("File", 0, reader.Length) { Description = HostText.File };
        Increasing? progress = options.BytesRead is { } report ? new Increasing(report) : null;
        var findings = new List<Finding>();
        var pending = new List<PendingFinding>();
        var failures = new List<Failure>();

        if (reader.Length == 0)
        {
            findings.Add(new Finding(Severity.Warning, "file.empty", "The file is empty.", root) { Path = [] });
            options.BytesRead?.Report(0);
            return new Report(source, root, findings);
        }

        var leadingRejections = new List<(ITagParser Parser, TagProbe Probe)>();
        List<Claim> leading = FindLeadingTags(reader, leadingRejections, failures);
        long dataStart = leading.Count > 0 ? leading[^1].Tag.End : 0;
        Container? container = FindContainer(reader, dataStart, Path.GetExtension(source), failures);
        List<Claim> trailing = FindTrailingTags(reader, dataStart, leading, container, pending, failures);
        long dataEnd = trailing.Count > 0 ? trailing[0].Tag.Offset : reader.Length;
        List<Finding> layout = CheckTrailingLayout(trailing);
        List<Claim> embedded = FollowNextTagOffsets(reader, dataStart, dataEnd, leading, trailing, container, findings, failures);
        pending.InsertRange(0, ResolveLeadingRejections(leadingRejections, trailing.Concat(embedded), reader));

        var detectionNodes = new HashSet<Node>();
        var shown = new Dictionary<Node, Node>(ReferenceEqualityComparer.Instance);
        foreach (Claim claim in leading.Concat(embedded).Concat(trailing))
        {
            AddClaim(root, claim, findings, detectionNodes, shown);
        }

        long embeddedBytes = embedded.Sum(c => c.Tag.Length);
        IReadOnlyList<StreamInfo> streams = [];
        if (dataEnd - dataStart > embeddedBytes)
        {
            List<EncoderClaim> claims = leading.Concat(embedded).Concat(trailing).OrderBy(c => c.Tag.Offset).SelectMany(c => c.Probe.EncoderClaims).ToList();

            // A container owns the bytes up to the end it declares, so the tags a pointer found lie
            // after it (FollowNextTagOffsets); its parser reads up to the first of them, as it would
            // up to a trailing tag, and the bytes between those tags are no format's.
            long formatEnd = container is not null && embedded.Count > 0 ? embedded[0].Tag.Offset : dataEnd;

            // Only the format parser's reads count as progress: tags are probed at both ends of the
            // file first, which would make the furthest offset read jump to the end.
            ByteReader window = new ByteReader(stream, options.CancellationToken, progress).Slice(dataStart, formatEnd - dataStart);
            List<Node> inWindow = embedded.Where(c => c.Tag.Offset < formatEnd).Select(c => shown[c.Tag]).ToList();
            var sniff = new SniffContext(window, Path.GetExtension(source)) { FirstEmbeddedTag = inWindow.Count > 0 ? inWindow.Min(t => t.Offset) : null };
            streams = ParseData(sniff, root, inWindow, claims, findings, failures);
            AddDataBetweenTags(root, embedded.Where(c => c.Tag.Offset >= formatEnd), dataEnd, findings);
        }
        else
        {
            findings.Add(new Finding(Severity.Warning, "layout.tags-only", "The file holds only tags; no bytes are left for audio data.", root));
        }

        root.SortChildrenByOffset();
        findings.AddRange(layout);

        foreach (PendingFinding finding in pending)
        {
            Node node = DeepestNodeAt(root, finding.Offset, detectionNodes);
            findings.Add(new Finding(finding.Severity, finding.Code, finding.Message(node, root), node));
        }

        findings.AddRange(failures.Select(f => new Finding(
            Severity.Error,
            "parse.failed",
            $"The {f.Parser} parser failed {f.Activity}: {f.Error.GetType().Name}: {f.Error.Message}",
            root)));

        // Findings on a tag point at the node the report shows for it. In file order, so the list
        // reads like the tree; stable for findings on the same bytes.
        var paths = new TreePaths(root);
        List<Finding> ordered = findings
            .Select(f => f.Node is { } node && shown.TryGetValue(node, out Node? copy) ? f with { Node = copy } : f)
            .OrderBy(f => f.Node?.Offset ?? 0)
            .Select(f => f with { Path = paths.Find(f.Node) })
            .ToList();
        streams = streams.Select(s => s with { NodePaths = s.Nodes.Select(paths.Find).ToList() }).ToList();
        progress?.Report(reader.Length);
        return new Report(source, root, ordered, streams);
    }

    // Progress only grows: the format parser's reads may already have reached the end of the file,
    // which the analysis reports again when it is done.
    private sealed class Increasing(IProgress<long> progress) : IProgress<long>
    {
        private long _last = -1;

        public void Report(long value)
        {
            if (value > _last)
            {
                _last = value;
                progress.Report(value);
            }
        }
    }

    private List<Claim> FindLeadingTags(ByteReader reader, List<(ITagParser Parser, TagProbe Probe)> rejections, List<Failure> failures)
    {
        var claims = new List<Claim>();
        long position = 0;

        while (position < reader.Length)
        {
            Claim? accepted = null;
            foreach (ITagParser parser in _tagParsers)
            {
                TagProbe? probe = Probe(parser, position, reader, leading: true, failures);
                if (probe is null)
                {
                    continue;
                }

                if (probe.IsAccepted)
                {
                    accepted = new Claim(parser, probe);
                    break;
                }

                rejections.Add((parser, probe));
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

    // A leading candidate is rejected where it would start a tag, but a trailing tag may still own
    // its bytes (a footer-only APE tag that is the whole file). Then the rejection is not reported:
    // the bytes belong to the same kind of tag, or are a near miss inside another kind.
    private static IEnumerable<PendingFinding> ResolveLeadingRejections(
        List<(ITagParser Parser, TagProbe Probe)> rejections,
        IEnumerable<Claim> later,
        ByteReader reader)
    {
        List<Claim> claims = later.ToList();
        foreach ((ITagParser parser, TagProbe probe) in rejections)
        {
            Claim? owner = claims.FirstOrDefault(c => c.Tag.Offset <= probe.SignatureOffset && probe.SignatureOffset < c.Tag.End);
            if (owner is null)
            {
                yield return Rejected(parser, probe, reader);
            }
            else if (owner.Parser != parser)
            {
                yield return NearMiss(parser, probe, owner, reader);
            }
        }
    }

    // Walks backward from the end of the file. At each anchor every parser is probed; among the
    // accepted tags that do not reach into the leading tags, the strongest evidence wins and the
    // parser order breaks ties. The winner's start becomes the next anchor, so every anchor moves
    // strictly toward the start.
    //
    // Evidence is the confidence first. All candidates end at the anchor, so of two with the same
    // confidence the longer one contains the shorter, whose signature is then just bytes inside the
    // longer one's structure: a "TAG" in an Enhanced TAG field or in Lyrics3 lyrics, or "LYRICSEND"
    // at the end of an ID3v1 comment. The containing tag accounts for those bytes and the contained
    // one cannot account for the rest, so the longer tag wins before the parser order is asked.
    //
    // A candidate that starts before the end a container declares is one of its chunks (an AIFF
    // file's ID3 chunk), so it is a near miss, and the walk stops at the container's end.
    private List<Claim> FindTrailingTags(ByteReader reader, long dataStart, List<Claim> leading, Container? container, List<PendingFinding> pending,
        List<Failure> failures)
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
                TagProbe? probe = Probe(parser, anchor, reader, leading: false, failures);
                if (probe is null || IsLeadingTagAgain(parser, probe, leading))
                {
                    continue;
                }

                // A parser that searches for its start (Lyrics3 v1) may run into the leading tags;
                // probed again within the bytes they leave, it may find a tag there. That tag
                // replaces the overlapping candidate unless its evidence is weaker.
                if (probe.IsAccepted && probe.Tag!.Offset < dataStart && anchor > dataStart
                    && Probe(parser, anchor, reader.Slice(dataStart, reader.Length - dataStart), leading: false, failures) is { IsAccepted: true } confined
                    && Effective(confined) >= Effective(probe))
                {
                    probe = confined;
                }

                if (container is { } owner && (probe.Tag?.Offset ?? probe.SignatureOffset) < owner.End)
                {
                    pending.Add(OwnedByContainer(parser, probe, owner, reader));
                }
                else if (probe.IsAccepted && probe.Tag!.Offset >= dataStart)
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
                .ThenByDescending(c => c.Claim.Tag.Length)
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
    // The bytes up to the end a container declares are its own, as for trailing tags, so a pointer
    // into them is searched from that end.
    private static List<Claim> FollowNextTagOffsets(ByteReader reader, long dataStart, long dataEnd, List<Claim> leading, List<Claim> trailing, Container? container,
        List<Finding> findings, List<Failure> failures)
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
            bool intoContainer = container is { } owner && from < owner.End;
            if (intoContainer)
            {
                from = container!.End;
            }

            long limit = Math.Min(Math.Min(dataEnd, next?.Tag.Offset ?? dataEnd), from + MaxNextTagSearch);
            limit = Math.Min(limit, embeddedIndex.FirstFrom(from)?.Tag.Offset ?? limit);

            TagProbe? found = null;
            if (from < limit)
            {
                try
                {
                    found = claim.Parser.FindFrom(new TagProbeContext(reader), from, limit);
                }
                catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
                {
                    found = null;
                }
                catch (Exception ex) when (IsParserBug(ex))
                {
                    failures.Add(new Failure(claim.Parser.Name, $"searching for a tag from 0x{from:X}", ex));
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
            else if (next is null && intoContainer)
            {
                findings.Add(new Finding(
                    Severity.Warning,
                    "tag.next-in-container",
                    $"{label}, inside the {container!.Format} data, which by its own size extends to 0x{container.End:X}; no tag of its kind follows that.",
                    tag));
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

    private static TagProbe? Probe(ITagParser parser, long anchor, ByteReader reader, bool leading, List<Failure> failures)
    {
        try
        {
            var context = new TagProbeContext(reader);
            TagProbe? probe = leading ? parser.ProbeAt(context, anchor) : parser.ProbeBefore(context, anchor);

            // Every claim must touch its anchor, or the anchor walks would not make progress.
            if (probe?.Tag is { } tag && (leading ? tag.Offset != anchor : tag.End != anchor))
            {
                return TagProbe.Reject(probe.SignatureOffset, $"the parser returned a tag at 0x{tag.Offset:X} that is not anchored at 0x{anchor:X}");
            }

            if (probe?.Tag is { } outside && (outside.Offset < reader.Start || outside.End > reader.End))
            {
                return TagProbe.Reject(probe.SignatureOffset, $"the parser returned a tag at 0x{outside.Offset:X} that does not fit the file");
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
        catch (Exception ex) when (IsParserBug(ex))
        {
            failures.Add(new Failure(parser.Name, $"probing for a tag at 0x{anchor:X}", ex));
            return TagProbe.Reject(anchor, $"the parser failed with {ex.GetType().Name}");
        }
    }

    // Anything a parser throws but cancellation, and running out of memory, which nothing can
    // recover from.
    internal static bool IsParserBug(Exception ex) => ex is not (OperationCanceledException or OutOfMemoryException);

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

    private static PendingFinding OwnedByContainer(ITagParser parser, TagProbe probe, Container container, ByteReader reader)
    {
        string where = Describe(probe.SignatureOffset, reader);
        return new PendingFinding(
            Severity.Info,
            "tag.near-miss",
            probe.SignatureOffset,
            (_, _) => $"{parser.Name} signature at {where} rejected: part of the {container.Format} data, which by its own size extends to 0x{container.End:X}.");
    }

    // The end the format declares for itself (IFormatParser.DeclaredEnd), asked of the parser that
    // sniffs best on everything after the leading tags, before the trailing tags are looked for;
    // null when it declares none, or an end that does not lie after the leading tags. An end past
    // the end of the file still makes every trailing candidate part of the container: its size
    // says so, and the format parser reports the truncation.
    private Container? FindContainer(ByteReader reader, long dataStart, string? extension, List<Failure> failures)
    {
        if (!_hasContainers || dataStart >= reader.Length)
        {
            return null;
        }

        var context = new SniffContext(reader.Slice(dataStart, reader.Length - dataStart), extension);
        if (FindParser(context, failures) is not { } parser)
        {
            return null;
        }

        long? end;
        try
        {
            end = parser.DeclaredEnd(context);
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
        {
            end = null;
        }
        catch (Exception ex) when (IsParserBug(ex))
        {
            AddFailure(failures, new Failure(parser.Name, "reading its declared end", ex));
            end = null;
        }

        return end > dataStart ? new Container(parser.Name, end.Value) : null;
    }

    // Finding a container's end costs a sniff before the trailing tags are found, on top of the one
    // after, so it is done only when a format parser implements DeclaredEnd.
    private static bool DeclaresEnd(IFormatParser parser)
    {
        InterfaceMapping map = parser.GetType().GetInterfaceMap(typeof(IFormatParser));
        int index = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IFormatParser.DeclaredEnd));
        return map.TargetMethods[index].DeclaringType != typeof(IFormatParser);
    }

    private static string Describe(long offset, ByteReader reader) => $"0x{offset:X} (EOF-{reader.Length - offset})";

    // The parser's node is never changed, so its children may be built on demand: the report shows
    // a node with the same name, value and bytes whose children are the parser's and Detection.
    private static void AddClaim(Node root, Claim claim, List<Finding> findings, HashSet<Node> detectionNodes, Dictionary<Node, Node> shown)
    {
        Node copy = ShowTag(claim.Tag, claim.Confidence, claim.Probe.Evidence, out Node detection);
        shown[claim.Tag] = copy;
        detectionNodes.Add(detection);
        root.Add(copy);
        findings.AddRange(claim.Probe.Findings);
        findings.AddRange(claim.LayoutFindings);
    }

    // The node the report shows for an accepted tag, also for one a container holds.
    internal static Node ShowTag(Node tag, Confidence confidence, IReadOnlyList<Evidence> evidence, out Node detection)
    {
        // A tag a container took apart (a Vorbis comment packet that spans Ogg pages) keeps its
        // ranges, so neither node covers the page headers between them.
        var shownDetection = new Node("Detection", tag.Offset, tag.Length, confidence.ToString())
        {
            Description = HostText.Detection,
            Topic = Topics.TagPlacement,
            Ranges = tag.Ranges,
        };
        foreach (Evidence item in evidence)
        {
            shownDetection.Add("Evidence", item.Offset, item.Length, item.Description, HostText.Evidence);
        }

        detection = shownDetection;
        return new Node(tag.Name, tag.Offset, tag.Length, tag.Value, () => [.. tag.Children, shownDetection])
        {
            Kind = NodeKind.Tag,
            Description = tag.Description,
            Topic = tag.Topic,
            Ranges = tag.Ranges,
        };
    }

    // The bytes between tags found after a container's declared end, and after the last of them up
    // to the trailing tags: no tag and no format claims them.
    private static void AddDataBetweenTags(Node root, IEnumerable<Claim> tags, long dataEnd, List<Finding> findings)
    {
        List<Claim> after = tags.ToList();
        if (after.Count == 0)
        {
            return;
        }

        long position = after[0].Tag.Offset;
        foreach (Claim? claim in after.Append<Claim?>(null))
        {
            long gapEnd = claim?.Tag.Offset ?? dataEnd;
            if (gapEnd > position)
            {
                Node data = root.Add("Data", position, gapEnd - position, null, HostText.DataAfterContainer);
                findings.Add(new Finding(Severity.Info, "layout.data-between-tags",
                    $"{gapEnd - position} bytes at 0x{position:X} lie after the end the format declares, between or after tags found there; nothing claims them.", data));
            }

            position = Math.Max(position, claim?.Tag.End ?? position);
        }
    }

    private IReadOnlyList<StreamInfo> ParseData(SniffContext sniff, Node root, List<Node> embedded, List<EncoderClaim> claims, List<Finding> findings,
        List<Failure> failures)
    {
        ByteReader window = sniff.Reader;
        IFormatParser? parser = FindParser(sniff, failures);
        if (parser is null)
        {
            Node? first = null;
            long position = window.Start;
            foreach (Node? tag in embedded.Append<Node?>(null))
            {
                long gapEnd = tag?.Offset ?? window.End;
                if (gapEnd > position)
                {
                    Node data = root.Add("Data", position, gapEnd - position, null, HostText.UnrecognizedData);
                    first ??= data;
                }

                position = tag?.End ?? position;
            }

            findings.Add(new Finding(Severity.Info, "format.unrecognized", "No format parser recognized this file.", first ?? root));
            return [];
        }

        root.Value = parser.Name;
        var context = new ParseContext(window, root, embedded, claims, new ParserRouter(_codecParsers, _tagParsers));

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
        catch (Exception ex) when (IsParserBug(ex))
        {
            failures.Add(new Failure(parser.Name, "parsing the data", ex));
        }

        findings.AddRange(context.Findings);
        return context.Streams;
    }

    private IFormatParser? FindParser(SniffContext context, List<Failure> failures)
    {
        ByteReader reader = context.Reader;
        IFormatParser? best = null;
        int bestConfidence = 0;

        foreach (IFormatParser parser in _formatParsers)
        {
            reader.Position = reader.Start;

            int confidence;
            try
            {
                confidence = parser.Sniff(context);
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException)
            {
                confidence = 0;
            }
            catch (Exception ex) when (IsParserBug(ex))
            {
                AddFailure(failures, new Failure(parser.Name, "sniffing the data", ex));
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
    // not rejections. Lyrics3 extended fields must agree with the ID3v1 tag after them. A tag that
    // gets a warning here is at most medium, like one whose parser reported it (plan, rule 2).
    private static List<Finding> CheckTrailingLayout(List<Claim> trailing)
    {
        var findings = new List<Finding>();
        for (int i = 0; i < trailing.Count; i++)
        {
            Node tag = trailing[i].Tag;
            Claim? next = i + 1 < trailing.Count ? trailing[i + 1] : null;
            int before = findings.Count;

            if (tag.Name == "Lyrics3" && next?.Tag.Name == "ID3v1")
            {
                CheckLyrics3ExtendedFields(tag, next.Tag, findings);
            }

            if (tag.Name == "ID3v1" && next is not null)
            {
                findings.Add(new Finding(
                    Severity.Warning,
                    "layout.id3v1-not-last",
                    $"The ID3v1 tag at 0x{tag.Offset:X} is followed by the {TagLabel(next.Parser.Name)} at 0x{next.Tag.Offset:X}; readers expect ID3v1 in the last 128 bytes.",
                    tag));
            }

            if (trailing[i].Confidence == Confidence.High && findings.Skip(before).Any(f => f.Severity >= Severity.Warning))
            {
                trailing[i] = trailing[i] with { Confidence = Confidence.Medium };
            }
        }

        return findings;
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
            string? v1 = id3v1.Children.FirstOrDefault(c => c.Name == v1Field)?.Value?.ToString();
            if (extended?.Value?.ToString() is not { } text || v1 is null)
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
    internal static Confidence Effective(TagProbe probe) =>
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

    private sealed record Failure(string Parser, string Activity, Exception Error);

    private sealed record Container(string Format, long End);

    // The data is sniffed twice when a container may declare its end, so a sniff that fails is
    // reported once.
    private static void AddFailure(List<Failure> failures, Failure failure)
    {
        if (!failures.Any(f => f.Parser == failure.Parser && f.Activity == failure.Activity))
        {
            failures.Add(failure);
        }
    }
}
