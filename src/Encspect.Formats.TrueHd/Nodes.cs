using System.Buffers.Binary;
using Encspect.Core;

namespace Encspect.Formats.TrueHd;

// The nodes of a stream, built from the records of the analysis when they are first read. A unit's node is made from its entry, and its fields
// from the bytes kept for it; findings point to the same nodes, which are equal to the ones of the tree in name, offset and length.
internal static class Nodes
{
    public const string UnitName = "Access Unit";
    public const string MajorSyncName = "Major Sync";

    // The node of an entry: an access unit, or a stretch of skipped bytes.
    public static Node Entry(Records r, int index) => r.Entries[index].Kind == EntryKind.Skipped ? Skipped(r, index) : Unit(r, index);

    // The units past the limit of records have no entry; what is said about them points to the node that counts them.
    private static bool IsUnrecorded(Records r, int index) => index >= r.Entries.Count;

    public static Node Skipped(Records r, int index)
    {
        if (IsUnrecorded(r, index))
        {
            return r.Fallback!;
        }

        Entry e = r.Entries[index];
        return new Node("Skipped Data", e.Offset, e.Header, NodeValue.Integer(e.Header, "bytes")) { Description = Explain.SkippedData };
    }

    private static int UnitLength(Records r, int index)
    {
        Entry e = r.Entries[index];
        return (e.Status & UnitStatus.Cut) != 0 ? r.CutLengths.GetValueOrDefault(index, e.Length) : e.Length;
    }

    public static Node Unit(Records r, int index)
    {
        if (IsUnrecorded(r, index))
        {
            return r.Fallback!;
        }

        Entry e = r.Entries[index];
        int length = UnitLength(r, index);
        string description = (e.Status & UnitStatus.Unsynchronized) != 0 ? Explain.UnitBeforeSync : (e.Status & UnitStatus.Cut) != 0 ? Explain.CutUnit : Explain.AccessUnit;
        return new Node(UnitName, e.Offset, length, NodeValue.Integer(length, "bytes"), () => UnitChildren(r, index, length))
        {
            Kind = NodeKind.Frame,
            Description = description,
            Topic = Topics.AccessUnits,
        };
    }

    public static Node MajorSyncOf(Records r, int index)
    {
        if (IsUnrecorded(r, index))
        {
            return r.Fallback!;
        }

        Entry e = r.Entries[index];
        int length = MajorSyncLengthIn(r, index);
        if (length <= 0)
        {
            return Unit(r, index);
        }

        return new Node(MajorSyncName, e.Offset + 4, length) { Description = Explain.MajorSync, Topic = Topics.AccessUnits };
    }

    // A major sync cut short by the end of its unit is a node of the bytes the unit has after its header.
    private static int MajorSyncLengthIn(Records r, int index)
    {
        Entry e = r.Entries[index];
        return Math.Min(e.SyncLength == 0 ? MajorSyncLengthOf(r, index) : e.SyncLength, UnitLength(r, index) - UnitHeaderLength);
    }

    private static int MajorSyncLengthOf(Records r, int index) => MajorSync28;

    private const int MajorSync28 = 28;
    private const int UnitHeaderLength = 4;

    // The node of substream k of a unit: the node findings about it point to. A unit whose substreams the tree does not list gives the unit's own.
    public static Node SubstreamOf(Records r, int index, int k)
    {
        if (IsUnrecorded(r, index))
        {
            return r.Fallback!;
        }

        UnitDetail? detail = r.DetailOf(index);
        if (detail is null || detail.KnownCount <= k)
        {
            return Unit(r, index);
        }

        Entry e = r.Entries[index];
        int length = UnitLength(r, index);
        var layout = new UnitLayout();
        byte[] unitBytes = UnitBytes(r, detail, length);
        layout.Read(unitBytes, detail.KnownCount, detail.KnownIsMlp);
        layout.ClampTo(unitBytes.Length);
        if (!layout.IsDirectoryRead)
        {
            return Unit(r, index);
        }

        DirectoryEntry entry = layout.Entries[k];
        return new Node($"Substream {k}", e.Offset + entry.Start, entry.Stop - entry.Start, NodeValue.Integer(entry.Stop - entry.Start, "bytes")) { Description = Explain.Substream, Topic = Topics.Substreams };
    }

    // The bytes a record keeps, put back at their places in a buffer of the unit's length.
    public static byte[] UnitBytes(Records r, UnitDetail detail, int length)
    {
        ReadOnlySpan<byte> kept = r.Store.Span(detail.Store, detail.Kept);
        if (detail.IsFull)
        {
            return kept.ToArray();
        }

        var bytes = new byte[length];
        int at = 0;
        while (at + 4 <= kept.Length)
        {
            int start = (kept[at] << 8) | kept[at + 1];
            int count = (kept[at + 2] << 8) | kept[at + 3];
            if (start + count <= bytes.Length && at + 4 + count <= kept.Length)
            {
                kept.Slice(at + 4, count).CopyTo(bytes.AsSpan(start));
            }

            at += 4 + count;
        }

        return bytes;
    }

    private static Node Field(string name, long offset, int startBit, int endBit, NodeValue? value, string description, string? topic = null) =>
        Node.FromBits(name, offset, startBit, endBit, value, NodeKind.Detail, description, topic);

    private static List<Node> UnitChildren(Records r, int index, int length)
    {
        Entry e = r.Entries[index];
        var children = new List<Node>();
        long offset = e.Offset;
        uint header = e.Header;
        var unitHeader = new Node("Unit Header", offset, Math.Min(4, length)) { Description = Explain.UnitHeader, Topic = Topics.AccessUnits };
        unitHeader.Add(Field("Check Nibble", offset, 0, 4, NodeValue.Hex(header >> 28, 1), Explain.CheckNibble, Topics.AccessUnits));
        unitHeader.Add(Field("Unit Length", offset, 4, 16, NodeValue.Integer(e.Length, "bytes"), Explain.UnitLength, Topics.AccessUnits));
        unitHeader.Add(new Node("Input Timing", offset + 2, 2, NodeValue.Integer(header & 0xFFFF)) { Description = Explain.InputTiming, Topic = Topics.AccessUnits });
        children.Add(unitHeader);
        UnitDetail? detail = r.DetailOf(index);
        if (detail is null || length < 8)
        {
            if (e.SyncLength > 0 && MajorSyncLengthIn(r, index) > 0)
            {
                children.Add(MajorSyncOf(r, index));
            }

            return children;
        }

        byte[] bytes = UnitBytes(r, detail, length);
        var layout = new UnitLayout();
        layout.Read(bytes, detail.KnownCount, detail.KnownIsMlp);
        layout.ClampTo(bytes.Length);
        if (layout.HasSync && layout.Sync is { } sync)
        {
            children.Add(SyncNode(offset + 4, sync));
        }
        else if (e.SyncLength > 0)
        {
            children.Add(MajorSyncOf(r, index));
        }

        if (!layout.IsDirectoryRead)
        {
            return children;
        }

        children.Add(DirectoryNode(offset, layout, bytes));

        // The parity covers the unit header and the directory, not the major sync between them.
        children.Add(new Node("IsParityValid", offset, layout.DataStart, NodeValue.Boolean((layout.Problems & UnitProblems.Parity) == 0))
        {
            Description = Explain.IsParityValid,
            Topic = Topics.AccessUnits,
            Ranges = [new ByteRange(offset, UnitHeaderLength), new ByteRange(offset + layout.DirectoryStart, layout.DirectoryLength)],
        });
        int samples = detail.InForce?.UnitSamples ?? layout.Sync?.UnitSamples ?? 0;
        for (int k = 0; k < layout.SubstreamCount; k++)
        {
            children.Add(SubstreamNode(r, index, detail, layout, bytes, k, samples));
        }

        return children;
    }

    public static Node SyncNode(long offset, in MajorSync s)
    {
        var node = new Node(MajorSyncName, offset, s.Length) { Description = Explain.MajorSync, Topic = Topics.AccessUnits };
        node.Add(new Node("Sync Word", offset, 3, NodeValue.Hex(MajorSync.SyncWord, 6)) { Description = Explain.SyncWord, Topic = Topics.AccessUnits });
        node.Add(new Node("Stream Type", offset + 3, 1, NodeValue.Hex(s.IsTrueHd ? 0xBA : 0xBB, 2)) { Description = Explain.StreamType, Topic = Topics.AccessUnits });
        var info = new Node("Format Info", offset + 4, 4, NodeValue.Hex(s.FormatInfo, 8)) { Description = Explain.FormatInfo, Topic = Topics.AccessUnits };
        if (s.IsTrueHd)
        {
            info.Add(Field("Sampling Rate", offset, 32, 36, s.SampleRate > 0 ? NodeValue.Integer(s.SampleRate, "Hz") : NodeValue.Hex(s.RateCode, 1), Explain.SamplingRate, Topics.AccessUnits));
            info.Add(Field("6-Channel Multichannel Type", offset, 36, 37, NodeValue.Integer(s.SixChannelType ? 1 : 0), Explain.SixChannelType, Topics.Substreams));
            info.Add(Field("8-Channel Multichannel Type", offset, 37, 38, NodeValue.Integer(s.EightChannelType ? 1 : 0), Explain.EightChannelType, Topics.Substreams));
            info.Add(Field("2-Channel Modifier", offset, 40, 42, NodeValue.Integer(s.TwoChannelModifier), Explain.TwoChannelModifier, Topics.Substreams));
            info.Add(Field("6-Channel Modifier", offset, 42, 44, NodeValue.Integer(s.SixChannelModifier), Explain.SixChannelModifier, Topics.Substreams));
            info.Add(Field("6-Channel Assignment", offset, 44, 49, NodeValue.Hex(s.SixChannelAssignment, 2), Explain.SixChannelAssignment, Topics.Substreams));
            info.Add(Field("8-Channel Modifier", offset, 49, 51, NodeValue.Integer(s.EightChannelModifier), Explain.EightChannelModifier, Topics.Substreams));
            info.Add(Field("8-Channel Assignment", offset, 51, 64, NodeValue.Hex(s.EightChannelAssignment, 4), Explain.EightChannelAssignment, Topics.Substreams));
        }
        else
        {
            info.Add(Field("Group 1 Bit Depth", offset, 32, 36, s.Group1Depth > 0 ? NodeValue.Integer(s.Group1Depth, "bits") : NodeValue.Hex(s.Group1DepthCode, 1), Explain.Group1BitDepth, Topics.AccessUnits));
            info.Add(Field("Group 2 Bit Depth", offset, 36, 40, s.Group2Depth > 0 ? NodeValue.Integer(s.Group2Depth, "bits") : s.Group2DepthCode == 0xF ? "none" : NodeValue.Hex(s.Group2DepthCode, 1), Explain.Group2BitDepth, Topics.AccessUnits));
            info.Add(Field("Group 1 Sampling Rate", offset, 40, 44, s.SampleRate > 0 ? NodeValue.Integer(s.SampleRate, "Hz") : NodeValue.Hex(s.RateCode, 1), Explain.Group1SamplingRate, Topics.AccessUnits));
            info.Add(Field("Group 2 Sampling Rate", offset, 44, 48, s.Group2RateCode == 0xF ? "none" : Tables.RateOf(s.Group2RateCode) is > 0 and var rate ? NodeValue.Integer(rate, "Hz") : NodeValue.Hex(s.Group2RateCode, 1), Explain.Group2SamplingRate, Topics.AccessUnits));
            info.Add(Field("Multichannel Type", offset, 52, 56, NodeValue.Integer(s.MultichannelType), Explain.MultichannelType, Topics.Substreams));
            info.Add(Field("Channel Arrangement", offset, 59, 64, NodeValue.Integer(s.Arrangement), Explain.ChannelArrangement, Topics.Substreams));
        }

        node.Add(info);
        node.Add(new Node("Signature", offset + 8, 2, NodeValue.Hex(s.Signature, 4)) { Description = Explain.Signature, Topic = Topics.AccessUnits });
        node.Add(new Node("Flags", offset + 10, 2, NodeValue.Hex(s.Flags, 4)) { Description = Explain.MajorSyncFlags, Topic = Topics.AccessUnits });
        node.Add(new Node("Reserved", offset + 12, 2, NodeValue.Hex(s.Reserved, 4)) { Description = Explain.ReservedWord, Topic = Topics.AccessUnits });
        node.Add(Field("IsVariableRate", offset, 112, 113, NodeValue.Boolean(s.IsVariableRate), Explain.IsVariableRate, Topics.AccessUnits));
        node.Add(Field("Peak Data Rate", offset, 113, 128, NodeValue.Integer(s.PeakBitsPerSecond, "bits per second"), Explain.PeakDataRate, Topics.AccessUnits));
        node.Add(Field("Substream Count", offset, 128, 132, NodeValue.Integer(s.SubstreamCount, "substreams", "substream"), Explain.SubstreamCount, Topics.Substreams));
        node.Add(Field("Extended Substream Info", offset, 134, 136, NodeValue.Integer(s.ExtendedSubstreamInfo), Explain.ExtendedSubstreamInfo, Topics.Substreams));
        node.Add(new Node("Substream Info", offset + 17, 1, NodeValue.Hex(s.SubstreamInfo, 2)) { Description = Explain.SubstreamInfo, Topic = Topics.Substreams });
        node.Add(new Node("Channel Meaning", offset + 18, 8, NodeValue.Hex(s.ChannelMeaning, 16)) { Description = Explain.ChannelMeaning, Topic = Topics.Substreams });
        if (s.HasExtension)
        {
            node.Add(new Node("Extension", offset + 26, 2 + (2 * s.ExtensionWords), NodeValue.Integer(s.ExtensionWords, "words", "word")) { Description = Explain.Extension, Topic = Topics.Substreams });
        }

        var crc = new Node("CRC", offset + s.Length - 2, 2, NodeValue.Hex(s.StoredCrc, 4)) { Description = Explain.MajorSyncCrc, Topic = Topics.Crc };
        crc.Add(new Node("Computed", offset + s.Length - 2, 2, NodeValue.Hex(s.ComputedCrc, 4)) { Description = Explain.Computed, Topic = Topics.Crc });
        crc.Add(new Node("IsValid", offset + s.Length - 2, 2, NodeValue.Boolean(s.IsCrcValid)) { Description = Explain.IsValid, Topic = Topics.Crc });
        node.Add(crc);
        return node;
    }

    private static Node DirectoryNode(long offset, UnitLayout layout, byte[] bytes)
    {
        var node = new Node("Substream Directory", offset + layout.DirectoryStart, layout.DirectoryLength, NodeValue.Integer(layout.SubstreamCount, "substreams", "substream"))
        {
            Description = Explain.SubstreamDirectory,
            Topic = Topics.Substreams,
        };
        int at = layout.DirectoryStart;
        for (int k = 0; k < layout.SubstreamCount; k++)
        {
            DirectoryEntry e = layout.Entries[k];
            int size = e.HasExtraWord ? 4 : 2;
            long entryOffset = offset + at;
            var entry = new Node($"Substream {k} Entry", entryOffset, size, NodeValue.Integer(e.End * 2, "bytes")) { Description = Explain.DirectoryEntry, Topic = Topics.Substreams };
            entry.Add(Field("HasExtraWord", entryOffset, 0, 1, NodeValue.Boolean(e.HasExtraWord), Explain.HasExtraWord, Topics.Substreams));
            entry.Add(Field("IsNonRestart", entryOffset, 1, 2, NodeValue.Boolean(e.IsNonRestart), Explain.IsNonRestart, Topics.Substreams));
            entry.Add(Field("HasCheckData", entryOffset, 2, 3, NodeValue.Boolean(e.HasCheckData), Explain.HasCheckData, Topics.Substreams));
            entry.Add(Field("Reserved Bit", entryOffset, 3, 4, NodeValue.Integer(e.Reserved ? 1 : 0), Explain.ReservedBit, Topics.Substreams));
            entry.Add(Field("Substream End", entryOffset, 4, 16, NodeValue.Integer(e.End * 2, "bytes"), Explain.SubstreamEnd, Topics.Substreams));
            if (e.HasExtraWord)
            {
                entry.Add(new Node("Extra Word", entryOffset + 2, 2, NodeValue.Hex(e.ExtraWord, 4)) { Description = Explain.ExtraWord, Topic = Topics.Substreams });
            }

            node.Add(entry);
            at += size;
        }

        return node;
    }

    private static Node SubstreamNode(Records r, int index, UnitDetail detail, UnitLayout layout, byte[] bytes, int k, int unitSamples)
    {
        Entry e = r.Entries[index];
        DirectoryEntry entry = layout.Entries[k];
        long start = e.Offset + entry.Start;
        int length = entry.Stop - entry.Start;
        bool isOpaque = k >= Analysis.ParsedSubstreams;
        var node = new Node($"Substream {k}", start, length, NodeValue.Integer(length, "bytes"), () => SubstreamChildren(r, index, detail, layout, bytes, k, unitSamples, start, entry))
        {
            Description = isOpaque ? Explain.SubstreamOpaque : Explain.Substream,
            Topic = Topics.Substreams,
        };
        return node;
    }

    private static List<Node> SubstreamChildren(Records r, int index, UnitDetail detail, UnitLayout layout, byte[] bytes, int k, int unitSamples, long start, DirectoryEntry entry)
    {
        var children = new List<Node>();
        ReadOnlySpan<byte> data = bytes.AsSpan(entry.Start, entry.Stop - entry.Start);
        bool hasCheck = entry.HasCheckData && data.Length >= 2;
        ReadOnlySpan<byte> content = hasCheck ? data[..^2] : entry.HasCheckData ? default : data;
        byte verdict = k < detail.Verdicts.Length ? detail.Verdicts[k] : (byte)0;
        if (k < Analysis.ParsedSubstreams && content.Length > 0)
        {
            if (detail.IsFull && detail.States is { } states && k < states.Length)
            {
                SubstreamState state = states[k].Clone();
                var log = new ParameterLog(start) { EndBit = content.Length * 8 };
                MajorSync? sync = detail.InForce ?? layout.Sync;
                WalkResult walk = SubstreamWalk.Run(state, content, entry.HasCheckData, detail.KnownIsMlp, unitSamples, sync is { } s ? Presentation.MaxListSize(s, k, detail.KnownCount) : 8, default, unitSamples, log);
                children.AddRange(log.Roots);
            }
            else if (content.Length >= 4 && (content[0] & 0xC0) == 0xC0)
            {
                int bits = Math.Min(content.Length, Analysis.HeadBytes) * 8;
                RestartHeader header = RestartHeader.Read(content[..Math.Min(content.Length, Analysis.HeadBytes)], 2);
                if (header.IsSyncValid && 2 + header.TotalBits <= bits)
                {
                    children.Add(RestartHeaderNode(start, header));
                }
            }
        }

        if (hasCheck)
        {
            // The fourth substream's last two bytes are shown as they are: the description does not say that substream follows these rules, so the
            // analyzer does not compare them.
            bool isCompared = k < Analysis.ParsedSubstreams;
            long at = start + data.Length - 2;
            var parity = new Node("Parity Byte", at, 1, NodeValue.Hex(data[^2], 2)) { Description = Explain.ParityByte, Topic = Topics.Crc };
            var crc = new Node("CRC-8", at + 1, 1, NodeValue.Hex(data[^1], 2)) { Description = Explain.SubstreamCrc, Topic = Topics.Crc };
            if (isCompared)
            {
                parity.Add(new Node("IsValid", at, 1, NodeValue.Boolean((verdict & 1) != 0)) { Description = Explain.IsValid, Topic = Topics.Crc });
                crc.Add(new Node("IsValid", at + 1, 1, NodeValue.Boolean((verdict & 2) != 0)) { Description = Explain.IsValid, Topic = Topics.Crc });
            }

            children.Add(parity);
            children.Add(crc);
        }

        return children;
    }

    // The node of a restart header that starts at the bit header.StartBit of the substream whose first byte is at substreamOffset.
    public static Node RestartHeaderNode(long substreamOffset, in RestartHeader h)
    {
        int s = h.StartBit;
        var node = Node.FromBits("Restart Header", substreamOffset, s, s + h.TotalBits, null, NodeKind.Detail, Explain.RestartHeader, Topics.RestartHeader);
        node.Add(Field("Restart Sync", substreamOffset, s, s + 13, NodeValue.Hex(RestartHeader.Sync, 4), Explain.RestartSync, Topics.RestartHeader));
        node.Add(Field("Noise Type", substreamOffset, s + 13, s + 14, NodeValue.Integer(h.NoiseType), Explain.NoiseType, Topics.Rematrixing));
        node.Add(Field("Output Timing", substreamOffset, s + 14, s + 30, NodeValue.Integer(h.OutputTiming), Explain.OutputTiming, Topics.RestartHeader));
        node.Add(Field("First Coded Channel", substreamOffset, s + 30, s + 34, NodeValue.Integer(h.FirstChannel), Explain.FirstCodedChannel, Topics.Substreams));
        node.Add(Field("Last Coded Channel", substreamOffset, s + 34, s + 38, NodeValue.Integer(h.LastChannel), Explain.LastCodedChannel, Topics.Substreams));
        node.Add(Field("Last Matrix Channel", substreamOffset, s + 38, s + 42, NodeValue.Integer(h.LastMatrixChannel), Explain.LastMatrixChannel, Topics.Substreams));
        node.Add(Field("Noise Shift", substreamOffset, s + 42, s + 46, NodeValue.Integer(h.NoiseShift), Explain.NoiseShift, Topics.Rematrixing));
        node.Add(Field("Noise Seed", substreamOffset, s + 46, s + 69, NodeValue.Hex(h.NoiseSeed, 6), Explain.NoiseSeed, Topics.Rematrixing));
        node.Add(Field("Largest Output Shift", substreamOffset, s + 69, s + 73, NodeValue.Integer(h.Skipped4), Explain.LargestOutputShift, Topics.RestartHeader));
        node.Add(Field("Largest LSB Count", substreamOffset, s + 73, s + 78, NodeValue.Integer(h.Skipped5A), Explain.LargestLsbCount, Topics.RestartHeader));
        node.Add(Field("Largest Bit Depth", substreamOffset, s + 78, s + 83, NodeValue.Integer(h.Skipped5B), Explain.LargestBitDepth, Topics.RestartHeader));
        node.Add(Field("Largest Bit Depth, Second", substreamOffset, s + 83, s + 88, NodeValue.Integer(h.Skipped5C), Explain.LargestBitDepth, Topics.RestartHeader));
        node.Add(Field("HasBlockSizeCheck", substreamOffset, s + 88, s + 89, NodeValue.Boolean(h.HasBlockSizeCheck), Explain.HasBlockSizeCheck, Topics.RestartHeader));
        node.Add(LosslessCheckField(substreamOffset, h));
        node.Add(Field("Skipped Bits", substreamOffset, s + 97, s + 113, NodeValue.Hex(h.Skipped16, 4), Explain.RestartSkipped16, Topics.RestartHeader));
        var assignments = Node.FromBits("Channel Assignments", substreamOffset, s + 113, s + 113 + (6 * (h.LastMatrixChannel + 1)), null, NodeKind.Detail, Explain.ChannelAssignments, Topics.Substreams);
        for (int m = 0; m <= h.LastMatrixChannel; m++)
        {
            assignments.Add(Field($"Matrix Channel {m}", substreamOffset, s + 113 + (6 * m), s + 119 + (6 * m), NodeValue.Integer(h.Assignment(m)), Explain.ChannelAssignment, Topics.Substreams));
        }

        node.Add(assignments);
        int crcStart = s + h.Bits;
        var crc = Field("CRC", substreamOffset, crcStart, crcStart + 8, NodeValue.Hex(h.StoredCrc, 2), Explain.RestartCrc, Topics.Crc);
        crc.Add(Field("Computed", substreamOffset, crcStart, crcStart + 8, NodeValue.Hex(h.ComputedCrc, 2), Explain.Computed, Topics.Crc));
        crc.Add(Field("IsValid", substreamOffset, crcStart, crcStart + 8, NodeValue.Boolean(h.IsCrcValid), Explain.IsValid, Topics.Crc));
        node.Add(crc);
        return node;
    }

    // The restart header's lossless check byte: the node the decoder's finding about a differing check points to.
    public static Node LosslessCheckField(long substreamOffset, in RestartHeader h) =>
        Field("Lossless Check", substreamOffset, h.StartBit + 89, h.StartBit + 97, NodeValue.Hex(h.LosslessCheck, 2), Explain.LosslessCheck, Topics.LosslessCheck);

    // The node a finding about the lossless check of the unit being decoded points to: the check byte where the tree lists the restart header, else
    // the unit's own node (the tree lists the detail of some units only), else the stream's (null) for a unit with no node.
    public static Node? LosslessCheckFor(Records r, long substreamOffset, in RestartHeader h)
    {
        int index = r.DecodingIndex;
        if (index < 0 || index >= r.Entries.Count)
        {
            return null;
        }

        UnitDetail? detail = r.DetailOf(index);
        return detail is not null && (detail.IsFull || h.StartBit == 2) ? LosslessCheckField(substreamOffset, h) : Unit(r, index);
    }
}
