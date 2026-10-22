using Encspect.Core;
using Encspect.Host;

namespace Encspect.Gui.Tests;

// Category series for the tests of the category charts. No format produces one yet, so they are made here from a real
// stream's frame offsets and per-second series, and handed to the detail window by a parser that wraps the MPEG one.
internal static class CategoryTestData
{
    public static readonly IReadOnlyList<Category> BlockTypes =
    [
        new("Long", "Long block."),
        new("Start", "Start block."),
        new("Short", "Short block."),
        new("Mixed", "Mixed block."),
        new("Stop", "Stop block."),
        new("Reserved", "Reserved block type."),
    ];

    public static readonly IReadOnlyList<Category> StereoCodings =
    [
        new("Left/right stereo", "Left and right channels coded apart."),
        new("Mid/side stereo", "Mid and side channels."),
        new("Intensity stereo", "One signal with intensity scaling."),
        new("Mid/side and intensity stereo", "Both tools."),
        new("Dual channel", "Two independent channels."),
        new("Mono", "One channel."),
    ];

    public static IReadOnlyList<Category> Numbered(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Category($"{prefix} {i}", $"The {prefix} number {i}."))];

    // The time index of a stream whose frames last equally long, over the starts of its per-second series.
    public static TimeIndex TimeOf(StreamInfo stream, int units)
    {
        double[] starts = [.. stream.Series.First(s => s.Axis == SeriesAxis.Seconds).X.Span.ToArray()];
        int[] first = [.. starts.Select(s => (int)Math.Min(units, Math.Ceiling((s * units / stream.Duration) - 1e-9)))];
        return new TimeIndex(starts, first);
    }

    // Block types of four values per frame with a start, short and stop frame at every 'every' frames, and a reserved
    // value once; the Block Usage of a Layer III stream and its grouping.
    public static CategorySeries[] BlockUsage(StreamInfo stream, int every = 29, bool withTime = true)
    {
        Series bitrate = stream.Series.First(s => s.Name == "Bitrate");
        int units = bitrate.X.Length;
        var builder = new CategorySeriesBuilder("Block Usage", BlockTypes, "granule and channel", 4,
            ["granule 1, channel 1", "granule 1, channel 2", "granule 2, channel 1", "granule 2, channel 2"], concept: GraphConcept.BlockSwitching);
        for (int frame = 0; frame < units; frame++)
        {
            if (frame % every == every - 3)
            {
                builder.Add([0, 0, 1, 1]);
            }
            else if (frame % every == every - 2)
            {
                builder.Add([2, 2, 2, 2]);
            }
            else if (frame % every == every - 1)
            {
                builder.Add([4, 4, 0, 0]);
            }
            else if (frame == 5)
            {
                builder.Add([0, 0, 0, 5]);
            }
            else
            {
                builder.Add([0, 0, 0, 0]);
            }
        }

        CategorySeries usage = builder.Build(bitrate.X, bitrate, null, "The block type of each granule and channel.", null, withTime ? TimeOf(stream, units) : null);
        Category[] sizes = [new("Long, start and stop", "Long transforms."), new("Short", "Short transforms."), new("Mixed", "Mixed transforms."), new("Reserved", "Reserved.")];
        return [usage, usage.Group("Block Sizes", sizes, [0, 0, 1, 2, 0, 3], null, "Long against short granules.")];
    }

    // One value per frame, left/right stereo with joint stereo codings now and then, in a series with a concept.
    public static CategorySeries StereoCoding(StreamInfo stream)
    {
        Series bitrate = stream.Series.First(s => s.Name == "Bitrate");
        int units = bitrate.X.Length;
        var builder = new CategorySeriesBuilder("Stereo Coding", StereoCodings, "frame", 1, concept: GraphConcept.StereoCoding);
        for (int frame = 0; frame < units; frame++)
        {
            builder.Add((byte)(frame % 40 < 28 ? 1 : frame % 40 < 36 ? 0 : 3));
        }

        return builder.Build(bitrate.X, bitrate, null, "The stereo coding of each frame.", null, TimeOf(stream, units));
    }

    // Channel streams per frame, one to three, as AAC's window sequences, in a variable layout.
    public static CategorySeries WindowSequences(StreamInfo stream)
    {
        Series bitrate = stream.Series.First(s => s.Name == "Bitrate");
        int units = bitrate.X.Length;
        var builder = new CategorySeriesBuilder("Window Sequences", [new("Only Long", "l"), new("Long Start", "s"), new("Eight Short", "e"), new("Long Stop", "t")],
            "channel stream", concept: GraphConcept.BlockSwitching);
        for (int frame = 0; frame < units; frame++)
        {
            byte code = (byte)(frame % 300 == 10 ? 1 : frame % 300 == 11 ? 2 : frame % 300 == 12 ? 3 : 0);
            builder.Add(Enumerable.Repeat(code, 1 + (frame % 3)).ToArray());
        }

        return builder.Build(bitrate.X, bitrate, null, "The window sequence of each channel stream.", null, TimeOf(stream, units));
    }

    // An ordered series of nine categories, which fold into gray past the eighth step.
    public static CategorySeries Submodes(StreamInfo stream)
    {
        Series bitrate = stream.Series.First(s => s.Name == "Bitrate");
        int units = bitrate.X.Length;
        var builder = new CategorySeriesBuilder("Submodes", Numbered("Submode", 9), "frame", 1, isOrdered: true);
        for (int frame = 0; frame < units; frame++)
        {
            builder.Add((byte)((frame / 7) % 9));
        }

        return builder.Build(bitrate.X, bitrate, null, "The submode of each frame.", null, TimeOf(stream, units));
    }

    public static CategorySeries[] All(StreamInfo stream) => [.. BlockUsage(stream), StereoCoding(stream), WindowSequences(stream), Submodes(stream)];

    // The default parsers with the MPEG one wrapped to add category series to the streams it reports.
    public static Analyzer Analyzer(Func<StreamInfo, IReadOnlyList<CategorySeries>>? make = null) =>
        new(DefaultParsers.Tags, [new CategoryParser(DefaultParsers.Formats[0], make ?? All), .. DefaultParsers.Formats.Skip(1)], DefaultParsers.Codecs);

    private sealed class CategoryParser(IFormatParser inner, Func<StreamInfo, IReadOnlyList<CategorySeries>> make) : IFormatParser
    {
        public string Name => inner.Name;

        public SniffResult Sniff(SniffContext context) => inner.Sniff(context);

        public long? DeclaredEnd(SniffContext context) => inner.DeclaredEnd(context);

        public void Parse(ParseContext context)
        {
            inner.Parse(context);
            foreach (StreamInfo stream in context.Streams.ToArray())
            {
                context.UpdateStream(stream, stream with { CategorySeries = make(stream) });
            }
        }
    }
}
