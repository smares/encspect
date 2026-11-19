using Encspect.Core;

namespace Encspect.Formats.WavPack;

// The correction file's blocks in step with the main file's, as libwavpack's read_wvc_block takes them one main block after the other (open_utils.c): the
// next block of the correction file with audio is compared with the main block (Pairing.Compare), one that comes before it is skipped, one that comes after
// it leaves the main block without a correction block and stays for the next, and a block that matches is used if its checksum holds and it is whole
// (WavpackVerifySingleBlock; the block's status says what the walk found). The bytes are read from the correction file as they are asked for.
internal sealed class CorrectionCursor(State other)
{
    private const BlockStatus Unusable = BlockStatus.Truncated | BlockStatus.Oversized | BlockStatus.ChecksumInvalid | BlockStatus.ChecksumMissing | BlockStatus.Malformed | BlockStatus.Refused;

    private int _next;
    private byte[] _buffer = [];

    // The correction block of the main block with this header as bytes of the correction file, valid until the next call, or null where it has none.
    public MappedBytes? Next(in BlockHeader main)
    {
        while (true)
        {
            int index = Pairing.NextCorrection(other, _next);
            if (index < 0)
            {
                return null;
            }

            Entry entry = other.Entries[index];
            int result = Pairing.Compare(main, entry.Header);
            if (result == -1)
            {
                return null;
            }

            _next = index + 1;
            if (result == 0)
            {
                if ((entry.Status & Unusable) != 0)
                {
                    return null;
                }

                int length = (int)entry.Length;
                if (_buffer.Length < length)
                {
                    _buffer = new byte[length];
                }

                other.Source.Cache.Read(entry.Offset, _buffer.AsSpan(0, length));
                return new MappedBytes(_buffer.AsMemory(0, length), entry.Offset);
            }
        }
    }
}
