using Encspect.Core;

namespace Encspect.Formats.Ac3;

// The categories of the flag series, shared by the series and the statistics nodes they are anchored to. Each series is a flag
// series of the tally's single value: the first category counts the blocks the tally counts, the second the other blocks read.
internal static class CodingCategories
{
    public static readonly Category[] BlockSwitching =
    [
        new("With block switching", Explain.CategoryBlockSwitched) { Topic = Topics.AudioBlocks },
        new("Without block switching", Explain.CategoryBlockLong) { Topic = Topics.AudioBlocks },
    ];

    public static readonly Category[] Coupling =
    [
        new("With coupling", Explain.CategoryCoupled) { Topic = Topics.Coupling },
        new("Without coupling", Explain.CategoryNotCoupled) { Topic = Topics.Coupling },
    ];

    public static readonly Category[] Rematrixing =
    [
        new("With rematrixing", Explain.CategoryRematrixed) { Topic = Topics.Rematrixing },
        new("Without rematrixing", Explain.CategoryNotRematrixed) { Topic = Topics.Rematrixing },
    ];

    public static readonly Category[] SpectralExtension =
    [
        new("With spectral extension", Explain.CategorySpectralExtension) { Topic = Topics.SpectralExtension },
        new("Without spectral extension", Explain.CategoryNoSpectralExtension) { Topic = Topics.SpectralExtension },
    ];
}

// The statistics nodes the coding series are anchored to; null where the program has none.
internal sealed record CodingNodes(Node? BlockSwitching, Node? Coupling, Node? Rematrixing, Node? SpectralExtension, Node? CodedBandwidth);

// Records the coding series of a program while its frames are read: the flags of every audio block read of each program frame (the
// independent frame's blocks, then each dependent frame's, one byte each), and the coded bandwidth of the program frame. The block
// reader decides every flag in the place that counts the same thing for the tally, so a series equals its tally; Frame is called for
// each frame that joins the program frame and EndUnit when the program frame is complete. The series are made from the flags when
// the stream's figures are built, so the layout can follow what the units hold: an AC-3 program whose frames all have six blocks or
// fewer is a fixed layout of six values (no count byte), anything else, E-AC-3 and an AC-3 core with dependent frames (ETSI TS 102 366
// J.1.5.2.2) among it, a variable one. A recorder belongs to one program (docs/design/coding-graphs.md, "When the codes are made").
internal sealed class CodingRecorder
{
    private const int BlocksPerFrame = 6;

    private readonly Ac3Analysis.ProgramKey _key;
    private readonly ChunkedList<byte> _blocks = new();
    private readonly ChunkedList<byte> _lengths = new();
    private readonly ChunkedList<byte> _ends = new();
    private int _unitBlocks;
    private int _unitEnd = 255;
    private int _longest;

    // Units ended so far.
    public int Count { get; private set; }

    // Units by coded bandwidth in whole hertz, for the statistics node.
    public SortedDictionary<int, long> Bandwidths { get; } = [];

    // The key is the independent frame's: the tallies of coupling and rematrixing exist from its acmod, spectral extension in E-AC-3.
    public CodingRecorder(Ac3Analysis.ProgramKey key) => _key = key;

    // A frame that joins the program frame: its blocks read, in order.
    public void Frame(FrameResult result)
    {
        int read = Math.Min(result.BlocksRead, result.BlockFlags.Length);
        for (int block = 0; block < read; block++)
        {
            _blocks.Add(result.BlockFlags[block]);
            _unitBlocks++;
            if (result.BlockEnds[block] != 255)
            {
                _unitEnd = _unitEnd == 255 ? result.BlockEnds[block] : Math.Max(_unitEnd, result.BlockEnds[block]);
            }
        }
    }

    public void EndUnit()
    {
        // At most 9 frames of 6 blocks.
        _lengths.Add((byte)Math.Min(_unitBlocks, 254));
        _longest = Math.Max(_longest, _unitBlocks);
        _ends.Add((byte)_unitEnd);
        if (_unitEnd != 255)
        {
            int hertz = Hertz(_unitEnd);
            Bandwidths[hertz] = Bandwidths.GetValueOrDefault(hertz) + 1;
        }

        _unitBlocks = 0;
        _unitEnd = 255;
        Count++;
    }

    // One series of the program: a value for each block read of each unit, the first category where the block has the flag. Blocks a
    // fixed unit lacks are None. A block that is not 2/0 is "without rematrixing": the tally counts it in the blocks read, which the
    // series total equals.
    private CategorySeries Make(string name, IReadOnlyList<Category> categories, GraphConcept concept, byte flag, bool isFixed, ReadOnlyMemory<double> offsets,
        Series? shared, Node anchor, string description, string topic, TimeIndex? time, CancellationToken cancellation)
    {
        var builder = new CategorySeriesBuilder(name, categories, "block", isFixed ? BlocksPerFrame : 0, concept: concept);
        Span<byte> codes = stackalloc byte[Math.Max(BlocksPerFrame, 254)];
        long at = 0;
        for (int unit = 0; unit < Count; unit++)
        {
            if (unit % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            int length = _lengths[unit];
            int count = isFixed ? BlocksPerFrame : length;
            for (int i = 0; i < count; i++)
            {
                codes[i] = i >= length ? CategorySeries.None : (byte)((_blocks[(int)(at + i)] & flag) != 0 ? 0 : 1);
            }

            builder.Add(codes[..count]);
            at += length;
        }

        return builder.Build(offsets, shared, anchor, description, topic, time);
    }

    // The top of bin k in Hz: a block's bins are spaced by the sample rate over 512 (A/52 7.1.3), rounded half to even.
    private int Hertz(int bin) => (int)RoundHalfEven((long)bin * _key.SampleRate, 512);

    private static long RoundHalfEven(long numerator, long denominator)
    {
        long quotient = Math.DivRem(numerator, denominator, out long rest);
        return rest * 2 > denominator || (rest * 2 == denominator && quotient % 2 != 0) ? quotient + 1 : quotient;
    }

    // The series of the program, once all its units are ended: offsets are the units' x values, those of the bitrate series
    // (shared), and time is the partition the per-second series use. The numeric series are added to series.
    public List<CategorySeries> Build(ReadOnlyMemory<double> offsets, Series? shared, TimeIndex? time, CodingNodes nodes, List<Series> series, CancellationToken cancellation)
    {
        var categories = new List<CategorySeries>();
        if (Count == 0)
        {
            return categories;
        }

        if (offsets.Length != Count)
        {
            throw new ArgumentException("Every unit of the coding series has an x value.", nameof(offsets));
        }

        bool isFixed = !_key.IsEac3 && _longest <= BlocksPerFrame;
        if (nodes.BlockSwitching is { } switching)
        {
            categories.Add(Make("Blocks With Block Switching", CodingCategories.BlockSwitching, GraphConcept.BlockSwitching, FrameResult.BlockSwitched, isFixed, offsets, shared,
                switching, Explain.BlockSwitchingSeries, Topics.AudioBlocks, time, cancellation));
        }

        if (_key.Acmod >= 2 && nodes.Coupling is { } coupling)
        {
            categories.Add(Make("Blocks With Coupling", CodingCategories.Coupling, GraphConcept.StereoCoding, FrameResult.BlockCoupled, isFixed, offsets, shared,
                coupling, Explain.CouplingSeries, Topics.Coupling, time, cancellation));
        }

        if (_key.Acmod == 2 && nodes.Rematrixing is { } rematrixing)
        {
            categories.Add(Make("Blocks With Rematrixing", CodingCategories.Rematrixing, GraphConcept.StereoCoding, FrameResult.BlockRematrixed, isFixed, offsets, shared,
                rematrixing, Explain.RematrixingSeries, Topics.Rematrixing, time, cancellation));
        }

        if (_key.IsEac3 && nodes.SpectralExtension is { } spectral)
        {
            categories.Add(Make("Blocks With Spectral Extension", CodingCategories.SpectralExtension, GraphConcept.CodedBandwidth, FrameResult.BlockSpectralExtension, isFixed, offsets,
                shared, spectral, Explain.SpectralExtensionSeries, Topics.SpectralExtension, time, cancellation));
        }

        AddBandwidth(offsets, nodes.CodedBandwidth, time, series, cancellation);
        return categories;
    }

    // "Coded Bandwidth" per unit, at the units that have a value, and "Coded Bandwidth Over Time", each second's highest.
    private void AddBandwidth(ReadOnlyMemory<double> offsets, Node? anchor, TimeIndex? time, List<Series> series, CancellationToken cancellation)
    {
        int valued = 0;
        for (int i = 0; i < Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            valued += _ends[i] != 255 ? 1 : 0;
        }

        if (valued == 0)
        {
            return;
        }

        var values = new double[valued];
        double[]? own = valued == Count ? null : new double[valued];
        for (int i = 0, n = 0; i < Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            if (_ends[i] != 255)
            {
                values[n] = Hertz(_ends[i]);
                if (own is not null)
                {
                    own[n] = offsets.Span[i];
                }

                n++;
            }
        }

        series.Add(new Series("Coded Bandwidth", "Hz", SeriesAxis.FrameOffset, own ?? offsets, values)
        {
            Description = Explain.CodedBandwidthSeries,
            Concept = GraphConcept.CodedBandwidth,
            Quantity = "Coded Bandwidth",
        });
        if (time is null)
        {
            return;
        }

        var starts = new List<double>();
        var highest = new List<double>();
        ReadOnlySpan<int> first = time.FirstUnits.Span;
        for (int interval = 0; interval < first.Length; interval++)
        {
            if (interval % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            int max = -1;
            for (int unit = first[interval]; unit < (interval + 1 < first.Length ? first[interval + 1] : Count); unit++)
            {
                if (_ends[unit] != 255)
                {
                    max = Math.Max(max, _ends[unit]);
                }
            }

            if (max >= 0)
            {
                starts.Add(time.Starts.Span[interval]);
                highest.Add(Hertz(max));
            }
        }

        if (starts.Count > 0)
        {
            series.Add(new Series("Coded Bandwidth Over Time", "Hz", SeriesAxis.Seconds, starts.ToArray(), highest.ToArray(), anchor)
            {
                Description = Explain.CodedBandwidthOverTimeSeries,
                Concept = GraphConcept.CodedBandwidth,
                Quantity = "Coded Bandwidth",
            });
        }
    }
}
