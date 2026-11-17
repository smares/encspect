using Encspect.Core;

namespace Encspect.Formats.MpegAudio;

// The packets a container hands over, laid out at their file offsets as a read-only stream, so that the parser reads them as it reads a file: the bytes between packets (a
// Matroska block's header, the chunks of another track in an MP4 file, the preamble and stuffing of an IEC 61937 burst) are not in the image and read as zeros, and the
// parser never goes there (BlockReader.Stretches). The packets are kept in memory, at most Limit bytes of them: the parser reads a stream back after its first pass
// (the bit reservoir, the ancillary data, the decoder), so a packet cannot be dropped once it is read.
internal sealed class PacketImage : Stream
{
    // The most bytes of packets that are kept; the packets after that are counted and not analyzed (mpeg-audio.packets-limit).
    public const long Limit = 256L << 20;

    private const int BlockSize = 1 << 20;

    private readonly List<byte[]> _blocks = [];
    private readonly List<(long Start, int Length, long Stored)> _packets = [];
    private long _stored;
    private long _position;

    // The stretches of the packets (those that touch are one), the first packet's offset and the end of the last.
    public List<ByteRange> Stretches { get; } = [];

    public long First { get; private set; } = -1;

    public long Last { get; private set; }

    // Whether packets were left out, and the first such reason.
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

    // Adds a packet; false (with Problem set) when it cannot be placed: its bytes are not file bytes in one piece, it lies before the one before it, or the limit is reached.
    public bool Add(MappedBytes packet)
    {
        ReadOnlyMemory<byte> data = packet.Data;
        if (data.Length == 0)
        {
            return true;
        }

        IReadOnlyList<ByteRange> ranges = packet.FileRanges(0, data.Length);
        if (packet.IsOpaque || ranges.Count != 1 || ranges[0].Length != data.Length)
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

        if (_stored + data.Length > Limit)
        {
            Problem ??= $"the packets are more than the {Limit >> 20} MiB the analyzer keeps in memory";
            Skipped++;
            return false;
        }

        int at = (int)(_stored % BlockSize);
        if (at == 0)
        {
            _blocks.Add(new byte[BlockSize]);
        }

        int done = 0;
        while (done < data.Length)
        {
            at = (int)((_stored + done) % BlockSize);
            if (at == 0 && _blocks.Count * (long)BlockSize <= _stored + done)
            {
                _blocks.Add(new byte[BlockSize]);
            }

            int count = Math.Min(data.Length - done, BlockSize - at);
            data.Span.Slice(done, count).CopyTo(_blocks[(int)((_stored + done) / BlockSize)].AsSpan(at));
            done += count;
        }

        _packets.Add((start, data.Length, _stored));
        _stored += data.Length;
        First = First < 0 ? start : First;
        if (Stretches.Count > 0 && Stretches[^1].End == start)
        {
            Stretches[^1] = Stretches[^1] with { Length = Stretches[^1].Length + data.Length };
        }
        else
        {
            Stretches.Add(new ByteRange(start, data.Length));
        }

        Last = start + data.Length;
        return true;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = (int)Math.Max(0, Math.Min(buffer.Length, Last - _position));
        buffer = buffer[..total];
        buffer.Clear();
        int low = 0;
        int high = _packets.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >> 1;
            if (_packets[middle].Start + _packets[middle].Length <= _position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        for (int i = low; i < _packets.Count && _packets[i].Start < _position + total; i++)
        {
            (long start, int length, long stored) = _packets[i];
            long from = Math.Max(start, _position);
            long to = Math.Min(start + length, _position + total);
            long source = stored + (from - start);
            long remaining = to - from;
            long target = from - _position;
            while (remaining > 0)
            {
                int at = (int)(source % BlockSize);
                int count = (int)Math.Min(remaining, BlockSize - at);
                _blocks[(int)(source / BlockSize)].AsSpan(at, count).CopyTo(buffer[(int)target..]);
                source += count;
                target += count;
                remaining -= count;
            }
        }

        _position += total;
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin) =>
        _position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, _ => Last + offset };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
