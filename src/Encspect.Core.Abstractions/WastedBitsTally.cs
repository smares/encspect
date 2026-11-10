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
    private readonly long[] _compared = new long[256];
    private readonly int[] _previous = new int[MaxChannels];
    private long _firstChange;
    private long _gridOfChanges;
    private bool _hasChange;
    private long _lastMoment;
    private bool _hasUnit;

    // The units that have a count, and those with a count above zero. A unit whose samples are all zero has none (SilentUnits).
    public long Units { get; private set; }

    // The units of digital silence: they say nothing of the audio around them, so they are listed apart and a channel's previous count stays.
    public long SilentUnits { get; private set; }

    // The distinct positions at which some channel changes: the gate counts these, so that one passage in many channels is not many.
    public long Moments { get; private set; }

    // The units that take part in the comparison: those of a channel that is coded as itself (not mid or side, whose counts differ from the source
    // channel's by the coding). The changes and the share of the pattern are taken over these only.
    public long ComparedUnits { get; private set; }

    public long UnitsWithWastedBits => Units - _distribution[0];

    // The changes between a unit and the previous unit of the same channel.
    public long Changes { get; private set; }

    public long CountOf(int bits) => (uint)bits < _distribution.Length ? _distribution[bits] : 0;

    // Counts one unit of a channel that starts at position (in samples) and states bits wasted bits. Units of the several channels of one frame or
    // block set share their position; positions are those of the stream, in samples, and units come in stream order (all channels of a position together).
    // isCompared is false for a unit that is not a source channel coded as itself (a mid or side subframe): it is in the distribution only.
    public void Add(int channel, long position, int bits, bool isSilent = false, bool isCompared = true)
    {
        if (isSilent)
        {
            SilentUnits++;
            return;
        }

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
        if (!isCompared)
        {
            return;
        }

        ComparedUnits++;
        _compared[bits]++;
        if ((uint)channel >= MaxChannels)
        {
            return;
        }

        int previous = _previous[channel];
        _previous[channel] = bits;
        if (previous >= 0 && previous != bits)
        {
            Changes++;
            if (Moments == 0 || position != _lastMoment)
            {
                Moments++;
                _lastMoment = position;
            }

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

    // The smallest number of moments (positions with a change) that can make the pattern; a convention of this analysis, set from the fixtures (design: From behavior).
    public const int MinimumChanges = 20;

    // The pattern lossyWAV's output shows in the coded counts, or null where the stream lacks it: a tested rate, at least 20 moments, the greatest common
    // divisor of the distances between the changes' positions equal to lossyWAV's block at that rate (so every change lies on its grid, and not all on a
    // coarser one, as a tool with longer blocks would put them), and more than half of the compared units above the stream's smallest count.
    public LossyWavPattern? Pattern(int sampleRate)
    {
        int block = LossyWavBlock(sampleRate);
        if (block == 0 || Moments < MinimumChanges || _gridOfChanges != block)
        {
            return null;
        }

        int smallest = 0;
        while (_compared[smallest] == 0)
        {
            smallest++;
        }

        long above = ComparedUnits - _compared[smallest];
        return above * 2 > ComparedUnits ? new LossyWavPattern(Changes, Moments, block, above, ComparedUnits) : null;
    }

    // The statistic: the node with an entry for each count that occurred and the changes, or null where no unit has a wasted bit (a stream without
    // any has nothing to distribute). cover makes a node over the stream's bytes, with its description.
    public Node? AddTo(Node statistics, Func<string, NodeValue?, string, string?, Node> cover, WastedBitsWording wording, string? topic)
    {
        if (UnitsWithWastedBits == 0)
        {
            return null;
        }

        long all = Units;
        Node node = statistics.Add(cover("Wasted Bits", NodeValue.Integer(all, wording.Plural, wording.Singular), wording.Statistic, topic));
        for (int bits = 0; bits < _distribution.Length; bits++)
        {
            long count = _distribution[bits];
            if (count > 0)
            {
                node.Add(cover($"{bits} {(bits == 1 ? "Bit" : "Bits")}", $"{Counted(count, wording)} ({Share(count, all)})", wording.Count, topic));
            }
        }

        if (SilentUnits > 0)
        {
            node.Add(cover("Silent", Counted(SilentUnits, wording), wording.SilentDescription, topic));
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
public sealed record WastedBitsWording(string Singular, string Plural, string Statistic, string Count, string ChangesDescription, string SilentDescription, string Between);

// What the counts show of lossyWAV's pattern: the changes and the positions they fall at, the grid's length in samples that those share, and the units
// above the stream's smallest count.
public sealed record LossyWavPattern(long Changes, long Moments, int Grid, long Above, long Units)
{
    // between names what a change is between, as the format counts it ("subframes of a channel").
    public string Evidence(string units, string between) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{Changes} changes of the wasted bits between {between}, at {Moments} moments, all on a grid of {Grid} samples (the greatest common divisor of the distances between them), and {WastedBitsTally.Share(Above, Units)} of the compared {units} above the smallest");
}
