using Encspect.Core;

namespace Encspect.Gui.ViewModels;

// The first bytes of the selected node bit by bit, with the node's bits marked, so a field of a
// few bits in a header shows exactly which bits it takes.
public sealed record BitStrip(long Offset, byte[] Bytes, bool[] Marked, bool IsCut)
{
    public const int MaxBytes = 8;

    public static BitStrip? For(HexHighlight? highlight, HexDocument? document)
    {
        if (highlight is null || document is null)
        {
            return null;
        }

        ByteRange first = highlight.Ranges[0];
        int count = (int)Math.Min(MaxBytes, first.Length);
        byte[] bytes = new byte[count];
        count = document.Read(first.Offset, bytes);
        if (count == 0)
        {
            return null;
        }

        var marked = new bool[count * 8];
        for (int i = 0; i < count; i++)
        {
            if (highlight.BitsOf(first.Offset + i) is { } bits)
            {
                for (int bit = bits.Start; bit < bits.End; bit++)
                {
                    marked[(i * 8) + bit] = true;
                }
            }
        }

        bool isCut = highlight.Ranges.Count > 1 || first.Length > count;
        return new BitStrip(first.Offset, bytes[..count], marked, isCut);
    }
}
