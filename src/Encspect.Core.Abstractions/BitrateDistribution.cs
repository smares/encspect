using System.Globalization;

namespace Encspect.Core;

// The tally of "Bitrate Distribution" and "Bitrate Mode" that the formats with units of varying size share: each unit's bitrate counted into
// ranges of 32 kbps, the last open-ended. A parser adds the bitrate of each unit it counts in its bitrate statistics and calls AddTo
// after "Maximum Bitrate". Memory is bounded by the number of ranges (at most 1,001), whatever the length of the stream.
public sealed class BitrateDistribution
{
    private const int BinWidth = 32;
    private const int LastBin = 1000;

    private readonly SortedDictionary<int, int> _bins = [];

    public const string ModeDescription = "Whether the units all have the same bitrate: variable where they differ, constant where they do not, and unknown for a single unit, since one unit shows no variation.";
    public const string DistributionDescription = "How many units fall in each bitrate range of 32 kbps.";
    public const string BinDescription = "The units whose bitrate falls in this range, and their share.";

    public long Units { get; private set; }

    public double Minimum { get; private set; } = double.MaxValue;

    public double Maximum { get; private set; }

    public void Add(double kbps, long count = 1)
    {
        Units += count;
        Minimum = Math.Min(Minimum, kbps);
        Maximum = Math.Max(Maximum, kbps);
        int bin = (int)Math.Min(LastBin, Math.Floor(kbps / BinWidth));
        _bins[bin] = _bins.GetValueOrDefault(bin) + (int)count;
    }

    // Adds "Bitrate Mode" and "Bitrate Distribution" to statistics, none without a unit; cover makes a node over the stream's bytes, with its
    // description. unitName and unitPlural are the format's unit nouns ("Frame", "Frames").
    public void AddTo(Node statistics, Func<string, NodeValue?, string, Node> cover, string unitName, string unitPlural, string? topic = null)
    {
        if (Units == 0)
        {
            return;
        }

        statistics.Add(cover("Bitrate Mode", Units == 1 ? "unknown" : Maximum > Minimum ? "variable" : "constant", ModeDescription));
        Node distribution = statistics.Add(cover("Bitrate Distribution", null, DistributionDescription));
        foreach ((int bin, int count) in _bins)
        {
            string range = bin == LastBin ? $"{bin * BinWidth} kbps and more" : $"{bin * BinWidth} to {(bin * BinWidth) + BinWidth - 1} kbps";
            string units = count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? unitName.ToLowerInvariant() : unitPlural.ToLowerInvariant());
            string share = (Math.Round(count * 1000.0 / Units, MidpointRounding.ToEven) / 10).ToString("F1", CultureInfo.InvariantCulture) + "%";
            distribution.Add(cover(range, $"{units} ({share})", BinDescription));
        }
    }
}
