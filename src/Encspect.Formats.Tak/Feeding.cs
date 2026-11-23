using Encspect.Core;

namespace Encspect.Formats.Tak;

// What the decoder takes from the parser besides the frames (DecodeSetup.Shared): the stream info as the parser read it, the stored MD5 where the stream has one and the
// layout of the channels.
internal sealed class DecodeShared
{
    public required StreamInfoFields Info { get; init; }

    public byte[]? Md5 { get; init; }

    // Where the 16 bytes of the MD5 lie in the file, for a finding about them.
    public long Md5At { get; init; }

    public IReadOnlyList<Speaker>? Layout { get; init; }
}

// Hands the frames of the records to the decoder, in stream order.
internal static class Feeding
{
    private const int NumberMask = (1 << 21) - 1;

    private const string FrameName = "Frame";

    // Every frame the records hold (the first FrameFacts.MaxEntries): a frame cut by the end of the data is not decoded, and the one after a skipped stretch or a break in the
    // numbers follows a gap. A frame's position is its number times the frame length where its header's CRC-24 holds, so frames lost between two show as a jump.
    public static void Run(State s, ByteReader reader, IDecodeSession session)
    {
        Records r = s.Records;
        if (r.Stream is not { } info || !Layout.IsFrameSizeValid(info.FrameSizeType, info.SampleRate))
        {
            return;
        }

        FrameFacts f = r.Frames;
        long frameLength = Layout.FrameSamples(info.FrameSizeType, info.SampleRate);
        long expected = 0;
        bool followsGap = false;
        for (int i = 0; i < f.Entries.Count && session.IsDecoding; i++)
        {
            Entry e = f.Entries[i];
            if (e.IsSkipped)
            {
                followsGap = true;
                continue;
            }

            long number = e.IsHeaderCrcValid ? e.Number : expected;
            followsGap |= number != expected;
            expected = (number + 1) & NumberMask;
            long first = number * frameLength;
            int samples = (int)((e.Flags & EntryFlags.IsLast) != 0 ? e.Samples : frameLength);
            if (e.IsCut)
            {
                session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, e.Offset), FrameName, first, samples) { IsUnreadable = true, FollowsGap = followsGap });
                followsGap = true;
                continue;
            }

            reader.Position = e.Offset;
            MappedBytes bytes = MappedBytes.Read(reader, [new ByteRange(e.Offset, e.Length)]);
            bool fails = !e.IsHeaderCrcValid || !e.IsDataCrcValid;
            session.Decode(new CodedFrame(bytes, FrameName, first, samples) { IsDamaged = fails, FailsChecksum = fails, FollowsGap = followsGap });
            followsGap = false;
        }
    }
}
