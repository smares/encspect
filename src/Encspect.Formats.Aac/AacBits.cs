using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Encspect.Formats.Aac;

// Reads a raw data block most significant bit first, up to a limit in bits; a read past the limit
// throws BlockOverrun, which the block reader catches and reports. Peek fills with zero bits past
// the data, so a Huffman lookup near the end reads safely and the length check decides.
//
// Over a skeleton (UnitStore) the bits are those of the block with its cut ranges removed, such as
// the Huffman-coded spectral data: Original counts the block's own bits, so nodes built from a
// skeleton lie at the bits they came from, and TakeCut steps over the next cut range.
internal ref struct AacBits
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly ReadOnlySpan<byte> _cuts;
    private int _cut;

    public AacBits(ReadOnlySpan<byte> data, int start, int limit)
    {
        _data = data;
        Position = start;
        Limit = limit;
        _cuts = default;
        IsSkeleton = false;
    }

    public AacBits(ReadOnlySpan<byte> skeleton, int limit, ReadOnlySpan<byte> cuts, int origin)
    {
        _data = skeleton;
        Position = 0;
        Limit = limit;
        _cuts = cuts;
        Skipped = origin;
        IsSkeleton = true;
    }

    public int Position { get; private set; }

    public int Limit { get; private set; }

    // Bits stepped over in cut ranges, and before the skeleton's first bit.
    public int Skipped { get; private set; }

    public bool IsSkeleton { get; }

    // The position in the block's own bits.
    public readonly int Original => Position + Skipped;

    public readonly int OriginalLimit => Limit + Skipped;

    public readonly int Remaining => Limit - Position;

    public uint Read(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        if (Position + count > Limit)
        {
            throw BlockOverrun.Instance;
        }

        uint value = (uint)(Peek64() >> (64 - count));
        Position += count;
        return value;
    }

    public bool ReadBit() => Read(1) != 0;

    // Moves to a position in the block's own bits that no cut lies before since the last read.
    public void MoveTo(int original)
    {
        int position = original - Skipped;
        if (position < 0 || position > Limit)
        {
            throw BlockOverrun.Instance;
        }

        Position = position;
    }

    // The byte that starts at a position in the block's own bits, zero past the data, without
    // moving on; the position must lie after the last cut taken.
    public readonly int ByteAt(int original)
    {
        int position = original - Skipped;
        int value = 0;
        for (int bit = position; bit < position + 8; bit++)
        {
            int index = bit >> 3;
            value = (value << 1) | (index >= 0 && index < _data.Length ? (_data[index] >> (7 - (bit & 7))) & 1 : 0);
        }

        return value;
    }

    // The next count bits (at most 32), zero past the data, without moving on.
    public readonly uint Peek(int count) => (uint)(Peek64() >> (64 - count));

    // Moves on without checking the limit, for a run of short reads that Peek already makes safe;
    // Check must follow before anything relies on the position.
    public void Advance(int count) => Position += count;

    public readonly void Check()
    {
        if (Position > Limit)
        {
            throw BlockOverrun.Instance;
        }
    }

    public void Skip(int count)
    {
        if (count < 0 || Position + (long)count > Limit)
        {
            throw BlockOverrun.Instance;
        }

        Position += count;
    }

    // Moves on to the next byte boundary counted from anchor, a position in the block's own bits.
    public void Align(int anchor)
    {
        int misaligned = (Original - anchor) & 7;
        if (misaligned != 0)
        {
            Skip(8 - misaligned);
        }
    }

    // Confines reading to the block's own bits before end, as for a fill element's payload, so a
    // payload that claims more throws here, as it does in a skeleton of the block; returns the limit
    // that Restore puts back. A cut taken after it leaves the limit as far from the cut's end as end
    // was, so payloads take their cut last.
    public int Narrow(int end)
    {
        int saved = Limit;
        Limit = Math.Clamp(end - Skipped, Position, Limit);
        Bound = end;
        return saved;
    }

    public void Restore(int limit)
    {
        Limit = limit;
        Bound = int.MaxValue;
    }

    // The end that Narrow set, in the block's own bits, or int.MaxValue: a skeleton's limit cannot
    // tell whether a payload that would be cut fits before it.
    public int Bound { get; private set; } = int.MaxValue;

    // The next cut range of a skeleton: its kind, its length in the block's own bits and the extra
    // value stored with it.
    public (CutKind Kind, int Length, int Extra) TakeCut()
    {
        int at = _cut;
        var kind = (CutKind)_cuts[at++];
        int length = UnitStore.ReadVarint(_cuts, ref at);
        int extra = UnitStore.ReadVarint(_cuts, ref at) - 1;
        _cut = at;
        Skipped += length;
        return (kind, length, extra);
    }

    public readonly bool HasCut => _cut < _cuts.Length;

    // A reader of the same bits from original start to original end, which no cut lies inside.
    public readonly AacBits Window(int start, int end)
    {
        var window = new AacBits(_data, start - Skipped, end - Skipped);
        return window;
    }

    private readonly ulong Peek64()
    {
        int index = Position >> 3;
        ulong word;
        if ((uint)index + 8u <= (uint)_data.Length)
        {
            word = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_data), index)));
        }
        else
        {
            word = 0;
            for (int i = 0; i < 8; i++)
            {
                word = (word << 8) | (index + i < _data.Length ? _data[index + i] : 0u);
            }
        }

        // The bits after the first 64 - 7 are lost to the shift; callers read at most 32.
        return word << (Position & 7);
    }
}

// A read past the end of the data a raw data block may use. One instance, since it carries nothing
// but the fact; the block reader catches it and reports where it happened. It is a truncation, so
// one that escaped would still be reported as one.
internal sealed class BlockOverrun : EndOfStreamException
{
    public static readonly BlockOverrun Instance = new();

    private BlockOverrun()
        : base("A syntax element runs past the end of the data.")
    {
    }
}

internal enum CutKind : byte
{
    ScaleFactors = 1,
    Spectral = 2,
    Fill = 3,
    Sbr = 4,
    Gains = 5,
    Payload = 6,
}
