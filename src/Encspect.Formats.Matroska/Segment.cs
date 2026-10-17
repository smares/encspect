using Encspect.Core;

namespace Encspect.Formats.Matroska;

internal sealed partial class FileReader
{
    // The Segment (RFC 9559 section 5.1): its Top-Level Elements are read in turn, the Clusters into records, the others into nodes.
    // headerMissing: the file starts inside a Segment, so the element at the header's position is the first of its children.
    private long ParseSegment(ElementHeader header, bool headerMissing)
    {
        _nodes += SegmentNodeCost;
        var segment = new SegmentState { Number = _segments.Count + 1, Records = new Records(), Document = _document, Offset = header.Offset };
        _segments.Add(segment);
        Records records = segment.Records;
        segment.IsHeaderMissing = headerMissing;
        segment.IsSizeUnknown = !headerMissing && header.IsUnknownSize;
        long dataStart = headerMissing ? header.Offset : header.DataOffset;
        segment.DataStart = dataStart;
        long declaredEnd = segment.IsSizeUnknown || headerMissing ? _reader.End : header.End!.Value;
        long end = Math.Min(declaredEnd, _reader.End);
        segment.End = end;
        segment.DeclaredEnd = declaredEnd;

        _segment = segment;
        var holder = new Node("Segment", header.Offset, end - header.Offset);
        ElementDefinition? definition = Schema.Find(Schema.Segment);
        long position = dataStart;
        bool crcPossible = true;
        long skippedStart = -1;
        Node? firstSkipped = null;

        // The run of elements skipped past the node budget ends at upTo.
        void CloseSkipped(long upTo)
        {
            if (skippedStart < 0)
            {
                return;
            }

            Node run = holder.Add(Unparsed(skippedStart, upTo, "the Segment"));
            firstSkipped ??= run;
            AddEntry(records, run);
            skippedStart = -1;
        }

        while (position < end)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            HeaderProblem problem = Ebml.TryReadHeader(_reader, position, end, segment.Document!.ReadIdLength, segment.Document.ReadSizeLength, out ElementHeader child);
            if (problem != HeaderProblem.None)
            {
                CloseSkipped(position);
                Node rest = AddUnparsed(holder, position, end, problem, "in the Segment");
                AddEntry(records, rest);
                position = end;
                break;
            }

            if (segment.IsSizeUnknown && child.Id is Schema.Ebml or Schema.Segment)
            {
                CloseSkipped(position);
                end = segment.End = position;
                break;
            }

            if (child.Id == Schema.Cluster)
            {
                CloseSkipped(position);
                if (!AccountRecord(position, end))
                {
                    Node rest = holder.Add(Unparsed(position, end, "the Segment"));
                    AddEntry(records, rest);
                    position = end;
                    break;
                }

                position = ParseCluster(segment, child, end);
                crcPossible = false;
                continue;
            }

            if (_nodes >= MaxNodes)
            {
                // Past the budget the other Top-Level Elements are skipped by their sizes, one Unparsed Data node for each run of them, and the Clusters
                // are still read: they are records, which have a limit of their own.
                segment.HasSkippedElements = true;
                skippedStart = skippedStart < 0 ? position : skippedStart;
                if (!_budgetReported)
                {
                    ReportBudget(position, () => firstSkipped!);
                }

                position = child.End is { } skippedEnd && skippedEnd <= end ? skippedEnd : end;
                continue;
            }

            Node node = ParseElement(child, definition, end, 2, out long next);
            // A Segment cut short by the end of the file, or of unknown size (its end is not known until it is read), has no CRC to check.
            if (child.Id == Schema.Crc32 && crcPossible && position == dataStart && !segment.IsSizeUnknown && declaredEnd <= _reader.End)
            {
                VerifyCrc(node, child, next, end, "Segment");
            }

            position = next;
            AddEntry(records, node);
            crcPossible = crcPossible && child.Id == Schema.Crc32;
        }

        CloseSkipped(position);
        segment.End = end;
        if (segment.IsSizeUnknown)
        {
            segment.DeclaredEnd = end;
        }

        int count = records.Entries.Count;
        long length = end - header.Offset;
        segment.Node = new Node("Segment", header.Offset, length, SegmentValue(segment), () => new BlockedChildren([], count, i => SegmentEntry(records, i)))
        {
            Description = headerMissing ? Explain.SegmentWithoutHeader : Schema.Find(Schema.Segment)!.Description,
            Topic = Topics.Container,
        };
        _file.Add(segment.Node);
        _segment = null;
        if (headerMissing)
        {
            _findings.AddNow(Severity.Warning, "matroska.segment-header-missing",
                "The file starts with Top-Level Elements and no Segment element before them: the Segment's header is not in the file, as in a recording or a file cut at its start.", segment.Node);
        }
        else if (segment.IsSizeUnknown)
        {
            _findings.AddNow(Severity.Info, "matroska.segment-size-unknown",
                "The Segment's size is unknown (all its bits are set), as a stream written live leaves it; it ends where the data ends or the next Segment or EBML Header starts (RFC 8794, section 6.2; RFC 9559, section 23.2).", segment.Node);
        }
        else if (declaredEnd > _reader.End)
        {
            _findings.AddNow(Severity.Warning, "matroska.element-truncated",
                $"The Segment states a size of {header.Size} bytes, but the file ends {Bytes(declaredEnd - _reader.End)} sooner: the file is cut short.", segment.Node);
        }

        return end;
    }

    private static void AddEntry(Records records, Node node)
    {
        records.Entries.Add(new TopLevel { Cluster = -1, Node = records.Nodes.Count });
        records.Nodes.Add(node);
    }

    private static Node SegmentEntry(Records records, int index)
    {
        TopLevel entry = records.Entries[index];
        return entry.Cluster >= 0 ? ClusterNodes.Cluster(records, entry.Cluster) : records.Nodes[entry.Node];
    }

    private static NodeValue SegmentValue(SegmentState segment) =>
        $"{(segment.Tracks.Count == 1 ? "1 track" : $"{segment.Tracks.Count} tracks")}, {(segment.ClusterCount == 1 ? "1 cluster" : $"{segment.ClusterCount} clusters")}";

    // The node budget ran out at position: the elements from there on are not read, but for the Clusters of a Segment. anchor is the Unparsed Data
    // node that covers position, made when the run it starts ends.
    private void ReportBudget(long position, Func<Node> anchor)
    {
        if (_budgetReported)
        {
            return;
        }

        _budgetReported = true;
        _findings.Add(Severity.Warning, "matroska.too-many-elements", anchor, _ =>
            $"The file holds more than {Counts.Of(MaxNodes, "element", "elements")} besides the blocks of its Clusters; the elements from 0x{position:X} on are not read, but for the Clusters of a Segment.");
    }
}
