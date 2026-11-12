using Encspect.Core;

namespace Encspect.Formats.Vorbis;

// The setup header (Vorbis I specification, section 4.2.4) as the audio packets use it: the codebooks with their decoding tables, the floors, residues,
// mappings and modes. SetupDecoder walks the same bits into nodes; this reads them again, without nodes, into what the walk of a packet (PacketWalker)
// and the decoder (Decoding) need, and refuses what libvorbis 1.3.7 refuses when it unpacks the header and sets up its look-ups (lib/info.c
// _vorbis_unpack_books, floor0.c, floor1.c, res0.c and mapping0.c unpack and look, BSD-3-Clause, Copyright (c) 2002-2020 Xiph.Org Foundation; the notice is
// in THIRD-PARTY-NOTICES.md). A refused header is the parser's findings already (SetupDecoder), so this gives no reason.

internal sealed class Floor0Model
{
    public int Order { get; init; }

    public int Rate { get; init; }

    public int BarkMapSize { get; init; }

    public int AmplitudeBits { get; init; }

    public int AmplitudeOffset { get; init; }

    public required int[] Books { get; init; }
}

internal sealed class Floor1Model
{
    // The values besides the two end posts that libvorbis 1.3.7 allows (VIF_POSIT).
    public const int MaxPosts = 63;

    public required int Partitions { get; init; }

    public required int[] PartitionClass { get; init; }

    public required int[] ClassDimensions { get; init; }

    public required int[] ClassSubclasses { get; init; }

    public required int[] ClassBook { get; init; }

    // Per class and subclass, the codebook or -1 for none.
    public required int[][] ClassSubbook { get; init; }

    public int Multiplier { get; init; }

    public int RangeBits { get; init; }

    // The X positions in the order they are coded, the two ends first: 0 and 2^rangebits.
    public required int[] PostList { get; init; }

    public int Posts => PostList.Length;

    // The amplitude steps: 256, 128, 86 or 64.
    public int QuantQ { get; init; }

    // Sorted order to coded order, coded order to sorted order, and the X positions in sorted order.
    public required int[] ForwardIndex { get; init; }

    public required int[] ReverseIndex { get; init; }

    public required int[] SortedIndex { get; init; }

    // For each coded post from the third, the coded index of the nearest lower and higher post among the ones before it.
    public required int[] LowNeighbor { get; init; }

    public required int[] HighNeighbor { get; init; }

    // The bits of the first two posts and of the others' amplitudes: ilog(QuantQ - 1).
    public int AmplitudeBits => Codebook.ILog(QuantQ - 1);
}

internal sealed class ResidueModel
{
    public int Type { get; init; }

    public int Begin { get; init; }

    public int End { get; init; }

    // The partition size.
    public int Grouping { get; init; }

    // The number of classifications.
    public int Partitions { get; init; }

    public int ClassBook { get; init; }

    // Per classification: which of the eight passes have a book (the cascade bits).
    public required int[] SecondStages { get; init; }

    // Per classification and pass, the codebook or -1.
    public required int[][] PartBooks { get; init; }

    // The most passes any classification uses: ilog of its largest cascade.
    public int Stages { get; init; }

    // The passes some classification has a book in; the others read nothing after the class words and are not visited.
    public int StageMask { get; init; }

    // partitions ^ dimensions of the class book: the class words that name a combination.
    public int PartitionValues { get; init; }

    // For each class word, the classifications of the partitions it names, the first partition first.
    public required int[][] DecodeMap { get; init; }
}

internal sealed class MappingModel
{
    public int Submaps { get; init; }

    public required int[] CouplingMagnitude { get; init; }

    public required int[] CouplingAngle { get; init; }

    // The submap of each channel.
    public required int[] ChannelMux { get; init; }

    public required int[] FloorOfSubmap { get; init; }

    public required int[] ResidueOfSubmap { get; init; }
}

internal sealed class SetupModel
{
    private const long EntryBudget = 1L << 22;

    // The entries a setup header may state per byte it takes (and 4,096 besides): an ordered codebook states millions of entries in a few bytes, which libvorbis accepts and which
    // would cost the analyzer more than the file's size allows (docs/design/lossy-decoders.md, "As built: Vorbis").
    private const long EntriesPerByte = 64;

    public required Codebook[] Books { get; init; }

    public required Floor0Model?[] Floor0 { get; init; }

    public required Floor1Model?[] Floor1 { get; init; }

    public required ResidueModel[] Residues { get; init; }

    public required MappingModel[] Mappings { get; init; }

    // For each mode, whether it codes a long block, and its mapping.
    public required bool[] ModeIsLong { get; init; }

    public required int[] ModeMapping { get; init; }

    public int Channels { get; init; }

    public int ModeBits => Codebook.ILog(ModeIsLong.Length - 1);

    // Reads the setup header packet (type 5, "vorbis", and the bits) for a stream of that many channels, or returns null where libvorbis refuses it. A
    // setup of more than 4 Mi codebook entries in all is refused too, to bound the memory a stream can ask for (libvorbis accepts up to 8 Mi in each of 256 books).
    // The parser hands the model to its decoder through DecodeSetup.Shared, so that the header is read once per stream. The decoder reads only what BuildTables leaves
    // alone (lengths, entries, lookups): the parser's walk is the one that builds a codebook's tables, on the same thread.
    public static SetupModel? Read(ReadOnlySpan<byte> packet, int channels, CancellationToken cancellationToken) => Read(packet, channels, cancellationToken, out _);

    // exceedsBudget: the header was refused for stating more codebook entries than its size allows (EntriesPerByte), not because libvorbis refuses it.
    public static SetupModel? Read(ReadOnlySpan<byte> packet, int channels, CancellationToken cancellationToken, out bool exceedsBudget)
    {
        exceedsBudget = false;
        if (packet.Length < 8 || packet[0] != 5 || !packet.Slice(1, 6).SequenceEqual("vorbis"u8) || channels < 1)
        {
            return null;
        }

        var bits = new PacketBits(packet[7..]);
        long budget = Math.Min(EntryBudget, 4096 + (EntriesPerByte * packet.Length));
        long bookCount = bits.Read(8) + 1;
        if (bookCount < 1)
        {
            return null;
        }

        var books = new Codebook[bookCount];
        long entries = 0;
        for (int i = 0; i < books.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Codebook? book = Codebook.Read(ref bits, budget - entries, cancellationToken, out bool tooLarge);
            if (book is null)
            {
                exceedsBudget = tooLarge;
                return null;
            }

            entries += book.EntryCount;
            books[i] = book;
        }

        long times = bits.Read(6) + 1;
        if (times < 1)
        {
            return null;
        }

        for (long i = 0; i < times; i++)
        {
            if (bits.Read(16) != 0)
            {
                return null;
            }
        }

        long floorCount = bits.Read(6) + 1;
        if (floorCount < 1)
        {
            return null;
        }

        var floor0 = new Floor0Model?[floorCount];
        var floor1 = new Floor1Model?[floorCount];
        for (int i = 0; i < floorCount; i++)
        {
            long type = bits.Read(16);
            if (type == 0)
            {
                floor0[i] = ReadFloor0(ref bits, books);
                if (floor0[i] is null)
                {
                    return null;
                }
            }
            else if (type == 1)
            {
                floor1[i] = ReadFloor1(ref bits, books);
                if (floor1[i] is null)
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        long residueCount = bits.Read(6) + 1;
        if (residueCount < 1)
        {
            return null;
        }

        var residues = new ResidueModel[residueCount];
        for (int i = 0; i < residueCount; i++)
        {
            long type = bits.Read(16);
            if (type is < 0 or > 2)
            {
                return null;
            }

            ResidueModel? residue = ReadResidue(ref bits, books, (int)type);
            if (residue is null)
            {
                return null;
            }

            residues[i] = residue;
        }

        long mappingCount = bits.Read(6) + 1;
        if (mappingCount < 1)
        {
            return null;
        }

        var mappings = new MappingModel[mappingCount];
        for (int i = 0; i < mappingCount; i++)
        {
            if (bits.Read(16) != 0)
            {
                return null;
            }

            MappingModel? mapping = ReadMapping(ref bits, channels, (int)floorCount, (int)residueCount);
            if (mapping is null)
            {
                return null;
            }

            mappings[i] = mapping;
        }

        long modeCount = bits.Read(6) + 1;
        if (modeCount < 1)
        {
            return null;
        }

        var isLong = new bool[modeCount];
        var modeMapping = new int[modeCount];
        for (int i = 0; i < modeCount; i++)
        {
            long flag = bits.Read(1);
            long window = bits.Read(16);
            long transform = bits.Read(16);
            long mapping = bits.Read(8);
            if (flag < 0 || window != 0 || transform != 0 || mapping < 0 || mapping >= mappingCount)
            {
                return null;
            }

            isLong[i] = flag == 1;
            modeMapping[i] = (int)mapping;
        }

        if (bits.Read(1) != 1)
        {
            return null;
        }

        return new SetupModel { Books = books, Floor0 = floor0, Floor1 = floor1, Residues = residues, Mappings = mappings, ModeIsLong = isLong, ModeMapping = modeMapping, Channels = channels };
    }

    private static Floor0Model? ReadFloor0(ref PacketBits bits, Codebook[] books)
    {
        long order = bits.Read(8);
        long rate = bits.Read(16);
        long barkMap = bits.Read(16);
        long amplitudeBits = bits.Read(6);
        long amplitudeOffset = bits.Read(8);
        long bookCount = bits.Read(4) + 1;
        if (order < 1 || rate < 1 || barkMap < 1 || amplitudeBits < 0 || amplitudeOffset < 0 || bookCount < 1)
        {
            return null;
        }

        var list = new int[bookCount];
        for (int j = 0; j < bookCount; j++)
        {
            long book = bits.Read(8);
            if (book < 0 || book >= books.Length || books[book].LookupType == 0 || books[book].Dimensions < 1)
            {
                return null;
            }

            list[j] = (int)book;
        }

        return new Floor0Model { Order = (int)order, Rate = (int)rate, BarkMapSize = (int)barkMap, AmplitudeBits = (int)amplitudeBits, AmplitudeOffset = (int)amplitudeOffset, Books = list };
    }

    private static Floor1Model? ReadFloor1(ref PacketBits bits, Codebook[] books)
    {
        long partitions = bits.Read(5);
        if (partitions < 0)
        {
            return null;
        }

        var partitionClass = new int[partitions];
        int maxClass = -1;
        for (int j = 0; j < partitions; j++)
        {
            long value = bits.Read(4);
            if (value < 0)
            {
                return null;
            }

            partitionClass[j] = (int)value;
            maxClass = Math.Max(maxClass, (int)value);
        }

        var dimensions = new int[maxClass + 1];
        var subclasses = new int[maxClass + 1];
        var classBook = new int[maxClass + 1];
        var subbook = new int[maxClass + 1][];
        for (int c = 0; c <= maxClass; c++)
        {
            long dims = bits.Read(3);
            long subs = bits.Read(2);
            if (dims < 0 || subs < 0)
            {
                return null;
            }

            dimensions[c] = (int)dims + 1;
            subclasses[c] = (int)subs;
            if (subs > 0)
            {
                long book = bits.Read(8);
                if (book < 0 || book >= books.Length)
                {
                    return null;
                }

                classBook[c] = (int)book;
            }

            subbook[c] = new int[1 << (int)subs];
            for (int k = 0; k < subbook[c].Length; k++)
            {
                long value = bits.Read(8) - 1;
                if (value < -1 || value >= books.Length)
                {
                    return null;
                }

                subbook[c][k] = (int)value;
            }
        }

        long multiplier = bits.Read(2) + 1;
        long rangeBits = bits.Read(4);
        if (multiplier < 1 || rangeBits < 0)
        {
            return null;
        }

        int count = 0;
        for (int j = 0; j < partitions; j++)
        {
            count += dimensions[partitionClass[j]];
        }

        if (count > Floor1Model.MaxPosts)
        {
            return null;
        }

        var postList = new int[count + 2];
        postList[0] = 0;
        postList[1] = 1 << (int)rangeBits;
        for (int k = 0; k < count; k++)
        {
            long value = bits.Read((int)rangeBits);
            if (value < 0)
            {
                return null;
            }

            postList[k + 2] = (int)value;
        }

        int posts = postList.Length;
        var forward = new int[posts];
        for (int i = 0; i < posts; i++)
        {
            forward[i] = i;
        }

        Array.Sort(forward, (a, b) => postList[a].CompareTo(postList[b]));
        for (int i = 1; i < posts; i++)
        {
            if (postList[forward[i - 1]] == postList[forward[i]])
            {
                return null;
            }
        }

        var reverse = new int[posts];
        var sorted = new int[posts];
        for (int i = 0; i < posts; i++)
        {
            reverse[forward[i]] = i;
            sorted[i] = postList[forward[i]];
        }

        var low = new int[Math.Max(0, posts - 2)];
        var high = new int[Math.Max(0, posts - 2)];
        int n = postList[1];
        for (int i = 0; i < posts - 2; i++)
        {
            int lo = 0;
            int hi = 1;
            int lx = 0;
            int hx = n;
            int current = postList[i + 2];
            for (int j = 0; j < i + 2; j++)
            {
                int x = postList[j];
                if (x > lx && x < current)
                {
                    lo = j;
                    lx = x;
                }

                if (x < hx && x > current)
                {
                    hi = j;
                    hx = x;
                }
            }

            low[i] = lo;
            high[i] = hi;
        }

        return new Floor1Model
        {
            Partitions = (int)partitions,
            PartitionClass = partitionClass,
            ClassDimensions = dimensions,
            ClassSubclasses = subclasses,
            ClassBook = classBook,
            ClassSubbook = subbook,
            Multiplier = (int)multiplier,
            RangeBits = (int)rangeBits,
            PostList = postList,
            QuantQ = multiplier switch { 1 => 256, 2 => 128, 3 => 86, _ => 64 },
            ForwardIndex = forward,
            ReverseIndex = reverse,
            SortedIndex = sorted,
            LowNeighbor = low,
            HighNeighbor = high,
        };
    }

    private static ResidueModel? ReadResidue(ref PacketBits bits, Codebook[] books, int type)
    {
        long begin = bits.Read(24);
        long end = bits.Read(24);
        long grouping = bits.Read(24) + 1;
        long partitions = bits.Read(6) + 1;
        long classBook = bits.Read(8);
        if (begin < 0 || end < 0 || grouping < 1 || partitions < 1 || classBook < 0)
        {
            return null;
        }

        var cascade = new int[partitions];
        int accumulated = 0;
        for (int j = 0; j < partitions; j++)
        {
            long low = bits.Read(3);
            long flag = bits.Read(1);
            if (flag < 0)
            {
                return null;
            }

            long value = low;
            if (flag == 1)
            {
                long high = bits.Read(5);
                if (high < 0)
                {
                    return null;
                }

                value |= high << 3;
            }

            cascade[j] = (int)value;
            accumulated += System.Numerics.BitOperations.PopCount((uint)value);
        }

        var list = new int[accumulated];
        for (int j = 0; j < accumulated; j++)
        {
            long book = bits.Read(8);
            if (book < 0)
            {
                return null;
            }

            list[j] = (int)book;
        }

        if (classBook >= books.Length)
        {
            return null;
        }

        foreach (int book in list)
        {
            if (book >= books.Length || books[book].LookupType == 0)
            {
                return null;
            }
        }

        Codebook phrase = books[classBook];
        long partitionValues = 1;
        int dimensions = phrase.Dimensions;
        if (dimensions < 1)
        {
            return null;
        }

        for (int d = dimensions; d > 0; d--)
        {
            partitionValues *= partitions;
            if (partitionValues > phrase.EntryCount)
            {
                return null;
            }
        }

        int stages = 0;
        var partBooks = new int[partitions][];
        int next = 0;
        for (int j = 0; j < partitions; j++)
        {
            int count = Codebook.ILog(cascade[j]);
            stages = Math.Max(stages, count);
            partBooks[j] = new int[count];
            for (int k = 0; k < count; k++)
            {
                partBooks[j][k] = (cascade[j] & (1 << k)) != 0 ? list[next++] : -1;
            }
        }

        var map = new int[partitionValues][];
        for (int j = 0; j < partitionValues; j++)
        {
            long value = j;
            long mult = partitionValues / partitions;
            map[j] = new int[dimensions];
            for (int k = 0; k < dimensions; k++)
            {
                long digit = value / mult;
                value -= digit * mult;
                mult /= partitions;
                map[j][k] = (int)digit;
            }
        }

        return new ResidueModel
        {
            Type = type,
            Begin = (int)begin,
            End = (int)end,
            Grouping = (int)grouping,
            Partitions = (int)partitions,
            ClassBook = (int)classBook,
            SecondStages = cascade,
            PartBooks = partBooks,
            Stages = stages,
            StageMask = StageMaskOf(partBooks, books),
            PartitionValues = (int)partitionValues,
            DecodeMap = map,
        };
    }

    // The passes some classification has a book with entries in: the others read nothing after the class words.
    private static int StageMaskOf(int[][] partBooks, Codebook[] books)
    {
        int mask = 0;
        foreach (int[] row in partBooks)
        {
            for (int stage = 0; stage < row.Length; stage++)
            {
                if (row[stage] >= 0 && books[row[stage]].UsedEntries > 0)
                {
                    mask |= 1 << stage;
                }
            }
        }

        return mask;
    }

    private static MappingModel? ReadMapping(ref PacketBits bits, int channels, int floors, int residues)
    {
        long hasSubmaps = bits.Read(1);
        if (hasSubmaps < 0)
        {
            return null;
        }

        int submaps = 1;
        if (hasSubmaps == 1)
        {
            long value = bits.Read(4);
            if (value < 0)
            {
                return null;
            }

            submaps = (int)value + 1;
        }

        long hasCoupling = bits.Read(1);
        if (hasCoupling < 0)
        {
            return null;
        }

        int[] magnitude = [];
        int[] angle = [];
        if (hasCoupling == 1)
        {
            long steps = bits.Read(8) + 1;
            if (steps < 1)
            {
                return null;
            }

            magnitude = new int[steps];
            angle = new int[steps];
            int width = Codebook.ILog(channels - 1);
            for (int i = 0; i < steps; i++)
            {
                long m = bits.Read(width);
                long a = bits.Read(width);
                if (m < 0 || a < 0 || m == a || m >= channels || a >= channels)
                {
                    return null;
                }

                magnitude[i] = (int)m;
                angle[i] = (int)a;
            }
        }

        if (bits.Read(2) != 0)
        {
            return null;
        }

        var mux = new int[channels];
        if (submaps > 1)
        {
            for (int i = 0; i < channels; i++)
            {
                long value = bits.Read(4);
                if (value < 0 || value >= submaps)
                {
                    return null;
                }

                mux[i] = (int)value;
            }
        }

        var floorOf = new int[submaps];
        var residueOf = new int[submaps];
        for (int i = 0; i < submaps; i++)
        {
            bits.Read(8);
            long floor = bits.Read(8);
            long residue = bits.Read(8);
            if (floor < 0 || floor >= floors || residue < 0 || residue >= residues)
            {
                return null;
            }

            floorOf[i] = (int)floor;
            residueOf[i] = (int)residue;
        }

        return new MappingModel { Submaps = submaps, CouplingMagnitude = magnitude, CouplingAngle = angle, ChannelMux = mux, FloorOfSubmap = floorOf, ResidueOfSubmap = residueOf };
    }
}
