using Encspect.Core;

namespace Encspect.Formats.Asf;

public sealed partial class AsfParser
{
    // At most this many index entries become nodes; the rest are one node, so an index of millions
    // of entries stays a small tree.
    private const int MaxIndexEntryNodes = 1000;

    // The same for the blocks of an Index Object; the blocks after them are read for the figures but get no nodes.
    private const int MaxIndexBlockNodes = 1000;

    // ---- Simple Index Object (section 6.1) ----

    private static void ParseSimpleIndex(State state, ObjectHeader header, long end)
    {
        (Node node, Fields f) = Begin(state, state.File, header, end, "Simple Index Object", Explain.SimpleIndex, Topics.AsfIndex);
        state.SimpleIndexObjects++;
        Body(state, node, f, 56, () =>
        {
            f.Guid("File ID", Explain.IndexFileId);
            ulong interval = f.Qword("Index Entry Time Interval", Explain.SimpleIndexInterval, Fields.Seconds);
            uint maxPackets = f.Dword("Maximum Packet Count", Explain.MaximumPacketCount);
            uint count = f.Dword("Index Entries Count", Explain.IndexEntriesCount);
            node.Value = NodeValue.Integer(count, "entries", "entry");
            Node countNode = node.Children[^1];
            long fit = f.Remaining / 6;
            if (count > fit)
            {
                state.Findings.AddNow(Severity.Warning, "asf.index-size", $"The Simple Index Object counts {count} entries, but its size leaves room for {fit}.", countNode);
            }

            long listed = Math.Min(count, fit);
            long largest = 0;
            long previous = -1;
            bool ordered = true;
            long beyond = 0;
            long packets = state.Figures?.PacketCount ?? long.MaxValue;
            for (long i = 0; i < listed; i++)
            {
                if (i % 4096 == 0)
                {
                    f.Reader.CancellationToken.ThrowIfCancellationRequested();
                }

                long at = f.Position;
                uint number = f.Reader.ReadUInt32LittleEndian();
                ushort entryPackets = f.Reader.ReadUInt16LittleEndian();
                largest = Math.Max(largest, entryPackets);
                ordered &= number >= previous;
                previous = number;
                if (number >= packets)
                {
                    beyond++;
                }

                if (i < MaxIndexEntryNodes)
                {
                    Node entry = node.Add("Index Entry", at, 6, $"packet {number}, {Plural(entryPackets, "packet", "packets")}", Explain.IndexEntry);
                    entry.Add("Packet Number", at, 4, NodeValue.Integer(number), Explain.IndexPacketNumber);
                    entry.Add("Packet Count", at + 4, 2, NodeValue.Integer(entryPackets), Explain.IndexPacketCount);
                }
            }

            if (listed > MaxIndexEntryNodes)
            {
                long from = f.Position - ((listed - MaxIndexEntryNodes) * 6);
                node.Add("Further Entries", from, (listed - MaxIndexEntryNodes) * 6, NodeValue.Integer(listed - MaxIndexEntryNodes, "entries", "entry"), Explain.FurtherEntries);
            }

            if (listed > 0 && maxPackets != largest)
            {
                state.Findings.AddNow(Severity.Warning, "asf.index-max-packets",
                    $"The Simple Index Object gives {maxPackets} as the largest packet count, but its largest entry is {largest} (section 6.1).", node.Children.First(c => c.Name == "Maximum Packet Count"));
            }

            if (!ordered)
            {
                state.Findings.AddNow(Severity.Warning, "asf.index-order", "The Simple Index Object's packet numbers do not go up.", countNode);
            }

            if (beyond > 0)
            {
                state.Findings.AddNow(Severity.Warning, "asf.index-packet-number", $"{beyond} entries of the Simple Index Object name a packet past the last one ({packets} packets).", countNode, (int)Math.Min(beyond, int.MaxValue));
            }

            if (interval == 0 && count > 0)
            {
                state.Findings.AddNow(Severity.Warning, "asf.index-interval", "The Simple Index Object states an interval of 0.", node.Children.First(c => c.Name == "Index Entry Time Interval"));
            }

            if (f.Remaining > 0 && count <= fit)
            {
                long extra = f.Remaining;
                f.Bytes("Extra Data", extra, Explain.ExtraData);
            }
        });
    }

    // ---- Index Object (6.2), Media Object Index Object (6.3), Timecode Index Object (6.4) ----

    private static void ParseIndex(State state, ObjectHeader header, long end)
    {
        bool timecode = header.Id == AsfGuids.TimecodeIndex;
        bool media = header.Id == AsfGuids.MediaObjectIndex;
        string name = timecode ? "Timecode Index Object" : media ? "Media Object Index Object" : "Index Object";
        string description = timecode ? Explain.TimecodeIndex : media ? Explain.MediaObjectIndex : Explain.IndexObject;
        (Node node, Fields f) = Begin(state, state.File, header, end, name, description, Topics.AsfIndex);
        state.IndexObjects++;
        Body(state, node, f, 34, () =>
        {
            uint interval = timecode
                ? f.Dword("Reserved", Explain.TimecodeIndexReserved)
                : f.Dword(media ? "Index Entry Count Interval" : "Index Entry Time Interval", media ? Explain.IndexEntryCountInterval : Explain.IndexEntryTimeInterval, media ? null : "ms");
            ushort specifierCount = f.Word("Index Specifiers Count", Explain.IndexSpecifiersCount);
            uint blockCount = f.Dword("Index Blocks Count", Explain.IndexBlocksCount);
            node.Value = Plural(blockCount, "block", "blocks");
            if (timecode && interval != 1)
            {
                state.Findings.AddNow(Severity.Info, "asf.index-reserved", $"The Timecode Index Object's reserved field is {interval}; the specification sets it to 1.", node.Children[^3]);
            }

            if (!timecode && interval == 0 && blockCount > 0)
            {
                state.Findings.AddNow(Severity.Warning, "asf.index-interval", $"The {name} states an interval of 0.", node.Children[^3]);
            }

            var specifiers = new List<(ushort Stream, ushort Type)>();
            for (int i = 0; i < specifierCount; i++)
            {
                long at = f.Position;
                ushort stream = f.Reader.ReadUInt16LittleEndian();
                ushort type = f.Reader.ReadUInt16LittleEndian();
                specifiers.Add((stream, type));
                Node specifier = node.Add("Index Specifier", at, 4, $"stream {stream}, {IndexTypeName(type)}", Explain.IndexSpecifier);
                specifier.Add("Stream Number", at, 2, NodeValue.Integer(stream), Explain.IndexSpecifierStream);
                specifier.Add("Index Type", at + 2, 2, NodeValue.Text($"{type} ({IndexTypeName(type)})"), Explain.IndexType);
            }

            if (!media && !timecode && state.IndexParametersNode is not null && !specifiers.SequenceEqual(state.IndexSpecifiers))
            {
                state.Findings.AddNow(Severity.Warning, "asf.index-specifiers",
                    "The Index Object's index specifiers differ from those of the Index Parameters Object in the header, which they must match exactly (section 4.9).", node);
            }
            else if (!media && !timecode && state.IndexParametersNode is null)
            {
                state.Findings.AddNow(Severity.Warning, "asf.index-parameters-missing", "The file holds an Index Object but no Index Parameters Object in its header, which the specification requires (section 4.9).", node);
            }

            int entryNodes = 0;
            long dataBytes = state.Figures is { } figures ? figures.PacketBytes : 0;
            long invalidOffsets = 0;
            long unalignedOffsets = 0;
            long outsideOffsets = 0;
            long oversizedBlocks = 0;
            Node? oversizedNode = null;
            bool oversizedHere = false;
            long unshownStart = -1;
            long unshownBlocks = 0;
            void Tally(uint[] offsets, List<ulong> positions)
            {
                for (int s = 0; s < offsets.Length; s++)
                {
                    if (offsets[s] == 0xFFFFFFFF)
                    {
                        invalidOffsets++;
                        continue;
                    }

                    // A frame number offset (index type 0xFF) counts frames, not bytes (section 4.10).
                    if (specifiers.Count > s && specifiers[s].Type == 0xFF)
                    {
                        continue;
                    }

                    ulong absolute = positions.Count > s ? positions[s] + offsets[s] : offsets[s];
                    if (dataBytes > 0 && absolute >= (ulong)dataBytes)
                    {
                        outsideOffsets++;
                    }
                    else if (state.PacketSize > 0 && absolute % (ulong)state.PacketSize != 0)
                    {
                        unalignedOffsets++;
                    }
                }
            }

            for (uint b = 0; b < blockCount; b++)
            {
                f.Reader.CancellationToken.ThrowIfCancellationRequested();
                if (f.Remaining < 4)
                {
                    state.Findings.AddNow(Severity.Warning, "asf.index-size", $"The {name} counts {blockCount} index blocks, but its data holds {b}.", node.Children[^1]);
                    break;
                }

                long blockAt = f.Position;
                if (b >= MaxIndexBlockNodes)
                {
                    if (unshownStart < 0)
                    {
                        unshownStart = blockAt;
                    }

                    unshownBlocks++;
                    if (b % 4096 == 0)
                    {
                        f.Reader.CancellationToken.ThrowIfCancellationRequested();
                    }

                    uint count = f.Reader.ReadUInt32LittleEndian();
                    if (timecode)
                    {
                        f.Reader.ReadUInt16LittleEndian();
                    }

                    var blockPositions = new List<ulong>(specifierCount);
                    for (int s = 0; s < specifierCount; s++)
                    {
                        blockPositions.Add(f.Reader.ReadUInt64LittleEndian());
                    }

                    long size = (timecode ? 4 : 0) + (4L * specifierCount);
                    long room = size == 0 ? 0 : f.Remaining / size;
                    if (count > room)
                    {
                        oversizedBlocks++;
                    }

                    var rawOffsets = new uint[specifierCount];
                    for (long e = 0, n = Math.Min(count, room); e < n; e++)
                    {
                        if (e % 4096 == 0)
                        {
                            f.Reader.CancellationToken.ThrowIfCancellationRequested();
                        }

                        if (timecode)
                        {
                            f.Reader.ReadUInt32LittleEndian();
                        }

                        for (int s = 0; s < specifierCount; s++)
                        {
                            rawOffsets[s] = f.Reader.ReadUInt32LittleEndian();
                        }

                        Tally(rawOffsets, blockPositions);
                    }

                    continue;
                }

                var block = new Node("Index Block", blockAt, 0) { Description = Explain.IndexBlock };
                var bf = new Fields(f.Reader, block);
                uint entries = bf.Dword("Index Entry Count", Explain.IndexBlockEntryCount);
                if (timecode)
                {
                    bf.Word("Timecode Range", Explain.TimecodeRange);
                }

                var positions = new List<ulong>();
                long positionsAt = bf.Position;
                for (int s = 0; s < specifierCount; s++)
                {
                    positions.Add(bf.Reader.ReadUInt64LittleEndian());
                }

                if (specifierCount > 0)
                {
                    block.Add("Block Positions", positionsAt, specifierCount * 8L, string.Join(", ", positions), Explain.BlockPositions);
                }

                int offsetsPerEntry = specifierCount;
                long entrySize = (timecode ? 4 : 0) + (4L * offsetsPerEntry);
                long entriesAt = bf.Position;
                long fit = entrySize == 0 ? 0 : bf.Remaining / entrySize;
                oversizedHere = entries > fit;
                if (oversizedHere)
                {
                    oversizedBlocks++;
                }

                long listed = Math.Min(entries, fit);
                Node entryList = block.Add("Index Entries", entriesAt, listed * entrySize, NodeValue.Integer(listed, "entries", "entry"), Explain.IndexEntries);
                for (long e = 0; e < listed; e++)
                {
                    if (e % 4096 == 0)
                    {
                        bf.Reader.CancellationToken.ThrowIfCancellationRequested();
                    }

                    long at = bf.Position;
                    uint code = timecode ? bf.Reader.ReadUInt32LittleEndian() : 0;
                    var offsets = new uint[offsetsPerEntry];
                    for (int s = 0; s < offsetsPerEntry; s++)
                    {
                        offsets[s] = bf.Reader.ReadUInt32LittleEndian();
                    }

                    Tally(offsets, positions);

                    if (entryNodes++ < MaxIndexEntryNodes)
                    {
                        string text = string.Join(", ", offsets.Select(o => o == 0xFFFFFFFF ? "invalid" : o.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        Node entry = entryList.Add("Index Entry", at, entrySize, timecode ? $"timecode 0x{code:X8}: {text}" : text, Explain.IndexEntryOffsets);
                        long offsetAt = at;
                        if (timecode)
                        {
                            entry.Add("Timecode", offsetAt, 4, NodeValue.Hex(code, 8), Explain.IndexTimecode);
                            offsetAt += 4;
                        }

                        for (int s = 0; s < offsetsPerEntry; s++)
                        {
                            entry.Add("Offset", offsetAt + (4L * s), 4, offsets[s] == 0xFFFFFFFF ? NodeValue.Text("0xFFFFFFFF (invalid)") : NodeValue.Integer(offsets[s], "bytes"), Explain.IndexOffset);
                        }
                    }
                }

                var shown = new Node("Index Block", blockAt, bf.Position - blockAt, NodeValue.Integer(entries, "entries", "entry")) { Description = Explain.IndexBlock };
                foreach (Node child in block.Children)
                {
                    shown.Add(child);
                }

                node.Add(shown);
                if (oversizedHere)
                {
                    oversizedNode ??= shown;
                }
            }

            Node? further = null;
            if (unshownBlocks > 0)
            {
                further = node.Add("Further Index Blocks", unshownStart, f.Position - unshownStart, NodeValue.Integer(unshownBlocks, "blocks", "block"), Explain.FurtherIndexBlocks);
            }

            if (oversizedBlocks > 0)
            {
                state.Findings.AddNow(Severity.Warning, "asf.index-size", $"{oversizedBlocks} index blocks of the {name} count more entries than its data leaves room for (the first is the node it points to).", oversizedNode ?? further ?? node, (int)Math.Min(oversizedBlocks, int.MaxValue));
            }

            if (outsideOffsets > 0)
            {
                state.Findings.AddNow(Severity.Warning, "asf.index-offset", $"{outsideOffsets} offsets of the {name} point past the data packets.", node, (int)Math.Min(outsideOffsets, int.MaxValue));
            }

            if (unalignedOffsets > 0)
            {
                state.Findings.AddNow(Severity.Warning, "asf.index-offset",
                    $"{unalignedOffsets} offsets of the {name} do not point to the start of a packet of {state.PacketSize} bytes (section 6.2).", node, (int)Math.Min(unalignedOffsets, int.MaxValue));
            }

            _ = invalidOffsets;
        });
    }
}
