namespace Encspect.Core;

// The parsers' side of the decoder "pcm": reads a data chunk of whole sample frames in slices and hands each to the session as a CodedFrame. It reads
// nothing where the session decodes nothing, so a file that is only analyzed costs no read of its samples.
public static class PcmFeed
{
    // The most bytes of a slice, a whole number of sample frames; a single sample frame can be larger (65,535 channels).
    public const int SliceBytes = 64 * 1024;

    // frames sample frames of frameBytes bytes each start at start in the reader's file. A slice is no unit of the parser's tree: NodeName is the data
    // chunk's node. The frames the reader holds are the most that are read.
    public static void Run(IDecodeSession session, ByteReader reader, long start, long frames, long frameBytes, string nodeName)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(reader);

        if (!session.IsDecoding || frameBytes < 1 || frames < 1 || start < reader.Start || start > reader.End)
        {
            return;
        }

        frames = Math.Min(frames, (reader.End - start) / frameBytes);
        long perSlice = Math.Max(1, SliceBytes / frameBytes);
        byte[] buffer = [];
        for (long done = 0; done < frames && session.IsDecoding;)
        {
            int count = (int)Math.Min(perSlice, frames - done);
            int length = checked((int)(count * frameBytes));
            if (buffer.Length < length)
            {
                buffer = new byte[length];
            }

            long at = start + (done * frameBytes);
            reader.Position = at;
            reader.Read(buffer.AsSpan(0, length));
            session.Decode(new CodedFrame(new MappedBytes(buffer.AsMemory(0, length), at), nodeName, done, count));
            done += count;
        }
    }
}
