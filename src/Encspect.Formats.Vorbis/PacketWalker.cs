using System.Runtime.CompilerServices;

namespace Encspect.Formats.Vorbis;

// The entropy pass over one audio packet (Vorbis I specification, sections 4.3, 6, 7 and 8): the floors of every channel, then the residues of every
// submap, read with the codebooks of the setup header as libvorbis 1.3.7 reads them (lib/mapping0.c mapping0_inverse, floor1.c floor1_inverse1, floor0.c
// floor0_inverse1 and res0.c _01inverse and res2_inverse, BSD-3-Clause, Copyright (c) 2002-2020 Xiph.Org Foundation; the notice is in THIRD-PARTY-NOTICES.md).
// The walk is integer-only. It stores the floor values and the entry numbers the codebooks gave in the layout below (the session's lent buffer, where there is one), and
// gives the figures the report needs: how far into the spectrum the residue partitions that were coded reach. The decoder adds the reconstruction: the curves, the vectors
// the entries stand for, the inverse coupling and the transform.
//
// Layout (LentLayout), in ints of the plane the session lends:
//   [Mode] the mode number; [Flags] bit 0 a long block, bit 1 the previous window flag, bit 2 the next window flag; [Used] the ints in all;
//   [ResidueStart] where the residue records begin; then, from [Floors], for each channel its floor:
//     0 for a floor that is unused (the packet ended, or the floor's amplitude was 0), or
//     1 and the floor 1 values after unwrapping (one per post, bit 15 set where the post is not used), or
//     2 and a floor 0 packet: the amplitude as coded, the book number, the count of entries and the entries;
//   then the residue records, each: the channel (or -1 for the interleaved vector of residue type 2), the submap, the offset of the partition in the vector,
//   the codebook, the number of entries and the entries.
internal static class LentLayout
{
    public const int Mode = 0;
    public const int Flags = 1;
    public const int Used = 2;
    public const int ResidueStart = 3;
    public const int Floors = 4;

    public const int FlagLong = 1;
    public const int FlagPreviousLong = 2;
    public const int FlagNextLong = 4;

    public const int FloorUnused = 0;
    public const int FloorOne = 1;
    public const int FloorZero = 2;

    // The ints of a residue record before its entries.
    public const int RecordHeader = 5;
}

// What the walk of a packet found.
internal readonly record struct WalkFacts(bool IsAudio, int Mode, bool IsLong, bool IsBadMode, int CodedEnd, int Used, bool IsCapped = false);

internal sealed class PacketWalker(SetupModel setup, int blockSize0, int blockSize1)
{
    private readonly int[][] _bounds = BuildBounds(setup, blockSize0, blockSize1);

    // The partition visits the walk has made since the parser last took the count (each stage over each partition of each channel, whether or not the partition reads
    // anything), so that a stream whose packets are cheap in bytes and dear in visits is canceled between packets (VorbisParser.Decoding.cs).
    public long Visits { get; set; }

    // The partition visits one packet may still make: 1,024 and 16 for each bit of the packet. A class word of one bit can name 65,535 partitions and eight passes visit each,
    // so the work a packet costs is bounded by what its bits pay for; a packet that outruns it is read as ended there (libvorbis would go on).
    private long _budget;
    private bool _isCapped;

    // The classifications of the partitions read so far, a row of the residue's decode map for each class word; kept between packets (a walker serves one stream on one thread).
    private int[][] _classRows = [];

    // The most ints a packet of this mode can store, for the buffer the session lends.
    public int MaxInts(int mode) => _bounds[mode][0];

    private static int[][] BuildBounds(SetupModel setup, int blockSize0, int blockSize1)
    {
        var bounds = new int[setup.ModeIsLong.Length][];
        for (int mode = 0; mode < bounds.Length; mode++)
        {
            int n = setup.ModeIsLong[mode] ? blockSize1 : blockSize0;
            MappingModel mapping = setup.Mappings[setup.ModeMapping[mode]];
            long total = LentLayout.Floors;
            for (int ch = 0; ch < setup.Channels; ch++)
            {
                int submap = mapping.ChannelMux[ch];
                int floor = mapping.FloorOfSubmap[submap];
                total += 1 + (setup.Floor1[floor] is { } one ? one.Posts : 3 + (setup.Floor0[floor]!.Order / Math.Max(1, MinDimensions(setup, setup.Floor0[floor]!.Books)) + 1));
            }

            for (int submap = 0; submap < mapping.Submaps; submap++)
            {
                ResidueModel residue = setup.Residues[mapping.ResidueOfSubmap[submap]];
                int channels = 0;
                for (int ch = 0; ch < setup.Channels; ch++)
                {
                    channels += mapping.ChannelMux[ch] == submap ? 1 : 0;
                }

                total += ResidueBound(setup, residue, n, channels);
            }

            bounds[mode] = [(int)Math.Min(total, int.MaxValue)];
        }

        return bounds;
    }

    private static int MinDimensions(SetupModel setup, int[] books)
    {
        int smallest = int.MaxValue;
        foreach (int book in books)
        {
            smallest = Math.Min(smallest, setup.Books[book].Dimensions);
        }

        return smallest;
    }

    private static long ResidueBound(SetupModel setup, ResidueModel residue, int n, int bundleChannels)
    {
        int max = residue.Type == 2 ? n * bundleChannels / 2 : n / 2;
        int end = Math.Min(residue.End, max);
        int span = end - residue.Begin;
        if (span <= 0 || bundleChannels == 0)
        {
            return 0;
        }

        long partitions = span / residue.Grouping;
        long total = 0;
        for (int stage = 0; stage < residue.Stages; stage++)
        {
            long per = 0;
            for (int cls = 0; cls < residue.Partitions; cls++)
            {
                if (stage < residue.PartBooks[cls].Length && residue.PartBooks[cls][stage] >= 0)
                {
                    int dimensions = Math.Max(1, setup.Books[residue.PartBooks[cls][stage]].Dimensions);
                    per = Math.Max(per, LentLayout.RecordHeader + ((residue.Grouping + dimensions - 1) / dimensions) + (residue.Type == 2 ? bundleChannels : 0));
                }
            }

            total += partitions * per * (residue.Type == 2 ? 1 : bundleChannels);
        }

        return total;
    }

    // Walks one packet. dest receives the layout above where it is not empty (it must hold MaxInts of the packet's mode); an empty dest only gives the facts.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public WalkFacts Walk(ReadOnlySpan<byte> packet, Span<int> dest)
    {
        var bits = new PacketBits(packet);
        if (packet.Length == 0 || bits.Read(1) != 0)
        {
            return default;
        }

        long mode = bits.Read(setup.ModeBits);
        if (mode < 0 || mode >= setup.ModeIsLong.Length)
        {
            return new WalkFacts(true, 0, false, true, 0, 0);
        }

        bool isLong = setup.ModeIsLong[mode];
        long previous = 0;
        long next = 0;
        if (isLong)
        {
            previous = bits.Read(1);
            next = bits.Read(1);
            if (next < 0)
            {
                return new WalkFacts(true, (int)mode, true, true, 0, 0);
            }
        }

        int n = isLong ? blockSize1 : blockSize0;
        MappingModel mapping = setup.Mappings[setup.ModeMapping[mode]];
        int channels = setup.Channels;
        bool stores = !dest.IsEmpty;
        _budget = 1024 + (16L * packet.Length * 8);
        _isCapped = false;
        int count = LentLayout.Floors;
        if (stores)
        {
            dest[LentLayout.Mode] = (int)mode;
            dest[LentLayout.Flags] = (isLong ? LentLayout.FlagLong : 0) | (previous == 1 ? LentLayout.FlagPreviousLong : 0) | (next == 1 ? LentLayout.FlagNextLong : 0);
        }

        Span<bool> nonzero = stackalloc bool[Math.Min(channels, 256)];
        Span<int> fit = stackalloc int[Floor1Model.MaxPosts + 2];
        for (int ch = 0; ch < channels; ch++)
        {
            int floor = mapping.FloorOfSubmap[mapping.ChannelMux[ch]];
            if (setup.Floor1[floor] is { } one)
            {
                bool used = ReadFloor1(ref bits, one, fit);
                nonzero[ch] = used;
                if (stores)
                {
                    dest[count] = used ? LentLayout.FloorOne : LentLayout.FloorUnused;
                    if (used)
                    {
                        fit[..one.Posts].CopyTo(dest.Slice(count + 1));
                    }
                }

                count += used ? 1 + one.Posts : 1;
            }
            else
            {
                Floor0Model zero = setup.Floor0[floor]!;
                int written = ReadFloor0(ref bits, zero, dest, count, stores);
                nonzero[ch] = written > 0;
                count += Math.Max(1, written);
            }
        }

        for (int step = 0; step < mapping.CouplingMagnitude.Length; step++)
        {
            int magnitude = mapping.CouplingMagnitude[step];
            int angle = mapping.CouplingAngle[step];
            if (nonzero[magnitude] || nonzero[angle])
            {
                nonzero[magnitude] = true;
                nonzero[angle] = true;
            }
        }

        if (stores)
        {
            dest[LentLayout.ResidueStart] = count;
        }

        int codedEnd = 0;
        Span<int> bundle = stackalloc int[Math.Min(channels, 256)];
        Span<int> usedChannels = stackalloc int[Math.Min(channels, 256)];
        for (int submap = 0; submap < mapping.Submaps; submap++)
        {
            int inBundle = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                if (mapping.ChannelMux[ch] == submap)
                {
                    bundle[inBundle++] = ch;
                }
            }

            ResidueModel residue = setup.Residues[mapping.ResidueOfSubmap[submap]];
            if (residue.Type == 2)
            {
                bool any = false;
                for (int i = 0; i < inBundle; i++)
                {
                    any |= nonzero[bundle[i]];
                }

                if (any)
                {
                    codedEnd = Math.Max(codedEnd, ReadResidue2(ref bits, residue, n, inBundle, submap, dest, ref count, stores));
                }
            }
            else
            {
                int usedCount = 0;
                for (int i = 0; i < inBundle; i++)
                {
                    if (nonzero[bundle[i]])
                    {
                        usedChannels[usedCount++] = bundle[i];
                    }
                }

                if (usedCount > 0)
                {
                    codedEnd = Math.Max(codedEnd, ReadResidue01(ref bits, residue, n, usedChannels[..usedCount], submap, dest, ref count, stores));
                }
            }
        }

        if (stores)
        {
            dest[LentLayout.Used] = count;
        }

        return new WalkFacts(true, (int)mode, isLong, false, codedEnd, count, _isCapped);
    }

    // floor1_inverse1: the amplitude of the first two posts, then the other posts by partition, each wrapped around its prediction from the posts before it.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool ReadFloor1(ref PacketBits bits, Floor1Model floor, scoped Span<int> fit)
    {
        if (bits.Read(1) != 1)
        {
            return false;
        }

        int amplitudeBits = floor.AmplitudeBits;
        fit[0] = (int)bits.Read(amplitudeBits);
        fit[1] = (int)bits.Read(amplitudeBits);
        int j = 2;
        for (int i = 0; i < floor.Partitions; i++)
        {
            int cls = floor.PartitionClass[i];
            int dimensions = floor.ClassDimensions[cls];
            int subclassBits = floor.ClassSubclasses[cls];
            int subclasses = 1 << subclassBits;
            int cval = 0;
            if (subclassBits > 0)
            {
                cval = setup.Books[floor.ClassBook[cls]].Decode(ref bits);
                if (cval == -1)
                {
                    return false;
                }
            }

            for (int k = 0; k < dimensions; k++)
            {
                int book = floor.ClassSubbook[cls][cval & (subclasses - 1)];
                cval >>= subclassBits;
                if (book >= 0)
                {
                    int value = setup.Books[book].Decode(ref bits);
                    fit[j + k] = value;
                    if (value == -1)
                    {
                        return false;
                    }
                }
                else
                {
                    fit[j + k] = 0;
                }
            }

            j += dimensions;
        }

        for (int i = 2; i < floor.Posts; i++)
        {
            int lo = floor.LowNeighbor[i - 2];
            int hi = floor.HighNeighbor[i - 2];
            int predicted = RenderPoint(floor.PostList[lo], floor.PostList[hi], fit[lo], fit[hi], floor.PostList[i]);
            int highRoom = floor.QuantQ - predicted;
            int lowRoom = predicted;
            int room = (highRoom < lowRoom ? highRoom : lowRoom) << 1;
            int value = fit[i];
            if (value != 0)
            {
                if (value >= room)
                {
                    value = highRoom > lowRoom ? value - lowRoom : -1 - (value - highRoom);
                }
                else if ((value & 1) != 0)
                {
                    value = -((value + 1) >> 1);
                }
                else
                {
                    value >>= 1;
                }

                fit[i] = (value + predicted) & 0x7FFF;
                fit[lo] &= 0x7FFF;
                fit[hi] &= 0x7FFF;
            }
            else
            {
                fit[i] = predicted | 0x8000;
            }
        }

        return true;
    }

    private static int RenderPoint(int x0, int x1, int y0, int y1, int x)
    {
        y0 &= 0x7FFF;
        y1 &= 0x7FFF;
        int dy = y1 - y0;
        int adx = x1 - x0;
        int ady = Math.Abs(dy);
        int err = ady * (x - x0);
        int off = err / adx;
        return dy < 0 ? y0 - off : y0 + off;
    }

    // floor0_inverse1: the amplitude, the book number and the entries of the book that give the order's coefficients. Returns the ints written (0 where the floor is unused).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int ReadFloor0(ref PacketBits bits, Floor0Model floor, Span<int> dest, int at, bool stores)
    {
        long amplitude = bits.Read(floor.AmplitudeBits);
        if (amplitude > 0)
        {
            long bookNumber = bits.Read(Codebook.ILog(floor.Books.Length));
            if (bookNumber != -1 && bookNumber < floor.Books.Length)
            {
                Codebook book = setup.Books[floor.Books[bookNumber]];
                int entries = 0;
                if (book.UsedEntries > 0)
                {
                    for (int i = 0; i < floor.Order;)
                    {
                        int entry = book.Decode(ref bits);
                        if (entry == -1)
                        {
                            return Unused(dest, at, stores);
                        }

                        if (stores)
                        {
                            dest[at + 4 + entries] = entry;
                        }

                        entries++;
                        i += book.Dimensions;
                    }
                }

                if (stores)
                {
                    dest[at] = LentLayout.FloorZero;
                    dest[at + 1] = (int)amplitude;
                    dest[at + 2] = (int)bookNumber;
                    dest[at + 3] = entries;
                }

                return 4 + entries;
            }
        }

        return Unused(dest, at, stores);
    }

    private static int Unused(Span<int> dest, int at, bool stores)
    {
        if (stores)
        {
            dest[at] = LentLayout.FloorUnused;
        }

        return 0;
    }

    private int[][] ClassRows(int count)
    {
        if (_classRows.Length < count)
        {
            _classRows = new int[Math.Max(count, _classRows.Length * 2)][];
        }

        return _classRows;
    }

    // _01inverse (residue types 0 and 1): the class words of every used channel, then the passes over the partitions. Returns the end of the highest partition coded.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int ReadResidue01(ref PacketBits bits, ResidueModel residue, int n, scoped ReadOnlySpan<int> used, int submap, Span<int> dest, ref int count, bool stores)
    {
        Codebook phrase = setup.Books[residue.ClassBook];
        int spp = residue.Grouping;
        int perWord = phrase.Dimensions;
        int end = Math.Min(residue.End, n / 2);
        int span = end - residue.Begin;
        int coded = 0;
        if (span <= 0)
        {
            return 0;
        }

        int partitions = span / spp;
        int words = (partitions + perWord - 1) / perWord;
        int[][] classes = ClassRows(used.Length * words);

        for (int stage = 0; stage < residue.Stages; stage++)
        {
            if (stage != 0 && (residue.StageMask & (1 << stage)) == 0)
            {
                continue;
            }

            for (int i = 0, l = 0; i < partitions; l++)
            {
                if (stage == 0)
                {
                    for (int j = 0; j < used.Length; j++)
                    {
                        int temp = phrase.Decode(ref bits);
                        if (temp == -1 || temp >= residue.PartitionValues)
                        {
                            return coded;
                        }

                        classes[(j * words) + l] = residue.DecodeMap[temp];
                    }
                }

                for (int k = 0; k < perWord && i < partitions; k++, i++)
                {
                    Visits += used.Length;
                    _budget -= used.Length;
                    if (_budget < 0)
                    {
                        _isCapped = true;
                        return coded;
                    }

                    for (int j = 0; j < used.Length; j++)
                    {
                        int cls = classes[(j * words) + l][k];
                        if ((residue.SecondStages[cls] & (1 << stage)) == 0)
                        {
                            continue;
                        }

                        int bookIndex = residue.PartBooks[cls][stage];
                        Codebook book = setup.Books[bookIndex];
                        int offset = residue.Begin + (i * spp);
                        if (book.UsedEntries == 0)
                        {
                            continue;
                        }

                        if (book.Dimensions == 0)
                        {
                            // vorbis_book_decodev_add of a book without dimensions reads codewords until the packet ends and adds nothing (type 0's n / dim is a division by zero in libvorbis: nothing is read).
                            if (residue.Type != 0)
                            {
                                while (book.Decode(ref bits) != -1 && bits.Remaining > 0)
                                {
                                }

                                return coded;
                            }

                            continue;
                        }

                        int entries = residue.Type == 0 ? spp / book.Dimensions : (spp + book.Dimensions - 1) / book.Dimensions;
                        int header = count;
                        count += LentLayout.RecordHeader;
                        int read = 0;
                        for (; read < entries; read++)
                        {
                            int entry = book.Decode(ref bits);
                            if (entry == -1)
                            {
                                break;
                            }

                            if (stores)
                            {
                                dest[count] = entry;
                            }

                            count++;
                        }

                        bool failed = read < entries;
                        if (failed && residue.Type == 0)
                        {
                            // The values of a partition of type 0 are added once all its entries are read: none of this partition's are.
                            count = header;
                            return coded;
                        }

                        if (read == 0)
                        {
                            count = header;
                        }
                        else
                        {
                            if (stores)
                            {
                                dest[header] = used[j];
                                dest[header + 1] = submap;
                                dest[header + 2] = offset;
                                dest[header + 3] = bookIndex;
                                dest[header + 4] = read;
                            }

                            coded = Math.Max(coded, offset + spp);
                        }

                        if (failed)
                        {
                            return coded;
                        }
                    }
                }
            }
        }

        return coded;
    }

    // res2_inverse (residue type 2): one vector of the bundle's channels interleaved. Returns the end of the highest partition coded, in coefficients of one channel.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int ReadResidue2(ref PacketBits bits, ResidueModel residue, int n, int channels, int submap, Span<int> dest, ref int count, bool stores)
    {
        Codebook phrase = setup.Books[residue.ClassBook];
        int spp = residue.Grouping;
        int perWord = phrase.Dimensions;
        int end = Math.Min(residue.End, n * channels / 2);
        int span = end - residue.Begin;
        int coded = 0;
        if (span <= 0)
        {
            return 0;
        }

        int partitions = span / spp;
        int words = (partitions + perWord - 1) / perWord;
        int[][] classes = ClassRows(words);
        for (int stage = 0; stage < residue.Stages; stage++)
        {
            if (stage != 0 && (residue.StageMask & (1 << stage)) == 0)
            {
                continue;
            }

            for (int i = 0, l = 0; i < partitions; l++)
            {
                if (stage == 0)
                {
                    int temp = phrase.Decode(ref bits);
                    if (temp == -1 || temp >= residue.PartitionValues)
                    {
                        return coded;
                    }

                    classes[l] = residue.DecodeMap[temp];
                }

                for (int k = 0; k < perWord && i < partitions; k++, i++)
                {
                    Visits++;
                    if (--_budget < 0)
                    {
                        _isCapped = true;
                        return coded;
                    }

                    int cls = classes[l][k];
                    if ((residue.SecondStages[cls] & (1 << stage)) == 0)
                    {
                        continue;
                    }

                    int bookIndex = residue.PartBooks[cls][stage];
                    Codebook book = setup.Books[bookIndex];
                    if (book.UsedEntries == 0)
                    {
                        continue;
                    }

                    if (book.Dimensions == 0)
                    {
                        while (book.Decode(ref bits) != -1 && bits.Remaining > 0)
                        {
                        }

                        return coded;
                    }

                    int offset = (i * spp) + residue.Begin;
                    int from = offset / channels;
                    int to = (offset + spp) / channels;
                    long values = (long)(to - from) * channels;
                    int entries = (int)((values + book.Dimensions - 1) / book.Dimensions);
                    int header = count;
                    count += LentLayout.RecordHeader;
                    int read = 0;
                    for (; read < entries; read++)
                    {
                        int entry = book.Decode(ref bits);
                        if (entry == -1)
                        {
                            break;
                        }

                        if (stores)
                        {
                            dest[count] = entry;
                        }

                        count++;
                    }

                    if (read == 0)
                    {
                        count = header;
                    }
                    else
                    {
                        if (stores)
                        {
                            dest[header] = -1;
                            dest[header + 1] = submap;
                            dest[header + 2] = offset;
                            dest[header + 3] = bookIndex;
                            dest[header + 4] = read;
                        }

                        coded = Math.Max(coded, (offset + spp) / channels);
                    }

                    if (read < entries)
                    {
                        return coded;
                    }
                }
            }
        }

        return coded;
    }
}
