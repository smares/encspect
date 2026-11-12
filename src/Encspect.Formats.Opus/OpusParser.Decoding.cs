using Encspect.Core;

namespace Encspect.Formats.Opus;

// The entropy pass and the feeding of the decoder (docs/design/lossy-decoders.md, "The parser and decoder split"). Every audio packet whose framing is valid is walked while the
// packets are read (OpusWalker): the range coded data of every SILK and CELT frame is read with the same code whether or not anything is decoded, which gives the transient flags,
// the stereo coding and the intensity stereo bound of CELT, and the final range coder state of every frame (RFC 6716, section 6); where the session decodes, the walk also stores
// the integers the decoder reads. The decoder is handed whole packets, the last of them one packet late so that the end trimming reaches it before them.
public sealed partial class OpusParser
{
    // The samples the parser keeps back before it hands packets to the decoder: the end trimming, known at the last page, can reach back over earlier packets (RFC 7845, section 4.4), and
    // the largest packet holds 120 ms. A packet of 2.5 ms keeps its record in memory for this long, which bounds what a stream holds (2,304 records of a few hundred integers).
    private const int HoldSamples = 5760;

    // A packet that has no intensity stereo bound: no CELT frame with intensity stereo, or one that was not walked.
    private const ushort NoBound = ushort.MaxValue;

    private static readonly Category[] BlockSizeCategories =
    [
        new("Long", Explain.UsageLong) { Topic = Topics.OpusCelt },
        new("Short", Explain.UsageShort) { Topic = Topics.OpusCelt },
    ];

    private static readonly Category[] StereoCategories =
    [
        new("Left/right stereo", Explain.StereoDual) { Topic = Topics.OpusCelt },
        new("Mid/side stereo", Explain.StereoAngle) { Topic = Topics.MidSideStereo },
        new("Intensity stereo", Explain.StereoIntensity) { Topic = Topics.IntensityStereo },
        new("Mid/side and intensity stereo", Explain.StereoAngleIntensity) { Topic = Topics.IntensityStereo },
    ];

    private sealed partial class StreamState
    {
        // One packet waiting to be handed to the decoder: its bytes (the record of the entropy pass is made again when it is fed, so that what a stream holds back is bytes, not
        // integers: 255 streams of frames of 2.5 ms keep 2,300 packets), whether it can be read (not cut, malformed or too deep), whether the walk restarts before it, and where it lies.
        private sealed record HeldPacket(MappedBytes Packet, int Index, bool IsReadable, bool RestartsWalk, long First, int Samples, bool FollowsGap);

        private readonly IntWriter _writer = new();
        private readonly IntWriter _feedWriter = new();
        private OpusWalker[]? _feedWalkers;
        private readonly Queue<HeldPacket> _held = new();
        private OpusWalker[]? _walkers;
        private IDecodeSession? _session;
        private byte[]? _headBytes;
        private long _headOffset;
        private bool _isDecodable;
        private long _heldSamples;
        private bool _areWalkersStale;
        private bool _isRedundancyOverrun;
        private readonly int _holdFrames = Math.Max(1, context.Configuration?.DiscardPaddingFrames ?? 1);

        // The figures of CELT's frames (docs/formats/opus.md): every frame that was walked.
        private long _celtFrames;
        private long _transientFrames;
        private long _stereoCelt;
        private readonly long[] _stereoFrames = new long[4];
        private long _silkFrames;
        private long _redundantFrames;
        private long _rangeOverruns;
        private readonly CategorySeriesBuilder _blockUsage = new("Block Sizes", BlockSizeCategories, "frame", concept: GraphConcept.BlockSwitching);
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
            bool restarts = follows || _areWalkersStale;
            if (!unreadable)
            {
                if (restarts)
                {
                    foreach (OpusWalker walker in _walkers)
                    {
                        walker.Reset();
                    }
                }

                _session ??= StartSession();
                _writer.IsEnabled = false;
                _writer.Clear();
                if (!WalkPacket(packet, index, out bool isOverrun))
                {
                    unreadable = true;
                    _blockUsage.Add([]);
                    _stereoCoding.Add([]);
                    _bounds.Add(NoBound);
                    if (isOverrun && _isRedundancyOverrun)
                    {
                        _findings.Add(Severity.Warning, "opus.redundancy-overrun", () => PacketNode(_records, index), count =>
                            $"{Count(count, "hybrid frame states", "hybrid frames state")} a redundant frame longer than the frame's data after its SILK part (RFC 6716, section 4.5.1). libopus ignores the redundancy, decodes the SILK part and conceals the CELT part; the analyzer conceals nothing, so the packet is not decoded. The first at 0x{packet.FileStart:X}.");
                    }
                    else if (isOverrun)
                    {
                        _rangeOverruns++;
                        _findings.Add(Severity.Warning, "opus.frame-overrun", () => PacketNode(_records, index), count =>
                            $"{Count(count, "CELT frame needs", "CELT frames need")} more bits than {(count == 1 ? "it holds" : "they hold")}, which libopus refuses with an internal error (RFC 6716, section 4.1.1: a range decoder reads zeros past its data; libopus checks the position after a CELT frame). The first at 0x{packet.FileStart:X}; the packet is not decoded.");
                    }
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

            _held.Enqueue(new HeldPacket(packet, index, !unreadable, restarts, first, samples, follows));
            _heldSamples += samples;
            while (_held.Count > _holdFrames && _heldSamples - _held.Peek().Samples >= HoldSamples)
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
            Span<long> stereoFrames = stackalloc long[4];
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
                        _isRedundancyOverrun = facts.IsRedundancyOverrun;
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
                            bool hasIntensity = facts.Celt.IntensityBands > 0;
                            // One entry for each frame, the names of the MPEG and AAC parsers: the bands below the intensity bound are coded as left and right (dual stereo) or as mid and side
                            // with an angle, the bands from the bound up as intensity stereo.
                            int code = facts.Celt.DualBands > 0 ? (hasIntensity ? 2 : 0) : facts.Celt.AngleBands > 0 || !hasIntensity ? (hasIntensity ? 3 : 1) : 2;
                            stereoFrames[code]++;
                            stereoCodes[Math.Min(stereos++, stereoCodes.Length - 1)] = (byte)code;
                            if (hasIntensity)
                            {
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
            for (int k = 0; k < 4; k++)
            {
                _stereoFrames[k] += stereoFrames[k];
            }
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
            _heldSamples -= held.Samples;
            if (!_hasTrim)
            {
                _hasTrim = true;
                _session!.Trim(_head!.PreSkip, long.MaxValue / 4);
            }

            if (!held.IsReadable || !_session!.IsDecoding || !MakeRecord(held))
            {
                _session!.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, held.Packet.FileStart), "Packet", held.First, held.Samples) { IsUnreadable = true, FollowsGap = held.FollowsGap });
                return;
            }

            ReadOnlySpan<int> record = _feedWriter.Written;
            SampleBuffers lent = _session.Lend(record.Length, 1);
            if (lent.IsEmpty)
            {
                _session.Decode(new CodedFrame(held.Packet, "Packet", held.First, held.Samples) { FollowsGap = held.FollowsGap });
                return;
            }

            record.CopyTo(lent.Narrow);
            _session.Decode(new CodedFrame(held.Packet, "Packet", held.First, held.Samples) { IsWalked = true, FollowsGap = held.FollowsGap });
        }

        // The record of the entropy pass for a packet that is about to be decoded: the walk again, with the walkers of the feeding side, which see the packets in the order the first walk did
        // and restart where it did.
        private bool MakeRecord(HeldPacket held)
        {
            if (_feedWalkers is null)
            {
                _feedWalkers = new OpusWalker[_records.StreamCount];
                int coupled = _head!.Family == 0 ? (_head.Channels == 2 ? 1 : 0) : _head.Coupled;
                for (int s = 0; s < _feedWalkers.Length; s++)
                {
                    _feedWalkers[s] = new OpusWalker(disableInv: s >= coupled);
                }
            }

            if (held.RestartsWalk)
            {
                foreach (OpusWalker walker in _feedWalkers)
                {
                    walker.Reset();
                }
            }

            _feedWriter.IsEnabled = true;
            _feedWriter.Clear();
            ReadOnlySpan<byte> data = held.Packet.Span;
            int streams = _records.StreamCount;
            _feedWriter.Add(streams);
            int position = 0;
            for (int s = 0; s < streams; s++)
            {
                PartRecord part = _records.Part(held.Index, s);
                if (part.Status != PartStatus.Valid)
                {
                    return false;
                }

                int channels = Toc.IsStereo(part.TocByte) ? 2 : 1;
                int at = position + part.Header;
                _feedWriter.Add(part.Frames);
                for (int f = 0; f < part.Frames; f++)
                {
                    int length = _records.FrameLengths[part.FirstFrame + f];
                    FrameFacts facts = _feedWalkers[s].Walk(_feedWriter, data.Slice(at, length), Toc.Mode(part.TocByte), Toc.Bandwidth(part.TocByte), Toc.FrameSamples(part.TocByte), channels);
                    at += length;
                    if (facts.IsOverrun)
                    {
                        return false;
                    }
                }

                position += part.Length;
            }

            return true;
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
