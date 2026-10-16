using Encspect.Core;

namespace Encspect.Formats.Matroska;

internal sealed partial class FileReader
{
    // The largest block whose frames are read.
    private const long MaxBlock = 1 << 26;

    // A SimpleBlock or Block (RFC 9559 sections 10.1 and 10.2): its track, timestamp, flags and lacing header are read, and its
    // frames located; the bytes of the frames are read again when a codec parser asks for them. Returns the record's index.
    private int ReadBlock(SegmentState segment, ClusterRecord cluster, ElementHeader element, long end, bool isInGroup)
    {
        Records records = segment.Records;
        long dataLength = end - element.DataOffset;
        var block = new BlockRecord
        {
            Offset = element.Offset,
            DataLength = (uint)Math.Min(dataLength, uint.MaxValue),
            HeaderLength = (byte)element.Length,
            IsInGroup = isInGroup,
        };
        string name = isInGroup ? "Block" : "SimpleBlock";
        if (dataLength > MaxBlock)
        {
            block.Status = BlockStatus.TooLarge;
            _findings.Add(Severity.Info, "matroska.block-too-large", () => new Node(name, element.Offset, end - element.Offset), n =>
                $"{(n == 1 ? "A block holds" : $"{n} blocks hold")} more than the {MaxBlock >> 20} MiB the analysis reads from one block (the frames of an uncompressed video track can be larger); the first is the {name} at 0x{element.Offset:X} with {Bytes(dataLength)}. Its frames are not read.");
            return AddBlock(records, block, name, cluster.Timestamp);
        }

        Span<byte> head = stackalloc byte[16];
        int headCount = (int)Math.Min(dataLength, head.Length);
        _reader.Position = element.DataOffset;
        _reader.Read(head[..headCount]);
        int trackLength = headCount == 0 ? 0 : Ebml.Width(head[0]);
        if (trackLength == 0 || dataLength < trackLength + 3)
        {
            block.Status = BlockStatus.Short;
            block.TrackLength = (byte)Math.Min(trackLength, dataLength);
            block.Track = trackLength != 0 && headCount >= trackLength ? TrackNumberOf(head[..trackLength]) : 0;
            block.FramesDelta = element.Length + (int)dataLength;
            _findings.Add(Severity.Warning, "matroska.block-short", () => new Node(name, element.Offset, end - element.Offset), n =>
                $"{(n == 1 ? "A block is" : $"{n} blocks are")} too short for the track number, timestamp and flags a block starts with (RFC 9559, section 10.1); the first is the {name} at 0x{element.Offset:X} with {Bytes(dataLength)}. No frame is read from {(n == 1 ? "it" : "them")}.");
            return AddBlock(records, block, name, cluster.Timestamp);
        }

        block.TrackLength = (byte)trackLength;
        block.Track = TrackNumberOf(head[..trackLength]);
        block.Timestamp = (short)((head[trackLength] << 8) | head[trackLength + 1]);
        block.Flags = head[trackLength + 2];
        int fixedPart = trackLength + 3;
        long remaining = dataLength - fixedPart;
        Lacing lacing = block.Lacing;
        if (lacing == Lacing.None)
        {
            block.Frames = 1;
            block.FramesDelta = element.Length + fixedPart;
            return AddBlock(records, block, name, cluster.Timestamp);
        }

        if (!TryReadLace(lacing, element.DataOffset + fixedPart, remaining, out int count, out long laceBytes, out string? why))
        {
            block.Status = BlockStatus.LaceInvalid;
            block.FramesDelta = element.Length + fixedPart;
            _findings.Add(Severity.Warning, "matroska.lacing-invalid", () => new Node(name, element.Offset, end - element.Offset), n =>
                $"{(n == 1 ? "A block's" : $"{n} blocks'")} lacing header does not add up (RFC 9559, section 10.3); the first, the {name} at 0x{element.Offset:X} with {LacingName(lacing)} lacing, {why}. Its frames are not read.");
            return AddBlock(records, block, name, cluster.Timestamp);
        }

        block.Frames = (ushort)count;
        block.FramesDelta = element.Length + fixedPart + (int)laceBytes;
        if (count > 1 && !AccountFrames(count, element.Offset, name))
        {
            block.Status = BlockStatus.FramesLimit;
            return AddBlock(records, block, name, cluster.Timestamp);
        }

        if (count == 1)
        {
            block.Status = BlockStatus.LaceSingle;
            _findings.Add(Severity.Warning, "matroska.lacing-single", () => new Node(name, element.Offset, end - element.Offset), n =>
                $"{(n == 1 ? "A block uses" : $"{n} blocks use")} lacing to store a single frame, which RFC 9559 forbids (section 10.3); the first is the {name} at 0x{element.Offset:X}.");
        }
        else if (lacing != Lacing.FixedSize)
        {
            block.Lace = records.LaceSizes.Count;
            foreach (int size in _laceSizes)
            {
                records.LaceSizes.Add(size);
            }
        }

        return AddBlock(records, block, name, cluster.Timestamp);
    }

    // Counts the frames of a laced block, and false once the file's blocks hold more than the analysis keeps: a block of seven bytes
    // can state 256 frames of no bytes.
    private bool AccountFrames(int count, long offset, string name)
    {
        _lacedFrames += count;
        if (_lacedFrames <= MaxLacedFrames)
        {
            return true;
        }

        if (!_lacedFramesReported)
        {
            _lacedFramesReported = true;
            _findings.AddNow(Severity.Warning, "matroska.too-many-elements",
                $"The laced blocks of the file hold more than {MaxLacedFrames} frames; the frames of the blocks from the {name} at 0x{offset:X} on are not read.", new Node(name, offset, 0));
        }

        return false;
    }

    private static uint TrackNumberOf(ReadOnlySpan<byte> vint)
    {
        ulong value = (ulong)vint[0] & (0xFFUL >> vint.Length);
        for (int i = 1; i < vint.Length; i++)
        {
            value = (value << 8) | vint[i];
        }

        return (uint)Math.Min(value, uint.MaxValue);
    }

    private static string LacingName(Lacing lacing) => lacing switch
    {
        Lacing.Xiph => "Xiph",
        Lacing.Ebml => "EBML",
        Lacing.FixedSize => "fixed-size",
        _ => "no",
    };

    private int AddBlock(Records records, BlockRecord block, string name, long clusterTimestamp)
    {
        records.Blocks.Add(block);
        NoteBlock(block, name, clusterTimestamp);
        return records.Blocks.Count - 1;
    }

    // Adds a block to its track's totals.
    private void NoteBlock(BlockRecord block, string name, long clusterTimestamp)
    {
        // A block too short to name its track belongs to none.
        if ((block.Status & (BlockStatus.Short | BlockStatus.TooLarge)) != 0 && block.Track == 0)
        {
            return;
        }

        SegmentState segment = _segment!;
        if (!segment.Statistics.TryGetValue(block.Track, out TrackStatistics? statistics))
        {
            ulong key = segment.Statistics.Count >= MaxTrackStatistics ? ulong.MaxValue : block.Track;
            if (!segment.Statistics.TryGetValue(key, out statistics))
            {
                segment.Statistics[key] = statistics = new TrackStatistics();
            }
        }

        statistics.Blocks++;
        statistics.FirstBlock ??= new Node(name, block.Offset, block.End - block.Offset);
        if (block.Lacing != Lacing.None)
        {
            statistics.Laced = true;
        }

        if (block.IsKeyframe && !block.IsInGroup)
        {
            statistics.Keyframes++;
        }

        if (block.Status is not (BlockStatus.None or BlockStatus.LaceSingle))
        {
            return;
        }

        statistics.Frames += block.Frames;
        statistics.Bytes += block.DataLength - (block.FramesDelta - block.HeaderLength);
        long timestamp = clusterTimestamp + block.Timestamp;
        if (!statistics.HasTimes)
        {
            statistics.HasTimes = true;
            statistics.First = timestamp;
            statistics.FirstOffset = block.FramesOffset;
            statistics.Smallest = timestamp;
            statistics.Largest = timestamp;
        }
        else if (timestamp < statistics.Last)
        {
            statistics.Backwards++;
            statistics.FirstBackwards ??= new Node(name, block.Offset, block.End - block.Offset);
        }

        statistics.Last = timestamp;
        statistics.Smallest = Math.Min(statistics.Smallest, timestamp);
        statistics.Largest = Math.Max(statistics.Largest, timestamp);
        statistics.LastEnd = block.End;
    }

    // The lacing header (RFC 9559 section 10.3) at position: the number of frames, and each frame's size into _laceSizes, the last
    // included. laceBytes is how many bytes the header takes. remaining is what the block holds after its fixed fields. False, with the
    // reason, where the sizes do not add up.
    private bool TryReadLace(Lacing lacing, long position, long remaining, out int count, out long laceBytes, out string? why)
    {
        _laceSizes.Clear();
        count = 0;
        laceBytes = 0;
        why = null;
        if (remaining < 1)
        {
            why = "ends before the number of frames";
            return false;
        }

        _reader.Position = position;
        count = _reader.ReadByte() + 1;
        laceBytes = 1;
        long total = 0;
        switch (lacing)
        {
            case Lacing.FixedSize:
                if ((remaining - 1) % count != 0)
                {
                    why = $"holds {remaining - 1} bytes for {count} frames of one size";
                    return false;
                }

                // Every frame has the same size, so none is stored (see Records.FrameSize).
                return true;

            case Lacing.Xiph:
                for (int i = 0; i < count - 1; i++)
                {
                    long size = 0;
                    while (true)
                    {
                        if (laceBytes >= remaining)
                        {
                            why = "ends inside the frame sizes";
                            return false;
                        }

                        byte value = _reader.ReadByte();
                        laceBytes++;
                        size += value;
                        if (value != 255)
                        {
                            break;
                        }
                    }

                    total += size;
                    if (total > remaining - laceBytes)
                    {
                        why = "names frames larger than the block";
                        return false;
                    }

                    _laceSizes.Add((int)size);
                }

                break;

            default:
                {
                    long previous = 0;
                    for (int i = 0; i < count - 1; i++)
                    {
                        if (laceBytes >= remaining)
                        {
                            why = "ends inside the frame sizes";
                            return false;
                        }

                        byte first = _reader.ReadByte();
                        int width = Ebml.Width(first);
                        if (width == 0 || laceBytes + width > remaining)
                        {
                            why = width == 0 ? "has a frame size that is no variable-size integer" : "ends inside the frame sizes";
                            return false;
                        }

                        long raw = first & (0xFFL >> width);
                        for (int j = 1; j < width; j++)
                        {
                            raw = (raw << 8) | _reader.ReadByte();
                        }

                        laceBytes += width;
                        long size = i == 0 ? raw : previous + (raw - ((1L << ((7 * width) - 1)) - 1));
                        if (size < 0 || size > remaining)
                        {
                            why = size < 0 ? "gives a frame a negative size" : "names frames larger than the block";
                            return false;
                        }

                        total += size;
                        if (total > remaining - laceBytes)
                        {
                            why = "names frames larger than the block";
                            return false;
                        }

                        _laceSizes.Add((int)size);
                        previous = size;
                    }

                    break;
                }
        }

        long last = remaining - laceBytes - total;
        if (last < 0)
        {
            why = "names frames larger than the block";
            return false;
        }

        _laceSizes.Add((int)last);
        return true;
    }

    // A BlockGroup (RFC 9559 section 5.1.3.5): its descendants go into child records, in file order, with their depth below it.
    private Item ReadGroup(SegmentState segment, ClusterRecord cluster, ElementHeader group, long end, out int blocks)
    {
        Records records = segment.Records;
        var item = new Item { Kind = ItemKind.BlockGroup, Id = Schema.BlockGroup, Offset = group.Offset, HeaderLength = (byte)group.Length, DataLength = end - group.DataOffset, Index = records.Children.Count };
        int found = ReadGroupChildren(segment, cluster, group.DataOffset, end, 0, "BlockGroup", item.Offset);
        item.Count = records.Children.Count - item.Index;
        blocks = found;
        if (found == 0)
        {
            _findings.Add(Severity.Warning, "matroska.group-no-block", () => new Node("BlockGroup", group.Offset, end - group.Offset), count =>
                $"{(count == 1 ? "A BlockGroup holds" : $"{count} BlockGroups hold")} no Block, which RFC 9559 requires (section 5.1.3.5.1); the first is at 0x{group.Offset:X}.");
        }
        else if (found > 1)
        {
            _findings.Add(Severity.Warning, "matroska.group-multiple-blocks", () => new Node("BlockGroup", group.Offset, end - group.Offset), count =>
                $"{(count == 1 ? "A BlockGroup holds" : $"{count} BlockGroups hold")} more than one Block, where RFC 9559 allows one (section 5.1.3.5.1); the first is at 0x{group.Offset:X}.");
        }

        return item;
    }

    // Returns how many Blocks the range holds.
    private int ReadGroupChildren(SegmentState segment, ClusterRecord cluster, long start, long end, int depth, string parentName, long groupOffset)
    {
        Records records = segment.Records;
        int blocks = 0;
        long position = start;
        while (position < end)
        {
            _context.CancellationToken.ThrowIfCancellationRequested();
            HeaderProblem problem = Ebml.TryReadHeader(_reader, position, end, segment.Document!.MaxIdLength, segment.Document.MaxSizeLength, out ElementHeader child);
            if (problem != HeaderProblem.None)
            {
                records.Children.Add(new Child { Id = uint.MaxValue, Offset = position, DataLength = end - position, Depth = (byte)depth, Block = -1 });
                _findings.AddNow(Severity.Warning, "matroska.element-header-invalid",
                    $"No element can be read at 0x{position:X}, in the {parentName} at 0x{groupOffset:X}: {HeaderWhy(problem)}. The {Bytes(end - position)} from there are not read.",
                    Unparsed(position, end, "the " + parentName));
                return blocks;
            }

            if (!AccountRecord(records))
            {
                return blocks;
            }

            ElementDefinition? definition = Schema.Find(child.Id);
            long childEnd = ChildEnd(child, definition, end, parentName);
            CheckHeaderForm(child, definition, childEnd);
            var record = new Child { Offset = child.Offset, HeaderLength = (byte)child.Length, DataLength = childEnd - child.DataOffset, Id = (uint)Math.Min(child.Id, uint.MaxValue), Depth = (byte)depth, Block = -1 };
            if (child.Id == Schema.Block && depth == 0)
            {
                record.Block = ReadBlock(segment, cluster, child, childEnd, true);
                blocks++;
            }
            else if (definition is { Type: ElementType.Unsigned or ElementType.Signed or ElementType.Float })
            {
                record.Value = ReadNumberBits(child, childEnd, definition.Type);
            }

            records.Children.Add(record);
            if (definition is not null && ((child.Id == 0x75A1 && depth == 0) || (child.Id == 0xA6 && depth == 1)))
            {
                blocks += ReadGroupChildren(segment, cluster, child.DataOffset, childEnd, depth + 1, definition.Name, child.Offset);
            }

            position = childEnd;
        }

        return blocks;
    }
}
