using Encspect.Core;

namespace Encspect.Formats.Flac;

// What reading one frame from its first byte found.
internal struct FrameWalk
{
    public FrameHeader Header;

    // Every subframe read to its end and the CRC-16 there: the frame's length is known.
    public bool IsComplete;

    // The data ended inside the frame.
    public bool IsCut;

    // The first subframe that cannot be read, when none can: its fault and channel.
    public SubframeFault Fault;
    public int FaultChannel;

    // The frame's length in bytes through the CRC-16, where IsComplete.
    public int Length;
    public ushort StoredCrc16;
    public ushort ComputedCrc16;
    public bool HasNonZeroPadding;

    // How many bytes of the data the walk consumed, to count the work of a search.
    public int BytesRead;

    // The walk stored the samples in the buffers the session lent.
    public bool IsStored;

    public readonly bool Crc16Holds => IsComplete && StoredCrc16 == ComputedCrc16;
}

// Reads a frame: header, subframes and CRC-16 (RFC 9639, section 9), the one pass over its coded syntax that the report and the decoder share.
internal static class FrameWalker
{
    // The longest frame the walk follows, which is the most STREAMINFO's 24-bit maximum frame size can state (RFC 9639, section 8.2).
    public const int MaxFrameBytes = (1 << 24) - 1;

    // Walks the frame at the start of data. facts gets one entry per channel read. A session that is decoding lends its buffers, so the
    // walk stores the samples there. data should hold the frame to its end, or as much of it as the file has.
    public static FrameWalk Walk(ReadOnlySpan<byte> data, in FrameHeader header, Span<SubframeFacts> facts, IDecodeSession? session, PartitionLog[]? logs = null)
    {
        var walk = new FrameWalk { Header = header };
        int channels = header.Channels;
        SampleBuffers lent = default;
        bool needsWide = header.BitsPerSample == 32 && header.Assignment != ChannelAssignment.Independent;
        if (session is { IsDecoding: true })
        {
            lent = session.Lend(header.BlockSize + SubframeLayout.MetaSlots, channels, needsWide);
        }

        walk.IsStored = !lent.IsEmpty;
        var bits = new BitCursor(data);
        bits.Skip(header.Length * 8);
        for (int channel = 0; channel < channels; channel++)
        {
            SubframeSamples samples = lent.IsEmpty ? default : new SubframeSamples(lent.Channel(channel), needsWide && header.IsSide(channel) ? lent.WideChannel(channel) : default);
            SubframeFacts subframe = SubframeSyntax.Read(ref bits, header.BlockSize, header.BitsPerSample, header.IsSide(channel), samples, logs?[channel]);
            facts[channel] = subframe;
            if (subframe.Fault != SubframeFault.None)
            {
                walk.Fault = subframe.Fault;
                walk.FaultChannel = channel;
                walk.IsCut = subframe.Fault == SubframeFault.Overrun;
                walk.BytesRead = Math.Min(data.Length, (bits.Position + 7) >> 3);
                return walk;
            }
        }

        int endBit = bits.Position;
        int bytes = (endBit + 7) >> 3;
        walk.BytesRead = Math.Min(data.Length, bytes + 2);
        if (bytes + 2 > data.Length)
        {
            walk.IsCut = true;
            walk.Fault = SubframeFault.EndsBeforeCrc;
            return walk;
        }

        walk.HasNonZeroPadding = (endBit & 7) != 0 && (data[endBit >> 3] & (0xFF >> (endBit & 7))) != 0;
        walk.StoredCrc16 = (ushort)((data[bytes] << 8) | data[bytes + 1]);
        walk.ComputedCrc16 = FlacCrc.Crc16(data[..bytes]);
        walk.Length = bytes + 2;
        walk.IsComplete = true;
        return walk;
    }
}
