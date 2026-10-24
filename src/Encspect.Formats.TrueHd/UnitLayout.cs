using System.Buffers.Binary;

namespace Encspect.Formats.TrueHd;

[Flags]
internal enum UnitProblems
{
    None = 0,

    // The length the header states is below 4 bytes.
    Length = 1,

    // The unit has a major sync that is not usable (its CRC fails, or a field is out of range).
    Sync = 2,

    // The parity of the unit header and the directory does not come out right.
    Parity = 4,

    // The directory runs past the unit, or past the bytes the data holds.
    DirectoryTooLong = 8,

    // An MLP directory entry states an extra word.
    ExtraWordInMlp = 16,

    // The non-restart bit is not 0 in a unit with a major sync and 1 in one without.
    NonRestartBit = 32,

    // A substream ends before the one before it.
    EndDecreases = 64,

    // A substream ends past the unit.
    EndPastUnit = 128,
}

// One entry of the substream directory.
internal struct DirectoryEntry
{
    public bool HasExtraWord { get; set; }

    public bool IsNonRestart { get; set; }

    public bool HasCheckData { get; set; }

    public bool Reserved { get; set; }

    // Where the substream ends, in words from the end of the directory.
    public int End { get; set; }

    public int ExtraWord { get; set; }

    // The substream's bytes in the unit, clamped to the unit.
    public int Start { get; set; }

    public int Stop { get; set; }

    // The bytes the unit has for the substream before clamping; past the unit when End is.
    public bool IsPastUnit { get; set; }
}

// The framing of one access unit: its header, the major sync where it has one and the substream directory (docs/design/truehd-format.md,
// "Access unit"). One instance is read again and again, so it keeps no references to a unit's bytes.
internal sealed class UnitLayout
{
    public const int MaxSubstreams = 4;

    public int Length { get; private set; }

    public int CheckNibble { get; private set; }

    public int InputTiming { get; private set; }

    public bool HasSync { get; private set; }

    public MajorSync? Sync { get; private set; }

    public int SubstreamCount { get; private set; }

    public bool IsMlp { get; private set; }

    public int DirectoryStart { get; private set; }

    public int DirectoryLength { get; private set; }

    // Where the substreams begin: the end of the directory.
    public int DataStart => DirectoryStart + DirectoryLength;

    public bool IsDirectoryRead { get; private set; }

    public UnitProblems Problems { get; private set; }

    public DirectoryEntry[] Entries { get; } = new DirectoryEntry[MaxSubstreams];

    // Bytes after the last substream's end that no substream holds.
    public int TrailingBytes { get; private set; }

    // Reads the framing of the unit that starts at the first byte of data, which holds at least its header and may end before the unit does.
    // knownCount: the substream count of the last usable major sync, or 0 before there was one; isMlp: the stream type of that sync.
    public void Read(ReadOnlySpan<byte> data, int knownCount, bool isMlp)
    {
        Problems = UnitProblems.None;
        HasSync = false;
        Sync = null;
        SubstreamCount = 0;
        IsDirectoryRead = false;
        DirectoryStart = 4;
        DirectoryLength = 0;
        TrailingBytes = 0;
        Array.Clear(Entries);
        if (data.Length < 4)
        {
            Length = 0;
            Problems = UnitProblems.Length;
            return;
        }

        CheckNibble = data[0] >> 4;
        Length = ((data[0] & 0xF) << 8 | data[1]) * 2;
        InputTiming = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (Length < 4)
        {
            Problems |= UnitProblems.Length;
            return;
        }

        ReadOnlySpan<byte> unit = data[..Math.Min(data.Length, Length)];
        int count = knownCount;
        IsMlp = isMlp;
        if (MajorSync.StartsAt(unit[4..]))
        {
            HasSync = true;
            Sync = MajorSync.Read(unit[4..]);
            if (Sync is not { } sync || !sync.IsUsable)
            {
                Problems |= UnitProblems.Sync;
                if (Sync is not { Problem: SyncProblem.None })
                {
                    return;
                }

                // A sync whose CRC fails but whose fields are in range: the directory is read with its substream count, which the CRC
                // does not vouch for, so that the parity can still be told.
            }

            DirectoryStart = 4 + Sync!.Value.Length;
            count = Sync.Value.SubstreamCount;
            IsMlp = !Sync.Value.IsTrueHd;
        }

        if (count == 0)
        {
            return;
        }

        SubstreamCount = count;
        int at = DirectoryStart;
        for (int i = 0; i < count; i++)
        {
            if (at + 2 > unit.Length)
            {
                Problems |= UnitProblems.DirectoryTooLong;
                return;
            }

            int word = BinaryPrimitives.ReadUInt16BigEndian(unit[at..]);
            ref DirectoryEntry entry = ref Entries[i];
            entry.HasExtraWord = (word & 0x8000) != 0;
            entry.IsNonRestart = (word & 0x4000) != 0;
            entry.HasCheckData = (word & 0x2000) != 0;
            entry.Reserved = (word & 0x1000) != 0;
            entry.End = word & 0xFFF;
            at += 2;
            if (entry.HasExtraWord)
            {
                if (IsMlp)
                {
                    Problems |= UnitProblems.ExtraWordInMlp;
                }

                if (at + 2 > unit.Length)
                {
                    Problems |= UnitProblems.DirectoryTooLong;
                    return;
                }

                entry.ExtraWord = BinaryPrimitives.ReadUInt16BigEndian(unit[at..]);
                at += 2;
            }
        }

        DirectoryLength = at - DirectoryStart;
        IsDirectoryRead = true;
        if (!Checks.ParityHolds(unit[..4], unit[DirectoryStart..at]))
        {
            Problems |= UnitProblems.Parity;
        }

        int previous = 0;
        for (int i = 0; i < count; i++)
        {
            ref DirectoryEntry entry = ref Entries[i];
            if (entry.IsNonRestart == HasSync)
            {
                Problems |= UnitProblems.NonRestartBit;
            }

            if (entry.End < previous)
            {
                Problems |= UnitProblems.EndDecreases;
            }

            int start = DataStart + (2 * previous);
            int stop = DataStart + (2 * Math.Max(entry.End, previous));
            entry.IsPastUnit = stop > Length;
            if (entry.IsPastUnit)
            {
                Problems |= UnitProblems.EndPastUnit;
            }

            entry.Start = Math.Min(start, Length);
            entry.Stop = Math.Min(stop, Length);
            previous = Math.Max(entry.End, previous);
        }

        TrailingBytes = Math.Max(0, Length - (DataStart + (2 * previous)));
    }

    // Whether the unit's own bytes are all in data (a unit the data cuts short has fewer).
    public bool IsComplete(int available) => Length >= 4 && available >= Length;
}
