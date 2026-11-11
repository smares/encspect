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
        // One audio packet that codes a block (the flags of its record read None).
        private void Walk(MappedBytes packet, int index, PacketRecord record)
        {
            if (_walker is null)
            {
                _records.CodedEnds.Add(NoBandwidth);
                return;
            }

            IDecodeSession session = _session ??= StartSession();
            long samples = _lastReturned;
            long first = _decoded - samples;
            bool isDecoding = session.IsDecoding;
            if (packet.IsCut)
            {
                // The container cut the packet: its block counts and is lost (a gap), and the packet after it follows one.
                if (isDecoding)
                {
                    session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, packet.FileStart), "Packet", first, (int)samples) { IsUnreadable = true, FollowsGap = _pendingGap });
                }

                _pendingGap = false;
                _records.CodedEnds.Add(NoBandwidth);
                return;
            }

            UpdateTrim(session);
            SampleBuffers lent = isDecoding ? session.Lend(_walker.MaxInts(record.Mode), 1) : default;
            WalkFacts facts = _walker.Walk(packet.Span, lent.IsEmpty ? default : lent.Narrow);
            if (_walker.Visits > 1 << 20)
            {
                _walker.Visits = 0;
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (isDecoding)
            {
                session.Decode(new CodedFrame(packet, "Packet", first, (int)samples) { IsWalked = !lent.IsEmpty, IsUnreadable = lent.IsEmpty, FollowsGap = _pendingGap });
            }

            _pendingGap = false;
            ushort end = (ushort)Math.Min(facts.CodedEnd, ushort.MaxValue - 1);
            _records.CodedEnds.Add(end);
            _bandwidths.TryGetValue(BandwidthOf(end, _records.BlockSize(record)), out long count);
            _bandwidths[BandwidthOf(end, _records.BlockSize(record))] = count + 1;
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
            });
        }

        // The range of positions the granule positions trim the stream to (Opus's pre-skip and end trimming in the same way): the start trimming from the first position, known
        // when the packet that carries it is read, and the end from an end-of-stream page, known when its last packet is. A trim stated at the first packet after one of them
        // reaches the decoder before the blocks it applies to (a granule position's page ends with its last packet).
        private void UpdateTrim(IDecodeSession session)
        {
            if (!session.IsDecoding || !HasTime)
            {
                return;
            }

            long leading = _hasAnchor ? StartTrimming(report: false) : 0;
            long end = _endSeen ? Math.Max(leading, _decoded - _endTrim) : long.MaxValue;
            if (_trim != (leading, end))
            {
                _trim = (leading, end);
                session.Trim(leading, end);
            }
        }

        // The upper edge of the last coded coefficient of a block of that size, in whole hertz.
        private int BandwidthOf(int coefficients, int blockSize) => (int)(((2 * coefficients * Rate) + blockSize) / (2L * blockSize));
    }
}
