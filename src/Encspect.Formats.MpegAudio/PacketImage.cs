using Encspect.Core;

namespace Encspect.Formats.MpegAudio;

// The packets a container hands over, as a read-only stream at their file offsets, so that the parser reads them as it reads a file: the bytes between packets (a Matroska
// block's header, the chunks of another track in an MP4 file, the preamble and stuffing of an IEC 61937 burst) are not in the image and read as zeros, and the parser
// never goes there (BlockReader.Stretches). Only the ranges of the packets are kept, merged where they touch; the bytes are read from the container's reader when asked for, and
// the pair of bytes swapped back for packets that are 16-bit words. The parser reads a stream back after its first pass (the bit reservoir, the ancillary data, the decoder), so
// memory does not grow with the stream: the stretches are two numbers each.
internal sealed class PacketImage(ByteReader source, bool isWordSwapped) : Stream
{
    private long _position;

    // The stretches of the packets (those that touch are one), the first packet's offset and the end of the last.
    public List<ByteRange> Stretches { get; } = [];

    public long First { get; private set; } = -1;

    public long Last { get; private set; }

    // How many packets were left out, and the first reason.
    public string? Problem { get; private set; }

    public long Skipped { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => Last;

    public override long Position
    {
        get => _position;
        set => _position = value;
    }

    // Adds a packet's range; false (with Problem set) when it cannot be placed: its bytes are not bytes of the file in one piece (a compressed or stripped block), or it lies
    // before the one it follows.
    public bool Add(MappedBytes packet)
    {
        int length = packet.Length;
        if (length == 0)
        {
            return true;
        }

        IReadOnlyList<ByteRange> ranges = packet.FileRanges(0, length);
        if (packet.IsOpaque || ranges.Count != 1 || ranges[0].Length != length)
        {
            Problem ??= "its bytes are not bytes of the file in one piece (a compressed or stripped block)";
            Skipped++;
            return false;
        }

        long start = ranges[0].Offset;
        if (start < Last)
        {
            Problem ??= "a packet lies before the one it follows in the stream";
            Skipped++;
            return false;
        }

        First = First < 0 ? start : First;
        if (Stretches.Count > 0 && Stretches[^1].End == start)
        {
            Stretches[^1] = Stretches[^1] with { Length = Stretches[^1].Length + length };
        }
        else
        {
            Stretches.Add(new ByteRange(start, length));
        }

        Last = start + length;
        return true;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = (int)Math.Max(0, Math.Min(buffer.Length, Last - _position));
        buffer = buffer[..total];
        buffer.Clear();
        int low = 0;
        int high = Stretches.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            if (Stretches[middle].End <= _position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        // The container's reader is shared with the container, whose reads set the position they need; it is put back all the same.
        long saved = source.Position;
        try
        {
            for (int i = low; i < Stretches.Count && Stretches[i].Offset < _position + total; i++)
            {
                long from = Math.Max(Stretches[i].Offset, _position);
                long to = Math.Min(Stretches[i].End, _position + total);
                Fill(buffer.Slice((int)(from - _position), (int)(to - from)), from);
            }
        }
        finally
        {
            source.Position = saved;
        }

        _position += total;
        return total;
    }

    // The file's bytes from offset, with the pairs swapped back where the packets are words: a pair is swapped within its two bytes, so an odd extent reads the byte after it.
    private void Fill(Span<byte> destination, long offset)
    {
        if (!isWordSwapped)
        {
            source.Position = offset;
            source.Read(destination);
            return;
        }

        long first = offset & ~1L;
        long last = Math.Min((offset + destination.Length + 1) & ~1L, source.End);
        byte[] raw = new byte[last - first];
        source.Position = first;
        source.Read(raw);
        for (int i = 0; i < destination.Length; i++)
        {
            long index = ((offset + i) ^ 1) - first;
            destination[i] = index < raw.Length ? raw[index] : (byte)0;
        }
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        _position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => Last + offset };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
