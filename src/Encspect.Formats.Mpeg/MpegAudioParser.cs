using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Mpeg;

// MPEG-1, 2 and 2.5 audio, Layers I to III, at frame-header level. A header starts a stream only
// when two more frames of the same stream follow at the positions its length predicts, or when
// its frames end exactly at the end of the data (plan, "MPEG sync"). Once in sync, frames are
// followed by their lengths and never searched inside; anything that is not a frame is a gap.
public sealed class MpegAudioParser : IFormatParser
{
    private const int SniffLimit = 1 << 20;

    // Free-format frames are found from the next matching header; 640 kbps at 32000 Hz is the
    // largest frame LAME writes, and this leaves room for Layer I.
    private const int MaxFreeFormatLength = 8192;

    public string Name => "MPEG Audio";

    public int Sniff(ByteReader reader)
    {
        var blocks = new BlockReader(reader);
        long limit = Math.Min(reader.End, reader.Start + SniffLimit);
        return FindStreamStart(blocks, reader.Start, limit, reader.End, out _) >= 0 ? 90 : 0;
    }

    public void Parse(ParseContext context)
    {
        var blocks = new BlockReader(context.Reader);
        var findings = new FindingCollector();
        long position = context.Reader.Start;

        foreach (Node? tag in context.EmbeddedTags.OrderBy(t => t.Offset).Append<Node?>(null))
        {
            long end = tag?.Offset ?? context.Reader.End;
            if (end > position)
            {
                ParseSegment(blocks, context.Root, position, end, findings);
            }

            position = tag?.End ?? position;
        }

        findings.Flush(context);
    }

    private static void ParseSegment(BlockReader blocks, Node root, long start, long end, FindingCollector findings)
    {
        Node region = root.Add("MPEG Audio", start, end - start);
        int frames = 0;
        long position = start;
        MpegHeader? stream = null;
        int freeLength = 0;

        while (position < end)
        {
            long at = position;
            if (stream is { } current && blocks.TryReadUInt32(at, out uint raw) && new MpegHeader(raw) is { IsValid: true } header)
            {
                if (header.IsSameStream(current))
                {
                    position = AddFrame(blocks, region, at, end, header, freeLength, findings);
                    if (header.ChannelModeBits != current.ChannelModeBits)
                    {
                        findings.Add(Severity.Info, "mpeg.channel-mode-changed", region.Children[^1], count =>
                            $"The channel mode changes {Times(count)}, first from {current.ChannelMode} to {header.ChannelMode} at 0x{at:X}.");
                    }

                    stream = header;
                    frames++;
                    continue;
                }

                if (FindStreamStart(blocks, at, at + 1, end, out int newFreeLength) == at)
                {
                    freeLength = newFreeLength;
                    stream = header;
                    position = AddFrame(blocks, region, at, end, header, freeLength, findings);
                    findings.Add(Severity.Info, "mpeg.stream-changed", region.Children[^1], count =>
                        $"The stream changes {Times(count)}, first from {current.StreamName} to {header.StreamName} at 0x{at:X}.");
                    frames++;
                    continue;
                }
            }

            long next = FindStreamStart(blocks, stream is null ? position : position + 1, end, end, out int foundFreeLength);
            long gapEnd = next < 0 ? end : next;
            if (gapEnd > position)
            {
                AddGap(blocks, region, position, gapEnd, frames == 0, next < 0 && frames > 0);
            }

            if (next < 0)
            {
                break;
            }

            position = next;
            blocks.TryReadUInt32(position, out uint first);
            stream = new MpegHeader(first);
            freeLength = foundFreeLength;
            if (stream.Value.IsFreeFormat)
            {
                findings.Add(Severity.Info, "mpeg.free-format", null, _ =>
                    $"The stream is free format: frames of {foundFreeLength} bytes plus padding.");
            }
        }

        region.Value = $"{frames} frames";

        void AddGap(BlockReader reader, Node parent, long gapStart, long gapEnd, bool leading, bool trailing)
        {
            long length = gapEnd - gapStart;
            Node gap = parent.Add("Gap", gapStart, length, $"{length} bytes");
            if (leading && !trailing && gapEnd < end)
            {
                bool zero = Enumerable.Range(0, (int)Math.Min(length, 1 << 20)).All(i => reader.ByteAt(gapStart + i) == 0);
                string what = zero ? " (all zero bytes)" : string.Empty;
                findings.AddNow(Severity.Warning, "mpeg.leading-junk", $"{length} bytes before the first frame are not audio{what}.", gap);
            }
            else if (leading)
            {
                return;
            }
            else if (trailing)
            {
                findings.AddNow(Severity.Warning, "mpeg.trailing-data", $"{length} bytes after the last frame at 0x{gapStart:X} are not a frame.", gap);
            }
            else
            {
                findings.AddNow(Severity.Warning, "mpeg.sync-lost", $"Sync is lost for {length} bytes at 0x{gapStart:X}.", gap);
            }
        }
    }

    // Adds the frame at position and returns where the next one starts.
    private static long AddFrame(BlockReader blocks, Node region, long position, long end, MpegHeader header, int freeLength, FindingCollector findings)
    {
        int length = header.FrameLength(freeLength);
        long available = Math.Min(length, end - position);
        var frame = region.Add("Frame", position, available, header.Summary);

        Node fields = frame.Add("Header", position, 4);
        fields.Add("Version", position + 1, 1, header.VersionName);
        fields.Add("Layer", position + 1, 1, header.LayerName);
        fields.Add("IsProtected", position + 1, 1, Bool(header.IsProtected));
        fields.Add("Bitrate", position + 2, 1, header.BitrateName);
        fields.Add("Sample Rate", position + 2, 1, $"{header.SampleRate} Hz");
        fields.Add("HasPadding", position + 2, 1, Bool(header.Padding == 1));
        fields.Add("IsPrivate", position + 2, 1, Bool(header.IsPrivate));
        fields.Add("Channel Mode", position + 3, 1, header.ChannelMode);
        fields.Add("Mode Extension", position + 3, 1, header.ModeExtensionName);
        fields.Add("IsCopyrighted", position + 3, 1, Bool(header.IsCopyrighted));
        fields.Add("IsOriginal", position + 3, 1, Bool(header.IsOriginal));
        Node emphasis = fields.Add("Emphasis", position + 3, 1, header.Emphasis);

        if (header.EmphasisBits == 2)
        {
            findings.Add(Severity.Warning, "mpeg.emphasis-reserved", emphasis, count =>
                $"{Frames(count)} use the reserved emphasis value, the first at 0x{position:X}.");
        }

        if (!header.IsLayer2BitrateModeAllowed)
        {
            findings.Add(Severity.Warning, "mpeg.bitrate-mode-not-allowed", frame, count =>
                $"{Frames(count)} combine Layer II at {header.Bitrate} kbps with {header.ChannelMode}, which ISO/IEC 11172-3 does not allow, the first at 0x{position:X}.");
        }

        long dataStart = position + 4;
        if (header.IsProtected && available >= 6)
        {
            byte[] crcBytes = blocks.Read(position + 4, 2);
            ushort stored = (ushort)((crcBytes[0] << 8) | crcBytes[1]);
            Node crc = frame.Add("CRC", position + 4, 2, $"0x{stored:X4}");
            dataStart += 2;

            int coverage = header.CrcCoverage;
            if (coverage < 0)
            {
                findings.Add(Severity.Info, "mpeg.crc-not-verified", crc, count =>
                    $"{Frames(count)} carry a CRC that is not verified yet; Layer II CRC coverage depends on the allocation tables.");
            }
            else if (6 + coverage <= available)
            {
                byte[] headerTail = blocks.Read(position + 2, 2);
                byte[] covered = blocks.Read(position + 6, coverage);
                ushort computed = Crc16.Compute(covered, Crc16.Compute(headerTail));
                crc.Add("Computed", position + 4, 2, $"0x{computed:X4}");
                if (computed != stored)
                {
                    findings.Add(Severity.Warning, "mpeg.crc-mismatch", crc, count =>
                        $"{Frames(count)} fail the CRC check, the first at 0x{position:X}.");
                }
            }
        }

        if (available > dataStart - position)
        {
            frame.Add("Data", dataStart, position + available - dataStart, $"{position + available - dataStart} bytes");
        }

        if (available < length)
        {
            findings.AddNow(Severity.Warning, "mpeg.frame-truncated", $"The last frame at 0x{position:X} has {available} of its {length} bytes.", frame);
        }

        return position + available;
    }

    // The first offset in [from, limit) where a stream starts, or -1. A stream starts at a valid
    // header followed by two more frames of the same stream, or whose frames end exactly at end.
    private static long FindStreamStart(BlockReader blocks, long from, long limit, long end, out int freeLength)
    {
        freeLength = 0;
        for (long position = from; position < limit; position++)
        {
            if (blocks.ByteAt(position) != 0xFF || !blocks.TryReadUInt32(position, out uint raw))
            {
                continue;
            }

            var header = new MpegHeader(raw);
            if (!header.IsValid)
            {
                continue;
            }

            foreach (int candidate in FreeLengths(blocks, position, header, end))
            {
                if (IsChain(blocks, position, header, candidate, end))
                {
                    freeLength = candidate;
                    return position;
                }
            }
        }

        return -1;
    }

    // Possible unpadded lengths of a free-format frame: distances to later headers of the same
    // stream. For other frames the length comes from the header, so there is one dummy value.
    private static IEnumerable<int> FreeLengths(BlockReader blocks, long position, MpegHeader header, long end)
    {
        if (!header.IsFreeFormat)
        {
            yield return 0;
            yield break;
        }

        long limit = Math.Min(end - 4, position + MaxFreeFormatLength);
        for (long next = position + 4; next <= limit; next++)
        {
            if (blocks.ByteAt(next) == 0xFF && blocks.TryReadUInt32(next, out uint raw)
                && new MpegHeader(raw) is { IsValid: true } candidate && candidate.IsSameStream(header))
            {
                yield return (int)(next - position) - (header.Padding * header.SlotSize);
            }
        }
    }

    private static bool IsChain(BlockReader blocks, long position, MpegHeader first, int freeLength, long end)
    {
        long next = position;
        MpegHeader header = first;
        for (int frame = 0; frame < 3; frame++)
        {
            next += header.FrameLength(freeLength);
            if (next == end)
            {
                return true;
            }

            if (frame == 2)
            {
                return true;
            }

            if (next > end || !blocks.TryReadUInt32(next, out uint raw) || new MpegHeader(raw) is not { IsValid: true } following
                || !following.IsSameStream(first))
            {
                return false;
            }

            header = following;
        }

        return true;
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Frames(int count) => count == 1 ? "1 frame" : $"{count.ToString(CultureInfo.InvariantCulture)} frames";

    private static string Times(int count) => count == 1 ? "once" : $"{count.ToString(CultureInfo.InvariantCulture)} times";

    // Findings of one kind are reported once per stream with a count, pointing at the first case.
    private sealed class FindingCollector
    {
        private readonly Dictionary<string, (Severity Severity, Node? Node, int Count, Func<int, string> Message)> _aggregated = [];
        private readonly List<Finding> _immediate = [];

        public void Add(Severity severity, string code, Node? node, Func<int, string> message)
        {
            _aggregated[code] = _aggregated.TryGetValue(code, out var existing)
                ? existing with { Count = existing.Count + 1 }
                : (severity, node, 1, message);
        }

        public void AddNow(Severity severity, string code, string message, Node node) =>
            _immediate.Add(new Finding(severity, code, message, node));

        public void Flush(ParseContext context)
        {
            foreach (Finding finding in _immediate)
            {
                context.AddFinding(finding.Severity, finding.Code, finding.Message, finding.Node);
            }

            foreach ((string code, var entry) in _aggregated)
            {
                context.AddFinding(entry.Severity, code, entry.Message(entry.Count), entry.Node ?? context.Root);
            }
        }
    }
}
