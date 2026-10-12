using System.Globalization;
using Encspect.Core;

namespace Encspect.Formats.Vorbis;

// A problem in a header, with the node it concerns.
internal sealed record Problem(Severity Severity, string Code, string Message, Node Node);

// What the setup header gives the audio packets: whether each mode codes a long block, and how
// many bits name the mode. Null modes when the header could not be read that far.
internal sealed record SetupResult(Node Node, bool[]? ModeIsLong, int ModeBits, IReadOnlyList<Problem> Problems)
{
    public bool HasModes => ModeIsLong is { Length: > 0 };
}

// The setup header (Vorbis I specification, section 4.2.4): packet type 5, "vorbis", then, packed
// least significant bit first (section 2.1.4), the codebooks (section 3.2.1), the time domain
// placeholders, the floors (sections 6.2.1 and 7.2.2), the residues (section 8.6.1), the mappings
// and the modes, then the framing bit. It is walked as a decoder
// unpacks it, without building the Huffman trees or vector lookup tables: each field becomes a node
// at its bits, and counts and key parameters become the values of the groups. What breaks the
// specification, or what libvorbis 1.3.7 refuses when it unpacks the header, is a problem; a
// structure that cannot be followed further (an undefined floor, residue or mapping type, or the end
// of the packet) stops the walk.
internal sealed class SetupDecoder
{
    private const string Invalid = "vorbis.setup-invalid";

    private readonly MappedBytes _packet;
    private readonly BitReader _bits;
    private readonly int _channels;
    private readonly CancellationToken _cancellationToken;
    private readonly List<Problem> _problems = [];
    private readonly Stack<Group> _open = new();
    private readonly List<Node> _top = [];

    // Per codebook, what the floors and residues that use it need to know.
    private readonly List<(int Dimensions, int Entries, int LookupType)> _books = [];
    private int _floors;
    private int _residues;
    private int _mappings;
    private bool[]? _modeIsLong;

    private SetupDecoder(MappedBytes packet, int channels, CancellationToken cancellationToken)
    {
        _packet = packet;
        _bits = new BitReader(packet, BitOrder.LeastSignificantFirst);
        _channels = channels;
        _cancellationToken = cancellationToken;
    }

    public static SetupResult Decode(MappedBytes packet, int channels, CancellationToken cancellationToken) =>
        new SetupDecoder(packet, channels, cancellationToken).Run();

    // ilog (section 9.2.1): the position of the highest set bit, 0 for 0 and below.
    public static int ILog(long value) => value <= 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)value);

    private SetupResult Run()
    {
        _top.Add(Node.FromMapped("Packet Type", _packet, 0, 1, NodeValue.Integer(5), description: Explain.PacketTypeSetup));
        _top.Add(Node.FromMapped("Vorbis String", _packet, 1, 7, "vorbis", description: Explain.VorbisString));
        _bits.ReadLong(56);
        string summary;
        try
        {
            Codebooks();
            Times();
            Floors();
            Residues();
            Mappings();
            Modes();
            Node framing = Field("Framing Bit", 1, v => NodeValue.Boolean(v == 1), Explain.FramingBit, out int bit);
            if (bit != 1)
            {
                Add(Severity.Warning, "vorbis.framing-missing", framing, "The setup header's framing bit is 0; libvorbis 1.3.7 refuses the header.");
            }

            int end = (_bits.Position + 7) / 8;
            if (end < _packet.Length)
            {
                Node trailing = Node.FromMapped("Trailing Data", _packet, end, _packet.Length, NodeValue.Integer(_packet.Length - end, "bytes", "byte"),
                    description: Explain.SetupTrailingData);
                _top.Add(trailing);
                Add(Severity.Info, "vorbis.header-extra-data", trailing, $"The setup header holds {_packet.Length - end} bytes after its framing bit, which decoders ignore.");
            }

            summary = $"{Count(_books.Count, "codebook", "codebooks")}, {Count(_floors, "floor", "floors")}, {Count(_residues, "residue", "residues")}, " +
                $"{Count(_mappings, "mapping", "mappings")}, {Count(_modeIsLong!.Length, "mode", "modes")}";
        }
        catch (SetupStop stop)
        {
            // What was read stays in the tree, each open group ending where the walk stopped.
            while (_open.Count > 0)
            {
                Close("cut short");
            }

            Node at = stop.Node ?? (_top.Count > 0 ? _top[^1] : Node.FromMapped("Setup Header", _packet, 0, _packet.Length));
            Add(Severity.Warning, stop.Code, at, stop.Message);
            summary = stop.Code == "vorbis.setup-truncated" ? "cut short" : "not readable to the end";
        }

        Node node = Node.FromMapped("Setup Header", _packet, 0, _packet.Length, summary, description: Explain.Setup, topic: Topics.VorbisSetupHeader);
        foreach (Node child in _top)
        {
            node.Add(child);
        }

        bool[]? modes = _modeIsLong;
        return new SetupResult(node, modes, modes is null ? 0 : ILog(modes.Length - 1), _problems);
    }

    // Section 3.2.1.
    private void Codebooks()
    {
        Open("Codebooks", Explain.Codebooks);
        Field("Codebook Count", 8, v => NodeValue.Integer(v + 1), Explain.CodebookCount, out int value);
        int count = value + 1;
        for (int i = 0; i < count; i++)
        {
            Codebook(i);
        }

        Close(Count(count, "codebook", "codebooks"));
    }

    private void Codebook(int index)
    {
        Open($"Codebook {index}", Explain.Codebook);
        Node sync = Field("Sync Pattern", 24, v => NodeValue.Hex(v, 6), Explain.SyncPattern, out int pattern);
        if (pattern != 0x564342)
        {
            throw new SetupStop(Invalid, $"Codebook {index} does not start with the sync pattern 0x564342 but 0x{pattern:X6}; decoders refuse the setup header, and its audio packets are only counted.", sync);
        }

        Node dimensionsNode = Field("Dimensions", 16, v => NodeValue.Integer(v), Explain.Dimensions, out int dimensions);
        Field("Entries", 24, v => NodeValue.Integer(v), Explain.Entries, out int entries);

        // libvorbis 1.3.7 (codebook.c, vorbis_staticbook_unpack) refuses books this large.
        if (ILog(dimensions) + ILog(entries) > 24)
        {
            Add(Severity.Warning, "vorbis.codebook-too-large", dimensionsNode,
                $"Codebook {index} has {dimensions} dimensions and {entries} entries, more than libvorbis 1.3.7 accepts (ilog of both above 24 together); it refuses the setup header.");
        }

        Field("IsOrdered", 1, v => NodeValue.Boolean(v == 1), Explain.IsOrdered, out int ordered);
        Span<long> lengths = stackalloc long[34];
        long used = 0;
        bool sparse = false;
        if (ordered == 0)
        {
            Field("IsSparse", 1, v => NodeValue.Boolean(v == 1), Explain.IsSparse, out int flag);
            sparse = flag == 1;
            Open("Codeword Lengths", Explain.CodewordLengths);
            for (int e = 0; e < entries; e++)
            {
                if (e % 65536 == 0)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                }

                if (sparse && Read(1, "the codeword lengths") == 0)
                {
                    continue;
                }

                lengths[Read(5, "the codeword lengths") + 1]++;
                used++;
            }
        }
        else
        {
            Open("Codeword Lengths", Explain.CodewordLengthsOrdered);
            long current = 0;
            int length = Read(5, "the codeword lengths") + 1;
            while (current < entries)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int number = Read(ILog(entries - current), "the codeword lengths");
                current += number;
                if (current > entries)
                {
                    Node node = Close($"{current} entries, {current - entries} more than the codebook has");
                    throw new SetupStop(Invalid, $"The ordered codeword lengths of codebook {index} cover {current} entries, more than its {entries}; decoders refuse the setup header.", node);
                }

                lengths[Math.Min(length, 33)] += number;
                used += number;
                length++;
            }
        }

        int shortest = 0;
        int longest = 0;
        for (int l = 1; l <= 33; l++)
        {
            if (lengths[l] > 0)
            {
                shortest = shortest == 0 ? l : shortest;
                longest = l;
            }
        }

        string range = used == 0 ? "no codewords" : shortest == longest ? $"{Math.Min(shortest, 32)} bits each" : $"{shortest} to {(longest > 32 ? "over 32" : longest)} bits";
        Node lengthsNode = Close($"{used} of {Count(entries, "entry", "entries")} used, {range}");
        CheckTree(index, lengths, used, lengthsNode);

        Field("Lookup Type", 4, v => NodeValue.Integer(v), Explain.LookupType, out int lookup);
        if (lookup is 1 or 2)
        {
            Field32("Minimum Value", Explain.MinimumValue);
            Field32("Delta Value", Explain.DeltaValue);
            Field("Value Bits", 4, v => NodeValue.Integer(v + 1, "bits", "bit"), Explain.ValueBits, out int valueBits);
            Field("IsSequence", 1, v => NodeValue.Boolean(v == 1), Explain.IsSequence, out _);
            long values = lookup == 1 ? Lookup1Values(entries, dimensions) : (long)entries * dimensions;
            if (lookup == 1 && dimensions == 0)
            {
                Add(Severity.Warning, Invalid, dimensionsNode, $"Codebook {index} has a value lookup of type 1 but no dimensions, for which the specification defines no lookup table.");
            }

            Skip("Multiplicands", values * (valueBits + 1), $"{values} values of {valueBits + 1} bits", Explain.Multiplicands, "the multiplicands");
        }
        else if (lookup != 0)
        {
            throw new SetupStop(Invalid, $"Codebook {index} has the lookup type {lookup}, which the specification does not define (section 3.2.1); decoders refuse the setup header.", _lastNode);
        }

        _books.Add((dimensions, entries, lookup));
        Close($"{dimensions} {(dimensions == 1 ? "dimension" : "dimensions")}, {Count(entries, "entry", "entries")}, lookup type {lookup}");
    }

    // The codeword lengths must make a complete prefix code (section 3.2.1): libvorbis 1.3.7
    // (sharedbook.c, _make_words) refuses a book whose lengths ask for more codewords than a code of
    // those lengths holds, and one that leaves codewords unused, except a book of a single entry of
    // length 1, or of no entries.
    private void CheckTree(int index, ReadOnlySpan<long> lengths, long used, Node node)
    {
        if (lengths[33] > 0)
        {
            Add(Severity.Warning, "vorbis.codebook-invalid", node, $"Codebook {index} has codewords longer than 32 bits; libvorbis 1.3.7 refuses the setup header.");
            return;
        }

        ulong sum = 0;
        for (int l = 1; l <= 32; l++)
        {
            sum += (ulong)lengths[l] << (32 - l);
        }

        const ulong full = 1UL << 32;
        if (sum > full)
        {
            Add(Severity.Warning, "vorbis.codebook-overpopulated", node,
                $"The codeword lengths of codebook {index} ask for more codewords than a prefix code of those lengths holds; libvorbis 1.3.7 refuses to decode the stream.");
        }
        else if (sum < full && used > 0 && !(used == 1 && lengths[1] == 1))
        {
            Add(Severity.Warning, "vorbis.codebook-underpopulated", node,
                $"The codeword lengths of codebook {index} leave codewords unused, an incomplete prefix code; libvorbis 1.3.7 refuses to decode the stream.");
        }
    }

    // lookup1_values (section 9.2.3): the largest r with r^dimensions not above entries.
    public static long Lookup1Values(long entries, int dimensions)
    {
        if (dimensions <= 0 || entries <= 0)
        {
            return 0;
        }

        long r = (long)Math.Floor(Math.Pow(entries, 1.0 / dimensions));
        while (r > 0 && Power(r, dimensions) > entries)
        {
            r--;
        }

        while (Power(r + 1, dimensions) <= entries)
        {
            r++;
        }

        return r;
    }

    // base^exponent, saturating above 2^40, far above the 2^24 entries a book can have.
    private static long Power(long value, int exponent)
    {
        if (value <= 1)
        {
            return value;
        }

        long result = 1;
        for (int i = 0; i < exponent; i++)
        {
            result *= value;
            if (result > 1L << 40)
            {
                return 1L << 40;
            }
        }

        return result;
    }

    // Section 4.2.4, "Time domain transforms": placeholders that must be 0.
    private void Times()
    {
        Open("Time Domain Transforms", Explain.Times);
        Field("Time Count", 6, v => NodeValue.Integer(v + 1), Explain.TimeCount, out int count);
        for (int i = 0; i <= count; i++)
        {
            Node node = Field("Time Type", 16, v => NodeValue.Integer(v), Explain.TimeType, out int value);
            if (value != 0)
            {
                Add(Severity.Warning, Invalid, node, $"A time domain transform type is {value}, where the specification allows only 0 (section 4.2.4); decoders refuse the setup header.");
            }
        }

        Close(Count(count + 1, "placeholder", "placeholders"));
    }

    private void Floors()
    {
        Open("Floors", Explain.Floors, Topics.VorbisSetupHeader);
        Field("Floor Count", 6, v => NodeValue.Integer(v + 1), Explain.FloorCount, out int count);
        _floors = count + 1;
        var types = new SortedSet<int>();
        for (int i = 0; i < _floors; i++)
        {
            Open($"Floor {i}", Explain.Floor);
            Node typeNode = Field("Floor Type", 16, v => NodeValue.Integer(v), Explain.FloorType, out int type);
            string value = type switch
            {
                0 => Floor0(i),
                1 => Floor1(i),
                _ => throw new SetupStop(Invalid, $"Floor {i} has the type {type}; the specification defines types 0 and 1 (section 4.2.4), so the setup header cannot be read further.", typeNode),
            };
            types.Add(type);
            Close(value);
        }

        Close($"{Count(_floors, "floor", "floors")}, type {string.Join(" and ", types)}");
    }

    // Section 6.2.1.
    private string Floor0(int index)
    {
        Node order = Field("Order", 8, v => NodeValue.Integer(v), Explain.Floor0Order, out int orderValue);
        Node rate = Field("Rate", 16, v => NodeValue.Integer(v, "Hz"), Explain.Floor0Rate, out int rateValue);
        Node bark = Field("Bark Map Size", 16, v => NodeValue.Integer(v), Explain.Floor0BarkMapSize, out int barkValue);
        Field("Amplitude Bits", 6, v => NodeValue.Integer(v, "bits", "bit"), Explain.Floor0AmplitudeBits, out _);
        Field("Amplitude Offset", 8, v => NodeValue.Integer(v), Explain.Floor0AmplitudeOffset, out _);
        Field("Number of Books", 4, v => NodeValue.Integer(v + 1), Explain.Floor0Books, out int books);
        foreach ((Node node, int value) in new[] { (order, orderValue), (rate, rateValue), (bark, barkValue) })
        {
            if (value == 0)
            {
                Add(Severity.Warning, Invalid, node, $"Floor {index}'s {node.Name.ToLowerInvariant()} is 0; libvorbis 1.3.7 refuses the setup header (floor0.c).");
            }
        }

        Open("Books", Explain.FloorBooks);
        var list = new List<int>();
        for (int j = 0; j <= books; j++)
        {
            Node book = Field("Book", 8, v => NodeValue.Integer(v), Explain.Floor0Book, out int value);
            list.Add(value);
            if (CheckBook(book, value, $"floor {index}") is { } info && (info.LookupType == 0 || info.Dimensions < 1))
            {
                Add(Severity.Warning, Invalid, book, $"Floor {index} uses codebook {value}, which has no value lookup; libvorbis 1.3.7 refuses the setup header (floor0.c).");
            }
        }

        Close(string.Join(", ", list));
        return $"type 0, order {orderValue}, {Count(books + 1, "book", "books")}";
    }

    // Section 7.2.2.
    private string Floor1(int index)
    {
        Field("Partitions", 5, v => NodeValue.Integer(v), Explain.Floor1Partitions, out int partitions);
        Open("Partition Classes", Explain.Floor1PartitionClasses);
        var classes = new int[partitions];
        int maxClass = -1;
        for (int j = 0; j < partitions; j++)
        {
            classes[j] = Read(4, "the partition classes");
            maxClass = Math.Max(maxClass, classes[j]);
        }

        Close(string.Join(", ", classes));
        var dimensions = new int[maxClass + 1];
        Open("Classes", Explain.Floor1Classes);
        for (int c = 0; c <= maxClass; c++)
        {
            Open($"Class {c}", Explain.Floor1Class);
            Field("Class Dimensions", 3, v => NodeValue.Integer(v + 1), Explain.Floor1ClassDimensions, out int dims);
            dimensions[c] = dims + 1;
            Field("Class Subclasses", 2, v => NodeValue.Integer(v), Explain.Floor1ClassSubclasses, out int subclasses);
            string master = string.Empty;
            if (subclasses > 0)
            {
                Node masterNode = Field("Class Masterbook", 8, v => NodeValue.Integer(v), Explain.Floor1Masterbook, out int masterbook);
                CheckBook(masterNode, masterbook, $"floor {index}");
                master = $", master book {masterbook}";
            }

            var books = new List<string>();
            for (int k = 0; k < 1 << subclasses; k++)
            {
                Node book = Field("Subclass Book", 8, v => v == 0 ? "0 (none)" : NodeValue.Integer(v - 1), Explain.Floor1SubclassBook, out int value);
                books.Add(value == 0 ? "none" : (value - 1).ToString(CultureInfo.InvariantCulture));
                if (value > 0)
                {
                    CheckBook(book, value - 1, $"floor {index}");
                }
            }

            Close($"{dims + 1} {(dims == 0 ? "dimension" : "dimensions")}, {Count(subclasses, "subclass bit", "subclass bits")}{master}, books {string.Join(", ", books)}");
        }

        Close(Count(maxClass + 1, "class", "classes"));
        Field("Multiplier", 2, v => NodeValue.Integer(v + 1), Explain.Floor1Multiplier, out _);
        Field("Range Bits", 4, v => NodeValue.Integer(v, "bits", "bit"), Explain.Floor1RangeBits, out int rangeBits);
        Open("X List", Explain.Floor1XList);
        var xs = new List<int> { 0, 1 << rangeBits };
        for (int j = 0; j < partitions; j++)
        {
            for (int k = 0; k < dimensions[classes[j]]; k++)
            {
                xs.Add(Read(rangeBits, "the X list"));
            }
        }

        Node xList = Close(Count(xs.Count - 2, "value", "values"));

        // libvorbis 1.3.7 (floor1.c, floor1_unpack) holds 63 values besides the two ends, and
        // refuses repeated ones, which would make segments of no length.
        if (xs.Count - 2 > 63)
        {
            Add(Severity.Warning, Invalid, xList, $"Floor {index} lists {xs.Count - 2} X values; libvorbis 1.3.7 refuses more than 63 (floor1.c).");
        }
        else if (xs.Distinct().Count() != xs.Count)
        {
            Add(Severity.Warning, Invalid, xList, $"Floor {index} lists an X value twice (the list includes 0 and 2^range bits); libvorbis 1.3.7 refuses the setup header (floor1.c).");
        }

        return $"type 1, {Count(partitions, "partition", "partitions")}, {Count(xs.Count, "X value", "X values")}";
    }

    // Section 8.6.1.
    private void Residues()
    {
        Open("Residues", Explain.Residues, Topics.VorbisSetupHeader);
        Field("Residue Count", 6, v => NodeValue.Integer(v + 1), Explain.ResidueCount, out int count);
        _residues = count + 1;
        var types = new SortedSet<int>();
        for (int i = 0; i < _residues; i++)
        {
            Open($"Residue {i}", Explain.Residue);
            Node typeNode = Field("Residue Type", 16, v => NodeValue.Integer(v), Explain.ResidueType, out int type);
            if (type > 2)
            {
                throw new SetupStop(Invalid, $"Residue {i} has the type {type}; the specification defines types 0 to 2 (section 4.2.4), so the setup header cannot be read further.", typeNode);
            }

            types.Add(type);
            Field("Begin", 24, v => NodeValue.Integer(v), Explain.ResidueBegin, out int begin);
            Field("End", 24, v => NodeValue.Integer(v), Explain.ResidueEnd, out int end);
            Field("Partition Size", 24, v => NodeValue.Integer(v + 1), Explain.ResiduePartitionSize, out int size);
            Field("Classifications", 6, v => NodeValue.Integer(v + 1), Explain.ResidueClassifications, out int classifications);
            Node classbookNode = Field("Classbook", 8, v => NodeValue.Integer(v), Explain.ResidueClassbook, out int classbook);
            if (CheckBook(classbookNode, classbook, $"residue {i}") is { } info)
            {
                // libvorbis 1.3.7 (res0.c, res0_unpack) refuses a classbook whose entries cannot
                // name every combination of classifications its dimensions code.
                long combinations = Power(classifications + 1, Math.Max(1, info.Dimensions));
                if (info.Dimensions < 1 || combinations > info.Entries)
                {
                    Add(Severity.Warning, Invalid, classbookNode,
                        $"Residue {i}'s classbook {classbook} has too few entries for {classifications + 1} classifications in {info.Dimensions} dimensions; libvorbis 1.3.7 refuses the setup header (res0.c).");
                }
            }

            Open("Cascade", Explain.ResidueCascade);
            var cascades = new int[classifications + 1];
            for (int j = 0; j <= classifications; j++)
            {
                int low = Read(3, "the residue cascade");
                int high = Read(1, "the residue cascade") == 1 ? Read(5, "the residue cascade") : 0;
                cascades[j] = (high << 3) | low;
            }

            Close(string.Join(", ", cascades.Select(c => $"0x{c:X2}")));
            Open("Books", Explain.ResidueBooks);
            int bookCount = 0;
            foreach (int cascade in cascades)
            {
                for (int pass = 0; pass < 8; pass++)
                {
                    if ((cascade & (1 << pass)) == 0)
                    {
                        continue;
                    }

                    Node book = Field("Book", 8, v => NodeValue.Integer(v), Explain.ResidueBook, out int value);
                    bookCount++;
                    if (CheckBook(book, value, $"residue {i}") is { LookupType: 0 })
                    {
                        Add(Severity.Warning, Invalid, book, $"Residue {i} uses codebook {value}, which has no value lookup; libvorbis 1.3.7 refuses the setup header (res0.c).");
                    }
                }
            }

            Close(Count(bookCount, "book", "books"));
            Close($"type {type}, coefficients {begin} to {end}, partitions of {size + 1}, {Count(classifications + 1, "classification", "classifications")}");
        }

        Close($"{Count(_residues, "residue", "residues")}, type {string.Join(" and ", types)}");
    }

    // Section 4.2.4, "Mappings".
    private void Mappings()
    {
        Open("Mappings", Explain.Mappings, Topics.VorbisSetupHeader);
        Field("Mapping Count", 6, v => NodeValue.Integer(v + 1), Explain.MappingCount, out int count);
        _mappings = count + 1;
        int totalSteps = 0;
        for (int i = 0; i < _mappings; i++)
        {
            Open($"Mapping {i}", Explain.Mapping);
            Node typeNode = Field("Mapping Type", 16, v => NodeValue.Integer(v), Explain.MappingType, out int type);
            if (type != 0)
            {
                throw new SetupStop(Invalid, $"Mapping {i} has the type {type}; the specification defines only type 0 (section 4.2.4), so the setup header cannot be read further.", typeNode);
            }

            Field("HasSubmaps", 1, v => NodeValue.Boolean(v == 1), Explain.HasSubmaps, out int hasSubmaps);
            int submaps = 1;
            if (hasSubmaps == 1)
            {
                Field("Submaps", 4, v => NodeValue.Integer(v + 1), Explain.Submaps, out int value);
                submaps = value + 1;
            }

            Field("HasCoupling", 1, v => NodeValue.Boolean(v == 1), Explain.HasCoupling, out int hasCoupling);
            int steps = 0;
            if (hasCoupling == 1)
            {
                if (_channels < 1)
                {
                    throw new SetupStop(Invalid, $"Mapping {i} couples channels of a stream that has none, so the setup header cannot be read further.", _lastNode);
                }

                Field("Coupling Step Count", 8, v => NodeValue.Integer(v + 1), Explain.CouplingStepCount, out int value);
                steps = value + 1;
                int width = ILog(_channels - 1);
                Open("Coupling Steps", Explain.CouplingSteps, Topics.SquarePolarCoupling);
                for (int s = 0; s < steps; s++)
                {
                    Open($"Coupling Step {s}", Explain.CouplingStep, Topics.SquarePolarCoupling);
                    Field("Magnitude Channel", width, v => NodeValue.Integer(v), Explain.MagnitudeChannel, out int magnitude);
                    Field("Angle Channel", width, v => NodeValue.Integer(v), Explain.AngleChannel, out int angle);
                    Node step = Close($"magnitude channel {magnitude}, angle channel {angle}");
                    if (magnitude == angle || magnitude >= _channels || angle >= _channels)
                    {
                        Add(Severity.Warning, Invalid, step,
                            $"Mapping {i} couples channel {magnitude} with channel {angle}, which the specification forbids for a stream of {_channels} channels (section 4.2.4); decoders refuse the setup header.");
                    }

                }

                Close(Count(steps, "step", "steps"));
            }

            totalSteps += steps;
            Node reserved = Field("Reserved", 2, v => NodeValue.Integer(v), Explain.MappingReserved, out int reservedValue);
            if (reservedValue != 0)
            {
                Add(Severity.Warning, Invalid, reserved, $"Mapping {i}'s reserved field is {reservedValue}, where the specification requires 0 (section 4.2.4); decoders refuse the setup header.");
            }

            if (submaps > 1)
            {
                Open("Channel Mux", Explain.ChannelMux);
                var mux = new List<int>();
                for (int c = 0; c < _channels; c++)
                {
                    Node muxNode = Field($"Channel {c}", 4, v => NodeValue.Integer(v), Explain.ChannelMuxEntry, out int value);
                    mux.Add(value);
                    if (value >= submaps)
                    {
                        Add(Severity.Warning, Invalid, muxNode, $"Mapping {i} assigns channel {c} to submap {value} of {submaps}; decoders refuse the setup header.");
                    }
                }

                Close(string.Join(", ", mux));
            }

            for (int s = 0; s < submaps; s++)
            {
                Open($"Submap {s}", Explain.Submap);
                Field("Time Configuration", 8, v => NodeValue.Integer(v), Explain.SubmapTime, out _);
                Node floorNode = Field("Floor", 8, v => NodeValue.Integer(v), Explain.SubmapFloor, out int floor);
                Node residueNode = Field("Residue", 8, v => NodeValue.Integer(v), Explain.SubmapResidue, out int residue);
                Close($"floor {floor}, residue {residue}");
                if (floor >= _floors)
                {
                    Add(Severity.Warning, Invalid, floorNode, $"Mapping {i} uses floor {floor}, but the setup header has {Count(_floors, "floor", "floors")}; decoders refuse it.");
                }

                if (residue >= _residues)
                {
                    Add(Severity.Warning, Invalid, residueNode, $"Mapping {i} uses residue {residue}, but the setup header has {Count(_residues, "residue", "residues")}; decoders refuse it.");
                }
            }

            Close($"{Count(submaps, "submap", "submaps")}, {(steps == 0 ? "no coupling" : Count(steps, "coupling step", "coupling steps"))}");
        }

        Close($"{Count(_mappings, "mapping", "mappings")}, {(totalSteps == 0 ? "no coupling" : Count(totalSteps, "coupling step", "coupling steps"))}");
    }

    // Section 4.2.4, "Modes".
    private void Modes()
    {
        Open("Modes", Explain.Modes, Topics.VorbisBlocks);
        Field("Mode Count", 6, v => NodeValue.Integer(v + 1), Explain.ModeCount, out int count);
        var modes = new bool[count + 1];
        for (int i = 0; i <= count; i++)
        {
            Open($"Mode {i}", Explain.Mode);
            Field("Block Flag", 1, v => v == 1 ? "1 (long block)" : "0 (short block)", Explain.BlockFlag, out int flag);
            Node window = Field("Window Type", 16, v => NodeValue.Integer(v), Explain.WindowType, out int windowType);
            Node transform = Field("Transform Type", 16, v => NodeValue.Integer(v), Explain.TransformType, out int transformType);
            Node mappingNode = Field("Mapping", 8, v => NodeValue.Integer(v), Explain.ModeMapping, out int mapping);
            modes[i] = flag == 1;
            Close($"{(flag == 1 ? "long" : "short")} block, mapping {mapping}");
            if (windowType != 0 || transformType != 0)
            {
                Add(Severity.Warning, Invalid, windowType != 0 ? window : transform,
                    $"Mode {i} has the window type {windowType} and transform type {transformType}, where the specification allows only 0 (section 4.2.4); decoders refuse the setup header.");
            }

            if (mapping >= _mappings)
            {
                Add(Severity.Warning, Invalid, mappingNode, $"Mode {i} uses mapping {mapping}, but the setup header has {Count(_mappings, "mapping", "mappings")}; decoders refuse it.");
            }
        }

        _modeIsLong = modes;
        int longModes = modes.Count(m => m);
        Close($"{Count(modes.Length, "mode", "modes")}: {longModes} long, {modes.Length - longModes} short");
    }

    private (int Dimensions, int Entries, int LookupType)? CheckBook(Node node, int book, string user)
    {
        if (book < _books.Count)
        {
            return _books[book];
        }

        Add(Severity.Warning, Invalid, node, $"The {user} uses codebook {book}, but the setup header has {Count(_books.Count, "codebook", "codebooks")}; decoders refuse it.");
        return null;
    }

    private void Add(Severity severity, string code, Node node, string message) => _problems.Add(new Problem(severity, code, message, node));

    // --- Reading --------------------------------------------------------------------------------

    private Node? _lastNode;

    private int Read(int count, string what)
    {
        if (_bits.Position + (long)count > _bits.Length)
        {
            throw new SetupStop("vorbis.setup-truncated",
                $"The setup header ends inside {what}; decoders refuse the stream, and its audio packets are only counted.", _open.Count > 0 ? null : _lastNode);
        }

        return (int)_bits.ReadLong(count);
    }

    private Node Field(string name, int count, Func<int, NodeValue> value, string description, out int read)
    {
        int start = _bits.Position;
        read = Read(count, ToLower(name));
        Node node = Node.FromBits(name, _packet, start, _bits.Position, value(read), description: description, order: BitOrder.LeastSignificantFirst);
        Place(node);
        _lastNode = node;
        return node;
    }

    // float32_unpack (section 9.2.2): a 21-bit mantissa, a sign and a 10-bit exponent biased by 788.
    private void Field32(string name, string description)
    {
        int start = _bits.Position;
        if (_bits.Position + 32L > _bits.Length)
        {
            Read(32, ToLower(name));
        }

        long raw = _bits.ReadLong(32);
        double mantissa = raw & 0x1FFFFF;
        if ((raw & 0x80000000) != 0)
        {
            mantissa = -mantissa;
        }

        int exponent = (int)((raw & 0x7FE00000) >> 21);
        double value = mantissa * Math.Pow(2, exponent - 788);
        Node node = Node.FromBits(name, _packet, start, _bits.Position, NodeValue.Decimal(value, 6), description: description, order: BitOrder.LeastSignificantFirst);
        Place(node);
        _lastNode = node;
    }

    private void Skip(string name, long count, string value, string description, string what)
    {
        long start = _bits.Position;
        if (start + count > _bits.Length)
        {
            Node cut = Node.FromBits(name, _packet, start, _bits.Length, $"{value}, cut short", description: description, order: BitOrder.LeastSignificantFirst);
            Place(cut);

            // The groups the walk closes end where the packet does, so they hold this node.
            while (_bits.Position < _bits.Length)
            {
                _bits.ReadLong(Math.Min(64, _bits.Length - _bits.Position));
            }

            throw new SetupStop("vorbis.setup-truncated", $"The setup header ends inside {what}; decoders refuse the stream, and its audio packets are only counted.", cut);
        }

        for (long left = count; left > 0; left -= Math.Min(left, 64))
        {
            _bits.ReadLong((int)Math.Min(left, 64));
        }

        Node node = Node.FromBits(name, _packet, start, _bits.Position, value, description: description, order: BitOrder.LeastSignificantFirst);
        Place(node);
        _lastNode = node;
    }

    private void Place(Node node)
    {
        if (_open.TryPeek(out Group? group))
        {
            group.Children.Add(node);
        }
        else
        {
            _top.Add(node);
        }
    }

    private void Open(string name, string description, string? topic = null) => _open.Push(new Group(name, _bits.Position, description, topic));

    private Node Close(NodeValue value)
    {
        Group group = _open.Pop();
        int end = Math.Min(_bits.Position, _bits.Length);
        Node node = Node.FromBits(group.Name, _packet, group.Start, Math.Max(group.Start, end), value, description: group.Description, topic: group.Topic,
            order: BitOrder.LeastSignificantFirst);
        foreach (Node child in group.Children)
        {
            node.Add(child);
        }

        Place(node);
        _lastNode = node;
        return node;
    }

    // "IsOrdered" reads as "the ordered flag", "Dimensions" as "the dimensions field".
    private static string ToLower(string name) =>
        name.StartsWith("Is", StringComparison.Ordinal) ? $"the {name[2..].ToLowerInvariant()} flag"
        : name.StartsWith("Has", StringComparison.Ordinal) ? $"the {name[3..].ToLowerInvariant()} flag"
        : $"the {name.ToLowerInvariant()} field";

    private static string Count(long count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";

    private sealed record Group(string Name, long Start, string Description, string? Topic)
    {
        public List<Node> Children { get; } = [];
    }

    // Ends the walk: the structure cannot be followed further.
    private sealed class SetupStop(string code, string message, Node? node) : Exception(message)
    {
        public string Code { get; } = code;

        public Node? Node { get; } = node;
    }
}
