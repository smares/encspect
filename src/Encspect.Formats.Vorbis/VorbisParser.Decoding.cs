using Encspect.Core;

namespace Encspect.Formats.Vorbis;

// The entropy pass and the feeding of the decoder (docs/design/lossy-decoders.md, "The parser and decoder split"). Every audio packet that codes a block is walked while the
// packets are read (PacketWalker): the walk reads the floors and the residues with the same code whether or not anything is decoded, and gives the coded bandwidth. Where the
// session decodes, the walk also fills the buffers it lends, and the decoder reads no coded bit.
public sealed partial class VorbisParser
{
    // A packet that has no coded bandwidth: no block, or one the container cut.
    private const ushort NoBandwidth = ushort.MaxValue;

    private sealed partial class StreamState
    {
        // One audio packet that codes a block (the flags of its record read None): the ranges the trims take out of the stream are stated first, then the packet
        // is walked and handed to the decoder. A packet held back (the last packets of a track with a DiscardPadding) is walked later with its own figures.
        private void Walk(MappedBytes packet, int index, PacketRecord record, bool isHeld = false, long first = 0, long samples = 0, bool followsGap = false)
        {
            List<(long First, long Samples)>? ranges = null;
            if (!isHeld)
            {
                samples = _lastReturned;
                first = _decoded - samples;
                followsGap = _pendingGap;
                _pendingGap = false;
                ranges = _ranges.Count > 0 ? [.. _ranges] : null;
                _ranges.Clear();
            }
            else
            {
                ranges = _heldRanges;
                _heldRanges = null;
            }

            if (_walker is null)
            {
                SetCodedEnd(index, NoBandwidth, isHeld);
                return;
            }

            IDecodeSession session = _session ??= StartSession();
            bool isDecoding = session.IsDecoding;
            if (packet.IsCut)
            {
                // The container cut the packet: its block counts and is lost (a gap), and the packet after it follows one.
                if (isDecoding)
                {
                    session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, packet.FileStart), "Packet", first, (int)samples) { IsUnreadable = true, FollowsGap = followsGap });
                }

                SetCodedEnd(index, NoBandwidth, isHeld);
                return;
            }

            foreach ((long rangeFirst, long rangeSamples) in ranges ?? [])
            {
                session.TrimInner(rangeFirst, rangeSamples);
            }

            SampleBuffers lent = isDecoding ? session.Lend(_walker.MaxInts(record.Mode), 1) : default;
            WalkFacts facts = _walker.Walk(packet.Span, lent.IsEmpty ? default : lent.Narrow);
            if (facts.IsCapped)
            {
                _findings.Add(Severity.Warning, "vorbis.walk-capped", PacketNode(_records, index), count =>
                    $"{Count(count, "audio packet names", "audio packets name")} more partitions than {(count == 1 ? "its" : "their")} bits can pay for (a class word of one bit can name 65,535 partitions, and the passes visit each); the analyzer reads {(count == 1 ? "it" : "them")} as ended where its budget of 1,024 visits and 16 for each bit runs out, so {(count == 1 ? "its" : "their")} coded bandwidth and decoded samples may differ from libvorbis's. The first at 0x{packet.FileStart:X}.");
            }

            if (_walker.Visits > 1 << 20)
            {
                _walker.Visits = 0;
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (isDecoding)
            {
                session.Decode(new CodedFrame(packet, "Packet", first, (int)samples) { IsWalked = !lent.IsEmpty, FollowsGap = followsGap });
            }

            ushort end = (ushort)Math.Min(facts.CodedEnd, ushort.MaxValue - 1);
            SetCodedEnd(index, end, isHeld);
            _bandwidths.TryGetValue(BandwidthOf(end, _records.BlockSize(record)), out long count);
            _bandwidths[BandwidthOf(end, _records.BlockSize(record))] = count + 1;
        }

        // A packet walked in its turn adds its record; a held one replaces the placeholder it left.
        private void SetCodedEnd(int index, ushort end, bool isHeld)
        {
            if (isHeld)
            {
                _records.CodedEnds.Set(index, end);
            }
            else
            {
                _records.CodedEnds.Add(end);
            }
        }

        private IDecodeSession StartSession()
        {
            var configuration = new byte[_identificationBytes!.Length + _setupBytes!.Length];
            _identificationBytes.CopyTo(configuration, 0);
            _setupBytes.CopyTo(configuration, _identificationBytes.Length);
            return context.StartDecoding(CodecId, new DecodeSetup
            {
                Stream = _parent,
                Channels = _identification!.Channels,
                BitsPerSample = 32,
                Configuration = new MappedBytes(configuration, _setupOffset),
                IsLossy = true,
                Shared = _model,
            });
        }

        // The upper edge of the last coded coefficient of a block of that size, in whole hertz.
        private int BandwidthOf(int coefficients, int blockSize) => (int)(((2 * coefficients * Rate) + blockSize) / (2L * blockSize));
    }
}
