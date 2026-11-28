namespace Encspect.Formats.Wma;

// The values a frame's walk stores for the decoder (docs/design/wma-standard-decoding.md names what the decoder needs): one int sequence for the frame, read back by
// Decoding.StdFrameDecoder in the same order. The layout:
//
//   count of blocks, then for each block
//     size index, previous size index, next size index, flags, total gain
//     where a channel is coded (flags bits 1 and 2), and the stream uses noise substitution: for each coded channel a mask of its flagged high bands, then for each
//       flagged band in band order its noise level (the first band's u(7) less 19, each later one the one before plus the difference)
//     where the exponents are sent (flags bit 3), for each coded channel: scale factor codes: the largest band value, then a value for each of the block's lines; line
//       spectral pairs: their 10 indexes
//     for each coded channel the block's lines as signed levels, 0 where nothing is coded
//
// flags: bit 0 mid/side, bit 1 first channel coded, bit 2 second channel coded, bit 3 exponents sent.
internal sealed class StdRecordWriter
{
    public const int MidSideFlag = 1;
    public const int CodedFlag = 2;
    public const int ExponentsSentFlag = 8;

    private int[] _data = new int[4096];

    public int Count { get; private set; }

    public ReadOnlySpan<int> Values => _data.AsSpan(0, Count);

    public void Clear() => Count = 0;

    public int Add(int value)
    {
        if (Count == _data.Length)
        {
            Array.Resize(ref _data, _data.Length * 2);
        }

        _data[Count] = value;
        return Count++;
    }

    // Appends count zeros and returns where they start.
    public int Reserve(int count)
    {
        while (Count + count > _data.Length)
        {
            Array.Resize(ref _data, _data.Length * 2);
        }

        Array.Clear(_data, Count, count);
        int at = Count;
        Count += count;
        return at;
    }

    public void Set(int index, int value) => _data[index] = value;

    public int Get(int index) => _data[index];
}
