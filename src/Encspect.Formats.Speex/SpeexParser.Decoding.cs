using Encspect.Core;

namespace Encspect.Formats.Speex;

// The entropy pass and the feeding of the decoder (docs/design/lossy-decoders.md, "The parser and decoder split"). Every audio packet is walked while the packets are read
// (SpeexFrames.Walk): the walk reads the parameters of every frame, whether or not anything is decoded. Where the session decodes, the packets are handed to it once the delay
// that the granule positions give is known, a few packets late so that the end trimming reaches them first, and each is walked again then, into the buffers the session lends:
// what the stream holds back is bytes, not parameters.
public sealed partial class SpeexParser
{
    // The audio packets the parser keeps back at the end of the stream, which the end trimming can reach (it is at most the last packet, past which speexdec trims nothing),
    // and the most it holds while the first granule position, which gives the delay, is still to come.
    private const int HoldPackets = 4;

    private const int HoldBeforeDelay = 512;

    // The most bytes of packets it holds while the delay is still to come (the last HoldPackets are held whatever their size); where nothing is decoded no bytes are held at all.
    private const long HoldBytesBeforeDelay = 1 << 20;

    // The largest position a trim is stated at: a delay a corrupt granule position makes absurd trims everything.
    private const long MaxPosition = long.MaxValue / 8;

    private sealed partial class StreamState
    {
        private sealed record HeldPacket(MappedBytes Packet, bool IsUnreadable, long First, int Samples, bool FollowsGap);

        private readonly Queue<HeldPacket> _held = new();
        private long _heldBytes;
        private IDecodeSession? _session;
        private byte[]? _headerBytes;
        private long _headerOffset;
        private bool _isTrimStated;

        private void NoteHeader(MappedBytes packet)
        {
            if (packet.Length >= SpeexHeader.Length)
            {
                _headerBytes = packet.Span[..SpeexHeader.Length].ToArray();
                _headerOffset = packet.FileStart;
            }
        }

        // Hands one audio packet to the decoder, in order: held back first (see the class comment).
        private void Hand(MappedBytes packet, long first, int samples, bool isUnreadable)
        {
            if (_headerBytes is null || Mode < 0)
            {
                return;
            }

            _session ??= context.StartDecoding(CodecId, new DecodeSetup
            {
                Stream = _parent,
                Channels = _header!.DecodedChannels,
                BitsPerSample = 16,
                Configuration = new MappedBytes(_headerBytes, _headerOffset),
                IsLossy = true,
            });

            // A session that decodes nothing takes the packets as lost, whatever their bytes, so only their place is held.
            MappedBytes kept = _session.IsDecoding ? packet : MappedBytes.Opaque(ReadOnlyMemory<byte>.Empty, packet.FileStart, packet.FileStart);
            _heldBytes += kept.Length;
            _held.Enqueue(new HeldPacket(kept, isUnreadable, first, samples, packet.FollowsGap));
            Release();
        }

        // Feeds the packets that nothing can reach any more: all but the last HoldPackets once the delay is known, or once the stream has held enough that it is assumed
        // (as 0, which is what the report's samples count from when no granule position comes).
        private void Release()
        {
            if (!_isTrimStated)
            {
                if (_firstDelay is null && _held.Count <= HoldBeforeDelay && _heldBytes <= HoldBytesBeforeDelay)
                {
                    return;
                }

                StateLeadingTrim(_firstDelay ?? 0);
            }

            while (_held.Count > HoldPackets)
            {
                Feed(Take());
            }
        }

        // The range of positions the granule positions leave in the stream: from the encoder's delay, with the end stated where the stream's end is known.
        private void StateLeadingTrim(long delay)
        {
            _isTrimStated = true;
            _session!.Trim(Math.Clamp(delay, 0, MaxPosition), long.MaxValue / 4);
        }

        private HeldPacket Take()
        {
            HeldPacket held = _held.Dequeue();
            _heldBytes -= held.Packet.Length;
            return held;
        }

        private void Feed(HeldPacket held)
        {
            IDecodeSession session = _session!;
            if (held.IsUnreadable || !session.IsDecoding)
            {
                session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, held.Packet.FileStart), "Packet", held.First, held.Samples) { IsUnreadable = true, FollowsGap = held.FollowsGap });
                return;
            }

            int expected = _header!.FramesPerPacketCounted;
            SampleBuffers lent = session.Lend((expected + 1) * SpeexLayout.FrameInts, 1);
            if (lent.IsEmpty)
            {
                session.Decode(new CodedFrame(held.Packet, "Packet", held.First, held.Samples) { FollowsGap = held.FollowsGap, FailsChecksum = held.Packet.FailsChecksum });
                return;
            }

            SpeexFrames.Walk(held.Packet.Span, Mode, expected + 1, null, expected, lent.Narrow);
            session.Decode(new CodedFrame(held.Packet, "Packet", held.First, held.Samples) { IsWalked = true, FollowsGap = held.FollowsGap, FailsChecksum = held.Packet.FailsChecksum });
        }

        // The end of the stream: the trim reaches the packets still held, which are then fed.
        private StreamVerification? FinishDecoding(long samples)
        {
            if (_session is null)
            {
                return null;
            }

            if (HasTime)
            {
                long first = Math.Clamp(_firstDelay ?? 0, 0, MaxPosition);
                _session.Trim(first, first + samples);
                _isTrimStated = true;
            }

            while (_held.Count > 0)
            {
                Feed(Take());
            }

            return _session.Finish([]);
        }
    }
}
