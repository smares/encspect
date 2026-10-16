using Encspect.Core;

namespace Encspect.Formats.Matroska;

internal sealed partial class FileReader
{
    // Distinct track numbers that blocks name, whose totals are kept apart; others share one.
    private const int MaxTrackStatistics = 4096;

    private readonly List<int> _laceSizes = [];

    // A Cluster (RFC 9559 section 5.1.3): its children go into records, and the nodes are built from them when read. A Cluster of
    // unknown size ends at the first element that belongs to the Segment or starts a document (RFC 8794 section 6.2).
    private long ParseCluster(SegmentState segment, ElementHeader header, long limit)
    {
        Records records = segment.Records;
        int clusterIndex = records.Clusters.Count;
        bool unknown = header.IsUnknownSize;
        long declaredEnd = header.End ?? limit;
        long end = Math.Min(declaredEnd, limit);
        bool isComplete = declaredEnd <= limit;
        long dataStart = header.DataOffset;
        if (!header.IsUnknownSize && declaredEnd > limit)
        {
            ChildEnd(header, Schema.Find(Schema.Cluster), limit, "Segment");
        }

        CheckHeaderForm(header, Schema.Find(Schema.Cluster), end);
        var cluster = new ClusterRecord { Offset = header.Offset, HeaderLength = (byte)header.Length, IsSizeUnknown = unknown, FirstItem = records.Items.Count };
        Item? crc = null;
        bool afterCrc = false;
        int timestamps = 0;
        int crcIndex = 0;
        long position = dataStart;
        int seen = 0;
        while (position < end)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            HeaderProblem problem = Ebml.TryReadHeader(_reader, position, end, segment.Document!.MaxIdLength, segment.Document.MaxSizeLength, out ElementHeader child);
            if (problem != HeaderProblem.None)
            {
                AddUnparsedItem(records, position, end);
                _findings.AddNow(Severity.Warning, "matroska.element-header-invalid",
                    $"No element can be read at 0x{position:X}, in the Cluster at 0x{header.Offset:X}: {HeaderWhy(problem)}. The {Bytes(end - position)} from there are not read.",
                    Unparsed(position, end, "the Cluster"));
                position = end;
                break;
            }

            ElementDefinition? definition = Schema.Find(child.Id);
            if (unknown && (child.Id is Schema.Ebml or Schema.Segment || definition is { IsTopLevel: true }))
            {
                end = position;
                break;
            }

            if (!AccountRecord(records))
            {
                // Past the limit on records: the Cluster is skipped from here on.
                position = end;
                break;
            }

            long childEnd = ChildEnd(child, definition, end, "Cluster");
            CheckHeaderForm(child, definition, childEnd);
            if (child.Id == Schema.SimpleBlock)
            {
                int block = ReadBlock(segment, cluster, child, childEnd, false);
                records.Items.Add(new Item { Kind = ItemKind.SimpleBlock, Offset = child.Offset, HeaderLength = (byte)child.Length, DataLength = childEnd - child.DataOffset, Id = Schema.SimpleBlock, Index = block });
                cluster.Blocks++;
            }
            else if (child.Id == Schema.BlockGroup)
            {
                Item group = ReadGroup(segment, cluster, child, childEnd, out int groupBlocks);
                records.Items.Add(group);
                cluster.Blocks += groupBlocks;
            }
            else if (child.Id == Schema.Timestamp)
            {
                (ulong value, bool valid) = ReadUnsigned(child, childEnd);
                if (timestamps++ == 0 && valid)
                {
                    cluster.Timestamp = (long)Math.Min(value, long.MaxValue);
                    cluster.HasTimestamp = true;
                    if (seen > 0 && !(seen == 1 && afterCrc))
                    {
                        _findings.Add(Severity.Info, "matroska.cluster-timestamp-position", () => ClusterAnchor(cluster, end), count =>
                            $"{(count == 1 ? "A Cluster does" : $"{count} Clusters do")} not start with their Timestamp element, which RFC 9559 says should be the first child, or the second after a CRC-32 (section 5.1.3.1); the first Cluster is at 0x{header.Offset:X}.");
                    }
                }

                records.Items.Add(new Item { Kind = ItemKind.Element, Id = child.Id, Offset = child.Offset, HeaderLength = (byte)child.Length, DataLength = childEnd - child.DataOffset, Value = (long)value });
            }
            else if (child.Id == Schema.Crc32)
            {
                var item = new Item { Kind = ItemKind.Crc, Id = child.Id, Offset = child.Offset, HeaderLength = (byte)child.Length, DataLength = childEnd - child.DataOffset };
                if (seen == 0 && child.Size == 4 && childEnd - child.DataOffset == 4 && isComplete)
                {
                    _reader.Position = child.DataOffset;
                    item.Value = _reader.ReadUInt32LittleEndian();
                    crc = item;
                    crcIndex = records.Items.Count;
                }
                else if (seen != 0)
                {
                    _findings.Add(Severity.Warning, "matroska.crc-position", new Node("CRC-32", child.Offset, childEnd - child.Offset), count =>
                        $"{(count == 1 ? "A CRC-32 element is" : $"{count} CRC-32 elements are")} not the first child of {(count == 1 ? "its" : "their")} parent, as RFC 8794 requires (section 11.3.1), so {(count == 1 ? "it is" : "they are")} not checked; the first is the CRC-32 at 0x{child.Offset:X} in the Cluster at 0x{header.Offset:X}.");
                    item.Kind = ItemKind.Element;
                }
                else if (child.Size != 4 || childEnd - child.DataOffset < 4)
                {
                    _findings.Add(Severity.Warning, "matroska.crc-size", new Node("CRC-32", child.Offset, childEnd - child.Offset), count =>
                        $"{(count == 1 ? "A CRC-32 element holds" : $"{count} CRC-32 elements hold")} other than the 4 bytes RFC 8794 gives it (section 11.3.1); the first, the CRC-32 at 0x{child.Offset:X} in the Cluster at 0x{header.Offset:X}, holds {Bytes(Math.Min((long)Math.Min(child.Size ?? 0, long.MaxValue), childEnd - child.DataOffset))}, so {(count == 1 ? "it is" : "they are")} not checked.");
                    item.Kind = ItemKind.Element;
                }
                else
                {
                    item.Kind = ItemKind.Element;
                }

                afterCrc = true;
                records.Items.Add(item);
            }
            else
            {
                long value = 0;
                if (definition is { Type: ElementType.Unsigned or ElementType.Signed or ElementType.Float })
                {
                    value = ReadNumberBits(child, childEnd, definition.Type);
                }

                if (definition is null)
                {
                    _findings.Add(Severity.Info, "matroska.unknown-element", () => new Node("Unknown Element", child.Offset, childEnd - child.Offset), count =>
                        $"{(count == 1 ? "An element is" : $"{count} elements are")} none the Matroska schema lists, which readers skip by their size (RFC 9559, section 4.4); the first has the ID 0x{child.Id:X} and lies at 0x{child.Offset:X}, in the Cluster.");
                }
                else if (!Schema.IsChildOf(definition, Schema.Find(Schema.Cluster)))
                {
                    _findings.Add(Severity.Warning, "matroska.misplaced-element", () => new Node(definition.Name, child.Offset, childEnd - child.Offset), count =>
                        MisplacedMessage(count, definition, child.Offset, Schema.Find(Schema.Cluster)));
                }
                else if (definition.IsDeprecated)
                {
                    _findings.Add(Severity.Info, "matroska.deprecated-element", () => new Node(definition.Name, child.Offset, childEnd - child.Offset), count =>
                        $"{(count == 1 ? "An element is one" : $"{count} elements are ones")} that RFC 9559 lists as no longer used (appendix A); the first is the {definition.Name} at 0x{child.Offset:X}.");
                }

                records.Items.Add(new Item { Kind = ItemKind.Element, Id = child.Id, Offset = child.Offset, HeaderLength = (byte)child.Length, DataLength = childEnd - child.DataOffset, Value = value });
            }

            seen++;
            position = childEnd;
        }

        cluster.DataLength = end - dataStart;
        cluster.ItemCount = records.Items.Count - cluster.FirstItem;
        records.Clusters.Add(cluster);
        records.Entries.Add(new TopLevel { Cluster = clusterIndex, Node = -1 });
        segment.ClusterCount++;
        segment.ClusterPositions.Add(header.Offset - segment.DataStart);
        segment.FirstClusterOffset ??= header.Offset;

        if (crc is { } stored)
        {
            long crcEnd = stored.Offset + stored.HeaderLength + stored.DataLength;
            uint computed = Crc32.Of(_reader, crcEnd, end);
            records.ComputedCrcs[crcIndex] = computed;
            if (computed != (uint)stored.Value)
            {
                _findings.Add(Severity.Warning, "matroska.crc-mismatch", () => new Node("CRC-32", stored.Offset, stored.HeaderLength + stored.DataLength), count =>
                    $"{(count == 1 ? "A CRC-32 element does" : $"{count} CRC-32 elements do")} not match the data {(count == 1 ? "it covers" : "they cover")}: the first, the CRC-32 at 0x{stored.Offset:X} in the Cluster at 0x{header.Offset:X}, stores 0x{(uint)stored.Value:X8} but the {Bytes(end - crcEnd)} after it give 0x{computed:X8}, so the Cluster is damaged or was edited without updating it.");
            }
        }

        if (!cluster.HasTimestamp)
        {
            _findings.Add(Severity.Warning, "matroska.cluster-timestamp-missing", () => ClusterAnchor(cluster, end), count =>
                $"{(count == 1 ? "A Cluster holds" : $"{count} Clusters hold")} no Timestamp element, which RFC 9559 requires (section 5.1.3.1), so its blocks' timestamps are relative to 0; the first Cluster is at 0x{header.Offset:X}.");
        }

        if (unknown)
        {
            _findings.Add(Severity.Info, "matroska.cluster-size-unknown", () => ClusterAnchor(cluster, end), count =>
                $"{(count == 1 ? "A Cluster's size is" : $"{count} Clusters' sizes are")} unknown (all bits set), as a stream written live leaves {(count == 1 ? "it" : "them")}; {(count == 1 ? "it" : "each")} ends where the next Top-Level Element starts (RFC 8794, section 6.2); the first is at 0x{header.Offset:X}.");
        }

        return end;
    }

    // An Element ID coded longer than it needs to be, or a size coded in more octets than EBMLMaxSizeLength allows.
    private void CheckHeaderForm(ElementHeader child, ElementDefinition? definition, long end)
    {
        if (!child.IsLongId && !child.IsLongSize)
        {
            return;
        }

        string name = definition?.Name ?? "Unknown Element";
        if (child.IsLongId)
        {
            _findings.Add(Severity.Info, "matroska.element-id-long", () => new Node(name, child.Offset, end - child.Offset), count =>
                $"{(count == 1 ? "An Element ID is" : $"{count} Element IDs are")} not coded at their shortest length, which RFC 8794 requires (section 5); the first is the {name}'s at 0x{child.Offset:X}.");
        }

        if (child.IsLongSize)
        {
            _findings.Add(Severity.Warning, "matroska.size-too-long", () => new Node(name, child.Offset, end - child.Offset), count =>
                $"{(count == 1 ? "An Element Data Size is" : $"{count} Element Data Sizes are")} coded in more octets than the EBML Header's EBMLMaxSizeLength allows, which makes the element invalid (RFC 8794, section 11.2.5); the first is the {name}'s at 0x{child.Offset:X}.");
        }
    }

    private static Node ClusterAnchor(ClusterRecord cluster, long end) => new("Cluster", cluster.Offset, end - cluster.Offset);

    private static string HeaderWhy(HeaderProblem problem) => problem switch
    {
        HeaderProblem.Truncated => "the data ends inside its element header",
        HeaderProblem.NotAnInteger => "it begins with a byte of zero bits, so it is no variable-size integer of eight octets or fewer (RFC 8794, section 4.1)",
        _ => "its Element ID is longer than the document's EBMLMaxIDLength allows or is the reserved value 0xFF (RFC 8794, section 5)",
    };

    private static void AddUnparsedItem(Records records, long start, long end) =>
        records.Items.Add(new Item { Kind = ItemKind.Unparsed, Offset = start, DataLength = end - start });

    // Counts a record, and false once the file holds more than the analysis keeps.
    private bool AccountRecord(Records records)
    {
        if (++_records <= MaxRecords)
        {
            return true;
        }

        if (!_recordsReported)
        {
            _recordsReported = true;
            long at = records.Items.Count > 0 ? records.Items[records.Items.Count - 1].Offset : 0;
            _findings.AddNow(Severity.Warning, "matroska.too-many-elements",
                $"The file holds more than {MaxRecords} blocks and elements inside Clusters; the rest, from 0x{at:X}, is not read.", new Node("Unparsed Data", at, 0));
        }

        return false;
    }

    // Where a child of a Master Element ends: where it says, or at the end of its parent when that is further.
    private long ChildEnd(ElementHeader child, ElementDefinition? definition, long parentEnd, string parentName)
    {
        if (child.IsUnknownSize)
        {
            _findings.Add(Severity.Warning, "matroska.unknown-size-not-allowed", () => new Node(definition?.Name ?? "Unknown Element", child.Offset, parentEnd - child.Offset), count =>
                $"{(count == 1 ? "An element" : $"{count} elements")} of unknown size where the schema allows it only for the Segment and the Cluster (RFC 8794, section 6.2); the first, {definition?.Name ?? "an unknown element"} at 0x{child.Offset:X}, is taken to run to the end of its parent.");
            return parentEnd;
        }

        long declared = child.End!.Value;
        if (declared <= parentEnd)
        {
            return declared;
        }

        string name = definition?.Name ?? "Unknown Element";
        if (declared > _reader.End)
        {
            _findings.Add(Severity.Warning, "matroska.element-truncated", () => new Node(name, child.Offset, parentEnd - child.Offset), count =>
                $"{(count == 1 ? "An element states" : $"{count} elements state")} a size that runs past the end of the file, which is cut short: the {name} at 0x{child.Offset:X} states {Bytes((long)child.Size!.Value)}, {Bytes(declared - _reader.End)} more than the file holds.");
        }
        else
        {
            _findings.Add(Severity.Warning, "matroska.element-overruns-parent", () => new Node(name, child.Offset, parentEnd - child.Offset), count =>
                $"{(count == 1 ? "An element states" : $"{count} elements state")} a size that runs past the end of {(count == 1 ? "its" : "their")} parent (RFC 8794, section 7.7); the {name} at 0x{child.Offset:X} states {Bytes((long)child.Size!.Value)}, {Bytes(declared - parentEnd)} more than the {parentName} has left, and is read only up to the parent's end.");
        }

        return parentEnd;
    }

    private (ulong Value, bool IsValid) ReadUnsigned(ElementHeader child, long end)
    {
        long length = end - child.DataOffset;
        if (length > 8)
        {
            return (0, false);
        }

        return (Ebml.Unsigned(ReadAt(child.DataOffset, (int)length)), true);
    }

    // The value of a number a Cluster holds besides its blocks, in the bits the record keeps: the integer, or the double's bits.
    private long ReadNumberBits(ElementHeader child, long end, ElementType type)
    {
        long length = end - child.DataOffset;
        if (type == ElementType.Float)
        {
            if (length is not (4 or 8))
            {
                return 0;
            }

            byte[] bytes = ReadAt(child.DataOffset, (int)length);
            double d = length == 4 ? System.Buffers.Binary.BinaryPrimitives.ReadSingleBigEndian(bytes) : System.Buffers.Binary.BinaryPrimitives.ReadDoubleBigEndian(bytes);
            return BitConverter.DoubleToInt64Bits(d);
        }

        if (length > 8)
        {
            return 0;
        }

        byte[] data = ReadAt(child.DataOffset, (int)length);
        return type == ElementType.Signed ? Ebml.Signed(data) : (long)Ebml.Unsigned(data);
    }
}
