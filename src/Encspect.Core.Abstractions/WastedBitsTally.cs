using System.Globalization;

namespace Encspect.Core;

// What FLAC's subframes and WavPack's blocks state about the low bits that are zero in every sample of the unit, counted as the parsers read them:
// the distribution of the counts over the units, the changes between consecutive units of a channel, and the positions at which the changes fall. It
// reads no sample. The same statistic in every format that has a count per unit (design: docs/design/lossywav-detection.md).
public sealed class WastedBitsTally
{
    // The most channels whose previous count is kept; a unit of a channel past it is in the distribution only.
    public const int MaxChannels = 64;

    // lossyWAV's longest block (2,048 samples at 176.4 and 192 kHz) is a multiple of every other, so a phase folded to it folds to each of them.
    private const int PhaseLength = 2048;

    private readonly long[] _distribution = new long[256];
    private readonly int[] _previous = new int[MaxChannels];
    private long[]? _phases;
    private long _firstPosition;
    private long _lastPosition;
    private long _step;
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
            _firstPosition = position;
            _lastPosition = position;
            for (int i = 0; i < _previous.Length; i++)
            {
                _previous[i] = -1;
            }
        }
        else if (position != _lastPosition)
        {
            _lastPosition = position;
            _step = Gcd(_step, Math.Abs(position - _firstPosition));
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
            _phases ??= new long[PhaseLength];
            _phases[(int)(((position - _firstPosition) % PhaseLength + PhaseLength) % PhaseLength)]++;
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

    // The smallest number of changes that can make the pattern, and the shares: 95% of the changes at one phase, more than half of the units above the
    // stream's smallest count. The numbers are conventions of this analysis, set from the fixtures (design: From behavior).
    public const int MinimumChanges = 20;

    // The pattern lossyWAV's output shows in the coded counts, or null where the stream lacks it: a tested rate, units whose starts all lie on a multiple
    // of a length that divides lossyWAV's block, at least 20 changes, 95% of them at one phase of the block's grid, and more than half of the units above
    // the stream's smallest count.
    public LossyWavPattern? Pattern(int sampleRate)
    {
        int block = LossyWavBlock(sampleRate);
        if (block == 0 || _step <= 0 || block % _step != 0 || Changes < MinimumChanges || _phases is null)
        {
            return null;
        }

        long best = 0;
        for (int phase = 0; phase < block; phase++)
        {
            long sum = 0;
            for (int at = phase; at < PhaseLength; at += block)
            {
                sum += _phases[at];
            }

            best = Math.Max(best, sum);
        }

        int smallest = 0;
        while (_distribution[smallest] == 0)
        {
            smallest++;
        }

        long above = Units - _distribution[smallest];
        return best * 20 >= Changes * 19 && above * 2 > Units ? new LossyWavPattern(Changes, best, block, above, Units) : null;
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

// What the counts show of lossyWAV's pattern: the changes, how many fall at the most common phase of the grid, the grid's length in samples, and the
// units above the stream's smallest count.
public sealed record LossyWavPattern(long Changes, long OnGrid, int Grid, long Above, long Units)
{
    public string Evidence(string units) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Changes} changes of the wasted bits between consecutive {units} of a channel, {WastedBitsTally.Share(OnGrid, Changes)} of them at one phase of a grid of {Grid} samples, and {WastedBitsTally.Share(Above, Units)} of the {units} above the stream's smallest count");
}
