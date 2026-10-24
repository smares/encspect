namespace Encspect.Formats.TrueHd;

// Finds access units that carry a usable major sync, which is how a stream is found at the start of the data and again after damage.
internal static class Locator
{
    // How far into the data the first major sync is looked for.
    public const long SearchLimit = 1 << 20;

    // The offset of the first access unit in [from, limit) whose major sync checks out and whose unit holds it, or -1. The unit starts four bytes
    // before the sync word.
    public static long FindSyncUnit(Source source, long from, long limit, out MajorSync sync)
    {
        sync = default;
        long start = Math.Max(from, source.Start + 4);
        limit = Math.Min(limit, source.End);
        for (long position = source.IndexOf(0xF8, start, limit); position >= 0; position = source.IndexOf(0xF8, position + 1, limit))
        {
            ReadOnlySpan<byte> window = source.Window(position, 40);
            if (window.Length < MajorSync.MinLength || window[1] != 0x72 || window[2] != 0x6F || (window[3] & 0xFE) != 0xBA)
            {
                continue;
            }

            MajorSync? found = MajorSync.Read(window);
            if (found is not { IsUsable: true } usable)
            {
                continue;
            }

            ReadOnlySpan<byte> header = source.Window(position - 4, 4);
            int length = header.Length < 4 ? 0 : (((header[0] & 0xF) << 8) | header[1]) * 2;
            if (length < 4 + usable.Length + 2)
            {
                continue;
            }

            sync = usable;
            return position - 4;
        }

        return -1;
    }

    // Whether following the units' own length words from position lands exactly on target, in at most limit units.
    public static bool ChainReaches(Source source, long position, long target, int limit = 1 << 16)
    {
        for (int i = 0; i < limit && position < target; i++)
        {
            ReadOnlySpan<byte> header = source.Window(position, 2);
            if (header.Length < 2)
            {
                return false;
            }

            int length = (((header[0] & 0xF) << 8) | header[1]) * 2;
            if (length < 4)
            {
                return false;
            }

            position += length;
        }

        return position == target;
    }
}
