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

    // The frame the parser walked last, which the decoder reconstructs when it is handed the frame.
    public WalkedFrame? Current { get; set; }
}

// Walks the coded data of every frame the records hold, for the coding statistics and graphs, and hands the frames to the decoder, in stream order.
internal static class Feeding
{
    private const int NumberMask = (1 << 21) - 1;

    private const string FrameName = "Frame";

    // Every frame the records hold (the first FrameFacts.MaxEntries): a frame cut by the end of the data is not read, and the one after a skipped stretch or a break in the numbers
    // follows a gap. A frame's position is its number times the frame length where its header's CRC-24 holds, so frames lost between two show as a jump. The walk reads each frame
    // once (FrameSyntax); with a decoding session it also stores the values, which the decoder reconstructs from. Returns the coding series of the frames that have a bitrate point,
    // or null where the stream is one the walk does not understand.
    public static CodingSeries? Run(State s, ByteReader reader, IDecodeSession session, DecodeShared shared)
    {
        Records r = s.Records;
        if (r.Stream is not { } info || FrameSyntax.Refusal(info) is not null)
        {
            return null;
        }

        FrameFacts f = r.Frames;
        var syntax = new FrameSyntax(info);
        var walked = new WalkedFrame();
        byte[] buffer = [];
        shared.Current = walked;
        var coding = new CodingSeries(info.SampleRate, info.Codec == 2 && info.Channels == 2);
        long frameLength = Layout.FrameSamples(info.FrameSizeType, info.SampleRate);
        long expected = 0;
        bool followsGap = false;
        Records records = r;
        for (int i = 0; i < f.Entries.Count; i++)
        {
            reader.CancellationToken.ThrowIfCancellationRequested();
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
                if (session.IsDecoding)
                {
                    session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, e.Offset), FrameName, first, samples) { IsUnreadable = true, FollowsGap = followsGap });
                }

                followsGap = true;
                continue;
            }

            bool isDecoding = session.IsDecoding;
            MappedBytes? bytes = isDecoding ? ReadFrame(reader, e) : null;
            if (!isDecoding)
            {
                // One buffer for the walk of a stream that is not decoded, grown to the longest frame.
                if (buffer.Length < e.Length)
                {
                    buffer = new byte[e.Length];
                }

                reader.Position = e.Offset;
                reader.Read(buffer.AsSpan(0, (int)e.Length));
            }

            ReadOnlySpan<byte> all = bytes is not null ? bytes.Span : buffer.AsSpan(0, (int)e.Length);
            ReadOnlySpan<byte> coded = all.Slice(e.HeaderLength, all.Length - e.HeaderLength - Layout.CrcLength);
            bool isWalked = false;
            if (samples > 0)
            {
                try
                {
                    syntax.Read(coded, samples, walked, isDecoding);
                    isWalked = true;
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                {
                    string why = ex is EndOfStreamException ? "the data ends inside the frame's coded fields" : char.ToLowerInvariant(ex.Message[0]) + ex.Message[1..].TrimEnd('.');
                    long index = i;
                    long offset = e.Offset;
                    s.Findings.Add(Severity.Warning, "tak.frame-syntax-invalid", () => FrameNodes.Entry(records, (int)index), count =>
                        $"The coded data of the frame at 0x{offset:X} cannot be read: {why}{(count > 1 ? $"; {Counts.Of(count - 1, "more frame", "more frames")} {Counts.Agree(count - 1, "is", "are")} unreadable too" : string.Empty)}.");
                }
            }

            if (isWalked)
            {
                coding.AddFrame(walked);
                if (walked.ConsumedBytes < coded.Length)
                {
                    long after = coded.Length - walked.ConsumedBytes;
                    long index = i;
                    long offset = e.Offset;
                    s.Findings.Add(Severity.Info, "tak.frame-bytes-after-data", () => FrameNodes.Entry(records, (int)index), count =>
                        $"The coded data of the frame at 0x{offset:X} ends {Counts.Of(after, "byte", "bytes")} before its CRC-24; the description reads nothing after the last field{(count > 1 ? $"; {Counts.Of(count - 1, "more frame", "more frames")} {Counts.Agree(count - 1, "has", "have")} such bytes" : string.Empty)}.");
                }
            }
            else if (samples > 0)
            {
                coding.AddEmpty(samples);
            }

            if (isDecoding)
            {
                bool fails = !e.IsHeaderCrcValid || !e.IsDataCrcValid;
                session.Decode(new CodedFrame(bytes!, FrameName, first, samples) { IsDamaged = fails, FailsChecksum = fails, FollowsGap = followsGap, IsUnreadable = !isWalked });
            }

            followsGap = false;
        }

        return coding;
    }

    private static MappedBytes ReadFrame(ByteReader reader, Entry e)
    {
        reader.Position = e.Offset;
        return MappedBytes.Read(reader, [new ByteRange(e.Offset, e.Length)]);
    }
}
