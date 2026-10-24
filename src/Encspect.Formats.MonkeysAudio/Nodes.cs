using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.MonkeysAudio;

// The nodes of the report built from the records when something reads them, and the nodes of the fixed structures. Functions only of the
// records and the header, so the findings can build the node they point to.
internal static class Nodes
{
    // A field of the frame's bytes in the stream (from, to counted from the first frame's first byte), at the file bytes that hold it.
    public static Node Field(string name, long start, long from, long to, NodeValue? value, string description, string? topic = null, NodeKind kind = NodeKind.Detail)
    {
        List<ByteRange> ranges = WordOrder.Ranges(start, from, to);
        return Over(name, ranges, value, description, topic, kind);
    }

    public static Node Over(string name, IReadOnlyList<ByteRange> ranges, NodeValue? value, string description, string? topic = null, NodeKind kind = NodeKind.Detail) =>
        ranges.Count == 0
            ? new Node(name, 0, 0, value) { Description = description, Topic = topic, Kind = kind }
            : new Node(name, ranges[0].Offset, ranges[^1].End - ranges[0].Offset, value) { Description = description, Topic = topic, Kind = kind, Ranges = ranges };

    // The end of the stream bytes the file holds, in whole words: a frame is read only as far as these go.
    public static long Available(Records r) => Math.Max(0, (r.DataLimit - r.DataStart) >> 2 << 2);

    // The bytes of the frame that the file holds and the node covers.
    public static List<ByteRange> FrameRanges(Records r, in Entry e) =>
        e.End <= e.Start || (e.Status & EntryStatus.Misplaced) != 0 ? [] : WordOrder.Ranges(r.DataStart, e.Start, Math.Min(e.End, Available(r)));

    public static Node Entry(Records r, int index) => Frame(r, index);

    public static Node Frame(Records r, int index)
    {
        Entry e = r.Entries[index];
        List<ByteRange> ranges = FrameRanges(r, e);
        NodeValue samples = NodeValue.Integer(e.Blocks, "samples", "sample");
        if (ranges.Count == 0)
        {
            return new Node("Frame", Math.Min(r.DataStart, r.DataLimit), 0, samples) { Kind = NodeKind.Frame, Description = Explain.Frame, Topic = Topics.Frames };
        }

        return new Node("Frame", ranges[0].Offset, ranges[^1].End - ranges[0].Offset, samples, () => FrameChildren(r, index))
        {
            Kind = NodeKind.Frame,
            Description = Explain.Frame,
            Topic = Topics.Frames,
            Ranges = ranges,
        };
    }

    // The frame node over the file bytes of a frame at index, equal to the node the parser builds, for a finding of the decoder.
    public static Node FrameNodeOf(Records r, int index) => Frame(r, index);

    public static Node CrcField(Records r, int index)
    {
        Entry e = r.Entries[index];
        return Field("Frame CRC", r.DataStart, e.Start, e.Start + 4, NodeValue.Hex(e.Crc, 8), Explain.FrameCrc, Topics.Frames);
    }

    // The CRC field of the frame whose bytes are given, equal to the node the parser builds, for a finding of the decoder. dataStart is the file offset of
    // the first frame's first byte.
    public static Node CrcOfFrame(MappedBytes frame, long dataStart, uint crc)
    {
        ByteRange first = frame.FileRanges()[0];
        long groupEnd = Math.Min(first.End, dataStart + ((((first.Offset - dataStart) >> 2) + 1) << 2));
        long firstByte = groupEnd - 1;
        long start = (((firstByte - dataStart) >> 2) << 2) + (3 - ((firstByte - dataStart) & 3));
        return Field("Frame CRC", dataStart, start, start + 4, NodeValue.Hex(crc, 8), Explain.FrameCrc, Topics.Frames);
    }

    private static IReadOnlyList<Node> FrameChildren(Records r, int index)
    {
        Entry e = r.Entries[index];
        long end = Math.Min(e.End, Available(r));
        long start = e.Start;
        var children = new List<Node>();
        long at = start;
        if ((e.Status & EntryStatus.NoStartFields) == 0 && r.HasFrameFields)
        {
            children.Add(Field("Frame CRC", r.DataStart, at, at + 4, NodeValue.Hex(e.Crc, 8), Explain.FrameCrc, Topics.Frames));
            at += 4;
            if ((e.Status & EntryStatus.HasSpecialCodes) != 0 && at + 4 <= end)
            {
                children.Add(Field("Special Frame Codes", r.DataStart, at, at + 4, SpecialCodes(e.Special, r.Channels), Explain.SpecialCodes, Topics.Frames));
                at += 4;
            }
        }

        if (end > at)
        {
            bool isRange = r.HasRangeCoder && r.HasFrameFields;
            children.Add(Field(isRange ? "Range-Coded Data" : "Coded Data", r.DataStart, at, end, NodeValue.Integer(end - at, "bytes"), isRange ? Explain.RangeCodedData : Explain.CodedData, isRange ? Topics.RangeCoding : Topics.Frames));
        }

        return children;
    }

    // The codes a decoder acts on: bit 0 alone for one channel (APE_SPECIAL_FRAME_MONO_SILENCE), all three for two, none for more
    // (APEDecompressCore.cpp, DecodeBlocksToFrameBuffer).
    public static string SpecialCodes(uint codes, int channels)
    {
        if (channels == 1)
        {
            return (codes & 1) != 0 ? "silent" : "none";
        }

        if (channels != 2)
        {
            return codes == 0 ? "none" : "not used with more than two channels";
        }

        var names = new List<string>();
        if ((codes & 1) != 0)
        {
            names.Add("left channel silent");
        }

        if ((codes & 2) != 0)
        {
            names.Add("right channel silent");
        }

        if ((codes & 4) != 0)
        {
            names.Add("pseudo-stereo");
        }

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    // The frames past the ones the report lists, as one node over their bytes.
    public static Node Unlisted(Records r)
    {
        List<ByteRange> ranges = WordOrder.Ranges(r.DataStart, Math.Max(0, r.UnlistedStart), Math.Min(Math.Max(r.UnlistedEnd, r.UnlistedStart), Available(r)));
        return Over("Further Frames", ranges, NodeValue.Integer(r.UnlistedFrames, "frames", "frame"), Explain.Unlisted, Topics.Frames, NodeKind.Frame);
    }

    public static Node SeekEntry(Records r, int index)
    {
        Entry e = r.Entries[index];
        long raw = e.Start + r.DataStart - r.Junk;
        return new Node("Frame Offset", r.SeekTableAt + (4L * index), 4, NodeValue.Integer((uint)raw, "bytes", "byte")) { Description = Explain.SeekEntry, Topic = Topics.SeekTable };
    }

    public static Node SeekTable(Records r, long length, int entries) =>
        new("Seek Table", Math.Min(r.SeekTableAt, r.DataLimit), length, NodeValue.Integer(entries, "entries", "entry"), () => new BlockedChildren([], Math.Min(entries, r.Entries.Count), i => SeekEntry(r, i)))
        {
            Description = Explain.SeekTable,
            Topic = Topics.SeekTable,
        };

    public static string FlagNames(FormatFlags flags)
    {
        var names = new List<string>();
        (FormatFlags Flag, string Name)[] table =
        [
            (FormatFlags.Is8Bit, "8-bit"), (FormatFlags.HasCrc, "frame CRCs"), (FormatFlags.HasPeakLevel, "peak level"), (FormatFlags.Is24Bit, "24-bit"),
            (FormatFlags.HasSeekElements, "seek element count"), (FormatFlags.CreatesWavHeader, "WAV header created on decompression"), (FormatFlags.IsAiff, "AIFF source"),
            (FormatFlags.IsW64, "W64 source"), (FormatFlags.IsSnd, "SND source"), (FormatFlags.IsBigEndian, "big-endian"), (FormatFlags.IsCaf, "CAF source"),
            (FormatFlags.IsSigned8Bit, "signed 8-bit"), (FormatFlags.IsFloatingPoint, "floating point"),
        ];
        foreach ((FormatFlags flag, string name) in table)
        {
            if ((flags & flag) != 0)
            {
                names.Add(name);
            }
        }

        FormatFlags unknown = flags & ~(FormatFlags)0x1FFF;
        if (unknown != 0)
        {
            names.Add(string.Create(CultureInfo.InvariantCulture, $"unknown bits 0x{(ushort)unknown:X4}"));
        }

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    // The nodes of a descriptor file's descriptor and header, over the bytes they came from. md5 is the digest computed over the coded data,
    // or null where the file cannot be read to the end.
    public static Node Descriptor(long at, ApeHeader h, byte[]? computed)
    {
        var node = new Node("Descriptor", at, ApeHeader.DescriptorLength) { Description = Explain.Descriptor, Topic = Topics.File };
        node.Add("Signature", at, 4, h.IsFloat ? "MACF" : "MAC ", Explain.Signature, Topics.File);
        node.Add("File Version", at + 4, 2, ApeHeader.VersionText(h.FileVersion), Explain.FileVersion, Topics.File);
        node.Add("Program Version", at + 6, 2, h.ProgramVersion == 0 ? "not stored" : ApeHeader.VersionText(h.ProgramVersion), Explain.ProgramVersion, Topics.File);
        node.Add("Descriptor Bytes", at + 8, 4, NodeValue.Integer(h.DescriptorBytes, "bytes", "byte"), Explain.DescriptorBytes, Topics.File);
        node.Add("Header Bytes", at + 12, 4, NodeValue.Integer(h.HeaderBytes, "bytes", "byte"), Explain.HeaderBytes, Topics.File);
        node.Add("Seek Table Bytes", at + 16, 4, NodeValue.Integer(h.SeekTableBytes, "bytes", "byte"), Explain.SeekTableBytes, Topics.SeekTable);
        node.Add("WAV Header Data Bytes", at + 20, 4, NodeValue.Integer(h.HeaderDataBytes, "bytes", "byte"), Explain.HeaderDataBytes, Topics.File);
        node.Add("Frame Data Bytes", at + 24, 8, NodeValue.Integer((long)Math.Min(h.FrameDataBytes, long.MaxValue), "bytes", "byte"), Explain.FrameDataBytes, Topics.File);
        node.Add("WAV Terminating Data Bytes", at + 32, 4, NodeValue.Integer(h.TerminatingBytes, "bytes", "byte"), Explain.TerminatingBytes, Topics.File);
        node.Add("MD5", at + 36, 16, Convert.ToHexStringLower(h.Md5.Span), Explain.DescriptorMd5, Topics.DescriptorMd5);
        if (computed is not null)
        {
            node.Add("Computed MD5", at + 36, 16, Convert.ToHexStringLower(computed), Explain.ComputedMd5, Topics.DescriptorMd5);
            node.Add("IsMd5Valid", at + 36, 16, NodeValue.Boolean(h.Md5.Span.SequenceEqual(computed)), Explain.IsMd5Valid, Topics.DescriptorMd5);
        }

        return node;
    }

    public static Node Header(long at, ApeHeader h, int length)
    {
        var node = new Node("Header", at, length) { Description = Explain.Header, Topic = Topics.File };
        node.Add("Compression Level", at, 2, $"{ApeHeader.LevelName(h.CompressionLevel)} ({h.CompressionLevel})", Explain.CompressionLevel, Topics.Prediction);
        node.Add("Format Flags", at + 2, 2, NodeValue.Hex((long)h.Flags, 4), Explain.FormatFlags, Topics.File);
        node.Add("Format Flags Set", at + 2, 2, FlagNames(h.Flags), Explain.FormatFlagNames, Topics.File);
        node.Add("Blocks Per Frame", at + 4, 4, NodeValue.Integer(h.BlocksPerFrame, "samples", "sample"), Explain.BlocksPerFrame, Topics.Frames);
        node.Add("Final Frame Blocks", at + 8, 4, NodeValue.Integer(h.FinalFrameBlocks, "samples", "sample"), Explain.FinalFrameBlocks, Topics.Frames);
        node.Add("Total Frames", at + 12, 4, NodeValue.Integer(h.TotalFrames, "frames", "frame"), Explain.TotalFrames, Topics.Frames);
        node.Add("Bits Per Sample", at + 16, 2, NodeValue.Integer(h.BitsPerSample, "bits"), Explain.BitsPerSample);
        node.Add("Channels", at + 18, 2, NodeValue.Integer(h.Channels, "channels", "channel"), Explain.Channels, Topics.MidSideStereo);
        node.Add("Sample Rate", at + 20, 4, NodeValue.Integer(h.SampleRate, "Hz"), Explain.SampleRate);
        return node;
    }

    public static Node OldHeader(long at, ApeHeader h)
    {
        var node = new Node("Header", at, h.OldHeaderBytes) { Description = Explain.OldHeader, Topic = Topics.File };
        node.Add("Signature", at, 4, "MAC ", Explain.Signature, Topics.File);
        node.Add("File Version", at + 4, 2, ApeHeader.VersionText(h.FileVersion), Explain.FileVersion, Topics.File);
        node.Add("Compression Level", at + 6, 2, $"{ApeHeader.LevelName(h.CompressionLevel)} ({h.CompressionLevel})", Explain.CompressionLevel, Topics.Prediction);
        node.Add("Format Flags", at + 8, 2, NodeValue.Hex((long)h.Flags, 4), Explain.FormatFlags, Topics.File);
        node.Add("Format Flags Set", at + 8, 2, FlagNames(h.Flags), Explain.FormatFlagNames, Topics.File);
        node.Add("Channels", at + 10, 2, NodeValue.Integer(h.Channels, "channels", "channel"), Explain.Channels, Topics.MidSideStereo);
        node.Add("Sample Rate", at + 12, 4, NodeValue.Integer(h.SampleRate, "Hz"), Explain.SampleRate);
        node.Add("WAV Header Data Bytes", at + 16, 4, NodeValue.Integer(h.HeaderDataBytes, "bytes", "byte"), Explain.HeaderDataBytes, Topics.File);
        node.Add("WAV Terminating Data Bytes", at + 20, 4, NodeValue.Integer(h.TerminatingBytes, "bytes", "byte"), Explain.TerminatingBytes, Topics.File);
        node.Add("Total Frames", at + 24, 4, NodeValue.Integer(h.TotalFrames, "frames", "frame"), Explain.TotalFrames, Topics.Frames);
        node.Add("Final Frame Blocks", at + 28, 4, NodeValue.Integer(h.FinalFrameBlocks, "samples", "sample"), Explain.FinalFrameBlocks, Topics.Frames);
        int next = 32;
        if (h.PeakLevel is { } peak)
        {
            node.Add("Peak Level", at + next, 4, NodeValue.Integer(peak), Explain.PeakLevel, Topics.File);
            next += 4;
        }

        if ((h.Flags & FormatFlags.HasSeekElements) != 0 && h.SeekElements is { } elements)
        {
            node.Add("Seek Elements", at + next, 4, NodeValue.Integer(elements, "entries", "entry"), Explain.SeekElements, Topics.SeekTable);
        }

        return node;
    }
}
