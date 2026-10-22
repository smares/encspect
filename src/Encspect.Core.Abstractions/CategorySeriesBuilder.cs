namespace Encspect.Core;

// Records a category series while a parser reads or derives its units, in chunks, so nothing is copied when it is built
// and nothing is kept per unit beyond the codes, the count byte of a variable layout and the weights. A builder belongs to
// one stream of one analysis, as a tally does, and is used once: Build hands its chunks to the series.
public sealed class CategorySeriesBuilder
{
    private readonly string _name;
    private readonly IReadOnlyList<Category> _categories;
    private readonly string _valueLabel;
    private readonly int _valuesPerUnit;
    private readonly IReadOnlyList<string> _valueNames;
    private readonly bool _isOrdered;
    private readonly GraphConcept _concept;

    // Weighted values by code, uncoded ones included; slot 255 holds the values without a category.
    private readonly long[] _counts = new long[256];
    private readonly ChunkedList<byte> _codes = new();
    private readonly ChunkedList<byte>? _unitCounts;
    private readonly List<int> _index = [];
    private ChunkedList<byte>? _weights;
    private int _codedUnits;
    private long _uncoded;
    private bool _isBuilt;

    // valuesPerUnit 0 means a variable layout.
    public CategorySeriesBuilder(string name, IReadOnlyList<Category> categories, string valueLabel, int valuesPerUnit = 0, IReadOnlyList<string>? valueNames = null, bool isOrdered = false, GraphConcept concept = GraphConcept.None)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        CheckCategories(categories);
        ArgumentNullException.ThrowIfNull(valueLabel);
        ArgumentOutOfRangeException.ThrowIfNegative(valuesPerUnit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(valuesPerUnit, CategorySeries.MaxValuesPerUnit);
        if (valueNames is { Count: > 0 } && valueNames.Count != valuesPerUnit)
        {
            throw new ArgumentException("A fixed layout names each of its values, or none.", nameof(valueNames));
        }

        _name = name;
        _categories = categories;
        _valueLabel = valueLabel;
        _valuesPerUnit = valuesPerUnit;
        _valueNames = valueNames ?? [];
        _isOrdered = isOrdered;
        _concept = concept;
        _unitCounts = valuesPerUnit == 0 ? new ChunkedList<byte>() : null;
    }

    // Units added, the ones without coded values included.
    public int Count { get; private set; }

    // One unit of one value.
    public void Add(byte code, byte weight = 1)
    {
        ReadOnlySpan<byte> codes = [code];
        ReadOnlySpan<byte> weights = [weight];
        Add(codes, weights);
    }

    // One unit; codes.Length equals valuesPerUnit for a fixed layout, else is any length, and weights are empty or of the
    // same length. Values past the caps are counted without a code.
    public void Add(ReadOnlySpan<byte> codes, ReadOnlySpan<byte> weights = default)
    {
        if (_isBuilt)
        {
            throw new InvalidOperationException("The builder has built its series.");
        }

        if (_valuesPerUnit > 0 && codes.Length != _valuesPerUnit)
        {
            throw new ArgumentException($"A unit of this series has {_valuesPerUnit} values.", nameof(codes));
        }

        if (!weights.IsEmpty && weights.Length != codes.Length)
        {
            throw new ArgumentException("Weights are empty or one for each value.", nameof(weights));
        }

        if (Count == int.MaxValue)
        {
            throw new InvalidOperationException("A series holds at most int.MaxValue units.");
        }

        // Everything is checked before a count changes, so a refused unit leaves the builder as it was.
        bool anyWeight = false;
        if (!weights.IsEmpty)
        {
            foreach (byte weight in weights)
            {
                if (weight == 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(weights), "A weight is at least 1.");
                }

                anyWeight |= weight != 1;
            }
        }

        for (int i = 0; i < codes.Length; i++)
        {
            _counts[codes[i]] += weights.IsEmpty ? 1 : weights[i];
        }

        int room = CategorySeries.MaxCodedValues - _codes.Count;
        int stored;
        if (_valuesPerUnit > 0)
        {
            stored = codes.Length <= room ? codes.Length : 0;
        }
        else
        {
            stored = room <= 0 ? -1 : Math.Min(Math.Min(codes.Length, CategorySeries.MaxValuesPerUnit), room);
        }

        if (stored >= 0 && (_valuesPerUnit == 0 || stored > 0))
        {
            if (_valuesPerUnit == 0 && _codedUnits % CategorySeries.IndexInterval == 0)
            {
                _index.Add(_codes.Count);
            }

            if (anyWeight && _weights is null)
            {
                _weights = new ChunkedList<byte>();
                for (int i = 0; i < _codes.Count; i++)
                {
                    _weights.Add((byte)1);
                }
            }

            for (int i = 0; i < stored; i++)
            {
                _codes.Add(codes[i]);
                _weights?.Add(weights.IsEmpty ? (byte)1 : weights[i]);
            }

            _unitCounts?.Add((byte)stored);
            _codedUnits++;
        }

        for (int i = Math.Max(stored, 0); i < codes.Length; i++)
        {
            _uncoded += codes[i] == CategorySeries.None ? 0 : 1;
        }

        Count++;
    }

    // x must hold Count values. Throws ArgumentException on a code past the categories, a mismatched x or a TimeIndex
    // that is not nondecreasing within 0 to Count: a parser bug, which the host reports as parse.failed.
    public CategorySeries Build(ReadOnlyMemory<double> x, Series? xSeries, Node? anchor, string description, string? topic = null, TimeIndex? time = null)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (_isBuilt)
        {
            throw new InvalidOperationException("The builder has built its series.");
        }

        if (x.Length != Count)
        {
            throw new ArgumentException("A series needs one x value per unit.", nameof(x));
        }

        if (xSeries is not null && !x.Equals(xSeries.X) && !x.Span.SequenceEqual(xSeries.X.Span))
        {
            throw new ArgumentException("A series that names an x series shares its x values.", nameof(xSeries));
        }

        for (int code = _categories.Count; code < CategorySeries.None; code++)
        {
            if (_counts[code] != 0)
            {
                throw new ArgumentException($"A value has code {code}, past the {_categories.Count} categories.");
            }
        }

        if (time is not null)
        {
            CheckTime(time);
        }

        _isBuilt = true;
        long[] counts = _counts[.._categories.Count];
        int[] index = _unitCounts is null ? [] : [.. _index];
        var storage = new CategorySeries.Storage(_valuesPerUnit, _codes, _unitCounts, _weights, index, _codedUnits, _codes.Count);
        return new CategorySeries(_name, _concept, _categories, _isOrdered, _valueLabel, _valuesPerUnit, _valueNames, x, xSeries, Count, storage, counts, _uncoded, time, anchor, null, null, description, topic);
    }

    internal static void CheckCategories(IReadOnlyList<Category> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        if (categories.Count is 0 or > CategorySeries.MaxCategories)
        {
            throw new ArgumentException($"A series has 1 to {CategorySeries.MaxCategories} categories.", nameof(categories));
        }

        foreach (Category category in categories)
        {
            ArgumentNullException.ThrowIfNull(category);
            ArgumentException.ThrowIfNullOrEmpty(category.Name);
        }
    }

    private void CheckTime(TimeIndex time)
    {
        ReadOnlySpan<int> first = time.FirstUnits.Span;
        if (time.Starts.Length != first.Length)
        {
            throw new ArgumentException("A time index has a start for each first unit.", nameof(time));
        }

        double previousStart = double.NegativeInfinity;
        foreach (double start in time.Starts.Span)
        {
            if (!double.IsFinite(start) || start < previousStart)
            {
                throw new ArgumentException("The starts of a time index are finite and nondecreasing.", nameof(time));
            }

            previousStart = start;
        }

        int previous = 0;
        foreach (int unit in first)
        {
            if (unit < previous || unit > Count)
            {
                throw new ArgumentException("The first units of a time index are nondecreasing, within 0 to the number of units.", nameof(time));
            }

            previous = unit;
        }
    }
}
