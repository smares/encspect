using Encspect.Core;

namespace Encspect.Tags.Id3;

// Bytes as a frame parser sees them after unsynchronization was undone, together with the
// file offset each byte came from, so nodes built from decoded data still point at the file.
internal sealed class DecodedBytes
{
    private readonly long _start;
    private readonly long _end;
    private readonly long[]? _map;
    private readonly bool _opaque;

    public DecodedBytes(byte[] data, long start, long end, long[]? map = null)
        : this(data, start, end, map, opaque: false)
    {
    }

    private DecodedBytes(byte[] data, long start, long end, long[]? map, bool opaque)
    {
        Data = data;
        _start = start;
        _end = end;
        _map = map;
        _opaque = opaque;
    }

    public byte[] Data { get; }

    public int Length => Data.Length;

    // Decompressed data has no byte-for-byte source in the file, so every range in it maps to
    // the whole compressed region [start, end).
    public static DecodedBytes Opaque(byte[] data, long start, long end) => new(data, start, end, null, opaque: true);

    // File offset of the decoded byte at index; Length maps to the end of the source range.
    public long OffsetOf(int index)
    {
        if (_opaque)
        {
            return _start;
        }

        if (index >= Data.Length)
        {
            return _end;
        }

        return _map is null ? _start + index : _map[index];
    }

    public long FileLength(int start, int end) => _opaque ? _end - _start : OffsetOf(end) - OffsetOf(start);

    public DecodedBytes Slice(int start)
    {
        byte[] data = Data[start..];
        if (_opaque)
        {
            return Opaque(data, _start, _end);
        }

        if (_map is null)
        {
            return new DecodedBytes(data, _start + start, _end);
        }

        return new DecodedBytes(data, OffsetOf(start), _end, _map[start..]);
    }

    // ID3v2.4 section 6.1: unsynchronization inserts $00 after every $FF that would otherwise
    // form a false sync, so decoding drops each $00 that directly follows $FF. falseSync is the
    // file offset of the first $FF that is still followed by %111xxxxx, which unsynchronized
    // data cannot hold.
    public DecodedBytes Resynchronize(out long? falseSync)
    {
        var data = new List<byte>(Data.Length);
        var map = new List<long>(Data.Length);
        bool previousWasFF = false;
        falseSync = null;

        for (int i = 0; i < Data.Length; i++)
        {
            byte b = Data[i];
            if (previousWasFF && b == 0x00)
            {
                previousWasFF = false;
                continue;
            }

            if (previousWasFF && b >= 0xE0)
            {
                falseSync ??= map[^1];
            }

            data.Add(b);
            map.Add(OffsetOf(i));
            previousWasFF = b == 0xFF;
        }

        return new DecodedBytes([.. data], OffsetOf(0), _end, [.. map]);
    }

    // The decoded length of the unsynchronized file bytes [start, end), and the offset of the
    // first false sync in them, read in chunks so a large frame is never held whole.
    // previousWasFF says whether the byte before start was $FF.
    public static (long Length, long? FalseSync) MeasureResynchronized(ByteReader reader, long start, long end, bool previousWasFF)
    {
        long length = 0;
        long? falseSync = null;
        for (long at = start; at < end;)
        {
            byte[] chunk = reader.ReadBytesAt(at, (int)Math.Min(end - at, 1 << 16));
            for (int i = 0; i < chunk.Length; i++)
            {
                byte b = chunk[i];
                if (previousWasFF && b == 0x00)
                {
                    previousWasFF = false;
                    continue;
                }

                if (previousWasFF && b >= 0xE0)
                {
                    falseSync ??= at + i - 1;
                }

                length++;
                previousWasFF = b == 0xFF;
            }

            at += chunk.Length;
        }

        return (length, falseSync);
    }
}
