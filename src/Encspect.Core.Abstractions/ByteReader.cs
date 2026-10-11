using System.Buffers.Binary;

namespace Encspect.Core;

// Every read checks the cancellation token, so a parser that reads through the reader stops when
// the analysis is canceled without checking it itself; CPU-only loops check
// ParseContext.CancellationToken. Out-of-range positions and extents throw EndOfStreamException,
// which the host reports as truncation.
public sealed class ByteReader
{
    // Progress is reported each time the furthest byte read moves on by this much; the host
    // reports the file length when the analysis ends.
    private const long ProgressStep = 1 << 18;

    private readonly Stream _stream;
    private readonly ReadMonitor _monitor;

    public ByteReader(Stream stream, CancellationToken cancellationToken = default, IProgress<long>? bytesRead = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }

        _stream = stream;
        _monitor = new ReadMonitor(cancellationToken, bytesRead);
        Start = 0;
        End = stream.Length;
        _stream.Position = 0;
    }

    private ByteReader(Stream stream, ReadMonitor monitor, long start, long end)
    {
        _stream = stream;
        _monitor = monitor;
        Start = start;
        End = end;
        _stream.Position = start;
    }

    // Positions are absolute file offsets, also in a slice, so nodes always point at file bytes.
    public long Start { get; }

    public long End { get; }

    public long Length => End - Start;

    // Shared by the reader and its slices.
    public CancellationToken CancellationToken => _monitor.CancellationToken;

    public long Position
    {
        get => _stream.Position;
        set
        {
            // A corrupt offset field must surface as truncation, which hosts turn into a finding.
            if (value < Start || value > End)
            {
                throw new EndOfStreamException();
            }

            _stream.Position = value;
        }
    }

    public long Remaining => Math.Max(0, End - Position);

    // Returns a reader limited to [start, start + length) that shares this reader's stream,
    // cancellation token and progress. An extent outside this reader is truncation, as a corrupt
    // size field makes it.
    public ByteReader Slice(long start, long length)
    {
        if (start < Start || start > End || length < 0 || length > End - start)
        {
            throw new EndOfStreamException();
        }

        return new ByteReader(_stream, _monitor, start, start + length);
    }

    // True when the bytes at offset equal expected. Out-of-range offsets simply do not match.
    public bool MatchesAt(long offset, ReadOnlySpan<byte> expected)
    {
        if (offset < Start || offset > End - expected.Length)
        {
            return false;
        }

        Position = offset;
        Span<byte> buffer = expected.Length <= 256 ? stackalloc byte[expected.Length] : new byte[expected.Length];
        Read(buffer);
        return buffer.SequenceEqual(expected);
    }

    public byte ReadByte()
    {
        _monitor.CancellationToken.ThrowIfCancellationRequested();
        if (Position >= End)
        {
            throw new EndOfStreamException();
        }

        int value = _stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException();
        }

        _monitor.Advance(_stream);
        return (byte)value;
    }

    // Fills buffer from the current position, without allocating.
    public void Read(Span<byte> buffer)
    {
        _monitor.CancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length > Remaining)
        {
            throw new EndOfStreamException();
        }

        _stream.ReadExactly(buffer);
        _monitor.Advance(_stream);
    }

    public byte[] ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        // A corrupt size field must not trigger a huge allocation, so check before allocating.
        if (count > Remaining)
        {
            throw new EndOfStreamException();
        }

        var buffer = new byte[count];
        Read(buffer);
        return buffer;
    }

    public byte[] ReadBytesAt(long offset, int count)
    {
        Position = offset;
        return ReadBytes(count);
    }

    public void Skip(long count)
    {
        _monitor.CancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > Remaining)
        {
            throw new EndOfStreamException();
        }

        _stream.Position += count;
    }

    public ushort ReadUInt16BigEndian()
    {
        Span<byte> buffer = stackalloc byte[2];
        Read(buffer);
        return BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }

    public ushort ReadUInt16LittleEndian()
    {
        Span<byte> buffer = stackalloc byte[2];
        Read(buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    public uint ReadUInt24BigEndian()
    {
        Span<byte> buffer = stackalloc byte[3];
        Read(buffer);
        return ((uint)buffer[0] << 16) | ((uint)buffer[1] << 8) | buffer[2];
    }

    public uint ReadUInt32BigEndian()
    {
        Span<byte> buffer = stackalloc byte[4];
        Read(buffer);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    public uint ReadUInt32LittleEndian()
    {
        Span<byte> buffer = stackalloc byte[4];
        Read(buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    public ulong ReadUInt64BigEndian()
    {
        Span<byte> buffer = stackalloc byte[8];
        Read(buffer);
        return BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }

    public ulong ReadUInt64LittleEndian()
    {
        Span<byte> buffer = stackalloc byte[8];
        Read(buffer);
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    // Progress is the furthest file offset read so far, so it only grows, however often parsers
    // jump back; it is reported in steps to keep the cost per read small.
    private sealed class ReadMonitor(CancellationToken cancellationToken, IProgress<long>? progress)
    {
        private long _furthest;
        private long _reported;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public void Advance(Stream stream)
        {
            if (progress is null)
            {
                return;
            }

            long position = stream.Position;
            if (position <= _furthest)
            {
                return;
            }

            _furthest = position;
            if (position - _reported >= ProgressStep)
            {
                _reported = position;
                progress.Report(position);
            }
        }
    }
}
