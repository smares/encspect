namespace Encspect.Tags.Id3;

// Bytes as a frame parser sees them after unsynchronization was undone, together with the
// file offset each byte came from, so nodes built from decoded data still point at the file.
internal sealed class DecodedBytes
{
    private readonly long _start;
    private readonly long _end;
    private readonly long[]? _map;

    public DecodedBytes(byte[] data, long start, long end, long[]? map = null)
    {
        Data = data;
        _start = start;
        _end = end;
        _map = map;
    }

    public byte[] Data { get; }

    public int Length => Data.Length;

    // File offset of the decoded byte at index; Length maps to the end of the source range.
    public long OffsetOf(int index)
    {
        if (index >= Data.Length)
        {
            return _end;
        }

        return _map is null ? _start + index : _map[index];
    }

    public long FileLength(int start, int end) => OffsetOf(end) - OffsetOf(start);

    public DecodedBytes Slice(int start)
    {
        byte[] data = Data[start..];
        if (_map is null)
        {
            return new DecodedBytes(data, _start + start, _end);
        }

        return new DecodedBytes(data, OffsetOf(start), _end, _map[start..]);
    }

    // ID3v2.4 section 6.1: unsynchronization inserts $00 after every $FF that would otherwise
    // form a false sync, so decoding drops each $00 that directly follows $FF.
    public DecodedBytes Resynchronize()
    {
        var data = new List<byte>(Data.Length);
        var map = new List<long>(Data.Length);
        bool previousWasFF = false;

        for (int i = 0; i < Data.Length; i++)
        {
            byte b = Data[i];
            if (previousWasFF && b == 0x00)
            {
                previousWasFF = false;
                continue;
            }

            data.Add(b);
            map.Add(OffsetOf(i));
            previousWasFF = b == 0xFF;
        }

        return new DecodedBytes([.. data], OffsetOf(0), _end, [.. map]);
    }
}
