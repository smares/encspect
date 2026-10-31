using Encspect.Core;

namespace Encspect.Formats.Flac;

// FLAC in Ogg (RFC 9639, section 10.1): the first packet is 0x7F, "FLAC", the mapping's version, the number of header packets that follow
// and then "fLaC" with the STREAMINFO block; each further header packet is one metadata block, and each audio packet one frame. The frames
// are read as native FLAC's are, from the packet's bytes, and handed to the decoder the same way.
public sealed class FlacCodecParser : ICodecParser
{
    public const string CodecId = "flac";

    private const int MappingLength = 13;

    public string Id => CodecId;

    public void Parse(CodecContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Packets is not { } packets)
        {
            context.AddFinding(Severity.Info, "flac.payload-not-analyzed", "FLAC handed over as one payload is not analyzed; only Ogg packets are.", context.Parent);
            return;
        }

        var state = new State(context, isContained: true) { Stream = context.Parent };
        var reader = new PacketReader(state);
        int index = 0;
        foreach (MappedBytes packet in packets)
        {
            if (index % 4096 == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            reader.Add(packet, index++);
        }

        reader.Finish();
    }

    private sealed class PacketReader(State s)
    {
        private readonly SubframeFacts[] _facts = new SubframeFacts[8];
        private readonly PartitionLog[] _logs = [new(), new(), new(), new(), new(), new(), new(), new()];
        private int _expectedHeaders = -1;
        private int _headerPackets;
        private bool _inHeaders = true;
        private bool _isBroken;
        private int _blocks;
        private Node? _firstTrailingFrame;
        private Node? _firstTrailingBlock;
        private long _moreStart = -1;
        private long _moreEnd;
        private int _more;
        private Node? _further;
        private long? _granuleBase;
        private long _granuleMismatches;
        private Node? _firstMismatch;
        private long _firstMismatchGranule;
        private long _firstMismatchCounted;
        private long _start = -1;
        private long _end;

        public void Add(MappedBytes packet, int index)
        {
            _start = _start < 0 ? packet.FileStart : _start;
            _end = Math.Max(_end, packet.FileEnd);
            if (_isBroken)
            {
                return;
            }

            if (index == 0)
            {
                Mapping(packet);
            }
            else if (_inHeaders)
            {
                if (FrameHeader.HasSync(packet.Span))
                {
                    s.Findings.Add(Severity.Warning, "flac.metadata-no-last-block", s.Stream, _ => $"A frame begins at 0x{packet.FileStart:X} but no metadata block has the last-block flag; the metadata ends there.");
                    _inHeaders = false;
                    AddFurther();
                    s.StartSession();
                    Frame(packet);
                }
                else
                {
                    Metadata(packet, index);
                }
            }
            else
            {
                Frame(packet);
            }
        }

        public void Finish()
        {
            AddFurther();
            if (_inHeaders && _blocks > 0 && !_isBroken)
            {
                s.Findings.Add(Severity.Warning, "flac.metadata-no-last-block", s.Stream, _ => "The stream's header packets end without a metadata block that has the last-block flag.");
            }

            if (_expectedHeaders > 0 && _headerPackets != _expectedHeaders && !_inHeaders)
            {
                int expected = _expectedHeaders;
                int found = _headerPackets;
                s.Findings.Add(Severity.Warning, "flac.ogg-header-count", s.Stream, _ =>
                    $"The mapping header says {Counts.Of(expected, "header packet", "header packets")} follow the first, but {found} hold metadata blocks.");
            }

            if (_granuleMismatches > 0 && _firstMismatch is { } at)
            {
                long mismatches = _granuleMismatches;
                long granule = _firstMismatchGranule;
                long counted = _firstMismatchCounted;
                s.Findings.Add(Severity.Warning, "flac.ogg-granule-mismatch", at, _ =>
                    $"A page's granule position is {granule}, but the frames up to its last packet hold {Counts.Of(counted, "sample", "samples")}{(mismatches > 1 ? $"; {mismatches - 1} more pages differ" : string.Empty)}.");
            }

            if (_start >= 0)
            {
                Summary.Finish(s, _start, _end);
            }
        }

        private void Mapping(MappedBytes packet)
        {
            ReadOnlySpan<byte> data = packet.Span;
            FieldMap map = FieldMap.Of(packet);
            if (data.Length < MappingLength + 4 + StreamInfoBlock.Length || !data[..5].SequenceEqual((ReadOnlySpan<byte>)[0x7F, (byte)'F', (byte)'L', (byte)'A', (byte)'C']) || !data[9..13].SequenceEqual("fLaC"u8))
            {
                Node bad = s.Stream.Add(map.Bytes("Mapping Header", 0, packet.Length, NodeValue.Integer(packet.Length, "bytes"), Explain.MappingHeader, Topics.Ogg));
                s.Findings.Add(Severity.Warning, "flac.ogg-header-invalid", bad, _ =>
                    $"The first packet of the FLAC stream is {Counts.Of(packet.Length, "byte", "bytes")} long and does not hold the mapping header with the \"fLaC\" marker and a STREAMINFO block.");
                _isBroken = true;
                return;
            }

            int major = data[5];
            int minor = data[6];
            _expectedHeaders = (data[7] << 8) | data[8];
            Node header = s.Stream.Add(map.Bytes("Mapping Header", 0, MappingLength, null, Explain.MappingHeader, Topics.Ogg));
            header.Add(map.Bytes("Packet Type", 0, 1, NodeValue.Hex(0x7F, 2), Explain.OggPacketType, Topics.Ogg));
            header.Add(map.Bytes("Signature", 1, 4, "FLAC", Explain.OggSignature, Topics.Ogg));
            header.Add(map.Bytes("Major Version", 5, 1, NodeValue.Integer(major), Explain.OggMajorVersion, Topics.Ogg));
            header.Add(map.Bytes("Minor Version", 6, 1, NodeValue.Integer(minor), Explain.OggMinorVersion, Topics.Ogg));
            header.Add(map.Bytes("Header Packet Count", 7, 2, _expectedHeaders == 0 ? "unknown" : NodeValue.Integer(_expectedHeaders, "packets"), _expectedHeaders == 0 ? Explain.OggHeaderCountUnknown : Explain.OggHeaderCount, Topics.Ogg));
            header.Add(map.Bytes("Stream Marker", 9, 4, "fLaC", Explain.Marker, Topics.Stream));
            if (major != 1)
            {
                s.Findings.Add(Severity.Warning, "flac.ogg-version", header, _ => $"The mapping header gives the version {major}.{minor}; only version 1.0 of the mapping is defined.");
            }

            MappedBytes block = packet.Slice(MappingLength);
            ReadBlock(block, 0, packet.Length - MappingLength - 4);
        }

        private void Metadata(MappedBytes packet, int index)
        {
            if (packet.Length < 4)
            {
                s.Findings.Add(Severity.Warning, "flac.metadata-truncated", s.Stream, _ => $"The header packet at 0x{packet.FileStart:X} is {Counts.Of(packet.Length, "byte", "bytes")} long, too short for a metadata block header.");
                _headerPackets++;
                return;
            }

            _headerPackets++;
            ReadBlock(packet, index, packet.Length - 4);
        }

        // block holds a metadata block's header and as much of its data as the packet has.
        private void ReadBlock(MappedBytes block, int index, int available)
        {
            ReadOnlySpan<byte> header = block.Span;
            bool isLast = (header[0] & 0x80) != 0;
            int type = header[0] & 0x7F;
            int length = (header[1] << 16) | (header[2] << 8) | header[3];
            bool isTruncated = length > available;
            int present = Math.Min(available, length);
            MappedBytes whole = block.Slice(0, 4 + Math.Min(present, Encspect.Formats.Flac.Metadata.MaxBodyBytes));
            IReadOnlyList<ByteRange> ranges = block.FileRanges(0, 4 + present);
            bool paddingIsZero = type != BlockTypes.Padding || !block.Span[4..(4 + present)].ContainsAnyExcept((byte)0);
            bool isListed = index < Encspect.Formats.Flac.Metadata.MaxListedBlocks;
            s.Findings.Redirect = isListed ? null : () => _further!;
            Func<Node, Node?>? comment = type == BlockTypes.VorbisComment && !isTruncated && isListed
                ? parent => s.Context.ParseTag("Vorbis Comment", block.Slice(4, length), parent)
                : null;
            Node node = Encspect.Formats.Flac.Metadata.Read(s, whole, isLast, type, length, present, index, paddingIsZero, comment, ranges);
            s.MetadataBlocks++;
            _blocks++;
            if (isListed)
            {
                s.AddLeading(node);
            }
            else
            {
                _moreStart = _moreStart < 0 ? node.Offset : _moreStart;
                _moreEnd = node.Offset + node.Length;
                _more++;
            }

            if (isTruncated)
            {
                int declared = length;
                s.Findings.Add(Severity.Warning, "flac.metadata-truncated", node, _ => $"The packet ends inside the metadata block at 0x{block.FileStart:X}, which states {Counts.Of(declared, "byte", "bytes")}.");
            }
            else if (available > length)
            {
                Trailing(block, 4 + length, "metadata block", ref _firstTrailingBlock);
            }

            if (type is BlockTypes.SeekTable or BlockTypes.VorbisComment or BlockTypes.CueSheet && s.Seen(type) > 1)
            {
                string name = BlockTypes.Name(type);
                s.Findings.Add(Severity.Warning, "flac.metadata-block-repeated", node, _ => $"The stream has more than one {name} block; the specification allows one.", name);
            }

            s.Findings.Redirect = null;
            if (isLast)
            {
                _inHeaders = false;
                AddFurther();
                if (s.Info is null)
                {
                    s.Findings.Add(Severity.Warning, "flac.no-streaminfo", s.Stream, _ => "The stream has no STREAMINFO block as its first metadata block, so the sample rate, bit depth and the MD5 of the audio are not known.");
                }

                s.StartSession();
            }
        }

        // The blocks past the listed ones are counted, as in a native file, and their findings point to the node that stands for them.
        private void AddFurther()
        {
            if (_more == 0 || _further is not null)
            {
                return;
            }

            _further = new Node("Further Metadata Blocks", _moreStart, _moreEnd - _moreStart, NodeValue.Integer(_more, "blocks", "block")) { Description = Explain.FurtherMetadataBlocks, Topic = Topics.MetadataBlocks };
            s.AddLeading(_further);
            int count = _more;
            s.Findings.Add(Severity.Info, "flac.metadata-blocks-not-listed", s.Stream, _ => $"The report lists the first {Encspect.Formats.Flac.Metadata.MaxListedBlocks} metadata blocks; {Counts.Of(count, "more", "more")} {Counts.Agree(count, "is", "are")} read but not listed.");
        }

        // Bytes in a packet after the frame or metadata block it carries: the first case gets a node, the rest are counted in the finding.
        private void Trailing(MappedBytes packet, int from, string what, ref Node? first)
        {
            int rest = packet.Length - from;
            if (first is null)
            {
                first = Node.FromMapped("Trailing Data", packet, from, packet.Length, NodeValue.Integer(rest, "bytes"), description: Explain.TrailingPacketData);
                s.AddLeading(first);
            }

            long at = packet.FileStart;
            Node node = first;
            s.Findings.Add(Severity.Warning, "flac.ogg-packet-trailing", node, count =>
                $"The {what} in the packet at 0x{at:X} ends {Counts.Of(rest, "byte", "bytes")} before the packet does{(count > 1 ? $"; {Counts.Of(count - 1, "more packet", "more packets")} {Counts.Agree(count - 1, "has", "have")} bytes after {(what == "frame" ? "its frame" : "its metadata block")}" : string.Empty)}.", what);
        }

        private void Frame(MappedBytes packet)
        {
            ReadOnlySpan<byte> data = packet.Span;
            HeaderFault fault = FrameHeader.TryRead(data, out FrameHeader header);
            if (fault is not (HeaderFault.None or HeaderFault.Crc8))
            {
                Invalid(packet, "does not start with a frame header");
                return;
            }

            header = FrameScanner.Resolve(header, s.Info);
            if (header.BitsPerSample == 0)
            {
                Invalid(packet, "leaves the sample size to a STREAMINFO block the stream does not have");
                return;
            }

            IDecodeSession? session = s.Session is { IsDecoding: true } active ? active : null;
            FrameWalk walk = FrameWalker.Walk(data, header, _facts, session, _logs);
            EntryStatus status = EntryStatus.None;
            MappedBytes bytes = packet;
            if (walk.IsComplete)
            {
                if (walk.Length < data.Length)
                {
                    bytes = packet.Slice(0, walk.Length);
                    Trailing(packet, walk.Length, "frame", ref _firstTrailingFrame);
                }
            }
            else if (walk.IsCut && walk.Fault == SubframeFault.None)
            {
                status = EntryStatus.Cut;
            }
            else
            {
                status = EntryStatus.Unreadable | EntryStatus.ExtentFound;
            }

            s.AddFrame(bytes, header, walk, _facts, status, _logs, packet.FollowsGap);
            if (packet.EndPosition is { } stated)
            {
                // RFC 9639, section 10.1: the first audio page of a stream that is joined mid-way may state more than the samples counted;
                // that excess is the base the other pages count from.
                _granuleBase ??= Math.Max(0, stated - s.NextSample);
            }

            if (packet.EndPosition is { } granule && granule - (_granuleBase ?? 0) != s.NextSample)
            {
                _granuleMismatches++;
                if (_firstMismatch is null)
                {
                    _firstMismatch = packet.EndPositionField ?? s.Stream;
                    _firstMismatchGranule = granule;
                    _firstMismatchCounted = s.NextSample;
                }
            }
        }

        private void Invalid(MappedBytes packet, string reason)
        {
            s.AddSkipped(packet.FileStart, packet.FileEnd - packet.FileStart);
            s.Findings.Add(Severity.Warning, "flac.ogg-packet-invalid", s.Stream, _ => $"The packet at 0x{packet.FileStart:X} {reason}, so it is not read as a frame.");
        }
    }
}
