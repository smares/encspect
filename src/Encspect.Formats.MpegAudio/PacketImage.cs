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

    // The ranges of the image that come from packets failing the container's checksum (MappedBytes.FailsChecksum), those that touch merged.
    public List<ByteRange> Failing { get; } = [];

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

    // Where the image's bytes lie in the file.
    public ImageMap Map { get; } = new();

    // The file offset where the last packet's last piece ends, which the next packet's pieces must not precede.
    private long _lastFile;

    // The parity of the file offset where the first packet begins: the 16-bit words of an IEC 61937 payload are aligned to the payload (the data chunk's start, which may
    // be at an odd file offset), and every payload of a chain shares that alignment.
    private long _phase;

    // Adds a packet; false (with Problem set) when it cannot be placed: its bytes are not bytes of the file (a compressed block, or one with bytes that are put back from nowhere), or
    // it lies before the one it follows. A packet of one piece is at its file offset in the image. A packet of several is laid out in one run, its pieces one after another and the
    // last at its file offset; pieces that are stripped bytes put back are read again from the file where they are stored, the same ones for every packet.
    public bool Add(MappedBytes packet)
    {
        int length = packet.Length;
        if (length == 0)
        {
            return true;
        }

        IReadOnlyList<ByteRange> pieces = packet.FileRanges(0, length);
        if (packet.IsOpaque || pieces.Count == 0 || pieces.Sum(r => r.Length) != length)
        {
            Problem ??= "its bytes are not bytes of the file (a compressed or stripped block)";
            Skipped++;
            return false;
        }

        // The last piece is where the packet is in the file: the image puts it at its file offset, and the pieces before it (the earlier fragments of a media object, or the stripped
        // bytes put back, which are stored apart) in front of it, so that a packet of one piece is where it is in the file and a message that quotes a position is near the file's.
        int anchor = pieces.Count - 1;
        long before = length - pieces[anchor].Length;
        if (pieces[anchor].Offset < _lastFile)
        {
            Problem ??= "a packet lies before the one it follows in the stream";
            Skipped++;
            return false;
        }

        long start = Math.Max(Last, pieces[anchor].Offset - before);
        bool continues = Stretches.Count > 0 && Stretches[^1].End == start && _lastFile == pieces[0].Offset;
        if (First < 0)
        {
            _phase = pieces[0].Offset & 1;
        }

        First = First < 0 ? start : First;
        long at = start;
        foreach (ByteRange piece in pieces)
        {
            Map.Add(at, piece.Length, piece.Offset);
            at += piece.Length;
        }

        if (continues)
        {
            Stretches[^1] = Stretches[^1] with { Length = Stretches[^1].Length + length };
        }
        else
        {
            Stretches.Add(new ByteRange(start, length));
        }

        if (packet.FailsChecksum)
        {
            if (Failing.Count > 0 && Failing[^1].Offset + Failing[^1].Length == start)
            {
                Failing[^1] = Failing[^1] with { Length = Failing[^1].Length + length };
            }
            else
            {
                Failing.Add(new ByteRange(start, length));
            }
        }

        Last = start + length;
        _lastFile = pieces[^1].End;
        return true;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = (int)Math.Max(0, Math.Min(buffer.Length, Last - _position));
        buffer = buffer[..total];
        buffer.Clear();
        IReadOnlyList<ImageMap.Segment> segments = Map.Segments;

        // The container's reader is shared with the container, whose reads set the position they need; it is put back all the same.
        long saved = source.Position;
        try
        {
            for (int i = Map.FirstEndingAfter(_position); i < segments.Count && segments[i].Virtual < _position + total; i++)
            {
                long from = Math.Max(segments[i].Virtual, _position);
                long to = Math.Min(segments[i].VirtualEnd, _position + total);
                Fill(buffer.Slice((int)(from - _position), (int)(to - from)), segments[i].File + (from - segments[i].Virtual));
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

        // Pairs are (_phase + 2k, _phase + 2k + 1); a byte whose partner lies outside the file reads as zero.
        long first = Math.Max(0, ((offset - _phase) & ~1L) + _phase);
        long last = Math.Min(((offset + destination.Length + 1 - _phase) & ~1L) + _phase, source.End);
        byte[] raw = new byte[Math.Max(0, last - first)];
        source.Position = first;
        source.Read(raw);
        for (int i = 0; i < destination.Length; i++)
        {
            long index = (((offset + i - _phase) ^ 1) + _phase) - first;
            destination[i] = index >= 0 && index < raw.Length ? raw[index] : (byte)0;
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
