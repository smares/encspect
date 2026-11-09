using Encspect.Core;

namespace Encspect.Formats.Tta;

// Hands the frames to the decoder: a file's from the data, a container's as they come.
internal static class Feeding
{
    // The largest frame read into memory to be decoded.
    public const long MaxDecodedFrameBytes = 64L << 20;

    private const string FrameName = "Frame";

    // Every frame the records hold, in order; the frames the records stopped before (Records.MaxEntries) are not decoded.
    public static void FromFile(State s, ByteReader reader, IDecodeSession session)
    {
        Records records = s.Records;
        if (records.Header is not { SampleRate: > 0 } header)
        {
            return;
        }

        long tooLarge = 0;
        long firstTooLarge = -1;
        for (int i = 0; i < records.Entries.Count && session.IsDecoding; i++)
        {
            Entry entry = records.Entries[i];
            int samples = (int)Math.Min(records.SamplesOf(i), int.MaxValue);
            long first = i * header.FrameLength;
            if (!entry.IsComplete || entry.Size > MaxDecodedFrameBytes)
            {
                if (entry.IsComplete)
                {
                    tooLarge++;
                    firstTooLarge = firstTooLarge < 0 ? i : firstTooLarge;
                }

                session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, entry.Offset), FrameName, first, samples) { IsUnreadable = true });
                continue;
            }

            reader.Position = entry.Offset;
            MappedBytes bytes = MappedBytes.Read(reader, [new ByteRange(entry.Offset, entry.Size)]);
            session.Decode(new CodedFrame(bytes, FrameName, first, samples) { IsDamaged = !entry.IsCrcValid });
        }

        if (tooLarge > 0)
        {
            long index = firstTooLarge;
            long count = tooLarge;
            s.Findings.Add(Severity.Info, "tta.frame-not-decoded", () => Nodes.Frame(records, (int)index), _ =>
                $"Frame {index} holds more than the {MaxDecodedFrameBytes >> 20} MiB the analyzer reads into memory to decode a frame, so it is not decoded{(count > 1 ? $"; {Counts.Of(count - 1, "more frame is", "more frames are")} not either" : string.Empty)}.", count);
        }
    }

    // One packet of a container: a frame, or what is left of one. With the header the frame's samples are the header's; without it a frame holds the frame
    // length for the container's rate, and the last one what the container's length leaves (a hint: the decoder counts the frame's codes).
    public static void FromPacket(State s, IDecodeSession session, MappedBytes packet, in Entry entry, int index, bool isLast)
    {
        Records records = s.Records;
        long frameLength = records.Header is { SampleRate: > 0 } header ? header.FrameLength : TtaHeader.FrameLengthOf(s.SampleRate);
        long first = index * frameLength;
        long declared = records.Header is not null ? records.SamplesOf(index) : frameLength;
        bool isUnknown = false;
        if (records.Header is null && isLast)
        {
            // A length that leaves no count of 1 to the frame length (FFmpeg's muxer counts the last frame as whole) still bounds the codes' count; the codes decide.
            isUnknown = s.ContainerSamples is null;
            declared = s.ContainerSamples is { } total ? Math.Clamp(total - first, 1, frameLength) : 0;
        }

        int samples = (int)Math.Min(Math.Max(declared, 0), int.MaxValue);
        bool isTooLarge = packet.Length > MaxDecodedFrameBytes;
        bool isUsable = !packet.IsCut && packet.Length >= 4;
        if (!isUsable || isTooLarge || isUnknown)
        {
            if (isTooLarge && !packet.IsCut)
            {
                s.Findings.Add(Severity.Info, "tta.frame-not-decoded", () => index < Records.MaxEntries ? Nodes.Frame(records, index) : s.Stream, count =>
                    $"Frame {index} holds more than the {MaxDecodedFrameBytes >> 20} MiB the analyzer reads into memory to decode a frame, so it is not decoded{(count > 1 ? $"; {Counts.Of(count - 1, "more frame is", "more frames are")} not either" : string.Empty)}.");
            }
            else if (isUnknown && isUsable)
            {
                s.Findings.Add(Severity.Info, "tta.last-frame-samples-unknown", () => index < Records.MaxEntries ? Nodes.Frame(records, index) : s.Stream, _ =>
                    $"The container stores no TTA header for the track and states no length, so the samples of the last frame ({index}) are not known and it is not decoded.");
            }

            session.Decode(new CodedFrame(new MappedBytes(ReadOnlyMemory<byte>.Empty, packet.FileStart), FrameName, first, samples) { IsUnreadable = true, FollowsGap = packet.FollowsGap });
            return;
        }

        session.Decode(new CodedFrame(packet, FrameName, first, samples) { IsDamaged = !entry.IsCrcValid, FollowsGap = packet.FollowsGap });
    }
}
