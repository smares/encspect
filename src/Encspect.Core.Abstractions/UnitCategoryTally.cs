using System.Globalization;

namespace Encspect.Core;

// A category series and the tally node it is held equal to, for a format whose units each have one value (a frame's channel assignment): a parser
// calls Add once for each unit of its per-unit series, in the order of the units, AddTo to make the tally and Build to make the series. Memory is
// the series' (one code per unit) and one counter for each category.
public sealed class UnitCategoryTally(string name, IReadOnlyList<Category> categories, string valueLabel, GraphConcept concept, bool isVariable = false)
{
    private readonly CategorySeriesBuilder _builder = new(name, categories, valueLabel, isVariable ? 0 : 1, concept: concept);
    private readonly long[] _counts = new long[categories.Count];

    // The units counted with a category.
    public long Total { get; private set; }

    public int Units => _builder.Count;

    // One unit; null for a unit without a value (a mono frame).
    public void Add(int? code)
    {
        if (code is { } value)
        {
            _counts[value]++;
            Total++;
            _builder.Add((byte)value);
        }
        else
        {
            _builder.Add(CategorySeries.None);
        }
    }

    // One unit of a variable layout: any number of values, none for a unit without any.
    public void AddAll(ReadOnlySpan<byte> codes)
    {
        foreach (byte code in codes)
        {
            _counts[code]++;
        }

        Total += codes.Length;
        _builder.Add(codes);
    }

    // The tally node, with an entry for each category that occurred; null when no unit had a value. cover makes a node over the stream's bytes,
    // with its description.
    public Node? AddTo(Node statistics, Func<string, NodeValue?, string, string?, Node> cover, string description, string? topic, string unitName, string unitPlural)
    {
        if (Total == 0)
        {
            return null;
        }

        Node node = statistics.Add(cover(name, NodeValue.Integer(Total, unitPlural.ToLowerInvariant(), unitName.ToLowerInvariant()), description, topic));
        for (int code = 0; code < categories.Count; code++)
        {
            long count = _counts[code];
            if (count > 0)
            {
                string units = count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? unitName.ToLowerInvariant() : unitPlural.ToLowerInvariant());
                string share = (Math.Round(count * 1000.0 / Total, MidpointRounding.ToEven) / 10).ToString("F1", CultureInfo.InvariantCulture) + "%";
                node.Add(cover(categories[code].Name, $"{units} ({share})", categories[code].Description, null));
            }
        }

        return node;
    }

    public CategorySeries? Build(Series bitrate, Node anchor, string description, string? topic, TimeIndex? time)
    {
        if (Total == 0 || _builder.Count != bitrate.X.Length)
        {
            return null;
        }

        return _builder.Build(bitrate.X, bitrate, anchor, description, topic, time);
    }
}
