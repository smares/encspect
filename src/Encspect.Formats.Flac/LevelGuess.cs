using System.Globalization;
using System.Text.RegularExpressions;

namespace Encspect.Formats.Flac;

// The compression level of the encoder whose settings the frames of a stream fit: what each level of libFLAC (1.2.1 to 1.5.0) and of FFmpeg's native
// encoder (libavcodec 60) sets is in docs/formats/flac.md, taken from the encoders' own help text and from the files they write. The frames do not
// store the level, so a level is named by the figures it limits (the block size, the highest linear predictor order and partition order, whether
// channels are decorrelated), and levels that limit the same figures are named together.
internal static partial class LevelGuess
{
    // The shortest stream that is guessed: the block size comes from the frames before the last, and the highest orders of fewer subframes say little.
    private const int MinimumFrames = 3;
    private const int MinimumResiduals = 8;

    // The share of the subframes (one in MinimumTopShare) that must use the highest partition order for it to count as reached: 20%. In Encspect's level
    // fixtures libFLAC's levels reach it with 31% to 91% of the subframes, and a level 5 file whose highest order is 4 has 10% at 4.
    private const int MinimumTopShare = 5;

    // The coefficient precision FFmpeg writes in every linear predictor at every level; libFLAC chooses its own by block size (12 at 4,096 samples).
    private const int FfmpegPrecision = 15;

    [GeneratedRegex(@"^libFLAC (\d+)\.(\d+)\.(\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex LibFlacName();

    // FFmpeg names itself in two places: the Vorbis comment of an Ogg stream names its encoder (Lavc), the vendor string of a native file its muxer (Lavf).
    [GeneratedRegex(@"^FFmpeg \(Lav([cf])(\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex FfmpegName();

    // What one level limits. Block: the family's small or large size for the sample rate. IsTopPartitionRare: the level's highest partition order is used by few subframes
    // (in Encspect's files 4% to 10% of FFmpeg's at order 8), so reaching it is no sign of the limit.
    private readonly record struct Level(int Number, int Block, int MaxLpcOrder, int MaxPartitionOrder, bool Decorrelates, int MaxFixedOrder = 4, bool IsTopPartitionRare = false);

    private const int Small = 0;
    private const int Large = 1;

    // libFLAC's levels 0 to 8 (the table of "flac --help" of each release). Level 7 sets a highest order of 8 before release 1.3.1 and of 12 from it.
    // Blocks: 1152 samples where no linear predictor is used (libFLAC's default when -l is 0) and 4096 otherwise. Decorrelation: level 0 and 3 code
    // the channels as they are; 1 and 4 choose adaptively (-M), the others try every assignment (-m).
    private static Level[] LibFlacLevels(bool isLevel7Order12) =>
    [
        new(0, Small, 0, 3, false),
        new(1, Small, 0, 3, true),
        new(2, Small, 0, 3, true),
        new(3, Large, 6, 4, false),
        new(4, Large, 8, 4, true),
        new(5, Large, 8, 5, true),
        new(6, Large, 8, 6, true),
        new(7, Large, isLevel7Order12 ? 12 : 8, 6, true),
        new(8, Large, 12, 6, true),
    ];

    // FFmpeg 6.1's native encoder (-compression_level 0 to 12), as the files it writes show: levels 0 to 2 write fixed predictors only (level 0 up to order 3)
    // in small blocks, 3 to 12 linear predictors in large ones; the stereo assignment is chosen per frame at every level.
    private static readonly Level[] FfmpegLevels =
    [
        new(0, Small, 0, 2, true, MaxFixedOrder: 3),
        new(1, Small, 0, 2, true),
        new(2, Small, 0, 3, true),
        new(3, Large, 6, 3, true),
        new(4, Large, 8, 3, true),
        new(5, Large, 8, 8, true, IsTopPartitionRare: true),
        new(6, Large, 8, 8, true, IsTopPartitionRare: true),
        new(7, Large, 8, 8, true, IsTopPartitionRare: true),
        new(8, Large, 12, 8, true, IsTopPartitionRare: true),
        new(9, Large, 12, 8, true, IsTopPartitionRare: true),
        new(10, Large, 12, 8, true, IsTopPartitionRare: true),
        new(11, Large, 32, 8, true, IsTopPartitionRare: true),
        new(12, Large, 32, 8, true, IsTopPartitionRare: true),
    ];

    // FFmpeg's block sizes by sample rate (small, large), as the files it writes show; a rate not listed was not run.
    private static readonly Dictionary<int, (int Small, int Large)> FfmpegBlocks = new()
    {
        [8000] = (192, 576),
        [11025] = (256, 1152),
        [12000] = (256, 1152),
        [16000] = (256, 1152),
        [22050] = (576, 2304),
        [24000] = (576, 2304),
        [32000] = (576, 2304),
        [44100] = (1152, 4608),
        [48000] = (1152, 4608),
        [64000] = (1152, 4608),
        [88200] = (2304, 8192),
        [96000] = (2304, 8192),
        [176400] = (4608, 16384),
        [192000] = (4608, 16384),
    };

    // IsFromWriter: the family comes from the writer the file names (FFmpeg's muxer), not from an encoder claim; the claim then holds at most medium
    // confidence, and only where the linear predictors' precision is FFmpeg's (IsSignedByPrecision).
    public sealed record Guess(string Name, IReadOnlyList<int> Levels, bool IsReached, bool IsFromWriter, bool IsSignedByPrecision, IReadOnlyList<string> Evidence);

    // The levels the stream fits, or null where the encoder is not one whose levels are known, the stream is too short, or no level fits (settings
    // of the person's own, a block size that is not a level's).
    public static Guess? Of(State s, string? encoder, string? writer)
    {
        if (encoder is null && writer is null || s.Info is not { } info || info.MinBlockSize != info.MaxBlockSize || s.VariableFrames != 0
            || s.Frames - s.CutFrames < MinimumFrames || s.ResidualSubframes < MinimumResiduals || s.MaxBlockSize != info.MaxBlockSize)
        {
            return null;
        }

        Level[] levels;
        string family;
        Func<Level, int> block;
        int rate = info.SampleRate;
        bool isFromWriter = false;
        if (LibFlacName().Match(encoder ?? string.Empty) is { Success: true } flac)
        {
            var version = (int.Parse(flac.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(flac.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(flac.Groups[3].Value, CultureInfo.InvariantCulture));
            if (version.CompareTo((1, 2, 1)) < 0 || version.CompareTo((1, 5, 0)) > 0)
            {
                return null;
            }

            levels = LibFlacLevels(version.CompareTo((1, 3, 1)) >= 0);
            family = "libFLAC";
            block = l => l.Block == Small ? 1152 : 4096;
        }
        else if ((FfmpegName().Match(encoder ?? string.Empty) is { Success: true, Groups: [_, { Value: "c" }, { Value: "60" }] } || (isFromWriter = FfmpegName().Match(writer ?? string.Empty) is { Success: true, Groups: [_, { Value: "f" }, { Value: "60" }] }))
            && FfmpegBlocks.TryGetValue(rate, out (int Small, int Large) sizes))
        {
            levels = FfmpegLevels;
            family = "FFmpeg";
            block = l => l.Block == Small ? sizes.Small : sizes.Large;
        }
        else
        {
            return null;
        }

        int highestOrder = s.LpcOrderMax;
        int highestPartition = 0;
        for (int order = 0; order < s.PartitionOrders.Length; order++)
        {
            if (s.PartitionOrders[order] > 0)
            {
                highestPartition = order;
            }
        }

        long decorrelated = s.AssignmentFrames[(int)ChannelAssignment.LeftSide] + s.AssignmentFrames[(int)ChannelAssignment.SideRight] + s.AssignmentFrames[(int)ChannelAssignment.MidSide];
        bool isLibFlac = family == "libFLAC";
        int highestFixed = 0;
        for (int order = 0; order < s.FixedOrders.Length; order++)
        {
            if (s.FixedOrders[order] > 0)
            {
                highestFixed = order;
            }
        }

        bool Fits(Level l) =>
            block(l) == s.MaxBlockSize
            && highestOrder <= l.MaxLpcOrder
            && highestPartition <= l.MaxPartitionOrder
            && (!isLibFlac || info.Channels != 2 || l.Decorrelates || decorrelated == 0)
            && (isLibFlac || s.SubframeTypes[(int)SubframeType.Lpc] == 0 || (s.LpcPrecisionMin == FfmpegPrecision && s.LpcPrecisionMax == FfmpegPrecision))
            && highestFixed <= l.MaxFixedOrder;
        List<Level> fitting = [.. levels.Where(Fits)];
        if (fitting.Count == 0)
        {
            return null;
        }

        bool Reaches(Level l)
        {
            // Where the level's own highest order is rare, the one below it counts as well.
            int top = l.MaxPartitionOrder - (l.IsTopPartitionRare ? 1 : 0);
            return highestOrder == l.MaxLpcOrder && highestPartition <= l.MaxPartitionOrder && SubframesFrom(s, top) * MinimumTopShare >= s.ResidualSubframes && (l.MaxLpcOrder > 0 || highestFixed == l.MaxFixedOrder);
        }

        // Every level the frames fit is named: frames that do not use the extra room of a higher level look like the lower level's. Whether the frames press against
        // the limits of the lowest level they fit only sets the confidence.
        Level lowest = fitting.OrderBy(l => l.MaxLpcOrder).ThenBy(l => l.MaxPartitionOrder).ThenBy(l => l.MaxFixedOrder).First();
        bool isReached = Reaches(lowest);
        List<Level> named = fitting;

        // Levels that limit the same figures cannot be told apart, so where all the levels the frames fit are such, the frames exceed the limits of every other level.
        bool isOneGroup = fitting.All(l => l.Block == fitting[0].Block && l.MaxLpcOrder == fitting[0].MaxLpcOrder && l.MaxPartitionOrder == fitting[0].MaxPartitionOrder && l.MaxFixedOrder == fitting[0].MaxFixedOrder);
        int[] numbers = [.. named.Select(l => l.Number)];

        int[] blockLevels = [.. levels.Where(l => block(l) == s.MaxBlockSize).Select(l => l.Number)];
        var evidence = new List<string>
        {
            $"The block size is {Format(s.MaxBlockSize)} samples in every frame but the last; {Noun(blockLevels.Length)} {Join(blockLevels)} {(blockLevels.Length == 1 ? "uses" : "use")} it.",
        };
        long lpc = s.SubframeTypes[(int)SubframeType.Lpc];
        evidence.Add(lpc == 0
            ? "No subframe is a linear predictor, so the highest linear predictor order is 0."
            : $"The highest linear predictor order is {highestOrder}; {Format(lpc)} of {Format(s.Subframes)} subframes are linear predictors.");
        if (!isLibFlac && lpc > 0)
        {
            evidence.Add($"Every linear predictor has a coefficient precision of {FfmpegPrecision} bits, the one FFmpeg's encoder writes.");
        }

        evidence.Add($"The highest partition order is {highestPartition}.");
        if (isLibFlac && info.Channels == 2)
        {
            evidence.Add(decorrelated == 0
                ? "Every frame codes the channels as left and right."
                : $"{Format(decorrelated)} of {Format(s.AssignmentFrames.Sum())} frames code the channels as left/side, side/right or mid/side, which levels 0 and 3 never do.");
        }

        evidence.Add($"{Noun(named.Count, true)} {Join(numbers)} {(named.Count == 1 ? "sets" : "set")} {Settings(named, s.MaxBlockSize)}.");
        evidence.Add(isOneGroup
            ? "Every level the frames fit sets the same figures: the frames exceed the limits of all the others."
            : isReached
                ? "The frames reach the highest linear predictor order and partition order of the lowest level they fit (the partition order in at least a fifth of the subframes)."
                : "The frames do not reach the highest linear predictor order and partition order of the lowest level they fit (the partition order in at least a fifth of the subframes), so they say nothing against the levels above.");
        return new Guess(family, numbers, isReached || isOneGroup, isFromWriter, !isLibFlac && lpc > 0, evidence);
    }

    // The subframes whose partition order is at least order: a level's highest order only shows as a limit where many subframes use it.
    private static long SubframesFrom(State s, int order)
    {
        long count = 0;
        for (int i = order; i < s.PartitionOrders.Length; i++)
        {
            count += s.PartitionOrders[i];
        }

        return count;
    }

    private static string Settings(List<Level> named, int size)
    {
        // Levels named together share these figures (they may differ in settings the frames do not show).
        Level first = named[0];
        string order = named.Select(l => l.MaxLpcOrder).Distinct().Count() == 1 ? (first.MaxLpcOrder == 0 ? "no linear predictor" : $"a highest linear predictor order of {first.MaxLpcOrder}") : "differing highest linear predictor orders";
        string partition = named.Select(l => l.MaxPartitionOrder).Distinct().Count() == 1 ? $"a highest partition order of {first.MaxPartitionOrder}" : "differing highest partition orders";
        return $"{order}, {partition} and a block size of {Format(size)} samples";
    }

    private static string Noun(int count, bool isCapital = false) => count == 1 ? (isCapital ? "Level" : "level") : (isCapital ? "Levels" : "levels");

    // "7 or 8", "5, 6 or 7", and "3 to 12" for a run of four or more.
    public static string Join(int[] numbers) => numbers.Length switch
    {
        1 => numbers[0].ToString(CultureInfo.InvariantCulture),
        2 => $"{numbers[0]} or {numbers[1]}",
        _ when numbers[^1] - numbers[0] == numbers.Length - 1 && numbers.Length >= 4 => $"{numbers[0]} to {numbers[^1]}",
        _ => $"{string.Join(", ", numbers[..^1])} or {numbers[^1]}",
    };

    private static string Format(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
