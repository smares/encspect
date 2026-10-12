using System.Globalization;
using System.Text.RegularExpressions;
using Encspect.Core;

namespace Encspect.Formats.Speex;

// A packet of audio: where it is, how long, and where its frames start among the stream's frame
// records. 16 bytes.
internal readonly record struct PacketRecord(long Offset, int Length, int FirstFrame);

public sealed partial class SpeexParser
{
    private const string TagName = "Vorbis Comment";

    // The records audio packet nodes are built from once the stream is closed.
    private sealed class Records
    {
        public ChunkedList<PacketRecord> Packets { get; } = new();

        public ChunkedList<FrameRecord> Frames { get; } = new();

        // Packets that span pages, by index: their file ranges.
        public Dictionary<int, ByteRange[]> Ranges { get; } = [];

        // Packets whose walk did not end with their frames: an undefined submode, an overrun, or
        // more frames than the header says.
        public Dictionary<int, PacketWalk> Irregular { get; } = [];

        public int Mode { get; set; } = -1;

        public int FrameCount(int packet) => (packet + 1 < Packets.Count ? Packets[packet + 1].FirstFrame : Frames.Count) - Packets[packet].FirstFrame;
    }

    private sealed partial class StreamState(CodecContext context)
    {
        private readonly Node _parent = context.Parent;
        private readonly FindingCollector _findings = new();
        private readonly Records _records = new();

        // Frames per submode, index FrameRecord.Absent for a layer the frame does not have.
        private readonly long[] _narrowband = new long[16];
        private readonly long[] _wideband = new long[16];
        private readonly long[] _ultraWideband = new long[16];
        private readonly SortedDictionary<int, long> _sizes = [];

        private SpeexHeader? _header;
        private Node? _headerNode;
        private Node? _comment;
        private int _seen;
        private int _packets;
        private long _frames;
        private long _bytes;
        private long _inBand;
        private long _stereo;
        private long _audioStart = -1;
        private long _audioEnd;

        // Samples coded so far (frames_per_packet frames of the mode's size per packet), the delay
        // the first granule position implies, and the last position with the samples coded by then.
        private long _coded;
        private long? _firstDelay;
        private long? _delay;
        private bool _afterGap;
        private long? _lastPosition;
        private long _lastPositionCoded;
        private bool _lastIsEnd;
        private Node? _lastPositionField;
        private Node? _firstPositionField;

        private int Mode => _header is { IsModeValid: true } h ? h.Mode : -1;

        private int FrameSize => Mode >= 0 ? 160 << Mode : 0;

        private long SamplesPerPacket => (long)FrameSize * (_header?.FramesPerPacketCounted ?? 1);

        private int Rate => _header?.Rate ?? 0;

        private bool HasTime => Mode >= 0 && Rate > 0;

        public void Add(MappedBytes packet, int index)
        {
            _seen = index + 1;
            if (index == 0)
            {
                AddHeader(packet);
            }
            else if (index == 1)
            {
                AddComment(packet);
            }
            else if (_header is { } header && index < 2 + (long)Math.Max(0, header.ExtraHeaders))
            {
                _parent.Add(Node.FromMapped("Extra Header", packet, 0, packet.Length, NodeValue.Integer(packet.Length, "bytes", "byte")));
            }
            else
            {
                AddAudio(packet);
            }
        }

        private void AddHeader(MappedBytes packet)
        {
            _header = SpeexHeader.Read(packet.Span);
            if (_header is not { } header)
            {
                Node node = _parent.Add(Node.FromMapped("Speex Header", packet, 0, packet.Length, NodeValue.Integer(packet.Length, "bytes", "byte")));
                _findings.Add(Severity.Warning, "speex.header-short", node, _ =>
                    $"The Speex header packet holds {packet.Length} bytes, fewer than the header's {SpeexHeader.Length}; decoders reject the stream, and its audio packets are only counted.");
                return;
            }

            _headerNode = _parent.Add(header.ToNode(packet));
            _records.Mode = Mode;
            if (packet.Length > SpeexHeader.Length)
            {
                Node extra = _parent.Add(Node.FromMapped("Header Extra Data", packet, SpeexHeader.Length, packet.Length, NodeValue.Integer(packet.Length - SpeexHeader.Length, "bytes", "byte")));
                _findings.Add(Severity.Info, "speex.header-extra-data", extra, _ =>
                    $"The Speex header packet holds {packet.Length - SpeexHeader.Length} bytes after the 80-byte header, which decoders ignore.");
            }

            CheckHeader(header);
        }

        private void CheckHeader(SpeexHeader header)
        {
            if (header.HeaderSize != SpeexHeader.Length)
            {
                _findings.Add(Severity.Warning, "speex.header-size", Field("Header Size"), _ =>
                    $"The header gives its size as {header.HeaderSize} bytes; the Speex header has {SpeexHeader.Length}.");
            }

            if (!header.IsModeValid)
            {
                _findings.Add(Severity.Warning, "speex.mode-invalid", Field("Mode"), _ =>
                    $"The header's mode {header.Mode} is none of narrowband (0), wideband (1) and ultra-wideband (2); decoders reject the stream, so its frames are not read.");
            }
            else if (header.FrameSize != 160 << header.Mode)
            {
                _findings.Add(Severity.Warning, "speex.frame-size", Field("Frame Size"), _ =>
                    $"The header gives a frame size of {header.FrameSize} samples, but {header.ModeName.ToLowerInvariant()} frames hold {160 << header.Mode}; decoders use the mode's.");
            }

            // speexdec refuses a bit-stream version other than its mode's (4 in Speex 1.2).
            if (header.ModeBitstreamVersion != 4)
            {
                _findings.Add(Severity.Warning, "speex.bitstream-version", Field("Mode Bitstream Version"), _ =>
                    $"The header's mode bit-stream version is {header.ModeBitstreamVersion}; Speex 1.2 decodes only version 4 and refuses the stream.");
            }

            if (header.Channels is not (1 or 2))
            {
                _findings.Add(Severity.Warning, "speex.channels-invalid", Field("Channels"), _ =>
                    $"The header gives {header.Channels} channels, where Speex has 1 or 2; decoders read it as {header.DecodedChannels}.");
            }

            if (header.Rate <= 0)
            {
                _findings.Add(Severity.Warning, "speex.rate-invalid", Field("Sample Rate"), _ =>
                    $"The header gives a sample rate of {header.Rate} Hz, so the stream has no duration.");
            }

            if (header.FramesPerPacket is < 1 or > 10)
            {
                _findings.Add(Severity.Warning, "speex.frames-per-packet", Field("Frames Per Packet"), _ =>
                    $"The header gives {header.FramesPerPacket} frames per packet; Speex packs 1 to 10.");
            }

            if (header.VersionId != 1)
            {
                _findings.Add(Severity.Info, "speex.version-id", Field("Version ID"), _ =>
                    $"The header's version ID is {header.VersionId}; Speex 1.0 to 1.2 write 1.");
            }

            if (header.Reserved1 != 0 || header.Reserved2 != 0)
            {
                _findings.Add(Severity.Info, "speex.reserved", Field(header.Reserved1 != 0 ? "Reserved 1" : "Reserved 2"), _ =>
                    "A reserved header field is not 0, as speex_header.h says it must be.");
            }
        }

        private Node Field(string name) => _headerNode!.Children.First(c => c.Name == name);

        // The second packet is a Vorbis comment without the "\x03vorbis" prefix and framing bit
        // Vorbis gives it (speexenc.c comment_init).
        private void AddComment(MappedBytes packet)
        {
            _comment = context.ParseTag(TagName, packet, _parent);
            if (_comment is null)
            {
                return;
            }

            int end = BytesBefore(packet, _comment.End);
            if (end < packet.Length)
            {
                Node trailing = _parent.Add(Node.FromMapped("Comment Trailing Data", packet, end, packet.Length, NodeValue.Integer(packet.Length - end, "bytes", "byte")));
                _findings.Add(Severity.Info, "speex.comment-trailing-data", trailing, _ =>
                    $"The comment packet holds {packet.Length - end} bytes after the comment, which readers ignore.");
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
            int index = _records.Packets.Count;
            _records.Packets.Add(new PacketRecord(packet.FileStart, packet.Length, _records.Frames.Count));
            IReadOnlyList<ByteRange> ranges = packet.FileRanges();
            if (ranges.Count > 1)
            {
                _records.Ranges[index] = [.. ranges];
            }

            _packets++;
            _bytes += packet.Length;
            _sizes[packet.Length] = _sizes.GetValueOrDefault(packet.Length) + 1;
            _audioStart = _audioStart < 0 ? packet.FileStart : _audioStart;
            _audioEnd = Math.Max(_audioEnd, packet.FileEnd);

            if (packet.Length == 0)
            {
                _findings.Add(Severity.Warning, "speex.empty-packet", PacketNode(_records, index), count =>
                    $"{Count(count, "audio packet is", "audio packets are")} empty; decoders treat {(count == 1 ? "it" : "them")} as lost and fill in the gap. The first at 0x{packet.FileStart:X}.");
            }
            else if (_header is { } header && Mode >= 0)
            {
                WalkFrames(packet, index, header);
            }

            _coded += SamplesPerPacket;
            TrackPosition(packet);
        }

        private void WalkFrames(MappedBytes packet, int index, SpeexHeader header)
        {
            int expected = header.FramesPerPacketCounted;
            PacketWalk walk = SpeexFrames.Walk(packet.Span, Mode, expected + 1, _records.Frames);
            int frames = walk.Frames;
            if (frames > expected)
            {
                _records.Frames.RemoveLast();
                frames = expected;
                _records.Irregular[index] = walk;
                _findings.Add(Severity.Warning, "speex.extra-frames", PacketNode(_records, index), count =>
                    $"{Count(count, "audio packet holds", "audio packets hold")} more frames than the {expected} per packet the header gives; decoders ignore the rest. The first at 0x{packet.FileStart:X}.");
            }
            else if (walk.End is WalkEnd.Invalid or WalkEnd.Overrun)
            {
                _records.Irregular[index] = walk;
                _findings.Add(Severity.Warning, walk.End == WalkEnd.Invalid ? "speex.frame-invalid" : "speex.frame-overrun", PacketNode(_records, index), count =>
                    $"{Count(count, "audio packet", "audio packets")} {(count == 1 ? "is" : "are")} corrupt: in the first, at 0x{packet.FileStart:X}, {walk.Error} after {Count(frames, "frame", "frames")}; decoders stop decoding the packet there.");
            }

            if (frames < expected && walk.End is not (WalkEnd.Invalid or WalkEnd.Overrun))
            {
                _findings.Add(Severity.Warning, "speex.missing-frames", PacketNode(_records, index), count =>
                    $"{Count(count, "audio packet holds", "audio packets hold")} fewer frames than the {expected} per packet the header gives, so decoders output fewer samples than the granule positions count. The first at 0x{packet.FileStart:X}, with {frames}.");
            }

            if (walk.StrayLayers > 0 && Mode == 0)
            {
                _findings.Add(Severity.Info, "speex.wideband-layer-in-narrowband", PacketNode(_records, index), count =>
                    $"{Count(count, "audio packet", "audio packets")} of this narrowband stream carry wideband layers, which narrowband decoders skip. The first at 0x{packet.FileStart:X}.");
            }

            _inBand += walk.InBand;
            _stereo += walk.Stereo;
            int first = _records.Packets[index].FirstFrame;
            for (int f = first; f < first + frames; f++)
            {
                FrameRecord frame = _records.Frames[f];
                _narrowband[frame.Narrowband]++;
                _wideband[frame.Wideband]++;
                _ultraWideband[frame.UltraWideband]++;
            }

            _frames += frames;
        }

        // Granule positions (speexenc.c): each page's counts the samples coded up to its last
        // packet, frames_per_packet frames of the mode's size each, less the encoder's lookahead;
        // the last page's may be smaller, where the input ended, which decoders trim.
        private void TrackPosition(MappedBytes packet)
        {
            _afterGap |= packet.FollowsGap;
            if (packet.EndPosition is not { } position || !HasTime)
            {
                return;
            }

            long delay = _coded - position;
            if (_firstDelay is null)
            {
                // A stream whose only position is the last one, where the end trimming is already
                // taken off, does not show its delay: libspeex's lookahead is assumed, as speexenc
                // and ffmpeg subtract it.
                _firstDelay = _delay = packet.IsEndOfStream ? SpeexHeader.Lookahead[Mode] : delay;
                _firstPositionField = packet.EndPositionField;
            }
            else if (_afterGap)
            {
                _delay = delay;
            }
            else if (delay != _delay && !packet.IsEndOfStream)
            {
                long expected = _coded - _delay!.Value;
                Node field = packet.EndPositionField ?? _parent;
                _findings.Add(Severity.Warning, "speex.granule-inconsistent", field, count =>
                    $"{Count(count, "granule position does", "granule positions do")} not match the samples of the packets before {(count == 1 ? "it" : "them")}: the first, at 0x{field.Offset:X}, is {position} where the packets give {expected}. Players that take the time from them show it wrong.");
            }

            _afterGap = false;
            _lastPosition = position;
            _lastPositionCoded = _coded;
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
            long trim = EndTrimming();
            long samples = HasTime ? Math.Clamp(_coded - Math.Max(0, _firstDelay ?? 0) - trim, 0, _coded) : 0;
            Node? encoder = AddEncoder();
            Node? distribution = AddStatistics(samples, trim);
            Node? packets = AddPackets();

            if (_header is { } header)
            {
                context.AddStream(new StreamInfo
                {
                    Nodes = [_parent],
                    Codec = Mode >= 0 ? $"Speex {header.ModeName.ToLowerInvariant()}" : "Speex",
                    SampleRate = Math.Max(0, header.Rate),
                    Channels = header.DecodedChannels,
                    ChannelMode = header.DecodedChannels == 1 ? "Mono" : "Stereo",
                    Samples = samples,
                    Duration = HasTime ? samples / (double)Rate : 0,
                    AverageBitrate = HasTime && _coded > 0 ? _bytes * 8.0 * Rate / _coded / 1000 : null,
                    IsVbr = _sizes.Count > 1,
                    Encoder = encoder?.Value?.ToString(),
                    EncoderConfidence = encoder is null ? null : Enum.Parse<Confidence>(encoder.Children[0].Value!.ToString()),
                    Series = Series(distribution, packets),
                });
            }

            _findings.Flush(context);
        }

        private void CheckPackets()
        {
            Node anchor = _headerNode ?? _parent;
            if (_seen < 2)
            {
                _findings.Add(Severity.Warning, "speex.comment-missing", anchor, _ =>
                    "The stream ends after its header, without the comment packet Speex in Ogg requires.");
            }
            else if (_header is { ExtraHeaders: > 0 } header && _seen < 2 + (long)header.ExtraHeaders)
            {
                _findings.Add(Severity.Warning, "speex.extra-headers-missing", Field("Extra Headers"), _ =>
                    $"The header announces {header.ExtraHeaders} extra headers, but the stream ends after {_seen - 2}.");
            }

            if (_packets == 0)
            {
                _findings.Add(Severity.Info, "speex.no-audio", anchor, _ => "The stream holds no audio packets.");
            }

            if (_firstDelay is { } delay && Mode >= 0 && delay != SpeexHeader.Lookahead[Mode])
            {
                _findings.Add(Severity.Info, "speex.delay-unusual", _firstPositionField ?? anchor, _ =>
                    $"The first granule position gives an encoder delay of {delay} samples, where libspeex's {SpeexHeader.ModeNames[Mode].ToLowerInvariant()} encoder delays {SpeexHeader.Lookahead[Mode]}; the stream was cut, or written by another encoder.");
            }
        }

        // What the last granule position cuts from the end of the last packets: the end trimming.
        private long EndTrimming()
        {
            if (_lastPosition is not { } position || _delay is not { } delay || !_lastIsEnd)
            {
                return 0;
            }

            long trim = _lastPositionCoded - delay - position;
            Node field = _lastPositionField ?? _parent;
            if (trim < 0)
            {
                _findings.Add(Severity.Warning, "speex.granule-beyond-packets", field, _ =>
                    $"The last granule position, {position} at 0x{field.Offset:X}, is {-trim} samples beyond what the packets before it hold; decoders end the stream earlier than it says.");
                return 0;
            }

            if (trim > SamplesPerPacket)
            {
                _findings.Add(Severity.Warning, "speex.end-trimming-large", field, _ =>
                    $"The last granule position, {position} at 0x{field.Offset:X}, cuts {trim} samples from the end, more than the {SamplesPerPacket} of the last packet; speexdec trims only within the last packet.");
            }

            return trim;
        }
    }
}
