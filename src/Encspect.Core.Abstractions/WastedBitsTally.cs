using System.Globalization;

namespace Encspect.Core;

// What FLAC's subframes and WavPack's blocks state about the low bits that are zero in every sample of the unit, counted as the parsers read them:
// the distribution of the counts over the units, the changes between consecutive units of a channel, and the positions at which the changes fall. It
// reads no sample. The same statistic in every format that has a count per unit (design: docs/design/lossywav-detection.md).
public sealed class WastedBitsTally
{
    // The most channels whose previous count is kept; a unit of a channel past it is in the distribution only.
    public const int MaxChannels = 64;

    private readonly long[] _distribution = new long[256];
    private readonly int[] _previous = new int[MaxChannels];
    private long _firstChange;
    private long _gridOfChanges;
    private bool _hasChange;
    private bool _hasUnit;

    // The units counted, and those with a count above zero.
    public long Units { get; private set; }

    public long UnitsWithWastedBits => Units - _distribution[0];

    // The changes between a unit and the previous unit of the same channel.
    public long Changes { get; private set; }

    public long CountOf(int bits) => (uint)bits < _distribution.Length ? _distribution[bits] : 0;

    // Counts one unit of a channel that starts at position (in samples) and states bits wasted bits. Units of the several channels of one frame or
    // block set share their position; positions are those of the stream, in samples.
    public void Add(int channel, long position, int bits)
    {
        bits = Math.Clamp(bits, 0, _distribution.Length - 1);
        if (!_hasUnit)
        {
            _hasUnit = true;
            for (int i = 0; i < _previous.Length; i++)
            {
                _previous[i] = -1;
            }
        }

        Units++;
        _distribution[bits]++;
        if ((uint)channel >= MaxChannels)
        {
            return;
        }

        int previous = _previous[channel];
        _previous[channel] = bits;
        if (previous >= 0 && previous != bits)
        {
            Changes++;
            if (!_hasChange)
            {
                _hasChange = true;
                _firstChange = position;
            }
            else
            {
                _gridOfChanges = Gcd(_gridOfChanges, Math.Abs(position - _firstChange));
            }
        }
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    // lossyWAV's block length at the sample rates it was tested at (22.05, 32, 44.1, 48, 88.2, 96, 176.4 and 192 kHz), or 0 at any other.
    public static int LossyWavBlock(int sampleRate) => sampleRate switch
    {
        22050 or 32000 => 256,
        44100 or 48000 => 512,
        88200 or 96000 => 1024,
        176400 or 192000 => 2048,
        _ => 0,
    };

    // The smallest number of changes that can make the pattern; a convention of this analysis, set from the fixtures (design: From behavior).
    public const int MinimumChanges = 20;

    // The pattern lossyWAV's output shows in the coded counts, or null where the stream lacks it: a tested rate, at least 20 changes, the greatest common
    // divisor of the distances between the changes' positions equal to lossyWAV's block at that rate (so every change lies on its grid, and not all on a
    // coarser one, as a tool with longer blocks would put them), and more than half of the units above the stream's smallest count.
    public LossyWavPattern? Pattern(int sampleRate)
    {
        int block = LossyWavBlock(sampleRate);
        if (block == 0 || Changes < MinimumChanges || _gridOfChanges != block)
        {
            return null;
        }

        int smallest = 0;
        while (_distribution[smallest] == 0)
        {
            smallest++;
        }

        long above = Units - _distribution[smallest];
        return above * 2 > Units ? new LossyWavPattern(Changes, block, above, Units) : null;
    }

    // The statistic: the node with an entry for each count that occurred and the changes, or null where no unit has a wasted bit (a stream without
    // any has nothing to distribute). cover makes a node over the stream's bytes, with its description.
    public Node? AddTo(Node statistics, Func<string, NodeValue?, string, string?, Node> cover, WastedBitsWording wording, string? topic)
    {
        if (UnitsWithWastedBits == 0)
        {
            return null;
        }

        Node node = statistics.Add(cover("Wasted Bits", NodeValue.Integer(Units, wording.Plural, wording.Singular), wording.Statistic, topic));
        for (int bits = 0; bits < _distribution.Length; bits++)
        {
            long count = _distribution[bits];
            if (count > 0)
            {
                node.Add(cover($"{bits} {(bits == 1 ? "Bit" : "Bits")}", $"{Counted(count, wording)} ({Share(count, Units)})", wording.Count, topic));
            }
        }

        node.Add(cover("Changes", NodeValue.Integer(Changes, "changes", "change"), wording.ChangesDescription, topic));
        return node;
    }

    private static string Counted(long count, WastedBitsWording wording) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? wording.Singular : wording.Plural);

    // A share in percent with one decimal, rounded half to even.
    public static string Share(long count, long total) =>
        total > 0 ? (Math.Round(count * 1000.0 / total, MidpointRounding.ToEven) / 10).ToString("F1", CultureInfo.InvariantCulture) + "%" : "0.0%";
}

// The unit a format counts wasted bits in and the descriptions its parser gives the statistic's nodes, which are constants of the format.
public sealed record WastedBitsWording(string Singular, string Plural, string Statistic, string Count, string ChangesDescription);

// What the counts show of lossyWAV's pattern: the changes, the grid's length in samples that their positions share, and the units above the stream's
// smallest count.
public sealed record LossyWavPattern(long Changes, int Grid, long Above, long Units)
{
    public string Evidence(string units) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Changes} changes of the wasted bits between consecutive {units} of a channel, all on a grid of {Grid} samples (the greatest common divisor of the distances between them), and {WastedBitsTally.Share(Above, Units)} of the {units} above the stream's smallest count");
}
