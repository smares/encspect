namespace Encspect.Core;

// What a format parser's Sniff found: how sure it is, and the evidence behind that, so the host can
// rank formats that claim the same bytes without a score that hides where a chain starts or how
// well it holds up (plan, "Format detection").
//
// Two candidates are ranked by Class first. Candidates of the same class that do not overlap are
// ranked by the earlier Offset, then more VerifiedFrames, then more Frames. Candidates of the same
// class that overlap, one starting inside the other's counted extent (a stray chain of one format
// made of the bytes inside another's frames), are ranked by VerifiedFrames first: the chain whose
// checksums hold is the real one wherever it starts. Whatever still ties is ambiguous: the host
// takes the first parser in its list and reports the other as a finding.
//
// Class is what the parser is sure of, from 0 (not this format) to 100 (certain), by the kind of
// structure found: a signature, a chain at the start of the data, a chain further in. The classes
// are the table in the plan; a parser puts a match in the class where its kind of evidence belongs
// and leaves where it starts and how far it holds to the other fields.
//
// Offset is the file offset where the matched structure starts (a signature, the first frame of
// the chain), End the offset where the frames counted end (at most the end of the data), or 0 for
// a match with no extent beyond its start. Frames counts the consistent whole frames, pages,
// blocks or boxes found from there, at most MaxFrames, or the fewer the check needed;
// VerifiedFrames counts those of them that pass a checksum the format defines, at most Frames.
// All of it is what the sniff has read anyway, never new parsing.
public readonly record struct SniffResult(int Class, long Offset = 0, int Frames = 0, int VerifiedFrames = 0, string? Note = null, long End = 0)
{
    // A sniff counts no further than this many frames.
    public const int MaxFrames = 16;

    // The class of a match its own parser distrusts, a chain that is probably another format's bytes
    // (MPEG audio's free-format chain that is not credible, or a protected chain none of whose CRCs
    // holds). It ranks below every other format's chain and is not an alternative to a better match.
    public const int WeakClass = 30;

    public static SniffResult None => default;

    public bool IsMatch => Class > 0;

    // Where the matched structure ends: End, or its start for a match with no extent.
    public long Extent => Math.Max(End, Offset);

    // Whether either starts inside the frames the other counted.
    public bool Overlaps(SniffResult other) =>
        (other.Offset >= Offset && other.Offset < Extent) || (Offset >= other.Offset && Offset < other.Extent);

    // Orders two results from best to worst, negative when a ranks first: by class, then, for
    // candidates that overlap, the more verified frames, then as CompareByStart does. It is not
    // transitive across overlapping and separate candidates, so a list is sorted with
    // CompareByStart and the winner is taken with Best.
    public static int Compare(SniffResult a, SniffResult b)
    {
        int result = b.Class.CompareTo(a.Class);
        if (result == 0 && a.Overlaps(b))
        {
            result = b.VerifiedFrames.CompareTo(a.VerifiedFrames);
        }

        return result != 0 ? result : CompareByStart(a, b);
    }

    // Class, then the earlier start, then more verified frames, then more frames: a total order.
    public static int CompareByStart(SniffResult a, SniffResult b)
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

    // The index of the winner of a list sorted with CompareByStart: the first, unless a later one of
    // the same class overlaps the winner so far and Compare ranks it ahead.
    public static int Best(IReadOnlyList<SniffResult> sorted)
    {
        int best = 0;
        for (int i = 1; i < sorted.Count; i++)
        {
            if (Compare(sorted[i], sorted[best]) < 0)
            {
                best = i;
            }
        }

        return best;
    }

    // The evidence in one line for a finding or a report: "class 50, starting at 0x160, 1 consistent
    // frame, 0 checksum-verified".
    public string Describe() =>
        $"class {Class}, starting at 0x{Offset:X}, {Frames} consistent {(Frames == 1 ? "unit" : "units")}, {VerifiedFrames} checksum-verified" + (Note is null ? string.Empty : $", {Note}");
}
