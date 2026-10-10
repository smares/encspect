using System.Buffers.Binary;

namespace Encspect.Core;

public sealed class ByteReader
{
    private readonly Stream _stream;

    public ByteReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }

        _stream = stream;
        Start = 0;
        End = stream.Length;
        _stream.Position = 0;
    }

    private ByteReader(Stream stream, long start, long end)
    {
        _stream = stream;
        Start = start;
        End = end;
        _stream.Position = start;
    }

    // Positions are absolute file offsets, also in a slice, so nodes always point at file bytes.
    public long Start { get; }

    public long End { get; }

    public long Length => End - Start;

    public long Position
    {
        get => _stream.Position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, Start);

            // A corrupt offset field must surface as truncation, which hosts turn into a finding.
            if (value > End)
            {
                throw new EndOfStreamException();
            }

            _stream.Position = value;
        }
    }

    public long Remaining => Math.Max(0, End - Position);

    // Returns a reader limited to [start, start + length) that shares this reader's stream.
    public ByteReader Slice(long start, long length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(start, Start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + length, End);

        return new ByteReader(_stream, start, start + length);
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
        _stream.ReadExactly(buffer);
        return buffer.SequenceEqual(expected);
    }

    public byte ReadByte()
    {
        if (Position >= End)
        {
            throw new EndOfStreamException();
        }

        int value = _stream.ReadByte();
        if (value < 0)
        {
            throw new EndOfStreamException();
        }

        return (byte)value;
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
        _stream.ReadExactly(buffer);
        return buffer;
    }

    public byte[] ReadBytesAt(long offset, int count)
    {
        Position = offset;
        return ReadBytes(count);
    }

    public void Skip(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > Remaining)
        {
            throw new EndOfStreamException();
        }

        _stream.Position += count;
    }

    public ushort ReadUInt16BigEndian() => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2));

    public uint ReadUInt32BigEndian() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));

    public uint ReadUInt32LittleEndian() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));
}
