namespace Encspect.Core;

// The concept a graph shows, which front ends group by in place of names: the GUI lists the series of a stream under one
// heading per concept, whatever the format. None for a series that belongs to no shared concept (modes, frame sizes).
public enum GraphConcept
{
    None,
    StereoCoding,
    BlockSwitching,
    CodedBandwidth,
    BitDistribution,
}

// One category a series' values take, such as a Layer III block type. Name is written as the tally entry for it is named
// ("Mid/side stereo", "Short"), Description as a node's (constant, citing the specification), Topic as a node's.
public sealed record Category(string Name, string Description)
{
    public string? Topic { get; init; }

    // Whether the category marks a value the specification does not allow (a reserved block type): front ends draw it apart from the
    // valid ones, which they cannot tell from its name.
    public bool IsReserved { get; init; }
}

// The intervals of audio time a parser counts its per-second series over: the start of each in seconds, shared with
// that series' x values, and the index of the first unit that starts in it. Starts and FirstUnits have the same length;
// FirstUnits is nondecreasing and within 0 to the series' Count.
public sealed record TimeIndex(ReadOnlyMemory<double> Starts, ReadOnlyMemory<int> FirstUnits);

// One category per value and unit over a stream, for graphs and for the summary over the stream. Immutable once built,
// so the writers and the GUI read it on any thread; built by CategorySeriesBuilder, or as a grouping by Group.
public sealed class CategorySeries
{
    // The code of a value without a category. Codes 0 to 254 are categories.
    public const byte None = 255;

    public const int MaxCategories = 255;

    // Coded values of one unit, and of one series; further values are counted without a code.
    public const int MaxValuesPerUnit = 254;

    public const int MaxCodedValues = 1 << 24;

    // A unit's index entry covers this many units: the position of the first code of every 1,024th unit of a variable layout.
    internal const int IndexInterval = 1024;

    private readonly Storage? _storage;
    private readonly long[] _counts;
    private readonly byte[]? _map;

    internal CategorySeries(
        string name,
        GraphConcept concept,
        IReadOnlyList<Category> categories,
        bool isOrdered,
        string valueLabel,
        int valuesPerUnit,
        IReadOnlyList<string> valueNames,
        ReadOnlyMemory<double> x,
        Series? xSeries,
        int count,
        Storage? storage,
        long[] counts,
        long uncodedValues,
        TimeIndex? time,
        Node? anchor,
        CategorySeries? source,
        byte[]? map,
        string description,
        string? topic)
    {
        Name = name;
        Concept = concept;
        Categories = categories;
        IsOrdered = isOrdered;
        ValueLabel = valueLabel;
        ValuesPerUnit = valuesPerUnit;
        ValueNames = valueNames;
        X = x;
        XSeries = xSeries;
        Count = count;
        _storage = storage;
        _counts = counts;
        Counts = Array.AsReadOnly(counts);
        UncodedValues = uncodedValues;
        Time = time;
        Anchor = anchor;
        Source = source;
        _map = map;
        Description = description;
        Topic = topic;
        Total = counts.Sum();
    }

    // Named as the tally node it is anchored to.
    public string Name { get; }

    // Which shared heading the GUI lists it under.
    public GraphConcept Concept { get; }

    // In the order the parser gives; index 0 is the plain coding (long blocks, left/right stereo).
    public IReadOnlyList<Category> Categories { get; }

    // Whether the categories are ordered, such as frame sizes or submodes; the GUI then draws a ramp of one hue.
    public bool IsOrdered { get; }

    // What one value is, such as "granule and channel", "block", "channel stream".
    public string ValueLabel { get; }

    // Values per unit when every unit has the same number, else 0.
    public int ValuesPerUnit { get; }

    // For a fixed layout, what each value is, such as "granule 1, channel 2"; empty otherwise.
    public IReadOnlyList<string> ValueNames { get; }

    // The byte offset of each unit.
    public ReadOnlyMemory<double> X { get; }

    // The per-frame numeric series of the stream whose x this series shares, or null; the JSON names it.
    public Series? XSeries { get; }

    // Units, the ones without coded values included.
    public int Count { get; }

    // Per category, the weighted values over the whole series, uncoded ones included, and their sum: kept as the
    // series is built, so a series equals its tally even past the caps.
    public IReadOnlyList<long> Counts { get; }

    public long Total { get; }

    // Values with a category counted without a code, past MaxValuesPerUnit of a unit or MaxCodedValues of the series.
    public long UncodedValues { get; }

    public TimeIndex? Time { get; }

    // The tally node whose entries are these categories; the text tree prints the per-second shares after it.
    public Node? Anchor { get; }

    // For a grouping, the series it groups; otherwise null.
    public CategorySeries? Source { get; }

    public string Description { get; }

    public string? Topic { get; }

    // For a grouping, the new category of each source code (255 for none); null otherwise.
    public ReadOnlySpan<byte> Map => _map;

    // Whether any value stands for other than one thing of the tally.
    public bool HasWeights => Source is not null ? Source.HasWeights : _storage!.Weights is not null;

    // The bytes this series keeps per unit and value: the codes, the count bytes of a variable layout, the weights and the
    // sparse index. A grouping keeps its map only. The x values are the stream's and not counted.
    public long StorageBytes => _storage is null ? _map!.Length : _storage.Bytes;

    // The coded values of a unit (a lookup by a sparse index of one int per 1,024 units for a variable layout); 0 for a
    // unit past the caps.
    public int ValueCount(int unit)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)unit, (uint)Count, nameof(unit));
        return RootStorage.ValueCount(unit);
    }

    // The code of one value of one unit: an index into Categories, or None.
    public byte this[int unit, int value]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)unit, (uint)Count, nameof(unit));
            if (_storage is null)
            {
                byte code = Source![unit, value];
                return code == None ? None : _map![code];
            }

            return _storage.Code(unit, value);
        }
    }

    // What the value stands for in the counts: 1 unless the parser gave weights.
    public int WeightAt(int unit, int value)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)unit, (uint)Count, nameof(unit));
        return RootStorage.Weight(unit, value);
    }

    // Adds the weighted values of the units first to end - 1 to counts, one entry per category: the per-second shares and
    // the GUI's pixel columns use it. Only coded values are counted; the caller clears counts.
    public void CountRange(int first, int end, Span<long> counts) => CountRange(first, end, -1, counts);

    // The same for one value of each unit (a fixed layout's lane).
    public void CountRange(int first, int end, int value, Span<long> counts)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, Count);
        ArgumentOutOfRangeException.ThrowIfLessThan(end, first);
        ArgumentOutOfRangeException.ThrowIfLessThan(value, -1);
        ArgumentOutOfRangeException.ThrowIfLessThan(counts.Length, Categories.Count);

        if (_storage is not null)
        {
            _storage.CountRange(first, end, value, counts);
            return;
        }

        Span<long> own = stackalloc long[Source!.Categories.Count];
        Source.CountRange(first, end, value, own);
        for (int code = 0; code < own.Length; code++)
        {
            if (_map![code] != None)
            {
                counts[_map[code]] += own[code];
            }
        }
    }

    // Reads units one after another, in time proportional to their values.
    public UnitReader Reader(int firstUnit = 0)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)firstUnit, (uint)Count, nameof(firstUnit));
        return new UnitReader(this, firstUnit);
    }

    // A grouping of this series: each code maps to a category of the new series (map has one entry per category here).
    // It shares this series' storage and costs the map, not a byte per value; Counts are the sums of this series'. A grouping
    // inherits its source's Concept, so "Block Sizes" sits under "Block Switching" with "Block Usage" without a parameter.
    public CategorySeries Group(string name, IReadOnlyList<Category> categories, ReadOnlySpan<byte> map, Node? anchor, string description, string? topic = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        CategorySeriesBuilder.CheckCategories(categories);
        ArgumentNullException.ThrowIfNull(description);
        if (map.Length != Categories.Count)
        {
            throw new ArgumentException("A grouping needs one entry for each category of its source.", nameof(map));
        }

        long[] counts = new long[categories.Count];
        for (int code = 0; code < map.Length; code++)
        {
            if (map[code] != None)
            {
                if (map[code] >= categories.Count)
                {
                    throw new ArgumentException("A grouping maps a category past its own.", nameof(map));
                }

                counts[map[code]] += _counts[code];
            }
        }

        return new CategorySeries(name, Concept, categories, false, ValueLabel, ValuesPerUnit, ValueNames, X, XSeries, Count, null, counts, UncodedValues, Time, anchor, this, map.ToArray(), description, topic);
    }

    private CategorySeries Root => Source is null ? this : Source.Root;

    private Storage RootStorage => Root._storage!;

    // Turns codes of the root series into this series' codes, through the maps of every grouping on the way.
    private void Translate(Span<byte> codes)
    {
        if (Source is null)
        {
            return;
        }

        Source.Translate(codes);
        for (int i = 0; i < codes.Length; i++)
        {
            codes[i] = codes[i] == None ? None : _map![codes[i]];
        }
    }

    // The codes, the count bytes of a variable layout, the weights and the sparse index of one built series.
    internal sealed class Storage(int valuesPerUnit, ChunkedList<byte> codes, ChunkedList<byte>? counts, ChunkedList<byte>? weights, int[] index, int codedUnits, long codedValues)
    {
        private readonly ChunkedList<byte> _codes = codes;
        private readonly ChunkedList<byte>? _counts = counts;
        private readonly int[] _index = index;
        private readonly int _codedUnits = codedUnits;

        public ChunkedList<byte>? Weights { get; } = weights;

        public long Bytes => codedValues + (_counts?.Count ?? 0) + (Weights?.Count ?? 0) + ((long)_index.Length * sizeof(int));

        public int ValueCount(int unit) => unit >= _codedUnits ? 0 : valuesPerUnit > 0 ? valuesPerUnit : _counts![unit];

        // The position in the codes of a unit's first value.
        public long Start(int unit)
        {
            if (unit >= _codedUnits)
            {
                return codedValues;
            }

            if (valuesPerUnit > 0)
            {
                return (long)unit * valuesPerUnit;
            }

            int block = unit / IndexInterval;
            long position = _index[block];
            for (int at = block * IndexInterval; at < unit; at++)
            {
                position += _counts![at];
            }

            return position;
        }

        public byte Code(int unit, int value)
        {
            if (value < 0 || value >= ValueCount(unit))
            {
                return None;
            }

            return _codes[(int)(Start(unit) + value)];
        }

        public int Weight(int unit, int value)
        {
            if (Weights is null || value < 0 || value >= ValueCount(unit))
            {
                return 1;
            }

            return Weights[(int)(Start(unit) + value)];
        }

        public void CountRange(int first, int end, int lane, Span<long> counts)
        {
            end = Math.Min(end, _codedUnits);
            if (first >= end)
            {
                return;
            }

            long position = Start(first);
            for (int unit = first; unit < end; unit++)
            {
                int n = ValueCount(unit);
                if (lane < 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        Add(counts, position + i);
                    }
                }
                else if (lane < n)
                {
                    Add(counts, position + lane);
                }

                position += n;
            }
        }

        public int Read(int unit, long position, Span<byte> codes, Span<byte> weights)
        {
            int n = ValueCount(unit);
            for (int i = 0; i < n; i++)
            {
                codes[i] = _codes[(int)(position + i)];
                if (!weights.IsEmpty)
                {
                    weights[i] = Weights is null ? (byte)1 : Weights[(int)(position + i)];
                }
            }

            return n;
        }

        private void Add(Span<long> counts, long position)
        {
            byte code = _codes[(int)position];
            if (code != None)
            {
                counts[code] += Weights is null ? 1 : Weights[(int)position];
            }
        }
    }

    // A read position: the units one after another.
    public struct UnitReader
    {
        private readonly CategorySeries _series;
        private int _unit;
        private long _position;

        internal UnitReader(CategorySeries series, int unit)
        {
            _series = series;
            _unit = unit;
            _position = unit < series.Count ? series.RootStorage.Start(unit) : 0;
        }

        // The unit the next call reads.
        public readonly int Unit => _unit;

        // Reads the next unit's coded values into codes (at least MaxValuesPerUnit long) and, when not empty, their weights;
        // returns their number, or -1 after the last unit.
        public int Next(Span<byte> codes, Span<byte> weights = default)
        {
            if (_unit >= _series.Count)
            {
                return -1;
            }

            int n = _series.RootStorage.Read(_unit, _position, codes, weights);
            _series.Translate(codes[..n]);

            _position += n;
            _unit++;
            return n;
        }
    }
}
