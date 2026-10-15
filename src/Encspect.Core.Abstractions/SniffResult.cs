namespace Encspect.Core;

// What a format parser's Sniff found: how sure it is, and the evidence behind that, so the host can
// rank formats that claim the same bytes without a score that hides where a chain starts or how
// well it holds up (plan, "Format detection").
//
// The host ranks by Class first, then the earlier Offset, then more VerifiedFrames, then more
// Frames. Whatever still ties is ambiguous: the host takes the first parser in its list and reports
// the other as a finding.
//
// Class is what the parser is sure of, from 0 (not this format) to 100 (certain), by the kind of
// structure found: a signature, a chain at the start of the data, a chain further in. The classes
// are the table in the plan; a parser puts a match in the class where its kind of evidence belongs
// and leaves where it starts and how far it holds to the other fields.
//
// Offset is the file offset where the matched structure starts (a signature, the first frame of
// the chain). Frames counts the consistent frames, pages, blocks or boxes found from there, at most
// MaxFrames, or the fewer the check needed; VerifiedFrames counts those of them that pass a
// checksum the format defines, at most Frames. Both are what the sniff has read anyway, never new
// parsing.
public readonly record struct SniffResult(int Class, long Offset = 0, int Frames = 0, int VerifiedFrames = 0, string? Note = null)
{
    // A sniff counts no further than this many frames.
    public const int MaxFrames = 16;

    // The class of a match its own parser distrusts, a chain that is probably another format's bytes
    // (MPEG audio's free-format chain that is not credible, or a protected chain none of whose CRCs
    // holds). It ranks below every other format's chain and is not an alternative to a better match.
    public const int WeakClass = 30;

    public static SniffResult None => default;

    public bool IsMatch => Class > 0;

    // Orders results from best to worst when sorted with this comparison.
    public static int Compare(SniffResult a, SniffResult b)
    {
        int result = b.Class.CompareTo(a.Class);
        if (result == 0)
        {
            result = a.Offset.CompareTo(b.Offset);
        }

        if (result == 0)
        {
            result = b.VerifiedFrames.CompareTo(a.VerifiedFrames);
        }

        return result != 0 ? result : b.Frames.CompareTo(a.Frames);
    }

    // The evidence in one line for a finding or a report: "chain of 3 frames at 0x160, 0 verified".
    public string Describe(string unit = "frames") =>
        $"class {Class}, starting at 0x{Offset:X}, {Frames} consistent {unit}, {VerifiedFrames} checksum-verified" + (Note is null ? string.Empty : $" ({Note})");
}
