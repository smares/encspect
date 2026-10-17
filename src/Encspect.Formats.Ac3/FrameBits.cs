using Encspect.Core;

namespace Encspect.Formats.Ac3;

// The bits of one syncframe, read in one of two ways by the same syntax code (FrameSyntax):
//
// - While the file is analyzed, from the frame's bytes. Every field the tree shows is also copied
//   into a compact record of the frame (Kept), which leaves out what makes a frame large: the
//   exponent groups, the skip fields, the mantissas and the auxiliary data. Values only the analysis
//   derives, such as how many bits an audio block's mantissas take, are written into the record too.
// - When the frame's nodes are built, long after the file is closed, from that record: the fields
//   come back with their positions in the frame, and what was left out is stepped over by the
//   lengths the record holds, so the nodes cover exactly the bits the analysis read.
//
// A field that runs past the end of the frame, or past the end of the record, throws
// EndOfStreamException, which the syntax catches: the frame's data ends there.
internal sealed class FrameBits
{
    private const int MaxKeptBytes = 4096;

    private byte[] _kept;
    private byte[] _source = [];
    private int _available;
    private int _limit;
    private int _keptBits;
    private int _keptLimit;

    private FrameBits(byte[] kept)
    {
        _kept = kept;
    }

    // While analyzing: the frame's bits, and the record they go into.
    public static FrameBits ForAnalysis() => new(new byte[MaxKeptBytes]);

    // While building nodes, from records as the analysis kept them (StartNodes).
    public static FrameBits ForNodes() => new([]) { IsBuildingNodes = true };

    public bool IsBuildingNodes { get; private init; }

    // The bit position in the frame, counted from its first bit.
    public int Position { get; private set; }

    // The bits of the frame the data holds.
    public int FrameBitCount => _available;

    // The bits fields may be read up to: the frame's, or less while LimitTo applies.
    public int Limit => _limit;

    // Where the last read or skip would have ended: past the data for the one that failed, so a field the file's end cuts
    // can be told from one that does not fit its frame.
    public int RequestedEnd { get; private set; }

    public int KeptBits => _keptBits;

    public int KeptBytes => (_keptBits + 7) >> 3;

    public ReadOnlySpan<byte> Kept => _kept.AsSpan(0, KeptBytes);

    // Starts a frame: bytes is the frame (shorter where the file ends inside it).
    public void Start(byte[] bytes, int length)
    {
        _source = bytes;
        _available = _limit = length * 8;
        Position = 0;
        RequestedEnd = 0;
        _keptBits = 0;
    }

    // Starts building the nodes of a frame of length bytes from its record of keptBits bits.
    public void StartNodes(byte[] record, int keptBits, int length)
    {
        _kept = record;
        _keptLimit = keptBits;
        _available = _limit = length * 8;
        Position = 0;
        _keptBits = 0;
    }

    // A field the tree shows.
    public int Read(int width)
    {
        if (IsBuildingNodes)
        {
            RequestedEnd = Position + width;
            if (Position + width > _limit)
            {
                throw new EndOfStreamException();
            }

            int kept = Take(_kept, _keptBits, width, _keptLimit);
            _keptBits += width;
            Position += width;
            return kept;
        }

        RequestedEnd = Position + width;
        int value = Take(_source, Position, width, _limit);
        Position += width;
        Keep(width, value);
        return value;
    }

    // A field the tree shows as part of a larger node, such as an exponent group: read while
    // analyzing, stepped over (as 0) while building nodes.
    public int ReadUnkept(int width)
    {
        if (IsBuildingNodes)
        {
            Skip(width);
            return 0;
        }

        RequestedEnd = Position + width;
        int value = Take(_source, Position, width, _limit);
        Position += width;
        return value;
    }

    // Bits the analysis neither reads nor keeps: skip fields, mantissas.
    public void Skip(long width)
    {
        RequestedEnd = (int)Math.Min(Position + width, int.MaxValue);
        if (width < 0 || Position + width > _limit)
        {
            throw new EndOfStreamException();
        }

        Position += (int)width;
    }

    // A value the analysis derives and the nodes need, such as the length of an audio block's
    // mantissas: written into the record while analyzing, read from it while building nodes.
    public int Derived(int width, int value)
    {
        if (IsBuildingNodes)
        {
            int kept = Take(_kept, _keptBits, width, _keptLimit);
            _keptBits += width;
            return kept;
        }

        Keep(width, value);
        return value;
    }

    // Ends reading at bit, until Unlimit: the audio blocks stop before the fields at the frame's end.
    public void LimitTo(int bit) => _limit = Math.Clamp(bit, 0, _available);

    public void Unlimit() => _limit = _available;

    // Moves to a bit of the frame without reading, such as the auxiliary data's fields at its end.
    public void MoveTo(int position)
    {
        if (position < 0 || position > _limit)
        {
            throw new EndOfStreamException();
        }

        Position = position;
    }

    // The bits of the frame from start, of any width up to 32, for checks the analysis makes; 0
    // while building nodes.
    public int Peek(int start, int width) => IsBuildingNodes ? 0 : Take(_source, start, width, _limit);

    // The first bit in [from, to) where a 16-bit pattern starts, or -1; while building nodes there
    // are no source bits, so -1. Every byte of the range is tested against the bytes that can lie
    // inside the pattern at some alignment, so only a rare byte is checked bit by bit.
    public int FindPattern16(int from, int to, int pattern, ReadOnlySpan<byte> candidates)
    {
        if (IsBuildingNodes)
        {
            return -1;
        }

        to = Math.Min(to, _limit);
        for (int j = from >> 3; j <= (to - 1) >> 3 && j < _source.Length; j++)
        {
            byte mask = candidates[_source[j]];
            for (int k = 0; mask != 0 && k < 8; k++, mask >>= 1)
            {
                int start = ((j - 1) * 8) + k;
                if ((mask & 1) != 0 && start >= from && start + 16 <= to && Take(_source, start, 16, _limit) == pattern)
                {
                    return start;
                }
            }
        }

        return -1;
    }

    private void Keep(int width, int value)
    {
        if (_keptBits + width > MaxKeptBytes * 8)
        {
            throw new EndOfStreamException();
        }

        for (int i = width - 1; i >= 0; i--)
        {
            if (((value >> i) & 1) != 0)
            {
                _kept[_keptBits >> 3] |= (byte)(0x80 >> (_keptBits & 7));
            }
            else
            {
                _kept[_keptBits >> 3] &= (byte)~(0x80 >> (_keptBits & 7));
            }

            _keptBits++;
        }
    }

    private static int Take(byte[] data, int start, int width, int limit)
    {
        if (width < 0 || width > 32 || start + width > limit || start + width > data.Length * 8)
        {
            throw new EndOfStreamException();
        }

        int value = 0;
        for (int bit = start; bit < start + width; bit++)
        {
            value = (value << 1) | ((data[bit >> 3] >> (7 - (bit & 7))) & 1);
        }

        return value;
    }
}
