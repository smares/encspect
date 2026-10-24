using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// Raw Dolby TrueHD (.thd) and MLP (.mlp) files: access units one after another, each stating its own length, some of them opening with a
// major sync that carries the stream's parameters (docs/design/truehd-format.md). A major sync with a CRC that holds is the evidence a
// stream is found by, and units are followed by their length words; a unit that breaks its structure is skipped to the next major sync that
// checks out. The residuals of every block are walked to the end, which is how the substreams are checked and how decoding gets them without
// reading them twice (docs/design/decoders.md, "The second entropy pass"). Tags after the stream (APEv2, ID3v1) are the host's.
public sealed class TrueHdParser : IFormatParser
{
    public const string FormatName = "TrueHD";

    public string Name => FormatName;

    // False makes the walk store nothing, so the decoder reads every unit's blocks itself: the contract as first drafted, which the tests compare the
    // lending walk with (docs/design/decoders.md, "The second entropy pass").
    internal bool LendsBuffers { get; init; } = true;

    // The units kept as records, past which they are only counted (Analysis.MaxRecords); tests lower it.
    internal int RecordLimit { get; init; } = Analysis.MaxRecords;

    // How many units keep all their bytes, and how many others keep the parts of a sparse record (Analysis.MaxFullUnits, MaxSparseUnits).
    internal int FullUnitLimit { get; init; } = Analysis.MaxFullUnits;

    internal int SparseUnitLimit { get; init; } = Analysis.MaxSparseUnits;

    // The size of the window the file is read through (Source.MinimumWindow); tests lower it to put window boundaries between units.
    internal int WindowSize { get; init; } = Source.MinimumWindow;

    // A usable major sync (its CRC holds) followed by units that fit: class 94 where the chain of units reaches it from the start of the data
    // (the units before the first sync have no sync of their own, so only their length words chain), 60 further in. Verified units are those
    // whose major sync CRC holds or whose substreams' parity bytes and CRC-8 all hold.
    public SniffResult Sniff(SniffContext context)
    {
        ByteReader reader = context.Reader;
        if (reader.Length < 4 + MajorSync.MinLength)
        {
            return SniffResult.None;
        }

        var source = new Source(reader);
        long start = reader.Start;
        long found = Locator.FindSyncUnit(source, start, Math.Min(reader.End, start + Locator.SearchLimit), out MajorSync sync);
        if (found < 0)
        {
            return SniffResult.None;
        }

        (int frames, int verified, long end) = CountChain(source, found, sync);
        bool isEnd = end >= reader.End;
        if (frames < 2 && !isEnd)
        {
            return SniffResult.None;
        }

        bool fromStart = found == start || Locator.ChainReaches(source, start, found);
        return new SniffResult(fromStart ? 94 : 60, fromStart ? start : found, frames, verified, End: end);
    }

    // The units of the chain that begins at the sync unit at position, at most SniffResult.MaxFrames, how many of them verify, and where the
    // last ends.
    private static (int Frames, int Verified, long End) CountChain(Source source, long position, MajorSync sync)
    {
        var layout = new UnitLayout();
        int frames = 0;
        int verified = 0;
        while (frames < SniffResult.MaxFrames && position < source.End)
        {
            ReadOnlySpan<byte> head = source.Window(position, 4);
            if (head.Length < 4)
            {
                break;
            }

            int length = (((head[0] & 0xF) << 8) | head[1]) * 2;
            if (length < 4 || position + length > source.End)
            {
                break;
            }

            ReadOnlySpan<byte> unit = source.Window(position, length);
            if (unit.Length < length)
            {
                break;
            }

            layout.Read(unit, sync.SubstreamCount, !sync.IsTrueHd);
            if (layout.HasSync)
            {
                if (layout.Sync is not { IsUsable: true })
                {
                    break;
                }
            }

            if (!layout.IsDirectoryRead || layout.Problems != UnitProblems.None)
            {
                break;
            }

            frames++;
            bool isVerified = layout.HasSync;
            bool hasAnyCheck = false;
            bool allHold = true;
            for (int k = 0; k < layout.SubstreamCount; k++)
            {
                DirectoryEntry entry = layout.Entries[k];
                if (!entry.HasCheckData)
                {
                    continue;
                }

                ReadOnlySpan<byte> data = unit[entry.Start..entry.Stop];
                hasAnyCheck = true;
                allHold &= data.Length > 2 && Checks.SubstreamParityHolds(data[..^2], data[^2]) && Checks.Substream(data[..^2]) == data[^1];
            }

            if (isVerified || (hasAnyCheck && allHold))
            {
                verified++;
            }

            position += length;
        }

        return (frames, verified, position);
    }

    // Following a stream's own length words cannot be mistaken for a tag's, but a tag has no sync: the stream is read up to the first
    // embedded tag, and the host's tag detection owns what follows the last unit.
    public void Parse(ParseContext context)
    {
        ByteReader window = context.Reader;
        // The bounds, not the reader, are what the lambda holds: the closure of the stream's children lives as long as the report, and the reader
        // would keep the input alive with it.
        long windowStart = window.Start;
        long windowEnd = window.End;
        List<Node> tags = [.. context.EmbeddedTags.Where(t => t.Offset >= windowStart && t.Offset < windowEnd).OrderBy(t => t.Offset)];
        ByteReader reader = tags.Count > 0 ? window.Slice(window.Start, tags[0].Offset - window.Start) : window;
        var source = new Source(reader, WindowSize);
        var analysis = new Analysis(context) { LendsBuffers = LendsBuffers, RecordLimit = RecordLimit, FullUnitLimit = FullUnitLimit, SparseUnitLimit = SparseUnitLimit };
        long first = Locator.FindSyncUnit(source, reader.Start, Math.Min(reader.End, reader.Start + Locator.SearchLimit), out MajorSync sync);
        bool isMlp = first >= 0 && !sync.IsTrueHd;
        Records records = analysis.Records;
        List<Node> leading = analysis.Leading;
        Node stream = new(isMlp ? "MLP" : "Dolby TrueHD", reader.Start, reader.End - reader.Start, null, () => new BlockedChildren(leading, records.Entries.Count, i => Nodes.Entry(records, i)))
        {
            Kind = NodeKind.Stream,
            Description = isMlp ? Explain.MlpStream : Explain.TrueHdStream,
            Topic = Topics.AccessUnits,
        };
        analysis.Stream = stream;
        context.Root.Add(stream);
        Run(analysis, source, first, context.CancellationToken);
        Summary.Finish(analysis, reader.Start, reader.End, context);

        long position = reader.End;
        foreach (Node? tag in tags.Append<Node?>(null))
        {
            long end = Math.Min(tag?.Offset ?? window.End, window.End);
            if (end > position)
            {
                Node data = context.Root.Add(new Node("Data", position, end - position, NodeValue.Integer(end - position, "bytes")) { Description = Explain.AfterEmbeddedTag });
                context.AddFinding(Severity.Warning, "truehd.data-after-tag",
                    $"{Counts.Of(end - position, "byte", "bytes")} at 0x{position:X} {Counts.Agree(end - position, "follows", "follow")} an ID3v2 tag inside the TrueHD data, which breaks the chain of access units; they are not read.", data);
            }

            position = Math.Max(position, tag?.End ?? position);
        }
    }

    // Reads the units from the start of the data. first: where the first access unit with a usable major sync is, or -1.
    internal static void Run(Analysis analysis, Source source, long first, CancellationToken cancellation)
    {
        long end = source.End;
        long position = source.Start;
        if (position >= end)
        {
            analysis.Findings.Add(Severity.Warning, "truehd.no-units", analysis.Stream, _ => "The data holds no access unit.");
            return;
        }

        if (first < 0)
        {
            analysis.AddSkipped(position, end - position);
            analysis.Findings.Add(Severity.Warning, "truehd.no-units", analysis.Stream, _ => "The data holds no access unit with a major sync that checks out, so the stream cannot be read.");
            return;
        }

        // The units before the first major sync are followed by their length words, which must land on it; otherwise they are skipped.
        bool isLeadingChain = first > position && Locator.ChainReaches(source, position, first);
        if (first > position && !isLeadingChain)
        {
            analysis.AddSkipped(position, first - position);
            position = first;
        }

        long skipFrom = -1;
        bool hasLoss = false;
        int step = 0;
        while (position < end)
        {
            if (++step % 1024 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            ReadOnlySpan<byte> head = source.Window(position, 4);
            int length = head.Length < 4 ? 0 : (((head[0] & 0xF) << 8) | head[1]) * 2;
            if (length >= 4)
            {
                int available = (int)Math.Min(length, end - position);
                bool isCut = available < length;
                bool isLeading = position < first;
                // A unit the data cuts short is believed only where its header and directory hold; a unit whose own structure breaks only where
                // another unit follows it.
                bool isAccepted = isLeading || analysis.Probe(source.Slice(position, available).Span[..available]) == UnitProblems.None;
                if (!isAccepted && !isCut)
                {
                    isAccepted = position + length == end || LooksLikeUnit(analysis, source, position + length, end);
                }

                if (isAccepted)
                {
                    if (skipFrom >= 0)
                    {
                        analysis.AddSkipped(skipFrom, position - skipFrom);
                        skipFrom = -1;
                        hasLoss = true;
                    }

                    // Sliced after the decision: looking at the next unit may have moved the window over the same array.
                    ReadOnlyMemory<byte> unit = source.Slice(position, available)[..available];
                    analysis.AddUnit(unit, position, isCut, hasLoss);
                    hasLoss = false;
                    position += available;
                    continue;
                }
            }

            skipFrom = skipFrom < 0 ? position : skipFrom;
            // A unit starts four bytes before its sync word, so a sync word from position + 5 on gives a unit after this one.
            long next = Locator.FindSyncUnit(source, position + 5, end, out _);
            position = next < 0 ? end : next;
        }

        if (skipFrom >= 0)
        {
            analysis.AddSkipped(skipFrom, end - skipFrom);
        }
    }

    private static bool LooksLikeUnit(Analysis analysis, Source source, long position, long end)
    {
        if (position >= end)
        {
            return true;
        }

        ReadOnlySpan<byte> head = source.Window(position, 4);
        if (head.Length < 4)
        {
            return false;
        }

        int length = (((head[0] & 0xF) << 8) | head[1]) * 2;
        int want = (int)Math.Min(Math.Max(length, 4), Math.Min(end - position, 128));
        return analysis.LooksLikeUnit(source.Window(position, want));
    }
}
