using System.Globalization;
using System.Text.RegularExpressions;

namespace Encspect.Formats.Flac;

// The compression level of the encoder whose settings the frames of a stream fit: what each level of libFLAC (1.1.0 to 1.5.0) and of FFmpeg's native
// encoder (libavcodec 54 to 62) sets is in docs/formats/flac.md, taken from the encoders' own help text and from the files they write. The frames do not
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

    // The coefficient precision every FFmpeg release run wrote in every linear predictor at every level. libFLAC chooses its own by block size and bit depth: 12 at 4,096 samples and
    // 16 bits, but 15 above 16 bits, so the precision tells the encoders apart only up to 16 bits per sample.
    private const int FfmpegPrecision = 15;

    [GeneratedRegex(@"^libFLAC (\d+)\.(\d+)\.(\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex LibFlacName();

    // FFmpeg names itself in two places: the Vorbis comment of an Ogg stream names its encoder (Lavc, libavcodec's version), the vendor string of a native file its muxer
    // (Lavf, libavformat's). Only the versions of the releases that were run are known (ffmpeg_tool.py, and the ffmpeg package of Ubuntu 24.04).
    [GeneratedRegex(@"^FFmpeg \(Lavc(\d+\.\d+\.\d+) flac\)$", RegexOptions.CultureInvariant)]
    private static partial Regex FfmpegEncoderName();

    [GeneratedRegex(@"^FFmpeg \(Lavf(\d+\.\d+\.\d+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex FfmpegWriterName();

    // The FFmpeg releases whose encoder was run at every level (docs/formats/flac.md, "Compression level"): libavcodec's and libavformat's versions (the first is also
    // the one of the other releases of the branch whose libavcodec version is the same, in docs/design/flac-ffmpeg-levels.json), and whether the release picks the candidate
    // orders of its levels 6 and 8 by the rule that names an order one above the level's highest (up to FFmpeg 1.0.x; the runs still wrote orders of 7 and 10 there).
    private readonly record struct FfmpegRelease(string Codec, string Format, bool IsPlainOrderRule = false);

    private static readonly FfmpegRelease[] FfmpegReleases =
    [
        new("54.23.100", "54.6.100", true),
        new("54.59.100", "54.29.104", true),
        new("54.92.100", "54.63.104"),
        new("56.1.100", "56.4.101"),
        new("56.60.100", "56.40.101"),
        new("57.107.100", "57.83.100"),
        new("58.91.100", "58.45.100"),
        new("58.134.100", "58.76.100"),
        new("59.18.100", "59.16.100"),
        new("59.37.100", "59.27.100"),
        new("60.3.100", "60.3.100"),
        new("60.31.102", "60.16.100"),
        new("61.3.100", "61.1.100"),
        new("61.19.100", "61.7.100"),
        new("61.19.101", "61.7.100"),
        new("62.11.100", "62.3.100"),
    ];

    // What one level limits. Block: the family's small or large size for the sample rate. IsTopPartitionRare: the level's highest partition order is used by few subframes
    // (in Encspect's files 4% to 10% of FFmpeg's at order 8), so reaching it is no sign of the limit.
    private readonly record struct Level(int Number, int Block, int MaxLpcOrder, int MaxPartitionOrder, bool Decorrelates, int MaxFixedOrder = 4, bool IsTopPartitionRare = false, bool IsAdaptive = false, int OrderSlack = 0);

    private const int Small = 0;
    private const int Large = 1;

    // libFLAC's levels 0 to 8 (the table of "flac --help" of each release, and the table in the source of the releases that have none in the help text of -0 to -8:
    // flac_tool.py --levels; docs/formats/flac.md). Two tables over the releases from 1.1.0 (the first built and run that writes a vendor string; 1.0.3 writes none and 1.0.4 does not build): up to 1.1.3 the blocks are 1,152 and
    // 4,608 samples and the highest partition orders 2, 2, 3, 3, 3, 3, 4, 6, 6; from 1.1.4 the blocks are 1,152 and 4,096 and the orders 3, 3, 3, 4, 4, 5, 6, 6, 6.
    // Level 7 sets a highest order of 8 before release 1.3.1 and of 12 from it. Blocks: 1152 samples where no linear predictor is used (libFLAC's default when -l is 0).
    // Decorrelation: level 0 and 3 code the channels as they are; 1 and 4 choose adaptively (-M), the others try every assignment (-m).
    private static Level[] LibFlacLevels((int, int, int) version)
    {
        bool isEarly = version.CompareTo((1, 1, 4)) < 0;
        bool isLevel7Order12 = version.CompareTo((1, 3, 1)) >= 0;
        int[] partitions = isEarly ? [2, 2, 3, 3, 3, 3, 4, 6, 6] : [3, 3, 3, 4, 4, 5, 6, 6, 6];
        int[] orders = [0, 0, 0, 6, 8, 8, 8, isLevel7Order12 ? 12 : 8, 12];
        bool[] decorrelates = [false, true, true, false, true, true, true, true, true];
        bool[] isAdaptive = [false, true, false, false, true, false, false, false, false];
        return [.. Enumerable.Range(0, 9).Select(n => new Level(n, n < 3 ? Small : Large, orders[n], partitions[n], decorrelates[n], IsAdaptive: isAdaptive[n]))];
    }

    // FFmpeg's native encoder (-compression_level 0 to 12), the same in every release run, as the files it writes show: levels 0 to 2 write fixed predictors only (level 0 up to order 3)
    // in small blocks, 3 to 12 linear predictors in large ones; the stereo assignment is chosen per frame at every level.
    private static Level[] FfmpegLevels(bool isPlainOrderRule) =>
    [
        new(0, Small, 0, 2, true, MaxFixedOrder: 3),
        new(1, Small, 0, 2, true),
        new(2, Small, 0, 3, true),
        new(3, Large, 6, 3, true),
        new(4, Large, 8, 3, true),
        new(5, Large, 8, 8, true, IsTopPartitionRare: true),
        new(6, Large, 8, 8, true, IsTopPartitionRare: true, OrderSlack: isPlainOrderRule ? 1 : 0),
        new(7, Large, 8, 8, true, IsTopPartitionRare: true),
        new(8, Large, 12, 8, true, IsTopPartitionRare: true, OrderSlack: isPlainOrderRule ? 1 : 0),
        new(9, Large, 12, 8, true, IsTopPartitionRare: true),
        new(10, Large, 12, 8, true, IsTopPartitionRare: true),
        new(11, Large, 32, 8, true, IsTopPartitionRare: true),
        new(12, Large, 32, 8, true, IsTopPartitionRare: true),
    ];

    // FFmpeg's block sizes by sample rate (small, large), as the files of every release run show; only the rates that have a committed fixture at level 0 and at level 5
    // (tests/fixtures/flac/external-ffmpeg-level-<n>-mono-<rate>) are guessed.
    private static readonly Dictionary<int, (int Small, int Large)> FfmpegBlocks = new()
    {
        [8000] = (192, 576),
        [22050] = (576, 2304),
        [44100] = (1152, 4608),
        [48000] = (1152, 4608),
        [96000] = (2304, 8192),
        [192000] = (4608, 16384),
    };

    // Name: whose levels these are ("libFLAC", "FFmpeg"). IsFromWriter: the family comes from the writer the file names (FFmpeg's muxer) and the stream's linear
    // predictors, not from an encoder claim; the claim then holds at most medium confidence.
    public sealed record Guess(string Name, IReadOnlyList<int> Levels, bool IsReached, bool IsFromWriter, IReadOnlyList<string> Evidence);

    // The release the encoder claim names: by the ENCODER comment's libavcodec version, or, where no encoder is named, by the muxer's libavformat version over the
    // stream's linear predictors. The muxer's string alone does not name the encoder (FFmpeg also remuxes other encoders' streams): the stream must show FFmpeg's, by linear
    // predictors of its precision, and a stream that names another encoder is not read through the writer.
    private static bool FindFfmpeg(string? encoder, string? writer, State s, out FfmpegRelease release, out bool isFromWriter)
    {
        release = default;
        isFromWriter = false;
        if (encoder is not null)
        {
            if (FfmpegEncoderName().Match(encoder) is { Success: true } named && FfmpegReleases.FirstOrDefault(r => r.Codec == named.Groups[1].Value) is { Codec: not null } byCodec)
            {
                release = byCodec;
                return true;
            }

            return false;
        }

        if (writer is not null && s.SubframeTypes[(int)SubframeType.Lpc] > 0
            && FfmpegWriterName().Match(writer) is { Success: true } muxer && FfmpegReleases.FirstOrDefault(r => r.Format == muxer.Groups[1].Value) is { Codec: not null } byFormat)
        {
            release = byFormat;
            isFromWriter = true;
            return true;
        }

        return false;
    }

    // The levels the stream fits, or null where the encoder is not one whose levels are known, the stream is too short, or no level fits (settings
    // of the person's own, a block size that is not a level's).
    public static Guess? Of(State s, string? encoder, string? writer)
    {
        if (encoder is null && writer is null || s.Info is not { } info || info.MinBlockSize != info.MaxBlockSize || s.VariableFrames != 0
            || s.Frames - s.CutFrames < MinimumFrames || s.ResidualSubframes < MinimumResiduals
            // Every frame but the last has STREAMINFO's block size, and the frames agree with STREAMINFO on the rate and the channels the guess reads.
            || s.FirstBlockSize != info.MaxBlockSize || s.DifferentBlockFrames != (s.IsLatestBlockDifferent ? 1 : 0) || s.FirstRate != info.SampleRate || s.FirstChannels != info.Channels)
        {
            return null;
        }

        Level[] levels;
        string family;
        Func<Level, int> block;
        int rate = info.SampleRate;
        bool isFromWriter = false;
        long adaptiveBreaks = 0;
        if (LibFlacName().Match(encoder ?? string.Empty) is { Success: true } flac)
        {
            var version = (int.Parse(flac.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(flac.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(flac.Groups[3].Value, CultureInfo.InvariantCulture));
            // 1.1.1 does not build with a current toolchain, so no run confirms its table (which its source gives as 1.1.0's and 1.1.2's).
            if (version.CompareTo((1, 1, 0)) < 0 || version.CompareTo((1, 5, 0)) > 0 || version == (1, 1, 1))
            {
                return null;
            }

            levels = LibFlacLevels(version);
            family = "libFLAC";
            int large = version.CompareTo((1, 1, 4)) < 0 ? 4608 : 4096;
            block = l => l.Block == Small ? 1152 : large;
            // The adaptive levels (-M) of 1.4.0 and later never write left/side or side/right (1.5.0 chooses between left/right and mid/side by the sum of the first
            // differences, 1.4.0 to 1.4.3 test only those two on every Nth frame); the earlier ones are as State.AdaptiveBreaks says.
            adaptiveBreaks = version.CompareTo((1, 4, 0)) >= 0 ? s.SideFrames : s.AdaptiveBreaks;
        }
        else if (FindFfmpeg(encoder, writer, s, out FfmpegRelease release, out isFromWriter) && FfmpegBlocks.TryGetValue(rate, out (int Small, int Large) sizes))
        {
            levels = FfmpegLevels(release.IsPlainOrderRule);
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
            block(l) == s.FirstBlockSize
            && highestOrder <= l.MaxLpcOrder + l.OrderSlack
            && highestPartition <= l.MaxPartitionOrder
            && (!isLibFlac || info.Channels != 2 || l.Decorrelates || decorrelated == 0)
            && (!l.IsAdaptive || info.Channels != 2 || adaptiveBreaks == 0)
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
            return highestOrder >= l.MaxLpcOrder && highestPartition <= l.MaxPartitionOrder && SubframesFrom(s, top) * MinimumTopShare >= s.ResidualSubframes && (l.MaxLpcOrder > 0 || highestFixed == l.MaxFixedOrder);
        }

        // Every level the frames fit is named: frames that do not use the extra room of a higher level look like the lower level's. Whether the frames press against
        // the limits of the lowest level they fit only sets the confidence.
        Level lowest = fitting.OrderBy(l => l.MaxLpcOrder).ThenBy(l => l.MaxPartitionOrder).ThenBy(l => l.MaxFixedOrder).First();
        bool isReached = Reaches(lowest);
        List<Level> named = fitting;

        // Levels that limit the same figures cannot be told apart, so where all the levels the frames fit are such, the frames exceed the limits of every other level.
        bool isOneGroup = fitting.All(l => l.Block == fitting[0].Block && l.MaxLpcOrder == fitting[0].MaxLpcOrder && l.MaxPartitionOrder == fitting[0].MaxPartitionOrder && l.MaxFixedOrder == fitting[0].MaxFixedOrder);
        int[] numbers = [.. named.Select(l => l.Number)];

        int[] blockLevels = [.. levels.Where(l => block(l) == s.FirstBlockSize).Select(l => l.Number)];
        var evidence = new List<string>
        {
            $"The block size is {Format(s.FirstBlockSize)} samples in every frame but the last; {Noun(blockLevels.Length)} {Join(blockLevels)} {(blockLevels.Length == 1 ? "uses" : "use")} it.",
        };
        long lpc = s.SubframeTypes[(int)SubframeType.Lpc];
        evidence.Add(lpc == 0
            ? "No subframe is a linear predictor, so the highest linear predictor order is 0."
            : $"The highest linear predictor order is {highestOrder}; {Format(lpc)} of {Format(s.Subframes)} subframes {(lpc == 1 ? "is a linear predictor" : "are linear predictors")}.");
        if (!isLibFlac && lpc > 0)
        {
            evidence.Add($"Every linear predictor has a coefficient precision of {FfmpegPrecision} bits, the precision every FFmpeg release run wrote in every level fixture of Encspect's tests."
                + (info.BitsPerSample > 16 ? " At more than 16 bits per sample libFLAC also writes it, so here only the block size tells the two encoders apart." : string.Empty));
        }

        evidence.Add($"The highest partition order is {highestPartition}.");
        if (isLibFlac && info.Channels == 2)
        {
            evidence.Add(decorrelated == 0
                ? "Every frame codes the channels as left and right."
                : $"{Format(decorrelated)} of {Format(s.AssignmentFrames.Sum())} frames code the channels as left/side, side/right or mid/side, which levels 0 and 3 never do.");
        }

        if (isLibFlac && info.Channels == 2 && adaptiveBreaks > 0)
        {
            evidence.Add($"{Format(adaptiveBreaks)} {(adaptiveBreaks == 1 ? "frame has" : "frames have")} a stereo assignment that the adaptive choice of levels 1 and 4 does not write in this release, which rules those levels out.");
        }

        evidence.Add($"{Noun(named.Count, true)} {Join(numbers)} {(named.Count == 1 ? "sets" : "set")} {Settings(named, s.FirstBlockSize)}.");
        evidence.Add(isOneGroup
            ? "Every level the frames fit sets the same figures: the frames exceed the limits of all the others, or, with two channels, their stereo coding rules the others out."
            : isReached
                ? "The frames reach the highest linear predictor order and partition order of the lowest level they fit (the partition order in at least a fifth of the subframes)."
                : "The frames do not reach the highest linear predictor order and partition order of the lowest level they fit (the partition order in at least a fifth of the subframes), so they say nothing against the levels above.");
        return new Guess(family, numbers, isReached || isOneGroup, isFromWriter, evidence);
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
