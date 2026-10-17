using System.Buffers.Binary;
using System.Text;
using Encspect.Core;

namespace Encspect.Formats.DtsHd;

// The payloads of the chunks of Annex E (A/103 E.2.4 to E.2.16), field by field. A chunk whose size is too small for a field ends there
// with a finding; bytes after the fields are reserved bytes or padding (E.2.1).
internal static class ChunkReader
{
    // A payload of up to this many bytes is read for its fields; tables and stream data are not.
    private const int FieldBytes = 256;

    // Table 5.1's loudspeaker positions of Table E.9 (the bits of Table 7-10 in ETSI TS 102 114): the speakers of each bit, and the number of channels.
    private static readonly Speaker[][] MaskBits =
    [
        [Speaker.FrontCenter],
        [Speaker.FrontLeft, Speaker.FrontRight],
        [Speaker.SideLeft, Speaker.SideRight],
        [Speaker.LowFrequency],
        [Speaker.BackCenter],
        [Speaker.TopFrontLeft, Speaker.TopFrontRight],
        [Speaker.BackLeft, Speaker.BackRight],
        [Speaker.TopFrontCenter],
        [Speaker.TopCenter],
        [Speaker.FrontLeftOfCenter, Speaker.FrontRightOfCenter],
        [Speaker.FrontLeftWide, Speaker.FrontRightWide],
        [Speaker.SideLeft, Speaker.SideRight],
        [Speaker.LowFrequency2],
        [Speaker.TopSideLeft, Speaker.TopSideRight],
        [Speaker.TopBackCenter],
        [Speaker.TopBackLeft, Speaker.TopBackRight],
    ];

    private static readonly string[] FrameRates =
    [
        "0 (not indicated)", "1 (23.976 frames per second)", "2 (24 frames per second)", "3 (25 frames per second)", "4 (29.97 frames per second, drop frame)",
        "5 (29.97 frames per second)", "6 (30 frames per second, drop frame)", "7 (30 frames per second)",
    ];

    public static void Read(State state, ByteReader reader, Node chunk, string id, long payloadStart, long end, bool truncated)
    {
        var cursor = new Cursor(state, reader, chunk, id, payloadStart, end, truncated);
        switch (id)
        {
            case "DTSHDHDR":
                Header(state, cursor);
                break;
            case "FILEINFO":
                cursor.Text("Text", Explain.FileInfoText, null);
                break;
            case "CORESSMD":
                CoreMetadata(cursor);
                break;
            case "EXTSS_MD":
                ExtensionMetadata(state, cursor);
                break;
            case "AUPR-HDR":
                PresentationHeader(state, cursor);
                break;
            case "AUPRINFO":
                cursor.Number("Audio Presentation Index", 1, Explain.PresentationInfoIndex, null);
                cursor.Text("Text", Explain.PresentationInfoText, null);
                break;
            case "NAVI-TBL":
                Table(cursor, true, Explain.NavigationEntries, Explain.NavigationInterval, Explain.NavigationEntrySize, Explain.NavigationData);
                break;
            case "BITSHVTB":
                BitShaving(cursor);
                break;
            case "STRMDATA":
                Stream(state, chunk, payloadStart, end);
                return;
            case "TIMECODE":
                Timecode(cursor);
                break;
            case "BUILDVER":
                BuildVersion(state, cursor);
                break;
            case "BLACKOUT":
                cursor.Opaque("Silence Frame Data", Explain.BlackoutData);
                break;
            case "BRANCHPT":
                Table(cursor, false, Explain.BranchEntries, null, Explain.BranchEntrySize, Explain.BranchData);
                break;
            default:
                cursor.Opaque("Data", Explain.UnknownData);
                return;
        }

        cursor.Rest();
    }

    private static void Stream(State state, Node chunk, long payloadStart, long end)
    {
        if (state.Stream is not null)
        {
            state.Note("dtshd.stream-repeated", "stream", $"The file has more than one STRMDATA chunk; only the first, at 0x{state.Stream.Offset:X}, is analyzed.", chunk);
            return;
        }

        state.Stream = chunk;
        state.StreamStart = payloadStart;
        state.StreamEnd = end;
    }

    private static void Header(State state, Cursor c)
    {
        if (state.Chunks.Count > 0 && state.StreamFlags >= 0)
        {
            state.Note("dtshd.header-repeated", "header", "The file has more than one DTSHDHDR chunk.", c.Chunk);
        }

        c.Number("Version", 4, Explain.HeaderVersion, null);
        int at = c.Position;
        if (c.Has(5))
        {
            byte[] bytes = c.Bytes(5);
            int clockCode = bytes[0] >> 6;
            uint stamp = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1));
            Node code = new("Time Code", c.Offset(at), 5, TimeText(clockCode, stamp)) { Description = Explain.TimeCode, Topic = Topics.File };
            int clock = clockCode switch { 0 => 32000, 1 => 44100, 2 => 48000, _ => 0 };
            code.Add(Node.FromBits("Reference Clock Code", c.Offset(at), 0, 2, clock > 0 ? NodeValue.Integer(clock, "Hz") : $"{clockCode} (reserved)", description: Explain.RefClock, topic: Topics.File));
            code.Add(new Node("Time Stamp", c.Offset(at + 1), 4, NodeValue.Integer(stamp, "samples", "sample")) { Description = Explain.TimeStamp, Topic = Topics.File });
            c.Add(code);
            if (clockCode == 3)
            {
                state.Note("dtshd.reserved-value", "clock", "The reference clock code of the time code is 3, which Table E.4 reserves.", code);
            }
        }

        c.FrameRate("Time Code Frame Rate", Explain.TimeCodeFrameRate, whole: false);

        int flagsAt = c.Position;
        if (!c.Has(2))
        {
            return;
        }

        int flags = BinaryPrimitives.ReadUInt16BigEndian(c.Bytes(2));
        state.StreamFlags = flags;
        Node meta = c.Add(new Node("Stream Metadata", c.Offset(flagsAt), 2, NodeValue.Hex(flags, 4)) { Description = Explain.StreamMetadata, Topic = Topics.File });
        void Flag(string name, int bit, string description, string? topic = null) =>
            meta.Add(Node.FromBits(name, c.Offset(flagsAt), 15 - bit, 16 - bit, NodeValue.Boolean((flags & (1 << bit)) != 0), description: description, topic: topic ?? Topics.File));
        Flag("IsVbr", 0, Explain.IsVbr, Topics.BitrateModes);
        Flag("HasPeakBitrateSmoothing", 1, Explain.HasSmoothing);
        Flag("HasNavigationTable", 2, Explain.HasNavigation);
        Flag("HasCoreSubstream", 3, Explain.HasCore);
        Flag("HasExtensionSubstream", 4, Explain.HasExtension);
        if ((flags & 0xFFE0) != 0)
        {
            meta.Add(Node.FromBits("Reserved", c.Offset(flagsAt), 0, 11, NodeValue.Hex(flags >> 5, 3), description: Explain.ReservedFlags, topic: Topics.File));
            state.Note("dtshd.reserved-value", "stream-flags", $"Bitw_Stream_Metadata has the reserved flags 0x{flags & 0xFFE0:X4} set (Table E.6).", meta);
        }

        int presentationsAt = c.Position;
        if (c.Has(1))
        {
            state.Presentations = c.Bytes(1)[0];
            c.Add(new Node("Audio Presentations", c.Offset(presentationsAt), 1, NodeValue.Integer(state.Presentations, "presentations", "presentation")) { Description = Explain.AudioPresentations, Topic = Topics.File });
        }

        int extensionsAt = c.Position;
        if (c.Has(1))
        {
            int count = c.Bytes(1)[0];
            long substreams = (flags & 0x10) != 0 ? count + 1 : 0;
            c.Add(new Node("Extension Substreams", c.Offset(extensionsAt), 1, NodeValue.Integer(substreams, "extension substreams", "extension substream")) { Description = Explain.ExtensionSubstreams, Topic = Topics.File });
        }
    }

    // The sample count since midnight at the reference clock as a time of day and the samples after the second.
    private static string TimeText(int clockCode, uint stamp)
    {
        int clock = clockCode switch { 0 => 32000, 1 => 44100, 2 => 48000, _ => 0 };
        if (clock == 0)
        {
            return $"{Counts.Of(stamp, "sample", "samples")}";
        }

        long seconds = stamp / clock;
        return $"{seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2} and {Counts.Of(stamp % clock, "sample", "samples")}";
    }

    private static void CoreMetadata(Cursor c)
    {
        c.Number("Maximum Sampling Frequency", 3, Explain.CoreRate, "Hz");
        c.Number("Bit Rate", 2, Explain.CoreBitrate, "kbps");
        c.Mask("Channel Mask", Explain.CoreMask);
        c.Number("Frame Payload", 4, Explain.CorePayload, "bytes");
    }

    private static void ExtensionMetadata(State state, Cursor c)
    {
        c.Number("Average Bit Rate", 3, Explain.ExtensionAverage, "kbps");
        if (state.StreamFlags < 0)
        {
            // Which fields follow depends on the VBR flag of DTSHDHDR, which this chunk comes before.
            c.Opaque("Data", Explain.ExtensionMetadataBeforeHeader);
            return;
        }

        if ((state.StreamFlags & 1) != 0)
        {
            c.Number("Peak Bit Rate", 3, Explain.ExtensionPeak, "kbps");
            c.Number("Peak Bit Rate Smoothing Buffer Size", 2, Explain.ExtensionBuffer, "kb");
        }
        else
        {
            c.Number("Frame Payload", 4, Explain.ExtensionPayload, "bytes");
        }
    }

    private static void PresentationHeader(State state, Cursor c)
    {
        c.Number("Audio Presentation Index", 1, Explain.PresentationIndex, null);
        int flagsAt = c.Position;
        int flags = -1;
        if (c.Has(2))
        {
            flags = BinaryPrimitives.ReadUInt16BigEndian(c.Bytes(2));
            Node meta = c.Add(new Node("Presentation Metadata", c.Offset(flagsAt), 2, NodeValue.Hex(flags, 4)) { Description = Explain.PresentationMetadata, Topic = Topics.File });
            void Flag(string name, int bit, string description) =>
                meta.Add(Node.FromBits(name, c.Offset(flagsAt), 15 - bit, 16 - bit, NodeValue.Boolean((flags & (1 << bit)) != 0), description: description, topic: Topics.File));
            Flag("HasBackwardCompatibleCore", 0, Explain.HasBcCore);
            Flag("IsBackwardCompatibleCoreInExtension", 1, Explain.BcCoreLocation);
            Flag("HasLossless", 2, Explain.HasLossless);
            Flag("HasLbr", 3, Explain.HasLbr);
            if ((flags & 0xFFF0) != 0)
            {
                meta.Add(Node.FromBits("Reserved", c.Offset(flagsAt), 0, 12, NodeValue.Hex(flags >> 4, 3), description: Explain.ReservedFlags, topic: Topics.File));
                state.Note("dtshd.reserved-value", "aupres-flags", $"Bitw_Aupres_Metadata has the reserved flags 0x{flags & 0xFFF0:X4} set (Table E.12).", meta);
            }
        }

        int rate = (int)c.Number("Maximum Sampling Frequency", 3, Explain.PresentationRate, "Hz");
        c.Number("Frames", 4, Explain.FrameCount, "frames");
        c.Number("Samples per Frame", 2, Explain.FrameSamples, "samples");
        c.Number("Original Samples", 5, Explain.OriginalSamples, "samples");
        int mask = c.Mask("Channel Mask", Explain.PresentationMask);
        c.Number("Codec Delay", 2, Explain.CodecDelay, "samples");
        if (flags >= 0 && (flags & 3) == 3)
        {
            c.Number("Core Maximum Sampling Frequency", 3, Explain.BcCoreRate, "Hz");
            c.Number("Core Bit Rate", 2, Explain.BcCoreBitrate, "kbps");
            c.Mask("Core Channel Mask", Explain.BcCoreMask);
        }

        if (flags >= 0 && (flags & 4) != 0)
        {
            c.Number("LSB Trim", 1, Explain.LsbTrim, "percent");
        }

        if (mask >= 0)
        {
            state.Presentation = new Presentation(c.Chunk, rate, ChannelCount((uint)mask), true);
        }
    }

    // Table E.9: the number of channels the bits of a mask name.
    internal static int ChannelCount(uint mask)
    {
        int count = 0;
        for (int bit = 0; bit < MaskBits.Length; bit++)
        {
            if ((mask & (1u << bit)) != 0)
            {
                count += MaskBits[bit].Length;
            }
        }

        return count;
    }

    internal static IReadOnlyList<Speaker> SpeakersOf(uint mask)
    {
        // Where a rear pair (Table E.9's LsrRsr) exists, the pair on the side is the surround pair, as the DTS parser names them.
        var speakers = new List<Speaker>();
        for (int bit = 0; bit < MaskBits.Length; bit++)
        {
            if ((mask & (1u << bit)) == 0)
            {
                continue;
            }

            foreach (Speaker speaker in MaskBits[bit])
            {
                Speaker named = bit == 2 && (mask & 0x40) == 0 ? (speaker == Speaker.SideLeft ? Speaker.BackLeft : Speaker.BackRight) : speaker;
                speakers.Add(speakers.Contains(named) ? Speaker.Discrete : named);
            }
        }

        return speakers;
    }

    private static void Table(Cursor c, bool hasInterval, string entriesDescription, string? intervalDescription, string sizeDescription, string dataDescription)
    {
        long entries = c.Number("Entries", 4, entriesDescription, null);
        if (hasInterval)
        {
            c.Number("Interval", 2, intervalDescription!, "frames");
        }

        long size = c.Number("Entry Size", 1, sizeDescription, "bytes");
        long need = entries * size;
        long available = Math.Min(c.Remaining, need);
        if (available > 0)
        {
            c.Opaque("Entry Data", dataDescription, available);
        }

        if (available < need)
        {
            c.State.Note("dtshd.table-size", c.Id, $"The table of the {c.Id} chunk has {Counts.Of(entries, "entry", "entries")} of {Counts.Of(size, "byte", "bytes")}, more than the chunk holds.", c.Chunk);
        }
    }

    private static void BitShaving(Cursor c)
    {
        c.Number("Audio Presentation Index", 1, Explain.BitShavingIndex, null);
        long entries = c.Number("Entries", 4, Explain.BitShavingEntries, null);
        long need = entries * 5;
        long available = Math.Min(c.Remaining, need);
        if (available > 0)
        {
            c.Opaque("Entry Data", Explain.BitShavingData, available);
        }

        if (available < need)
        {
            c.State.Note("dtshd.table-size", "BITSHVTB", $"The table of the BITSHVTB chunk has {Counts.Of(entries, "entry", "entries")} of 5 bytes, more than the chunk holds.", c.Chunk);
        }
    }

    private static void Timecode(Cursor c)
    {
        c.Number("Clock", 4, Explain.TimecodeClock, "Hz");
        c.FrameRate("Frame Rate", Explain.TimecodeFrameRate, whole: true);
        c.Number("Start", 8, Explain.TimecodeStart, "samples");
        c.Number("Start Residual", 4, Explain.TimecodeStartResidual, null);
        c.Number("Reference", 8, Explain.TimecodeReference, "samples");
        c.Number("Reference Residual", 4, Explain.TimecodeReferenceResidual, null);
    }

    private static void BuildVersion(State state, Cursor c)
    {
        Node? text = c.Text("Text", Explain.BuildVersionText, null);
        if (text?.Value?.ToString() is { Length: > 0 } build)
        {
            state.Context.AddEncoderClaim(new EncoderClaim(build, text, "BUILDVER chunk (\"DTS-HD Encoder Version Identification\", ATSC A/103:2014 Table E.1 and E.2.14)", ClaimRole.Encoder)
            {
                Confidence = Confidence.Low,
            });
        }
    }

    // A cursor over the fields of one chunk's payload, which nodes it adds to the chunk.
    private sealed class Cursor
    {
        private readonly ByteReader _reader;
        private readonly long _start;
        private readonly long _end;
        private readonly byte[] _data;
        private int _at;
        private bool _short;
        private readonly bool _truncated;

        public Cursor(State state, ByteReader reader, Node chunk, string id, long start, long end, bool truncated)
        {
            _truncated = truncated;
            State = state;
            _reader = reader;
            Chunk = chunk;
            Id = id;
            _start = start;
            _end = end;
            int length = (int)Math.Min(FieldBytes, Math.Max(0, end - start));
            _data = length > 0 ? reader.ReadBytesAt(start, length) : [];
        }

        public State State { get; }

        public Node Chunk { get; }

        public string Id { get; }

        public int Position => _at;

        public long Remaining => _end - _start - _at;

        public long Offset(int at) => _start + at;

        public bool Has(int count)
        {
            if (_at + count <= _data.Length && _at + count <= _end - _start)
            {
                return true;
            }

            if (!_short)
            {
                _short = true;
                if (_end - _start < 4 || _truncated)
                {
                    return false;
                }

                State.Note("dtshd.chunk-size", "fields:" + Id, $"The {Id} chunk's size of {Counts.Of(_end - _start, "byte", "bytes")} is too small for its fields; they end at 0x{_start + _at:X}.", Chunk);
            }

            return false;
        }

        public byte[] Bytes(int count)
        {
            byte[] bytes = _data.AsSpan(_at, count).ToArray();
            _at += count;
            return bytes;
        }

        public Node Add(Node node)
        {
            Chunk.Add(node);
            return node;
        }

        public long Number(string name, int bytes, string description, string? unit)
        {
            if (!Has(bytes))
            {
                return 0;
            }

            int at = _at;
            ulong value = 0;
            foreach (byte b in Bytes(bytes))
            {
                value = (value << 8) | b;
            }

            long number = (long)Math.Min(value, long.MaxValue);
            Add(new Node(name, Offset(at), bytes, unit is null ? NodeValue.Integer(number) : NodeValue.Integer(number, unit)) { Description = description, Topic = Topics.File });
            return number;
        }

        // A time code frame rate code of one byte (Table E.5): the low four bits, the high ones being part of a field of their own in DTSHDHDR.
        public void FrameRate(string name, string description, bool whole)
        {
            int at = _at;
            if (!Has(1))
            {
                return;
            }

            int code = Bytes(1)[0];
            int rate = whole ? code : code & 0x0F;
            Node node = Add(new Node(name, Offset(at), 1, rate < FrameRates.Length ? FrameRates[rate] : $"{rate} (reserved)") { Description = description, Topic = Topics.File });
            if (rate >= FrameRates.Length)
            {
                State.Note("dtshd.reserved-value", "rate", $"The time code frame rate {rate} is reserved (Table E.5).", node);
            }
        }

        // A loudspeaker mask of 2 bytes with the speakers it names; -1 when the chunk has no room for it.
        public int Mask(string name, string description)
        {
            if (!Has(2))
            {
                return -1;
            }

            int at = _at;
            int mask = BinaryPrimitives.ReadUInt16BigEndian(Bytes(2));
            Node node = Add(new Node(name, Offset(at), 2, NodeValue.Hex(mask, 4)) { Description = description, Topic = Topics.ChannelArrangements });
            if (mask != 0)
            {
                node.Add(new Node("Speakers", Offset(at), 2, ChannelLayouts.Speakers(SpeakersOf((uint)mask))) { Description = Explain.Speakers, Topic = Topics.ChannelArrangements });
            }

            return mask;
        }

        // A null-terminated string at the cursor, up to the end of the chunk or its first null byte.
        public Node? Text(string name, string description, string? topic)
        {
            int max = (int)Math.Min(Math.Max(0, _end - _start - _at), 1024);
            if (max == 0)
            {
                Has(1);
                return null;
            }

            byte[] bytes = _reader.ReadBytesAt(_start + _at, max);
            int length = Array.IndexOf(bytes, (byte)0);
            bool terminated = length >= 0;
            if (length < 0)
            {
                length = bytes.Length;
            }

            var text = new StringBuilder();
            foreach (char ch in Encoding.UTF8.GetString(bytes, 0, length))
            {
                text.Append(char.IsControl(ch) ? ' ' : ch);
            }

            int at = _at;
            Node node = Add(new Node(name, Offset(at), length + (terminated ? 1 : 0), text.ToString().Trim()) { Description = description, Topic = topic ?? Topics.File });
            _at += length + (terminated ? 1 : 0);
            return node;
        }

        public void Opaque(string name, string description, long? length = null)
        {
            long count = Math.Min(length ?? Remaining, Remaining);
            if (count <= 0)
            {
                return;
            }

            Add(new Node(name, Offset(_at), count, NodeValue.Integer(count, "bytes", "byte")) { Description = description, Topic = Topics.File });
            _at += (int)Math.Min(count, int.MaxValue - _at);
        }

        // What the size covers after the fields: reserved bytes, or one to three padding bytes.
        public void Rest()
        {
            long rest = Remaining;
            if (rest <= 0)
            {
                return;
            }

            Add(new Node(rest <= 3 ? "Padding" : "Reserved", Offset(_at), rest, NodeValue.Integer(rest, "bytes", "byte")) { Description = rest <= 3 ? Explain.Padding : Explain.Reserved, Topic = Topics.File });
        }
    }
}
