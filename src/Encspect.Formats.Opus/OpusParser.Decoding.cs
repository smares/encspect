using Encspect.Core;

namespace Encspect.Formats.Opus;

// The entropy pass and the feeding of the decoder (docs/design/lossy-decoders.md, "The parser and decoder split"). Every audio packet whose framing is valid is walked while the
// packets are read (OpusWalker): the range coded data of every SILK and CELT frame is read with the same code whether or not anything is decoded, which gives the transient flags,
// the stereo coding and the intensity stereo bound of CELT, and the final range coder state of every frame (RFC 6716, section 6); where the session decodes, the walk also stores
// the integers the decoder reads. The decoder is handed whole packets, the last of them one packet late so that the end trimming reaches it before them.
public sealed partial class OpusParser
{
    // A packet that has no intensity stereo bound: no CELT frame with intensity stereo, or one that was not walked.
    private const ushort NoBound = ushort.MaxValue;

    private static readonly Category[] BlockUsageCategories =
    [
        new("Long Blocks", Explain.UsageLong) { Topic = Topics.OpusCelt },
        new("Short Blocks", Explain.UsageShort) { Topic = Topics.OpusCelt },
    ];

    private static readonly Category[] StereoCategories =
    [
        new("Mid/Side Stereo", Explain.StereoAngle) { Topic = Topics.OpusCelt },
        new("Dual Stereo", Explain.StereoDual) { Topic = Topics.OpusCelt },
        new("Intensity Stereo", Explain.StereoIntensity) { Topic = Topics.IntensityStereo },
    ];

    private sealed partial class StreamState
    {
        // One packet waiting to be handed to the decoder: its record (null for a packet the decoder cannot read: cut, malformed or too deep) and where it lies.
        private sealed record HeldPacket(MappedBytes Packet, int[]? Record, long First, int Samples, bool FollowsGap);

        private readonly IntWriter _writer = new();
        private readonly Queue<HeldPacket> _held = new();
        private OpusWalker[]? _walkers;
        private IDecodeSession? _session;
        private byte[]? _headBytes;
        private long _headOffset;
        private bool _isDecodable;
        private bool _areWalkersStale;
        private readonly int _holdFrames = Math.Max(1, context.Configuration?.DiscardPaddingFrames ?? 1);

        // The figures of CELT's frames (docs/formats/opus.md): every frame that was walked.
        private long _celtFrames;
        private long _transientFrames;
        private long _stereoCelt;
        private long _angleFrames;
        private long _dualFrames;
        private long _intensityFrames;
        private long _silkFrames;
        private long _redundantFrames;
        private long _rangeOverruns;
        private readonly CategorySeriesBuilder _blockUsage = new("Block Usage", BlockUsageCategories, "frame", concept: GraphConcept.BlockSwitching);
        private readonly CategorySeriesBuilder _stereoCoding = new("Stereo Coding", StereoCategories, "frame", concept: GraphConcept.StereoCoding);
        private readonly ChunkedList<ushort> _bounds = new();
        private int _boundCount;
        private ushort _lowestBound = NoBound;
        private ushort _highestBound;

        private void NoteHead(MappedBytes packet, OpusHead head)
        {
            _headBytes = packet.Span[..Math.Min(head.Length, packet.Length)].ToArray();
            _headOffset = packet.FileStart;
            _isDecodable = head.Length >= head.ExpectedLength && OpusDecodable.Check(head);
        }

        // Walks and hands over one audio packet (docs/design/lossy-decoders.md, decisions 4 and 5): a packet the decoder cannot read is lost, and the packet after it follows a gap.
        private void Process(MappedBytes packet, int index, bool isMalformed, long first, int samples)
        {
            if (!_isDecodable || _head is null)
            {
                _blockUsage.Add([]);
                _stereoCoding.Add([]);
                _bounds.Add(NoBound);
                return;
            }

            if (_walkers is null)
            {
                _walkers = new OpusWalker[_records.StreamCount];
                int coupled = _head.Family == 0 ? (_head.Channels == 2 ? 1 : 0) : _head.Coupled;
                for (int s = 0; s < _walkers.Length; s++)
                {
                    _walkers[s] = new OpusWalker(disableInv: s >= coupled);
                }
            }

            bool unreadable = packet.IsCut || isMalformed || samples == 0;
            bool follows = packet.FollowsGap;
            int[]? record = null;
            if (!unreadable)
            {
                if (follows || _areWalkersStale)
                {
                    foreach (OpusWalker walker in _walkers)
                    {
                        walker.Reset();
                    }
                }

                _session ??= StartSession();
                _writer.IsEnabled = _session.IsDecoding;
                _writer.Clear();
                if (!WalkPacket(packet, index, out bool isOverrun))
                {
                    unreadable = true;
                    _blockUsage.Add([]);
                    _stereoCoding.Add([]);
                    _bounds.Add(NoBound);
                    if (isOverrun)
                    {
                        _rangeOverruns++;
                        _findings.Add(Severity.Warning, "opus.frame-overrun", () => PacketNode(_records, index), count =>
                            $"{Count(count, "frame needs", "frames need")} more bits than {(count == 1 ? "it holds" : "they hold")}, which libopus refuses with an internal error (RFC 6716, section 4.1.1: a range decoder reads zeros past its data). The first at 0x{packet.FileStart:X}; the packet is not decoded.");
                    }
                }
                else if (_session.IsDecoding)
                {
                    record = _writer.Written.ToArray();
                }
            }
            else
            {
                _blockUsage.Add([]);
                _stereoCoding.Add([]);
                _bounds.Add(NoBound);
            }

            _areWalkersStale = unreadable;
            if (_session is null)
            {
                return;
            }

            _held.Enqueue(new HeldPacket(packet, record, first, samples, follows));
            while (_held.Count > _holdFrames)
            {
                FeedOldest();
            }
        }

        // The packet's frames in stream order, each stream's one after the other: the record of the session's decoder and the figures of the report. Returns false for a packet
        // a decoder refuses (a frame needs more bits than it holds).
        private bool WalkPacket(MappedBytes packet, int index, out bool isOverrun)
        {
            isOverrun = false;
            ReadOnlySpan<byte> data = packet.Span;
            int streams = _records.StreamCount;
            _writer.Add(streams);
            Span<byte> blockCodes = stackalloc byte[CategorySeries.MaxValuesPerUnit];
            Span<byte> stereoCodes = stackalloc byte[CategorySeries.MaxValuesPerUnit];
            long silkFrames = 0;
            long redundantFrames = 0;
            long celtFrames = 0;
            long transientFrames = 0;
            long stereoCelt = 0;
            long angleFrames = 0;
            long dualFrames = 0;
            long intensityFrames = 0;
            ushort lowest = NoBound;
            ushort highest = 0;
            int blocks = 0;
            int stereos = 0;
            ushort bound = NoBound;
            int position = 0;
            for (int s = 0; s < streams; s++)
            {
                PartRecord part = _records.Part(index, s);
                if (part.Status != PartStatus.Valid)
                {
                    return false;
                }

                int channels = Toc.IsStereo(part.TocByte) ? 2 : 1;
                int mode = Toc.Mode(part.TocByte);
                int bandwidth = Toc.Bandwidth(part.TocByte);
                int size = Toc.FrameSamples(part.TocByte);
                int at = position + part.Header;
                _writer.Add(part.Frames);
                for (int f = 0; f < part.Frames; f++)
                {
                    int length = _records.FrameLengths[part.FirstFrame + f];
                    FrameFacts facts = _walkers![s].Walk(_writer, data.Slice(at, length), mode, bandwidth, size, channels);
                    at += length;
                    if (facts.IsOverrun)
                    {
                        isOverrun = true;
                        return false;
                    }

                    silkFrames += facts.HasSilk ? 1 : 0;
                    redundantFrames += facts.HasRedundancy ? 1 : 0;
                    if (facts.HasCelt)
                    {
                        celtFrames++;
                        transientFrames += facts.Celt.IsTransient ? 1 : 0;
                        if (blocks < blockCodes.Length)
                        {
                            blockCodes[blocks++] = facts.Celt.IsTransient ? (byte)1 : (byte)0;
                        }

                        if (facts.Celt.Channels == 2 && !facts.Celt.IsSilence)
                        {
                            stereoCelt++;
                            if (facts.Celt.AngleBands > 0)
                            {
                                angleFrames++;
                                stereoCodes[Math.Min(stereos++, stereoCodes.Length - 1)] = 0;
                            }

                            if (facts.Celt.DualBands > 0)
                            {
                                dualFrames++;
                                stereoCodes[Math.Min(stereos++, stereoCodes.Length - 1)] = 1;
                            }

                            if (facts.Celt.IntensityBands > 0)
                            {
                                intensityFrames++;
                                stereoCodes[Math.Min(stereos++, stereoCodes.Length - 1)] = 2;
                                ushort hertz = (ushort)(CeltTables.EBand5Ms[Math.Min(facts.Celt.Intensity, CeltSyntax.Bands)] * 200);
                                bound = bound == NoBound ? hertz : Math.Min(bound, hertz);
                                lowest = lowest == NoBound ? hertz : Math.Min(lowest, hertz);
                                highest = Math.Max(highest, hertz);
                            }
                        }
                    }
                }

                position += part.Length;
            }

            _silkFrames += silkFrames;
            _redundantFrames += redundantFrames;
            _celtFrames += celtFrames;
            _transientFrames += transientFrames;
            _stereoCelt += stereoCelt;
            _angleFrames += angleFrames;
            _dualFrames += dualFrames;
            _intensityFrames += intensityFrames;
            _lowestBound = lowest == NoBound ? _lowestBound : _lowestBound == NoBound ? lowest : Math.Min(_lowestBound, lowest);
            _highestBound = Math.Max(_highestBound, highest);
            _blockUsage.Add(blockCodes[..blocks]);
            _stereoCoding.Add(stereoCodes[..Math.Min(stereos, stereoCodes.Length)]);
            _bounds.Add(bound);
            _boundCount += bound == NoBound ? 0 : 1;
            return true;
        }

        private IDecodeSession StartSession() => context.StartDecoding(CodecId, new DecodeSetup
        {
            Stream = _parent,
            Channels = _head!.Channels,
            BitsPerSample = 32,
            Configuration = new MappedBytes(_headBytes!, _headOffset),
            IsLossy = true,
        });

        // The leading trim is stated once, before the first packet; the end trimming where the stream's end is known (FinishDecoding).
        private void FeedOldest()
        {
            HeldPacket held = _held.Dequeue();
            if (!_hasTrim)
            {
                _hasTrim = true;
                _session!.Trim(_head!.PreSkip, long.MaxValue / 4);
            }

            if (held.Record is null)
            {
                _session!.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, held.Packet.FileStart), "Packet", held.First, held.Samples) { IsUnreadable = true, FollowsGap = held.FollowsGap });
                return;
            }

            SampleBuffers lent = _session!.Lend(held.Record.Length, 1);
            if (lent.IsEmpty)
            {
                _session.Decode(new CodedFrame(held.Packet, "Packet", held.First, held.Samples) { FollowsGap = held.FollowsGap });
                return;
            }

            held.Record.CopyTo(lent.Narrow);
            _session.Decode(new CodedFrame(held.Packet, "Packet", held.First, held.Samples) { IsWalked = true, FollowsGap = held.FollowsGap });
        }

        private bool _hasTrim;

        // The end of the stream is known: the packets still waiting are the ones the end trimming takes samples from.
        private StreamVerification? FinishDecoding(long trim)
        {
            if (_session is null)
            {
                return null;
            }

            int preSkip = _head?.PreSkip ?? 0;
            if (_hasTrim || _held.Count > 0)
            {
                _hasTrim = true;
                _session.Trim(preSkip, Math.Max(preSkip, _decoded - trim));
            }

            while (_held.Count > 0)
            {
                FeedOldest();
            }

            return _session.Finish([]);
        }
    }
}
