using Encspect.Core;

namespace Encspect.Tags.Id3;

// Reads an ID3v2 tag body sequentially. For 2.2 and 2.3 tags with the unsynchronization flag,
// unsynchronization covers the whole body (ID3v2.3 section 5), so it is undone here while
// streaming; sizes inside the body count decoded bytes.
internal sealed class Id3v2BodyReader
{
    private readonly ByteReader _reader;
    private readonly long _end;
    private readonly bool _unsync;
    private bool _previousWasFF;

    public Id3v2BodyReader(ByteReader reader, long start, long end, bool unsync)
    {
        _reader = reader;
        _end = end;
        _unsync = unsync;
        FilePosition = start;
    }

    public long FilePosition { get; private set; }

    public long End => _end;

    public ByteReader Reader => _reader;

    public bool AtEnd => FilePosition >= _end;

    // The file offset of the first $FF followed by %111xxxxx, a false sync that an
    // unsynchronized tag cannot hold (ID3v2.3 section 5).
    public long? FalseSync { get; private set; }

    // Reads up to count decoded bytes; fewer when the body ends first.
    public MappedBytes Read(int count)
    {
        long start = FilePosition;

        if (!_unsync)
        {
            int available = (int)Math.Min(count, _end - FilePosition);
            _reader.Position = FilePosition;
            byte[] raw = _reader.ReadBytes(available);
            FilePosition += available;
            return new MappedBytes(raw, start);
        }

        var data = new List<byte>(Math.Min(count, 4096));
        var ranges = new List<ByteRange>();
        _reader.Position = FilePosition;

        while (data.Count < count && FilePosition < _end)
        {
            byte b = _reader.ReadByte();
            long offset = FilePosition++;
            if (_previousWasFF && b == 0x00)
            {
                _previousWasFF = false;
                continue;
            }

            NoteFalseSync(b, offset);
            data.Add(b);
            Unsynchronization.Extend(ranges, offset);
            _previousWasFF = b == 0xFF;
        }

        AbsorbStuffing();
        return new MappedBytes(data.ToArray(), ranges, FilePosition);
    }

    // Skips up to count decoded bytes and returns how many were skipped.
    public long Skip(long count)
    {
        if (!_unsync)
        {
            long skipped = Math.Min(count, _end - FilePosition);
            FilePosition += skipped;
            return skipped;
        }

        long done = 0;
        _reader.Position = FilePosition;
        while (done < count && FilePosition < _end)
        {
            byte b = _reader.ReadByte();
            long offset = FilePosition++;
            if (_previousWasFF && b == 0x00)
            {
                _previousWasFF = false;
                continue;
            }

            NoteFalseSync(b, offset);
            done++;
            _previousWasFF = b == 0xFF;
        }

        AbsorbStuffing();
        return done;
    }

    // The $FF that set _previousWasFF is always the file byte directly before offset.
    private void NoteFalseSync(byte b, long offset)
    {
        if (_previousWasFF && b >= 0xE0)
        {
            FalseSync ??= offset - 1;
        }
    }

    // A $00 inserted after the last $FF of a structure belongs to that structure's bytes.
    private void AbsorbStuffing()
    {
        if (_previousWasFF && FilePosition < _end)
        {
            _reader.Position = FilePosition;
            if (_reader.ReadByte() == 0x00)
            {
                FilePosition++;
                _previousWasFF = false;
            }
        }
    }
}
