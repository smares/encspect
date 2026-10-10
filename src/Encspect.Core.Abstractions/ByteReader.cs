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
    }

    public long Length => _stream.Length;

    public long Position
    {
        get => _stream.Position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _stream.Position = value;
        }
    }

    public long Remaining => Math.Max(0, Length - Position);

    public byte ReadByte()
    {
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

    public ushort ReadUInt16BigEndian() => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2));

    public uint ReadUInt32BigEndian() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));

    public uint ReadUInt32LittleEndian() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));
}
