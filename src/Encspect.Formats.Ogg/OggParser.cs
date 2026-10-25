using Encspect.Core;

namespace Encspect.Formats.Ogg;

// The Ogg container (RFC 3533): pages that carry the packets of logical bitstreams, told apart by
// their serial numbers, multiplexed within a group of streams that start together (a link) and
// chained one group after another. A page is only taken as a page while a chain of consistent pages
// holds: at the start of the data, and after damage, a page must pass its CRC and be followed by
// another page or the end of the data, as MPEG sync needs a chain of frames; once in sync, the next
// page is read where the previous one ends. Anything else is a gap.
//
// The data is read twice: the scan records every page in a compact record and follows each stream's
// packets through their lacing values, reporting what breaks the specification; then each stream's
// packets are read again, page by page, and handed to the codec parser its first packet names,
// through ParsePackets.
public sealed partial class OggParser : IFormatParser
{
    // Data before the first page is searched this far, so a file that holds some junk first is
    // still recognized; another format with a stronger signature wins over it.
    private const int SniffLimit = 1 << 16;


    public string Name => "Ogg";

    // A chain of pages from the start of the data is certain (class 100); one found within the first
    // 64 KiB is class 60, below MPEG audio. A chain is a page that passes its CRC (verified) and the
    // start of another page or the end of the data after it (Pages.StartsChain).
    public SniffResult Sniff(SniffContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ByteReader reader = context.Reader;
        var window = new PageWindow(reader);
        if (Pages.StartsChain(window, reader.Start, reader.End))
        {
            return Chain(100, window, reader.Start, reader.End);
        }

        long limit = Math.Min(reader.End, reader.Start + SniffLimit);
        long found = Pages.FindChain(window, reader.Start + 1, limit, reader.End, ResyncBudget.For(limit - reader.Start), context.CancellationToken);
        return found >= 0 ? Chain(60, window, found, reader.End) : SniffResult.None;
    }

    private static SniffResult Chain(int sniffClass, PageWindow window, long offset, long end)
    {
        long position = offset;
        int pages = 0;
        while (pages < SniffResult.MaxFrames && Pages.Probe(window, position, end, out PageHeader header) == PageProbe.Fits && header.Length <= end - position)
        {
            pages++;
            position += header.Length;
        }

        return new SniffResult(sniffClass, offset, pages, 1, End: position);
    }

    public bool HasDeclaredEnd => true;

    // Ogg states no size of its own, but every page states its own length, so a chain of pages from
    // the start of the data says where the Ogg data ends: a trailing tag signature found before that
    // end lies inside the last pages, not after them. The chain ends where the next bytes are not a
    // page that fits the data, and only counts when its last page passes its CRC.
    public long? DeclaredEnd(SniffContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        ByteReader reader = context.Reader;
        var window = new PageWindow(reader);
        if (!Pages.StartsChain(window, reader.Start, reader.End))
        {
            return null;
        }

        long position = reader.Start;
        PageHeader last = default;
        int count = 0;
        while (Pages.Probe(window, position, reader.End, out PageHeader header) == PageProbe.Fits && header.Length <= reader.End - position)
        {
            if (++count % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            last = header;
            position += header.Length;
        }

        return Pages.ComputeCrc(window, last) == last.Crc ? position : null;
    }

    public void Parse(ParseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var window = new PageWindow(context.Reader);
        long position = context.Reader.Start;
        int links = 0;

        // A tag found inside the data (through an ID3v2.4 SEEK frame) splits it into regions, each
        // read on its own.
        foreach (Node? tag in context.EmbeddedTags.OrderBy(t => t.Offset).Append<Node?>(null))
        {
            long end = tag?.Offset ?? context.Reader.End;
            if (end > position)
            {
                links += ParseRegion(context, window, position, end, links);
            }

            position = Math.Max(position, tag?.End ?? position);
        }
    }

    // The streams of one link play at the same time and form a playback group; the links of a chain, in this region and the regions
    // before it (linksBefore), follow one another. Returns the region's number of links.
    private static int ParseRegion(ParseContext context, PageWindow window, long start, long end, int linksBefore)
    {
        var region = new Region(start, end) { GroupBase = linksBefore };
        Scan(region, window, context.CancellationToken);
        CloseStreams(region);

        Node node = context.Root.Add(new Node("Ogg", start, end - start, NodeValue.Integer(region.PageCount, "pages", "page"))
        {
            Description = Explain.Ogg,
            Topic = Topics.OggPages,
        });
        foreach (LogicalStream stream in region.Streams)
        {
            AddStream(context, region, window, node, stream);
        }

        node.Add(new Node("Pages", start, end - start, NodeValue.Integer(region.PageCount, "pages", "page"), () => new BlockedChildren([], region.Pages.Count, i => PageNode(region, i)))
        {
            Kind = NodeKind.Stream,
            Description = Explain.Pages,
        });

        region.Findings.Flush(context);
        return region.Link;
    }

    // The scan: pages, gaps, and each page's place in its logical stream.
    private static void Scan(Region region, PageWindow window, CancellationToken cancellationToken)
    {
        long position = region.Start;
        long end = region.End;
        bool inSync = false;
        var runs = new List<Run>();
        int steps = 0;

        while (position < end)
        {
            if (++steps % 4096 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            PageProbe probe = Pages.Probe(window, position, end, out PageHeader header);
            bool trusted = inSync || position == region.Start;
            if (probe == PageProbe.Fits && header.Length <= end - position)
            {
                uint computed = Pages.ComputeCrc(window, header);
                long next = position + header.Length;
                bool followed = next == end || Pages.Probe(window, next, end, out _) == PageProbe.Fits;
                if (computed == header.Crc && (inSync || followed))
                {
                    AddPage(region, window, header, computed, header.Length, PageStatus.None, runs);
                    position = next;
                    inSync = true;
                    continue;
                }

                if (computed != header.Crc && trusted)
                {
                    // A page that another page starts inside was cut short there; otherwise the
                    // page that follows confirms its length and only its bytes are damaged.
                    long cut = Pages.FindChain(window, position + header.HeaderLength, next, end, region.Resync, cancellationToken);
                    if (cut >= 0)
                    {
                        AddPage(region, window, header, 0, (int)(cut - position), PageStatus.Truncated, runs);
                        position = cut;
                        inSync = true;
                        continue;
                    }

                    if (followed)
                    {
                        AddPage(region, window, header, computed, header.Length, PageStatus.CrcMismatch, runs);
                        position = next;
                        inSync = true;
                        continue;
                    }
                }
            }
            else if (probe != PageProbe.NotAPage && trusted)
            {
                // The page runs past the end of the data, or the next page starts inside it.
                long cut = Pages.FindChain(window, position + 4, end, end, region.Resync, cancellationToken);
                long stop = cut >= 0 ? cut : end;
                if (stop - position >= PageHeader.FixedLength)
                {
                    AddPage(region, window, header, 0, (int)Math.Min(stop - position, PageHeader.MaxLength), PageStatus.Truncated, runs);
                }
                else
                {
                    AddCutHeader(region, position, (int)(stop - position));
                }

                position += (int)Math.Min(stop - position, PageHeader.MaxLength);
                inSync = position == cut;
                continue;
            }

            long found = Pages.FindChain(window, position + 1, end, end, region.Resync, cancellationToken);
            long gapEnd = found < 0 ? end : found;
            AddGap(region, position, gapEnd);
            position = gapEnd;
            inSync = found >= 0;
        }

        if (region.Resync.IsExhausted)
        {
            region.Findings.Add(-1, Severity.Warning, "ogg.resync-limit", new Node("Ogg", region.Start, region.End - region.Start), _ =>
                "The search for pages after damaged data stopped checking candidates, having spent the checksum work it allows for data of this size; what no page was found for is read as a gap.");
        }
    }

    private static void AddGap(Region region, long start, long end)
    {
        region.Pages.Add(new PageRecord { Offset = start, Length = end - start, Stream = -1, Status = PageStatus.Gap });
        region.GapBytes += end - start;
        region.Findings.Add(-1, Severity.Warning, "ogg.junk", new Node("Gap", start, end - start), count =>
            count == 1
                ? $"{Bytes(end - start)} at 0x{start:X} {Counts.Agree(end - start, "is", "are")} not an Ogg page; readers skip them to the next page."
                : $"{count} stretches of bytes, {Bytes(region.GapBytes)} in all, are not Ogg pages; readers skip them to the next page. The first at 0x{start:X}.");
    }

    // Fewer than 27 bytes after a capture pattern at the end of the data: too few for the fields of a
    // page header.
    private static void AddCutHeader(Region region, long start, int length)
    {
        region.Pages.Add(new PageRecord { Offset = start, Length = length, Stream = -1, Status = PageStatus.Truncated | PageStatus.HeaderCut });
        region.PageCount++;
        region.Findings.Add(-1, Severity.Warning, "ogg.page-truncated", new Node("Page", start, length), _ =>
            $"The page at 0x{start:X} is cut short after {Bytes(length)}, inside its 27-byte header.");
    }

    private static void AddPage(Region region, PageWindow window, in PageHeader header, uint computed, int length, PageStatus status, List<Run> runs)
    {
        int index = region.Pages.Count;
        if (Assign(region, header, index) is not { } stream)
        {
            AddUntracked(region, window, header, computed, length, status);
            return;
        }

        int present = Math.Min(header.Segments, length - PageHeader.FixedLength);
        ReadOnlySpan<byte> lacing = window.Get(header.Offset + PageHeader.FixedLength, present);

        var record = new PageRecord
        {
            Offset = header.Offset,
            Length = length,
            Granule = header.Granule,
            Serial = header.Serial,
            Sequence = header.Sequence,
            Crc = header.Crc,
            ComputedCrc = computed,
            Flags = header.Flags,
            Segments = (byte)header.Segments,
            Lacing = region.Lacing.Add(lacing),
            Stream = stream.Index,
            Status = status,
        };

        CheckSequence(region, stream, header, ref record);
        CheckGranuleOrder(region, stream, header, index);

        if ((status & (PageStatus.CrcMismatch | PageStatus.Truncated)) != 0)
        {
            record.CompletedBefore = stream.Assembler.Completed;
            record.Status |= stream.Assembler.IsPending ? PageStatus.PendingBefore : PageStatus.None;
            stream.Assembler.Lose();
            ReportDamage(region, stream, header, length, status);
        }
        else
        {
            if ((record.Status & PageStatus.DropsPending) != 0)
            {
                stream.Assembler.Lose();
            }

            PageOutcome outcome = stream.Assembler.Feed(header.IsContinued, lacing, runs);
            record.CompletedBefore = outcome.CompletedBefore;
            record.Status |= outcome.PendingBefore ? PageStatus.PendingBefore : PageStatus.None;
            CapturePacketStart(stream, window, header, runs);
            CheckPackets(region, stream, header, outcome, runs);
        }

        // Decoders drop a damaged page, so its end-of-stream flag does not end the stream.
        if (header.IsEnd && (status & (PageStatus.CrcMismatch | PageStatus.Truncated)) == 0 && !stream.HasEos)
        {
            stream.HasEos = true;
            region.OpenInLink -= stream.Link == region.Link ? 1 : 0;
        }

        stream.Pages.Add(index);
        stream.End = header.Offset + length;
        stream.LastPage = index;
        region.Pages.Add(record);
        region.PageCount++;
    }

    // A page of a stream beyond the region's limit: kept as a record of the page and its segment table, never followed, so a file of
    // beginning-of-stream pages costs the records of its pages and no more than the limit's streams cost.
    private static void AddUntracked(Region region, PageWindow window, in PageHeader header, uint computed, int length, PageStatus status)
    {
        int present = Math.Min(header.Segments, length - PageHeader.FixedLength);
        region.Pages.Add(new PageRecord
        {
            Offset = header.Offset,
            Length = length,
            Granule = header.Granule,
            Serial = header.Serial,
            Sequence = header.Sequence,
            Crc = header.Crc,
            ComputedCrc = computed,
            Flags = header.Flags,
            Segments = (byte)header.Segments,
            Lacing = region.Lacing.Add(window.Get(header.Offset + PageHeader.FixedLength, present)),
            Stream = -1,
            Status = status | PageStatus.Untracked,
        });
        region.PageCount++;
        long offset = header.Offset;
        int limit = region.MaxStreams;
        long size = region.End - region.Start;
        region.Findings.Add(-1, Severity.Info, "ogg.too-many-streams", HeaderTypeField(offset), count =>
            $"{CountPages(count)} of logical streams past the analyzer's limit of {limit} streams for {Bytes(size)} of data {(count == 1 ? "is" : "are")} not followed, the first at 0x{offset:X}; the limit bounds what a file of many tiny streams can cost and grows with the data.");
    }

    // Which logical stream a page belongs to. Beginning-of-stream pages start streams; one that
    // comes after other pages, or after every stream of the link has ended, starts the next link of
    // a chain (RFC 3533, section 4: "grouping" puts all beginning-of-stream pages first, "chaining"
    // starts a new group after the last one ends).
    // Null for a page of a stream beyond the region's limit (Region.MaxStreams), which is not followed.
    private static LogicalStream? Assign(Region region, PageHeader header, int index)
    {
        if (header.IsBeginning)
        {
            if (region.Streams.Count >= region.MaxStreams)
            {
                return null;
            }

            if (region.LinkHasData || (region.Streams.Count > 0 && region.OpenInLink == 0))
            {
                region.Link++;
                region.LinkHasData = false;
                region.OpenInLink = 0;
            }

            if (region.BySerial.TryGetValue(header.Serial, out LogicalStream? existing))
            {
                // RFC 3533, section 4: a serial number is unique "within the scope of the physical bitstream", so also across the links
                // of a chain, as in two Ogg files that were joined.
                uint serial = header.Serial;
                bool sameLink = existing.Link == region.Link;
                region.Findings.Add(existing.Index, Severity.Warning, "ogg.serial-reused", SerialField(header.Offset), _ => sameLink
                    ? $"The beginning-of-stream page at 0x{header.Offset:X} starts a second logical stream with the serial number 0x{serial:X8} in the same link, which RFC 3533 forbids; its pages are read as a new stream."
                    : $"The beginning-of-stream page at 0x{header.Offset:X} starts a logical stream in a later link with the serial number 0x{serial:X8} of an earlier one, which RFC 3533 forbids: every logical stream of the physical bitstream has its own; its pages are read as a new stream.");
            }

            return NewStream(region, header, index, hasBos: true);
        }

        region.LinkHasData = true;
        if (region.BySerial.TryGetValue(header.Serial, out LogicalStream? stream))
        {
            if (stream.HasEos)
            {
                long offset = header.Offset;
                region.Findings.Add(stream.Index, Severity.Warning, "ogg.page-after-eos", HeaderTypeField(offset), count =>
                    $"{CountPages(count)} of the logical stream 0x{stream.Serial:X8} {(count == 1 ? "comes" : "come")} after its end-of-stream page; the first at 0x{offset:X}.");
            }
            else if (stream.Link != region.Link && !stream.ContinuesAcrossLinks)
            {
                stream.ContinuesAcrossLinks = true;
                region.Findings.Add(stream.Index, Severity.Warning, "ogg.pages-across-links", HeaderTypeField(header.Offset), _ =>
                    $"The logical stream 0x{stream.Serial:X8} goes on at 0x{header.Offset:X}, after the beginning-of-stream pages of the next link; RFC 3533 puts all of a link's beginning-of-stream pages before its other pages.");
            }

            return stream;
        }

        if (region.Streams.Count >= region.MaxStreams)
        {
            return null;
        }

        LogicalStream started = NewStream(region, header, index, hasBos: false);
        region.Findings.Add(started.Index, Severity.Warning, "ogg.bos-missing", HeaderTypeField(header.Offset), _ =>
            $"The logical stream 0x{header.Serial:X8} starts at 0x{header.Offset:X} without a beginning-of-stream page; its first pages are missing, so decoders may not know its codec.");
        return started;
    }

    private static LogicalStream NewStream(Region region, in PageHeader header, int index, bool hasBos)
    {
        var stream = new LogicalStream(region.Streams.Count, header.Serial, region.Link, hasBos)
        {
            Start = header.Offset,
            FirstPage = index,
        };
        region.Streams.Add(stream);
        region.BySerial[header.Serial] = stream;
        region.OpenInLink++;
        return stream;
    }

    // Page sequence numbers count each stream's pages (RFC 3533, section 6, "page_sequence_number"),
    // so a gap means lost pages, and a packet left unfinished before them cannot be completed.
    private static void CheckSequence(Region region, LogicalStream stream, in PageHeader header, ref PageRecord record)
    {
        uint sequence = header.Sequence;
        if (stream.LastSequence is uint last && sequence != unchecked(last + 1))
        {
            uint missing = unchecked(sequence - (last + 1));
            long offset = header.Offset;
            record.Status |= PageStatus.DropsPending;
            if (missing < 0x80000000)
            {
                region.Findings.Add(stream.Index, Severity.Warning, "ogg.sequence-gap", SequenceField(offset), count =>
                    count == 1
                        ? $"The page sequence of the logical stream 0x{stream.Serial:X8} jumps from {last} to {sequence} at 0x{offset:X}, so {missing} {(missing == 1 ? "page is" : "pages are")} missing."
                        : $"The page sequence of the logical stream 0x{stream.Serial:X8} has {count} gaps, so pages are missing; the first at 0x{offset:X}, from {last} to {sequence}.");
            }
            else
            {
                region.Findings.Add(stream.Index, Severity.Warning, "ogg.sequence-repeat", SequenceField(offset), count =>
                    $"The page sequence of the logical stream 0x{stream.Serial:X8} goes back {Times(count)}, the first time at 0x{offset:X} from {last} to {sequence}; pages are repeated or out of order.");
            }
        }

        stream.LastSequence = sequence;
    }

    // Granule positions count the codec's units up to the end of the last packet a page completes,
    // so they never go back (RFC 3533, section 6, "granule_position"); -1 marks a page on which no
    // packet ends.
    private static void CheckGranuleOrder(Region region, LogicalStream stream, in PageHeader header, int index)
    {
        if (header.Granule == -1)
        {
            return;
        }

        if (stream.LastGranule is long last && header.Granule < last)
        {
            long offset = header.Offset;
            long granule = header.Granule;
            region.Findings.Add(stream.Index, Severity.Warning, "ogg.granule-backwards", GranuleField(offset), count =>
                $"The granule position of the logical stream 0x{stream.Serial:X8} goes back {Times(count)}, the first time at 0x{offset:X} from {last} to {granule}; players that seek or show the time by it get lost.");
        }

        stream.LastGranule = header.Granule;
        stream.LastGranulePage = index;
    }

    private static void ReportDamage(Region region, LogicalStream stream, in PageHeader header, int length, PageStatus status)
    {
        long offset = header.Offset;
        if ((status & PageStatus.CrcMismatch) != 0)
        {
            region.Findings.Add(stream.Index, Severity.Warning, "ogg.crc-mismatch", new Node("CRC", offset + 22, 4), count =>
                $"{CountPages(count)} of the logical stream 0x{stream.Serial:X8} {(count == 1 ? "fails" : "fail")} the CRC check; decoders drop such a page with its packets. The first at 0x{offset:X}.");
            return;
        }

        int declared = header.Length;
        region.Findings.Add(stream.Index, Severity.Warning, "ogg.page-truncated", new Node("Page", offset, length), count =>
            count == 1
                ? $"The page at 0x{offset:X} is cut short after {Bytes(length)} of {Bytes(declared)}; decoders drop it with its packets."
                : $"{Counts.Of(count, "page", "pages")} of the logical stream 0x{stream.Serial:X8} {Counts.Agree(count, "is", "are")} cut short; decoders drop them with their packets. The first at 0x{offset:X}, after {Bytes(length)} of {Bytes(declared)}.");
    }

    // The first bytes of a stream's first packet, which name its codec.
    private static void CapturePacketStart(LogicalStream stream, PageWindow window, in PageHeader header, List<Run> runs)
    {
        if (stream.Magic is not null)
        {
            return;
        }

        foreach (Run run in runs)
        {
            if (run is { Kind: RunKind.Packet, Packet: 0, Continues: false })
            {
                int length = Math.Min(OggCodecs.MagicLength, run.Length);
                stream.Magic = window.Get(header.BodyOffset + run.Start, length).ToArray();
                stream.MagicOffset = header.BodyOffset + run.Start;
                return;
            }
        }
    }

    private static void CheckPackets(Region region, LogicalStream stream, in PageHeader header, in PageOutcome outcome, List<Run> runs)
    {
        long offset = header.Offset;
        if (outcome.ContinuationMissing)
        {
            region.Findings.Add(stream.Index, Severity.Warning, "ogg.continuation-missing", HeaderTypeField(offset), count =>
                $"{CountPages(count)} of the logical stream 0x{stream.Serial:X8} {(count == 1 ? "starts" : "start")} a fresh packet while the packet before is unfinished, without the continued-packet flag; that packet is dropped, as the flag says it does not go on. The first at 0x{offset:X}.");
        }

        if (outcome.Orphaned && !outcome.OrphanExpected)
        {
            region.Findings.Add(stream.Index, Severity.Warning, "ogg.continuation-orphaned", HeaderTypeField(offset), count =>
                $"{CountPages(count)} of the logical stream 0x{stream.Serial:X8} {(count == 1 ? "sets" : "set")} the continued-packet flag, but no packet was left unfinished before; the continued bytes are skipped, as libogg skips them. The first at 0x{offset:X}.");
        }

        if (outcome.TooLarge > 0)
        {
            region.Findings.Add(stream.Index, Severity.Warning, "ogg.packet-too-large", HeaderTypeField(offset), count =>
                $"{CountPackets(count)} of the logical stream 0x{stream.Serial:X8} {(count == 1 ? "is" : "are")} larger than {PacketAssembler.MaxPacket / (1 << 20)} MiB and not read. The first ends on the page at 0x{offset:X}.");
        }

        int ending = 0;
        foreach (Run run in runs)
        {
            ending += run.Ends ? 1 : 0;
        }

        if (ending > 0 && header.Granule == -1)
        {
            region.Findings.Add(stream.Index, Severity.Warning, "ogg.granule-missing", GranuleField(offset), count =>
                $"{CountPages(count)} of the logical stream 0x{stream.Serial:X8} {(count == 1 ? "completes" : "complete")} a packet but {(count == 1 ? "has" : "have")} the granule position -1, which RFC 3533 keeps for pages on which no packet ends. The first at 0x{offset:X}.");
        }
        else if (ending == 0 && header.Granule != -1 && header.Segments > 0)
        {
            long granule = header.Granule;
            region.Findings.Add(stream.Index, Severity.Warning, "ogg.granule-without-packet", GranuleField(offset), count =>
                $"{CountPages(count)} of the logical stream 0x{stream.Serial:X8} {(count == 1 ? "completes" : "complete")} no packet but {(count == 1 ? "has" : "have")} a granule position ({granule} at 0x{offset:X}, the first) instead of -1, which RFC 3533 requires there.");
        }

        if (header.IsEnd && outcome.EndsInsidePacket)
        {
            region.Findings.Add(stream.Index, Severity.Warning, "ogg.packet-unfinished", HeaderTypeField(offset), _ =>
                $"The end-of-stream page of the logical stream 0x{stream.Serial:X8} at 0x{offset:X} ends inside a packet, which is never completed.");
        }
    }

    // A stream without an end-of-stream page was cut short, or the next link of a chain began
    // before it ended.
    internal static void CloseStreams(Region region)
    {
        // The first beginning-of-stream stream of a later link, per stream; links only grow with the streams' order, so one pass
        // from the end finds them all.
        var nextBos = new LogicalStream?[region.Streams.Count];
        LogicalStream? after = null;
        LogicalStream? inGroup = null;
        int groupLink = -1;
        for (int i = nextBos.Length - 1; i >= 0; i--)
        {
            LogicalStream candidate = region.Streams[i];
            if (candidate.Link != groupLink)
            {
                after = inGroup ?? after;
                inGroup = null;
                groupLink = candidate.Link;
            }

            nextBos[i] = after;
            inGroup = candidate.HasBos ? candidate : inGroup;
        }

        foreach (LogicalStream stream in region.Streams)
        {
            ref readonly PageRecord last = ref region.Pages[stream.LastPage];
            long offset = last.Offset;
            if (!stream.HasEos)
            {
                LogicalStream? next = nextBos[stream.Index];
                string where = next is not null && !stream.ContinuesAcrossLinks
                    ? $"before the next link of the chain begins at 0x{next.Start:X}"
                    : "before the end of the data, so the file was probably cut short or not finished";
                string page = (last.Flags & Pages.End) != 0
                    ? $"its last page at 0x{offset:X} sets the flag but is damaged, so decoders drop it"
                    : $"its last page at 0x{offset:X} does not set the flag";
                region.Findings.Add(stream.Index, Severity.Warning, "ogg.eos-missing", HeaderTypeField(offset), _ =>
                    $"The logical stream 0x{stream.Serial:X8} has no end-of-stream page: {page}, and it ends {where}.");
            }

            if (stream.Assembler.IsPending && !(stream.HasEos && (last.Flags & Pages.End) != 0))
            {
                region.Findings.Add(stream.Index, Severity.Warning, "ogg.packet-unfinished", HeaderTypeField(offset), _ =>
                    $"The last page of the logical stream 0x{stream.Serial:X8} at 0x{offset:X} ends inside a packet, which is never completed.");
            }
        }
    }

    private static Node SerialField(long page) => new("Serial Number", page + 14, 4);

    private static Node SequenceField(long page) => new("Sequence Number", page + 18, 4);

    private static Node GranuleField(long page) => new("Granule Position", page + 6, 8);

    private static Node HeaderTypeField(long page) => new("Header Type", page + 5, 1);

    private static string Bytes(long count) => count == 1 ? "1 byte" : $"{count} bytes";

    private static string CountPages(int count) => count == 1 ? "1 page" : $"{count} pages";

    private static string CountPackets(int count) => count == 1 ? "1 packet" : $"{count} packets";

    private static string Times(int count) => count == 1 ? "once" : $"{count} times";
}
