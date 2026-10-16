using Encspect.Core;

namespace Encspect.Formats.Vorbis;

// An audio packet: its length, its mode and what the first bits say. 8 bytes; its offset is kept
// apart (Records.OffsetOf).
internal readonly record struct PacketRecord(int Length, byte Mode, PacketFlags Flags)
{
    public bool IsBlock => (Flags & PacketFlags.NoBlock) == 0;

    public bool IsLong => (Flags & PacketFlags.Long) != 0;
}

[Flags]
internal enum PacketFlags : byte
{
    None = 0,
    Long = 1,
    PreviousLong = 2,
    NextLong = 4,

    // Packets that code no block: an empty packet, one whose type bit is 1, one whose mode is
    // missing or undefined, and any packet when the setup header gave no modes.
    Empty = 8,
    NotAudio = 16,
    BadMode = 32,
    Unread = 64,
    NoBlock = Empty | NotAudio | BadMode | Unread,
}

public sealed partial class VorbisParser
{
    private const string TagName = "Vorbis Comment";

    // The records audio packet nodes are built from once the stream is closed.
    private sealed class Records
    {
        private ChunkedList<long>? _offsets = new();
        private double[]? _seriesOffsets;

        public ChunkedList<PacketRecord> Packets { get; } = new();

        // Packets that span pages, by index: their file ranges.
        public Dictionary<int, ByteRange[]> Ranges { get; } = [];


        // For packets a container changed (MappedBytes.IsOpaque), whose parts map to the whole range they come from: the length of that
        // range plus one, 0 for the others. A list of its own, filled to the last such packet, because a stream with many of them keeps
        // four bytes for each instead of a dictionary entry.
        private ChunkedList<int>? _spans;

        public void AddOpaque(int packet, long span)
        {
            _spans ??= new ChunkedList<int>();
            while (_spans.Count < packet)
            {
                _spans.Add(0);
            }

            _spans.Add((int)Math.Min(span + 1, int.MaxValue));
        }

        public bool IsOpaque(int packet) => _spans is { } spans && packet < spans.Count && spans[packet] != 0;

        // The length of the file range an opaque packet comes from.
        public long SpanOf(int packet) => _spans![packet] - 1;

        public int ModeBits { get; set; }

        public int Blocksize0 { get; set; }

        public int Blocksize1 { get; set; }

        public long OffsetOf(int packet) => _seriesOffsets is { } offsets ? (long)offsets[packet] : _offsets![packet];

        public void Add(long offset, PacketRecord record)
        {
            _offsets!.Add(offset);
            Packets.Add(record);
        }

        // The offsets as doubles, which hold every file offset exactly, for the bitrate series; the
        // records read them from there from now on.
        public double[] OffsetsForSeries(CancellationToken cancellationToken)
        {
            var offsets = new double[Packets.Count];
            for (int i = 0; i < offsets.Length; i++)
            {
                if (i % 65536 == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                offsets[i] = _offsets![i];
            }

            _seriesOffsets = offsets;
            _offsets = null;
            return offsets;
        }

        public int BlockSize(PacketRecord packet) => packet.IsLong ? Blocksize1 : Blocksize0;
    }

    private sealed partial class StreamState(CodecContext context)
    {
        private readonly Node _parent = context.Parent;
        private readonly FindingCollector _findings = new();
        private readonly Records _records = new();
        private readonly long[] _modeCounts = new long[64];

        // A container that holds the headers apart (CodecContext.Configuration, Matroska's CodecPrivate) and hands them over as the first
        // packets has no Ogg pages, so nothing is said of where they lie on a page.
        private readonly bool _isContained = context.Configuration is not null;

        private Identification? _identification;
        private Node? _identificationNode;
        private SetupResult? _setup;
        private Node? _comment;
        private int _seen;
        private bool _hasComment;
        private bool _hasSetup;

        // Audio packets: counts, bytes per block size, and the stretch of the file they cover.
        private int _packets;
        private long _bytes;
        private long _longBlocks;
        private long _shortBlocks;
        private readonly long[] _blockBytes = new long[2];
        private readonly int[] _smallest = [int.MaxValue, int.MaxValue];
        private readonly int[] _largest = new int[2];
        private readonly HashSet<int> _sizes = [];
        private long _audioStart = -1;
        private long _audioEnd;

        // The overlap rule (section 4.3.8): a block returns a quarter of the previous block and a
        // quarter of its own, the first block nothing. The granule positions (section A.2) count the
        // samples returned up to a page's last packet; the first audio page's may be offset from
        // that count, and the last page's may be smaller, where the decoder trims the end.
        private int _previousIndex = -1;
        private long _decoded;
        private long _firstReturned;
        private long _secondReturned;
        private int _blocks;
        private long? _firstOffset;
        private long? _offset;
        private bool _afterGap;
        private long? _lastPosition;
        private long _lastPositionDecoded;
        private bool _lastIsEnd;
        private Node? _lastPositionField;
        private Node? _firstPositionField;
        private int _firstPositionPacket;

        private bool HasTime => _identification is { HasTime: true } && _setup is { HasModes: true };

        private long Rate => _identification?.Rate ?? 0;

        public void Add(MappedBytes packet, int index)
        {
            _seen = index + 1;
            if (index == 0)
            {
                AddIdentification(packet);
            }
            else if (index == 1)
            {
                CheckHeaderGranule(packet, "comment");
                if (IsHeader(packet, 3))
                {
                    AddComment(packet);
                }
                else
                {
                    UnexpectedHeader(packet, "comment header (type 3)", "second");
                }
            }
            else if (index == 2)
            {
                CheckHeaderGranule(packet, "setup");
                if (IsHeader(packet, 5))
                {
                    AddSetup(packet);
                }
                else
                {
                    UnexpectedHeader(packet, "setup header (type 5)", "third");
                }
            }
            else
            {
                AddAudio(packet);
            }
        }

        private static bool IsHeader(MappedBytes packet, byte type) =>
            packet.Length >= 7 && packet.Span[0] == type && packet.Span[1..7].SequenceEqual("vorbis"u8);

        private void UnexpectedHeader(MappedBytes packet, string expected, string ordinal)
        {
            Node node = _parent.Add(Node.FromMapped("Unexpected Packet", packet, 0, packet.Length, NodeValue.Integer(packet.Length, "bytes", "byte"), description: Explain.UnexpectedPacket));
            _findings.Add(Severity.Warning, "vorbis.header-missing", node, _ =>
                $"The stream's {ordinal} packet is not the {expected} the Vorbis I specification puts there (section 4.2.1); decoders refuse the stream, and its audio packets are only counted.");
        }

        // Section A.2: pages that hold only headers have the granule position 0.
        private void CheckHeaderGranule(MappedBytes packet, string header)
        {
            if (packet.EndPosition is { } position && position != 0 && packet.EndPositionField is { } field)
            {
                _findings.Add(Severity.Warning, "vorbis.header-granule", field, _ =>
                    $"The page that ends the {header} header has the granule position {position}, where pages of headers have 0 (Vorbis I specification, section A.2).");
            }
        }

        private void AddIdentification(MappedBytes packet)
        {
            CheckHeaderGranule(packet, "identification");
            _identification = Identification.Read(packet.Span);
            if (_identification is not { } id)
            {
                Node node = _parent.Add(Node.FromMapped("Identification Header", packet, 0, packet.Length, NodeValue.Integer(packet.Length, "bytes", "byte"),
                    description: Explain.IdentificationShort, topic: Topics.VorbisBlocks));
                _findings.Add(Severity.Warning, "vorbis.header-short", node, _ =>
                    $"The identification header holds {packet.Length} bytes, fewer than its {Identification.Length}; decoders refuse the stream, and its audio packets are only counted.");
                return;
            }

            _identificationNode = _parent.Add(id.ToNode(packet));
            _records.Blocksize0 = id.Blocksize0;
            _records.Blocksize1 = id.Blocksize1;

            // Section A.2: the identification header is alone on the first page.
            if (!_isContained && packet.EndPosition is null)
            {
                _findings.Add(Severity.Warning, "vorbis.page-layout", _identificationNode, _ =>
                    "The identification header shares its page with other packets, where the Vorbis I specification puts it on a page of its own (section A.2).");
            }

            if (packet.Length > Identification.Length)
            {
                Node extra = _parent.Add(Node.FromMapped("Identification Extra Data", packet, Identification.Length, packet.Length,
                    NodeValue.Integer(packet.Length - Identification.Length, "bytes", "byte"), description: Explain.IdentificationExtraData));
                _findings.Add(Severity.Info, "vorbis.header-extra-data", extra, _ =>
                    $"The identification header holds {packet.Length - Identification.Length} bytes after its framing bit, which decoders ignore.");
            }

            CheckIdentification(id);
        }

        // What libvorbis 1.3.7 checks when it unpacks the header (info.c, _vorbis_unpack_info).
        private void CheckIdentification(Identification id)
        {
            if (id.Version != 0)
            {
                _findings.Add(Severity.Warning, "vorbis.version", Field("Vorbis Version"), _ =>
                    $"The identification header gives the Vorbis version {id.Version}; Vorbis I requires 0, and decoders refuse other versions, so the audio packets are only counted.");
            }

            if (id.Channels == 0)
            {
                _findings.Add(Severity.Warning, "vorbis.channels-invalid", Field("Channels"), _ =>
                    "The identification header gives 0 channels; the Vorbis I specification requires at least one, and decoders refuse the stream.");
            }

            if (id.Rate == 0 || id.Rate > int.MaxValue)
            {
                _findings.Add(Severity.Warning, "vorbis.rate-invalid", Field("Sample Rate"), _ =>
                    $"The identification header gives a sample rate of {id.Rate} Hz, which decoders refuse, so the stream has no duration.");
            }

            if (!id.AreBlocksizesValid)
            {
                _findings.Add(Severity.Warning, "vorbis.blocksize-invalid", Field(id.Blocksize0Exponent is >= 6 and <= 13 ? "Blocksize 1" : "Blocksize 0"), _ =>
                    $"The identification header gives the block sizes {1L << id.Blocksize0Exponent} and {1L << id.Blocksize1Exponent}; the Vorbis I specification allows 64 to 8192 with the first not larger than the second (section 4.2.2), so decoders refuse the stream and its audio packets are only counted.");
            }

            if (!id.HasFraming)
            {
                _findings.Add(Severity.Warning, "vorbis.framing-missing", Field("Framing Bit"), _ =>
                    "The identification header's framing bit is 0; libvorbis 1.3.7 refuses the header.");
            }
        }

        private Node Field(string name) => _identificationNode!.Children.First(c => c.Name == name);

        // Section 5.2.1: the comment follows "\x03vorbis", and a framing bit follows the comment.
        private void AddComment(MappedBytes packet)
        {
            _hasComment = true;
            Node prefix = Node.FromMapped("Comment Header", packet, 0, 7, "\\x03vorbis", description: Explain.CommentPrefix);
            _parent.Add(prefix);
            MappedBytes body = packet.Slice(7);
            _comment = body.Length > 0 ? context.ParseTag(TagName, body, _parent) : null;
            int end = _comment is null ? 7 : 7 + BytesBefore(body, _comment.End);
            if (end < packet.Length)
            {
                bool framing = (packet.Span[end] & 1) != 0;
                Node node = _parent.Add(Node.FromBits("Framing Bit", packet, end * 8L, (end * 8L) + 1, NodeValue.Boolean(framing), description: Explain.FramingBit,
                    order: BitOrder.LeastSignificantFirst));
                if (!framing)
                {
                    _findings.Add(Severity.Warning, "vorbis.framing-missing", node, _ => "The comment header's framing bit is 0; libvorbis 1.3.7 refuses the header.");
                }

                if (end + 1 < packet.Length)
                {
                    Node trailing = _parent.Add(Node.FromMapped("Comment Trailing Data", packet, end + 1, packet.Length, NodeValue.Integer(packet.Length - end - 1, "bytes", "byte"),
                        description: Explain.CommentTrailingData));
                    _findings.Add(Severity.Info, "vorbis.comment-trailing-data", trailing, _ =>
                        $"The comment header holds {packet.Length - end - 1} bytes after its framing bit, which readers ignore.");
                }
            }
            else
            {
                _findings.Add(Severity.Warning, "vorbis.framing-missing", _comment ?? prefix, _ =>
                    "The comment header ends without its framing bit; libvorbis 1.3.7 refuses the header.");
            }
        }

        // How many of the packet's bytes lie before a file offset.
        private static int BytesBefore(MappedBytes packet, long offset)
        {
            int count = 0;
            foreach (ByteRange range in packet.FileRanges())
            {
                if (range.End <= offset)
                {
                    count += (int)range.Length;
                    continue;
                }

                count += (int)Math.Max(0, offset - range.Offset);
                break;
            }

            return count;
        }

        private void AddSetup(MappedBytes packet)
        {
            _hasSetup = true;
            _setup = SetupDecoder.Decode(packet, _identification?.Channels ?? 0, context.CancellationToken);
            _parent.Add(_setup.Node);
            foreach (Problem problem in _setup.Problems)
            {
                _findings.Add(problem.Severity, problem.Code, problem.Node, count => count == 1 ? problem.Message : $"{problem.Message} ({count} such problems in the setup header.)");
            }

            // Section A.2: the setup header ends its page, so the first audio packet starts a new one.
            if (!_isContained && packet.EndPosition is null)
            {
                _findings.Add(Severity.Warning, "vorbis.page-layout", _setup.Node, _ =>
                    "The setup header does not end its page, where the Vorbis I specification starts the audio on a fresh page (section A.2).");
            }

            if (_setup.HasModes)
            {
                _records.ModeBits = _setup.ModeBits;
            }
        }

        private void AddAudio(MappedBytes packet)
        {
            int index = _records.Packets.Count;
            PacketRecord record = Read(packet);
            _records.Add(packet.FileStart, record);
            if (packet.IsOpaque)
            {
                _records.AddOpaque(index, packet.FileEnd - packet.FileStart);
            }
            else if (packet.FileEnd - packet.FileStart != packet.Length)
            {
                _records.Ranges[index] = [.. packet.FileRanges()];
            }

            _packets++;
            _bytes += packet.Length;
            _sizes.Add(packet.Length);
            _audioStart = _audioStart < 0 ? packet.FileStart : _audioStart;
            _audioEnd = Math.Max(_audioEnd, packet.FileEnd);
            _afterGap |= packet.FollowsGap;

            switch (record.Flags & PacketFlags.NoBlock)
            {
                case PacketFlags.Empty:
                    _findings.Add(Severity.Warning, "vorbis.empty-packet", PacketNode(_records, index), count =>
                        $"{Count(count, "audio packet is", "audio packets are")} empty; decoders skip {(count == 1 ? "it" : "them")}. The first at 0x{packet.FileStart:X}.");
                    break;
                case PacketFlags.NotAudio:
                    _findings.Add(Severity.Warning, "vorbis.not-audio", PacketNode(_records, index), count =>
                        $"{Count(count, "packet", "packets")} after the headers {(count == 1 ? "has" : "have")} the packet type bit set, so {(count == 1 ? "it is" : "they are")} not audio; decoders skip {(count == 1 ? "it" : "them")} (section 4.3.1). The first at 0x{packet.FileStart:X}.");
                    break;
                case PacketFlags.BadMode:
                    _findings.Add(Severity.Warning, "vorbis.mode-invalid", PacketNode(_records, index), count =>
                        $"{Count(count, "audio packet names", "audio packets name")} a mode the setup header does not define, or end{(count == 1 ? "s" : string.Empty)} before the mode and window flags; decoders skip {(count == 1 ? "it" : "them")} (section 4.3.1). The first at 0x{packet.FileStart:X}.");
                    break;
                case PacketFlags.None:
                    AddBlock(index, record);
                    break;
            }

            TrackPosition(packet);
        }

        // Section 4.3.1: the packet type bit, the mode number in ilog(modes - 1) bits, and for a
        // long block the previous and next window flags.
        private PacketRecord Read(MappedBytes packet)
        {
            if (packet.Length == 0)
            {
                return new PacketRecord(0, 0, PacketFlags.Empty);
            }

            ReadOnlySpan<byte> data = packet.Span;
            if ((data[0] & 1) != 0)
            {
                return new PacketRecord(packet.Length, 0, PacketFlags.NotAudio);
            }

            if (_setup is not { HasModes: true } setup || _identification is not { IsUsable: true })
            {
                return new PacketRecord(packet.Length, 0, PacketFlags.Unread);
            }

            var bits = new BitReader(packet.Data, BitOrder.LeastSignificantFirst);
            bits.Read(1);
            int mode = bits.Read(setup.ModeBits);
            if (bits.IsOverrun || mode >= setup.ModeIsLong!.Length)
            {
                return new PacketRecord(packet.Length, (byte)Math.Min(mode, 255), PacketFlags.BadMode);
            }

            if (!setup.ModeIsLong[mode])
            {
                return new PacketRecord(packet.Length, (byte)mode, PacketFlags.None);
            }

            int previous = bits.Read(1);
            int next = bits.Read(1);
            if (bits.IsOverrun)
            {
                return new PacketRecord(packet.Length, (byte)mode, PacketFlags.BadMode | PacketFlags.Long);
            }

            return new PacketRecord(packet.Length, (byte)mode, PacketFlags.Long | (previous == 1 ? PacketFlags.PreviousLong : 0) | (next == 1 ? PacketFlags.NextLong : 0));
        }

        private void AddBlock(int index, PacketRecord record)
        {
            _modeCounts[record.Mode]++;
            int kind = record.IsLong ? 1 : 0;
            if (record.IsLong)
            {
                _longBlocks++;
            }
            else
            {
                _shortBlocks++;
            }

            _blockBytes[kind] += record.Length;
            _smallest[kind] = Math.Min(_smallest[kind], record.Length);
            _largest[kind] = Math.Max(_largest[kind], record.Length);

            int size = _records.BlockSize(record);
            long returned = 0;
            if (_previousIndex >= 0)
            {
                PacketRecord previous = _records.Packets[_previousIndex];
                returned = (_records.BlockSize(previous) / 4) + (size / 4);
                CheckWindows(index, record, _previousIndex, previous);
            }

            _blocks++;
            if (_blocks == 2)
            {
                _secondReturned = returned;
            }

            _firstReturned = _blocks == 1 ? returned : _firstReturned;
            _decoded += returned;
            _previousIndex = index;
        }

        // The window flags of a long block say what size its neighbors are, and decoders shape the
        // window's halves by them (section 4.3.1), so a flag that does not match the neighbor
        // shapes the overlapping halves differently.
        private void CheckWindows(int index, PacketRecord current, int previousIndex, PacketRecord previous)
        {
            if (_afterGap)
            {
                return;
            }

            bool previousSaysLong = (current.Flags & PacketFlags.PreviousLong) != 0;
            bool nextSaysLong = (previous.Flags & PacketFlags.NextLong) != 0;
            if (current.IsLong && previousSaysLong != previous.IsLong)
            {
                WindowMismatch(index, "previous", previous.IsLong);
            }

            if (previous.IsLong && nextSaysLong != current.IsLong)
            {
                WindowMismatch(previousIndex, "next", current.IsLong);
            }
        }

        private void WindowMismatch(int index, string which, bool isLong)
        {
            _findings.Add(Severity.Warning, "vorbis.window-flag-mismatch", PacketNode(_records, index), count =>
                $"{Count(count, "window flag does", "window flags do")} not match the size of the neighboring block: the first, in the packet at 0x{_records.OffsetOf(index):X}, gives a {(isLong ? "short" : "long")} {which} block where it is {(isLong ? "long" : "short")}. Decoders shape the window by the flag (section 4.3.1), so the overlapping halves differ.");
        }

        // Section A.2: a page's granule position is the number of the last sample its last packet
        // returns. The first audio page's may differ from the count of returned samples: a larger
        // one starts the stream later in a longer program, a smaller one discards samples at the
        // start; the last page's may be smaller, which trims the end.
        private void TrackPosition(MappedBytes packet)
        {
            if (packet.EndPosition is not { } position || !HasTime)
            {
                return;
            }

            long offset = position - _decoded;
            if (_firstOffset is null)
            {
                // A stream whose only position is the last one: a smaller position trims the end.
                _firstOffset = _offset = packet.IsEndOfStream ? Math.Max(0, offset) : offset;
                _firstPositionField = packet.EndPositionField;
                _firstPositionPacket = _packets - 1;
            }
            else if (_afterGap)
            {
                _offset = offset;
            }
            else if (offset != _offset && !packet.IsEndOfStream)
            {
                long expected = _decoded + _offset!.Value;
                Node field = packet.EndPositionField ?? _parent;
                _findings.Add(Severity.Warning, "vorbis.granule-inconsistent", field, count =>
                    $"{Count(count, "granule position does", "granule positions do")} not match the samples the packets before {(count == 1 ? "it" : "them")} return: the first, at 0x{field.Offset:X}, is {position} where the packets give {expected}. Only the last page may end the stream early (section A.2).");
            }

            _afterGap = false;
            _lastPosition = position;
            _lastPositionDecoded = _decoded;
            _lastIsEnd = packet.IsEndOfStream;
            _lastPositionField = packet.EndPositionField;
        }

        public void Finish()
        {
            if (_seen == 0)
            {
                return;
            }

            CheckPackets();
            long startTrim = StartTrimming();
            long trim = EndTrimming();
            long samples = HasTime ? Math.Clamp(_decoded - startTrim - trim, 0, _decoded) : 0;
            Node? encoder = AddEncoder();
            Node? writer = AddWriter();
            ClaimRoles.CompleteVersion(encoder, CommentClaims());
            Node? anchor = AddStatistics(samples, startTrim, trim);
            Node? packets = AddPackets();

            if (_identification is { } id)
            {
                context.AddStream(new StreamInfo
                {
                    Nodes = [_parent],
                    Codec = "Vorbis",
                    SampleRate = (int)Math.Min(id.Rate, int.MaxValue),
                    Channels = id.Channels,
                    ChannelMode = id.Channels > 0 ? ChannelMode(id.Channels) : null,
                    ChannelLayout = VorbisParser.Speakers(id.Channels),
                    Samples = samples,
                    Duration = HasTime ? samples / (double)Rate : 0,
                    AverageBitrate = HasTime && _decoded > 0 ? _bytes * 8.0 * Rate / _decoded / 1000 : null,
                    IsVbr = _sizes.Count > 1,
                    Encoder = ClaimRoles.Resolved(encoder).Name,
                    EncoderConfidence = ClaimRoles.Resolved(encoder).Confidence,
                    Writer = ClaimRoles.Resolved(writer).Name,
                    WriterConfidence = ClaimRoles.Resolved(writer).Confidence,
                    Series = Series(anchor, packets),
                });
            }

            _findings.Flush(context);
        }

        private void CheckPackets()
        {
            Node anchor = _identificationNode ?? _parent;
            if (!_hasComment && _seen < 2)
            {
                _findings.Add(Severity.Warning, "vorbis.header-missing", anchor, _ =>
                    "The stream ends after its identification header, without the comment and setup headers Vorbis requires; decoders refuse it.");
            }
            else if (!_hasSetup && _seen < 3)
            {
                _findings.Add(Severity.Warning, "vorbis.header-missing", anchor, _ =>
                    "The stream ends before its setup header, which Vorbis requires; decoders refuse it.");
            }

            if (_packets == 0)
            {
                _findings.Add(Severity.Info, "vorbis.no-audio", anchor, _ => "The stream holds no audio packets.");
            }
        }

        // A first granule position below the samples returned discards samples at the start, at
        // most what the first two blocks return (section A.2); a larger one starts the stream later.
        private long StartTrimming()
        {
            if (_firstOffset is not { } offset || offset == 0)
            {
                return 0;
            }

            Node field = _firstPositionField ?? _parent;

            // Section A.2: with a start offset, the second audio packet ends its page, so a decoder
            // adjusts positions before it returns samples.
            if (_firstPositionPacket != 1)
            {
                _findings.Add(Severity.Warning, "vorbis.start-page-layout", field, _ =>
                    $"The first audio page with a granule position ends after audio packet {_firstPositionPacket + 1}, where a stream that does not start at sample 0 ends it after the second (section A.2); decoders may report wrong positions at the start.");
            }

            if (offset > 0)
            {
                _findings.Add(Severity.Info, "vorbis.start-offset", field, _ =>
                    $"The first granule position, {offset} samples beyond what the packets before it return, starts the stream at a later time, as in a stream cut from a longer one (section A.2).");
                return 0;
            }

            // libvorbisfile discards no more than the limit, which oggdec's output confirms.
            long limit = _firstReturned + _secondReturned;
            if (-offset > limit)
            {
                _findings.Add(Severity.Warning, "vorbis.start-trimming-large", field, _ =>
                    $"The first granule position discards {-offset} samples at the start, more than the {limit} the first two audio packets return, which the Vorbis I specification allows at most (section A.2); libvorbisfile discards {limit}.");
            }

            return Math.Min(Math.Min(-offset, limit), _decoded);
        }

        // What the last granule position cuts from the end: the end trimming.
        private long EndTrimming()
        {
            if (_lastPosition is not { } position || _offset is not { } offset || !_lastIsEnd)
            {
                return 0;
            }

            long trim = _lastPositionDecoded + offset - position;
            Node field = _lastPositionField ?? _parent;
            if (trim < 0)
            {
                _findings.Add(Severity.Warning, "vorbis.granule-beyond-packets", field, _ =>
                    $"The last granule position, {position} at 0x{field.Offset:X}, is {-trim} samples beyond what the packets before it return; decoders end the stream earlier than it says.");
                return 0;
            }

            return trim;
        }
    }
}
