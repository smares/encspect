using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Opus;

public sealed partial class OpusParser
{
    private const string TagName = "Vorbis Comment";

    // The records audio packet nodes are built from once the stream is closed: per packet its
    // offset, and per stream of each packet its part, StreamCount parts in a row.
    private sealed class Records(int streams)
    {
        private ChunkedList<long>? _offsets = new();
        private double[]? _seriesOffsets;

        public int StreamCount { get; } = streams;

        // Only the parts that were read: after a stream whose framing broke, the packet's remaining streams are implied as missing,
        // so a hostile multistream file costs no more per packet than the bytes it holds. With several streams, _firstParts holds
        // per packet the index of its first part.
        public ChunkedList<PartRecord> Parts { get; } = new();

        private readonly ChunkedList<int> _firstParts = new();

        public ChunkedList<ushort> FrameLengths { get; } = new();

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

        public int Count { get; private set; }

        public long OffsetOf(int packet) => _seriesOffsets is { } offsets ? (long)offsets[packet] : _offsets![packet];

        public void AddPacket(long offset)
        {
            _offsets!.Add(offset);
            Count++;
        }

        // Called before the packet's parts are added.
        public void StartParts()
        {
            if (StreamCount > 1)
            {
                _firstParts.Add(Parts.Count);
            }
        }

        private int FirstPart(int packet) => StreamCount == 1 ? packet : _firstParts[packet];

        public int PartCount(int packet) => StreamCount == 1 ? 1 : (packet + 1 < _firstParts.Count ? _firstParts[packet + 1] : Parts.Count) - _firstParts[packet];

        public PartRecord Part(int packet, int stream) =>
            stream < PartCount(packet) ? Parts[FirstPart(packet) + stream] : new PartRecord(0, 0, 0, 0, 0, 0, 0, 0, PartStatus.Missing);

        public int LengthOf(int packet)
        {
            int length = 0;
            for (int s = 0; s < PartCount(packet); s++)
            {
                length += Part(packet, s).Length;
            }

            return length;
        }

        // The samples at 48 kHz the packet codes, by its first stream's TOC.
        public int SamplesOf(int packet) => Part(packet, 0).Samples;

        public double[] OffsetsForSeries(CancellationToken cancellationToken)
        {
            var offsets = new double[Count];
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
    }

    private sealed partial class StreamState(CodecContext context)
    {
        private readonly Node _parent = context.Parent;
        private readonly FindingCollector _findings = new();
        private Records _records = new(1);

        // A container that holds the identification header apart (CodecContext.Configuration, Matroska's CodecPrivate) hands it over as the
        // first packet and has no comment header and no Ogg pages (RFC 7845 sections 3 and 5.2 describe the Ogg encapsulation).
        private readonly bool _isContained = context.Configuration is not null;

        private OpusHead? _head;
        private Node? _headNode;
        private Node? _comment;
        private bool _hasTags;
        private int _seen;

        // Audio packets: counts and the stretch of the file they cover.
        private int _packets;
        private long _bytes;
        private int _smallest = int.MaxValue;
        private int _largest;
        private int _lastSize = -1;
        private bool _sizesDiffer;
        private long _audioStart = -1;
        private long _audioEnd;

        // Per stream part with a TOC: stereo and mono, and the frame count codes; per frame of a
        // valid part: its mode, bandwidth and size; frames without data, and padding bytes.
        private readonly long[] _stereo = new long[2];
        private readonly long[] _codes = new long[4];
        private readonly long[] _modes = new long[3];
        private readonly long[] _bandwidths = new long[5];
        private readonly SortedDictionary<int, long> _frameSizes = [];
        private long _frames;
        private long _emptyFrames;
        private long _padding;
        private long _parts;

        // Granule positions (RFC 7845, section 4): samples at 48 kHz up to a page's last packet,
        // the pre-skip included, offset by where the stream starts; the last page's may be
        // smaller, which trims the end.
        private long _decoded;
        private int _lastPacketSamples;
        private long? _firstOffset;
        private long? _offset;
        private bool _afterGap;
        private long? _lastPosition;
        private long _lastPositionDecoded;
        private bool _lastIsEnd;
        private Node? _lastPositionField;
        private Node? _firstPositionField;

        private bool HasHead => _head is not null;

        public void Add(MappedBytes packet, int index)
        {
            _seen = index + 1;
            if (index == 0)
            {
                AddHead(packet);
            }
            else if (index == 1 && !_isContained)
            {
                if (packet.Span.StartsWith("OpusTags"u8))
                {
                    CheckHeaderPage(packet, "comment header", requireEnd: true);
                    AddTags(packet);
                }
                else
                {
                    Node node = _parent.Add(Node.FromMapped("Unexpected Packet", packet, 0, packet.Length, NodeValue.Integer(packet.Length, "bytes", "byte"), description: Explain.UnexpectedPacket));
                    _findings.Add(Severity.Warning, "opus.tags-missing", node, _ =>
                        "The stream's second packet is not the comment header (\"OpusTags\") RFC 7845 puts there (section 3); decoders refuse the stream. It is counted as audio.");
                    AddAudio(packet);
                }
            }
            else
            {
                AddAudio(packet);
            }
        }

        // RFC 7845, section 3: the identification header alone on the first page, the comment
        // header ending its last page, and both on pages of granule position 0.
        private void CheckHeaderPage(MappedBytes packet, string header, bool requireEnd)
        {
            if (_isContained)
            {
                return;
            }

            if (packet.EndPosition is { } position && position != 0 && packet.EndPositionField is { } field)
            {
                _findings.Add(Severity.Warning, "opus.header-granule", field, _ =>
                    $"The page that ends the {header} has the granule position {position}, where pages of headers have 0 (RFC 7845, section 3).");
            }

            if (requireEnd && packet.EndPosition is null)
            {
                Node at = _headNode ?? _parent;
                _findings.Add(Severity.Warning, "opus.page-layout", at, _ =>
                    $"The {header} does not end its page, where RFC 7845 starts the next packet on a fresh page (section 3).");
            }
        }

        private void AddHead(MappedBytes packet)
        {
            _head = OpusHead.Read(packet.Span);
            if (_head is not { } head)
            {
                Node node = _parent.Add(Node.FromMapped("Opus Head", packet, 0, packet.Length, NodeValue.Integer(packet.Length, "bytes", "byte"), description: Explain.HeadShort,
                    topic: Topics.OpusPreSkip));
                _findings.Add(Severity.Warning, "opus.header-short", node, _ =>
                    $"The identification header holds {Count(packet.Length, "byte", "bytes")}, too few for its fields (RFC 7845, section 5.1); decoders refuse the stream, and its audio packets are read as single-stream packets.");
                return;
            }

            _headNode = _parent.Add(head.ToNode(packet));
            CheckHeaderPage(packet, "identification header", requireEnd: true);
            if (head.Length < head.ExpectedLength)
            {
                _findings.Add(Severity.Warning, "opus.header-short", _headNode, _ =>
                    $"The identification header holds {Count(packet.Length, "byte", "bytes")}, fewer than the {head.ExpectedLength} its channel mapping needs (RFC 7845, section 5.1.1); decoders refuse the stream.");
            }
            else if (packet.Length > head.Length)
            {
                Node extra = _parent.Add(Node.FromMapped("Head Extra Data", packet, head.Length, packet.Length, NodeValue.Integer(packet.Length - head.Length, "bytes", "byte"),
                    description: Explain.HeadExtraData));
                _findings.Add(Severity.Info, "opus.header-extra-data", extra, _ =>
                    $"The identification header holds {Count(packet.Length - head.Length, "byte", "bytes")} after its fields, which decoders ignore.");
            }

            CheckHead(head);
            _records = new Records(Math.Max(1, head.StreamCount));
        }

        // RFC 7845, section 5.1 and 5.1.1.
        private void CheckHead(OpusHead head)
        {
            // Matroska's CodecDelay is the pre-skip in nanoseconds (the Matroska codec mapping, A_OPUS): 48,000 samples a second.
            if (context.Configuration?.Delay is { } delay && Math.Abs((delay * Rate) - head.PreSkip) >= 0.5)
            {
                _findings.Add(Severity.Warning, "opus.container-delay-differs", Field("Pre-skip"), _ =>
                    $"The container states a codec delay of {delay.ToString("0.#######", CultureInfo.InvariantCulture)} s, {(delay * Rate).ToString("0.#", CultureInfo.InvariantCulture)} samples at 48 kHz, but the identification header's pre-skip is {Count(head.PreSkip, "sample", "samples")}; the Matroska codec mapping makes the delay the pre-skip (draft-ietf-cellar-codec, section 3.4.32).");
            }

            if ((head.Version & 0xF0) != 0)
            {
                _findings.Add(Severity.Warning, "opus.version", Field("Version"), _ =>
                    $"The identification header gives the version {head.Version}, whose upper four bits name an encapsulation this decoder does not know; RFC 7845 version 1 decoders refuse it (section 5.1).");
            }
            else if (head.Version != 1)
            {
                _findings.Add(Severity.Info, "opus.version-minor", Field("Version"), _ =>
                    $"The identification header gives the version {head.Version}, where RFC 7845 writes 1; decoders read it as compatible (section 5.1).");
            }

            if (head.Channels == 0)
            {
                _findings.Add(Severity.Warning, "opus.channels-invalid", Field("Channels"), _ =>
                    "The identification header gives 0 output channels, which RFC 7845 forbids (section 5.1); decoders refuse the stream.");
            }
            else if (head.Family == 0 && head.Channels > 2)
            {
                _findings.Add(Severity.Warning, "opus.channels-invalid", Field("Channels"), _ =>
                    $"Channel mapping family 0 allows one or two channels, but the header gives {head.Channels} (RFC 7845, section 5.1.1.1); decoders refuse the stream.");
            }
            else if (head.Family == 1 && head.Channels > 8)
            {
                _findings.Add(Severity.Warning, "opus.channels-invalid", Field("Channels"), _ =>
                    $"Channel mapping family 1 allows one to eight channels, but the header gives {head.Channels} (RFC 7845, section 5.1.1.2); decoders refuse the stream.");
            }

            else if (head.IsAmbisonic && head.Ambisonics is null)
            {
                _findings.Add(Severity.Warning, "opus.channels-invalid", Field("Channels"), _ =>
                    $"Channel mapping family {head.Family} allows (1 + n)² + 2j channels for an ambisonic order n of 0 to 14 and j of 0 or 1 (1, 3, 4, 6, 9, 11, 16, 18, ...), but the header gives {head.Channels} (RFC 8486, section 3.3).");
            }

            if (head.Family is not (0 or 1 or 2 or 3 or 255))
            {
                _findings.Add(Severity.Info, "opus.mapping-family-reserved", Field("Mapping Family"), _ =>
                    $"Channel mapping family {head.Family} is reserved; decoders that do not know it should not decode the packets (RFC 8486, section 5.2, which updates RFC 7845, section 5.1.1.4). The analyzer reads its table in the format RFC 8486 recommends for new families.");
            }

            if (!head.HasTable || head.Length < 21)
            {
                return;
            }

            if (head.Streams == 0)
            {
                _findings.Add(Severity.Warning, "opus.stream-count-invalid", Field("Stream Count"), _ =>
                    "The identification header gives 0 streams, where at least one is required (RFC 7845, section 5.1.1); decoders refuse the stream.");
            }

            if (head.Coupled > head.Streams)
            {
                _findings.Add(Severity.Warning, "opus.coupled-count-invalid", Field("Coupled Count"), _ =>
                    $"The identification header gives {head.Coupled} coupled streams of {head.Streams}, more coupled streams than streams (RFC 7845, section 5.1.1); decoders refuse the stream.");
            }
            else if (head.Streams + head.Coupled > 255)
            {
                _findings.Add(Severity.Warning, "opus.coupled-count-invalid", Field("Coupled Count"), _ =>
                    $"{head.Streams} streams and {head.Coupled} coupled streams decode to more than 255 channels (RFC 7845, section 5.1.1); decoders refuse the stream.");
            }

            if (head.Family != 3 && _headNode!.Children.FirstOrDefault(c => c.Name == "Channel Mapping") is { } table)
            {
                for (int c = 0; c < head.Mapping.Length; c++)
                {
                    int value = head.Mapping[c];
                    if (value != 255 && value >= head.Streams + head.Coupled)
                    {
                        int channel = c;
                        _findings.Add(Severity.Warning, "opus.mapping-invalid", table.Children[c], count =>
                            $"{Count(count, "entry", "entries")} of the channel mapping name a decoded channel the {head.Streams + head.Coupled} the streams give do not include: the first, channel {channel}, names {value} (RFC 7845, section 5.1.1); decoders refuse the stream.");
                    }
                }
            }
        }

        private Node Field(string name) => _headNode!.Children.First(c => c.Name == name);

        // RFC 7845, section 5.2: "OpusTags", a Vorbis comment, then optional data whose first
        // byte's least significant bit says whether it is binary data to keep (1) or padding.
        private void AddTags(MappedBytes packet)
        {
            _hasTags = true;
            Node prefix = _parent.Add(Node.FromMapped("Magic Signature", packet, 0, 8, "OpusTags", description: Explain.MagicTags));
            MappedBytes body = packet.Slice(8);
            _comment = body.Length > 0 ? context.ParseTag(TagName, body, _parent) : null;
            if (_comment is null)
            {
                _findings.Add(Severity.Warning, "opus.tags-empty", prefix, _ => "The comment header ends after \"OpusTags\", without the Vorbis comment RFC 7845 requires (section 5.2).");
                return;
            }

            int end = 8 + BytesBefore(body, _comment.End);
            if (end < packet.Length)
            {
                bool binary = (packet.Span[end] & 1) != 0;
                Node extra = _parent.Add(Node.FromMapped("Extra Data", packet, end, packet.Length,
                    $"{Count(packet.Length - end, "byte", "bytes")}, {(binary ? "binary data to keep" : "padding, which may be discarded")}",
                    description: Explain.TagsExtraData));
                extra.Add(Node.FromBits("HasBinaryData", packet, (end * 8L) + 7, (end * 8L) + 8, NodeValue.Boolean(binary), description: Explain.HasBinaryData));
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

        private void AddAudio(MappedBytes packet)
        {
            int index = _records.Count;
            _records.AddPacket(packet.FileStart);
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
            _sizesDiffer |= _lastSize >= 0 && packet.Length != _lastSize;
            _lastSize = packet.Length;
            _smallest = Math.Min(_smallest, packet.Length);
            _largest = Math.Max(_largest, packet.Length);
            _audioStart = _audioStart < 0 ? packet.FileStart : _audioStart;
            _audioEnd = Math.Max(_audioEnd, packet.FileEnd);
            _afterGap |= packet.FollowsGap;

            ReadParts(packet, index);
            _lastPacketSamples = _records.SamplesOf(index);
            _decoded += _lastPacketSamples;
            TrackPosition(packet);
        }

        private void ReadParts(MappedBytes packet, int index)
        {
            ReadOnlySpan<byte> data = packet.Span;
            int streams = _records.StreamCount;
            int position = 0;
            bool lost = false;
            int samples = -1;

            // The packet's node needs all its parts, so what is wrong is reported once they are read.
            (int Stream, PartStatus Status, string Error)? broken = null;
            (int Stream, int Samples, int First)? differs = null;
            _records.StartParts();
            for (int s = 0; s < streams && !lost; s++)
            {
                PartRecord part = Framing.Read(data[position..], s < streams - 1, _records.FrameLengths, out string? error);
                _records.Parts.Add(part);
                position += part.Length;
                if (part.Status != PartStatus.Valid)
                {
                    lost = true;
                    broken = (s, part.Status, error!);
                }

                Tally(part);
                if (part.HasToc)
                {
                    if (samples < 0)
                    {
                        samples = part.Samples;
                    }
                    else if (part.Samples != samples && differs is null)
                    {
                        differs = (s, part.Samples, samples);
                    }
                }
            }

            if (broken is { } b)
            {
                Report(index, b.Stream, b.Status, b.Error);
            }

            if (differs is { } d)
            {
                _findings.Add(Severity.Warning, "opus.stream-durations-differ", PacketNode(_records, index), count =>
                    $"{Count(count, "multistream packet holds", "multistream packets hold")} streams of different durations: in the first, at 0x{_records.OffsetOf(index):X}, stream {d.Stream} codes {d.Samples} samples where stream 0 codes {d.First}; every stream of a packet must code the same duration, and decoders treat the others as malformed (RFC 7845, section 3).");
            }
        }

        private void Report(int index, int stream, PartStatus status, string error)
        {
            string where = _records.StreamCount > 1 ? $"stream {stream} of the first, at 0x{_records.OffsetOf(index):X}" : $"the first, at 0x{_records.OffsetOf(index):X}";
            if (status == PartStatus.SelfDelimitedOverrun)
            {
                _findings.Add(Severity.Warning, "opus.self-delimiting", PacketNode(_records, index), count =>
                    $"{Count(count, "multistream packet's", "multistream packets'")} self-delimited framing (RFC 6716, appendix B) does not fit: in {where}, {error}; decoders refuse the packet.");
                return;
            }

            int rule = (int)status;
            _findings.Add(Severity.Warning, $"opus.packet-r{rule}", PacketNode(_records, index), count =>
                $"{Count(count, "packet breaks", "packets break")} rule R{rule} of RFC 6716, section 3.4: in {where}, {error}; decoders refuse such a packet.");
        }

        private void Tally(PartRecord part)
        {
            if (!part.HasToc)
            {
                return;
            }

            _parts++;
            _stereo[Toc.IsStereo(part.TocByte) ? 1 : 0]++;
            _codes[part.Code]++;
            if (part.Status != PartStatus.Valid)
            {
                return;
            }

            int frames = part.Frames;
            _frames += frames;
            _modes[Toc.Mode(part.TocByte)] += frames;
            _bandwidths[Toc.Bandwidth(part.TocByte)] += frames;
            int size = Toc.FrameSamples(part.TocByte);
            _frameSizes[size] = _frameSizes.GetValueOrDefault(size) + frames;
            long data = 0;
            for (int f = part.FirstFrame; f < part.FirstFrame + frames; f++)
            {
                ushort length = _records.FrameLengths[f];
                data += length;
                _emptyFrames += length == 0 ? 1 : 0;
            }

            _padding += part.Length - part.Header - data;
        }

        private void TrackPosition(MappedBytes packet)
        {
            if (packet.EndPosition is not { } position || !HasHead)
            {
                return;
            }

            long offset = position - _decoded;
            if (_firstOffset is null)
            {
                // A stream whose only position is the last one: a smaller position trims the end.
                _firstOffset = _offset = packet.IsEndOfStream ? Math.Max(0, offset) : offset;
                _firstPositionField = packet.EndPositionField;
            }
            else if (_afterGap)
            {
                _offset = offset;
            }
            else if (offset != _offset && !packet.IsEndOfStream)
            {
                long expected = _decoded + _offset!.Value;
                Node field = packet.EndPositionField ?? _parent;
                if (offset < _offset)
                {
                    _findings.Add(Severity.Warning, "opus.end-trimming-early", field, count =>
                        $"{Count(count, "granule position", "granule positions")} before the last page {(count == 1 ? "is" : "are")} smaller than the samples of the packets before {(count == 1 ? "it" : "them")}: the first, at 0x{field.Offset:X}, is {position} where the packets give {expected}. RFC 7845 allows trimming the end only on the end-of-stream page (section 4.4).");
                }
                else
                {
                    _findings.Add(Severity.Warning, "opus.granule-inconsistent", field, count =>
                        $"{Count(count, "granule position does", "granule positions do")} not match the samples of the packets before {(count == 1 ? "it" : "them")}: the first, at 0x{field.Offset:X}, is {position} where the packets give {expected} (RFC 7845, section 4).");
                }
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
            CheckStart();
            long trim = EndTrimming();
            int preSkip = _head?.PreSkip ?? 0;
            long samples = Math.Clamp(_decoded - preSkip - trim, 0, _decoded);
            if (HasHead && _packets > 0 && preSkip + trim > _decoded)
            {
                _findings.Add(Severity.Warning, "opus.pre-skip-beyond", Field("Pre-skip"), _ =>
                    $"The pre-skip of {Count(preSkip, "sample", "samples")} and the end trimming of {trim} remove more than the {Count(_decoded, "sample", "samples")} the packets hold, so the stream plays nothing.");
            }

            Node? encoder = AddEncoder();
            Node? writer = AddWriter();
            ClaimRoles.CompleteVersion(encoder, CommentClaims());
            Node? anchor = AddStatistics(samples, trim);
            Node? packets = AddPackets();

            if (_head is { } head)
            {
                context.AddStream(new StreamInfo
                {
                    Nodes = [_parent],
                    Codec = "Opus",
                    SampleRate = Rate,
                    Channels = head.Channels,
                    ChannelMode = head.Channels > 0 ? head.ChannelMode : null,
                    ChannelLayout = head.Speakers,
                    Samples = samples,
                    Duration = samples / (double)Rate,
                    AverageBitrate = _decoded > 0 ? _bytes * 8.0 * Rate / _decoded / 1000 : null,
                    IsVbr = _sizesDiffer,
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
            Node anchor = _headNode ?? _parent;
            if (_seen < 2 && !_isContained)
            {
                _findings.Add(Severity.Warning, "opus.tags-missing", anchor, _ =>
                    "The stream ends after its identification header, without the comment header RFC 7845 requires (section 3).");
            }

            // A container that knows the samples are encrypted hands over the header and no packet.
            if (context.Configuration is not { AreSamplesEncrypted: true } && (_packets == 0 || (!_isContained && !_hasTags && _packets == 1 && _seen == 2)))
            {
                _findings.Add(Severity.Info, "opus.no-audio", anchor, _ => "The stream holds no audio packets.");
            }
        }

        // RFC 7845, section 4.5: the first audio page's granule position less the samples up to it
        // is where the stream starts; it may be later than 0, never earlier, except on a stream
        // whose only page with a position is its last.
        private void CheckStart()
        {
            if (_firstOffset is not { } offset || offset == 0)
            {
                return;
            }

            Node field = _firstPositionField ?? _parent;
            if (offset > 0)
            {
                _findings.Add(Severity.Info, "opus.start-offset", field, _ =>
                    $"The first granule position is {Count(offset, "sample", "samples")} beyond what the packets before it hold, so the stream starts at a later time, as a stream cut from a longer one does (RFC 7845, section 4.5).");
            }
            else
            {
                _findings.Add(Severity.Warning, "opus.start-negative", field, _ =>
                    $"The first granule position is {Count(-offset, "sample", "samples")} smaller than what the packets before it hold, which gives a negative start; RFC 7845 allows that only on the end-of-stream page, and decoders treat the stream as invalid (section 4.5).");
            }
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
                _findings.Add(Severity.Warning, "opus.granule-beyond-packets", field, _ =>
                    $"The last granule position, {position} at 0x{field.Offset:X}, is {Count(-trim, "sample", "samples")} beyond what the packets before it hold; decoders end the stream earlier than it says.");
                return 0;
            }

            if (trim > _lastPacketSamples)
            {
                _findings.Add(Severity.Warning, "opus.end-trimming-large", field, _ =>
                    $"The last granule position, {position} at 0x{field.Offset:X}, trims {Count(trim, "sample", "samples")} from the end, more than the {_lastPacketSamples} of the last packet, which RFC 7845 asks trimming not to exceed (section 4.4).");
            }

            return trim;
        }
    }
}
