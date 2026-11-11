using System.Buffers.Binary;

namespace Encspect.Formats.Vorbis;

// The bits of one audio packet, least significant bit first (Vorbis I specification, section 2.1.4). Reading past the end is an end of packet, which the
// specification and libvorbis treat as a state of the decoding, not a fault: Read gives -1 and the packet is at its end from then on (libogg's
// oggpack_read, which libvorbis 1.3.7 reads its packets with).
internal ref struct PacketBits
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly long _length;
    private long _position;

    public PacketBits(ReadOnlySpan<byte> data)
    {
        _data = data;
        _length = data.Length * 8L;
        _position = 0;
    }

    public readonly long Position => _position;

    public readonly long Remaining => _length - _position;

    // The next count bits, 0 to 32, without taking them; zeros stand for bits past the end.
    public readonly uint Peek(int count)
    {
        long bytePosition = _position >> 3;
        int shift = (int)(_position & 7);
        ulong word;
        if (bytePosition + 8 <= _data.Length)
        {
            word = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice((int)bytePosition));
        }
        else
        {
            word = 0;
            for (int i = 0; i < 8 && bytePosition + i < _data.Length; i++)
            {
                word |= (ulong)_data[(int)bytePosition + i] << (8 * i);
            }
        }

        return (uint)((word >> shift) & ((1UL << count) - 1));
    }

    public void Advance(int count) => _position += count;

    // Takes count bits (0 to 32), or ends the packet and gives -1 where fewer remain or count is out of range.
    public long Read(int count)
    {
        if ((uint)count > 32 || count > _length - _position)
        {
            _position = _length;
            return -1;
        }

        long value = Peek(count);
        _position += count;
        return value;
    }
}
