using System.Runtime.InteropServices;
using Encspect.Core;

namespace Encspect.Formats.Aac;

// The categories of the coding series, shared by the series and the statistics nodes they are anchored to, so a category has
// one name and one description in the tree and in the graph.
internal static class CodingCategories
{
    // By window_sequence (ISO/IEC 14496-3 4.5.2): only long, long start, eight short, long stop.
    public static readonly string[] WindowSequenceNames = ["Only Long", "Long Start", "Eight Short", "Long Stop"];

    public static readonly Category[] WindowSequences =
    [
        new(WindowSequenceNames[0], Explain.WindowOnlyLong) { Topic = Topics.Windows },
        new(WindowSequenceNames[1], Explain.WindowLongStart) { Topic = Topics.Windows },
        new(WindowSequenceNames[2], Explain.WindowEightShort) { Topic = Topics.Windows },
        new(WindowSequenceNames[3], Explain.WindowLongStop) { Topic = Topics.Windows },
    ];

    // By window sequence: the size of the transform. Long, start and stop windows are counted together, as EncSpot counts
    // MPEG audio's block types.
    public static readonly byte[] SizeOfWindow = [0, 0, 1, 0];

    public static readonly Category[] Sizes =
    [
        new("Long, start and stop", Explain.SizeLong) { Topic = Topics.Windows },
        new("Short", Explain.SizeShort) { Topic = Topics.Windows },
    ];

    // The codes of the "Stereo Coding" series. The first four are MPEG audio's stereo codings in the order of its charts' colors.
    public const byte CodeLeftRight = 0;
    public const byte CodeMidSide = 1;
    public const byte CodeIntensity = 2;
    public const byte CodeMidSideAndIntensity = 3;
    public const byte CodeReserved = 4;

    public static readonly Category[] WindowShapes =
    [
        new("Sine", Explain.ShapeSine) { Topic = Topics.Windows },
        new("KBD", Explain.ShapeKbd) { Topic = Topics.Windows },
    ];

    public static readonly Category[] WithTns =
    [
        new("With TNS", Explain.FlagWithTns) { Topic = Topics.Tns },
        new("Without TNS", Explain.FlagWithoutTns) { Topic = Topics.Tns },
    ];

    public static readonly Category[] WithPulses =
    [
        new("With pulse data", Explain.FlagWithPulses) { Topic = Topics.PulseData },
        new("Without pulse data", Explain.FlagWithoutPulses) { Topic = Topics.PulseData },
    ];

    public static readonly Category[] WithNoise =
    [
        new("With noise substitution", Explain.FlagWithNoise) { Topic = Topics.Pns },
        new("Without noise substitution", Explain.FlagWithoutNoise) { Topic = Topics.Pns },
    ];

    public static readonly Category[] WithSbr =
    [
        new("With SBR", Explain.FlagWithSbr) { Topic = Topics.Sbr },
        new("Without SBR", Explain.FlagWithoutSbr) { Topic = Topics.Sbr },
    ];

    public static readonly Category[] WithParametricStereo =
    [
        new("With parametric stereo", Explain.FlagWithPs) { Topic = Topics.ParametricStereo },
        new("Without parametric stereo", Explain.FlagWithoutPs) { Topic = Topics.ParametricStereo },
    ];

    public static readonly Category[] SbrFrameClasses =
    [
        new("FIXFIX", Explain.SbrFrameClassFixFix) { Topic = Topics.Sbr },
        new("FIXVAR", Explain.SbrFrameClassFixVar) { Topic = Topics.Sbr },
        new("VARFIX", Explain.SbrFrameClassVarFix) { Topic = Topics.Sbr },
        new("VARVAR", Explain.SbrFrameClassVarVar) { Topic = Topics.Sbr },
    ];

    public static readonly Category[] SbrEnvelopes = EnvelopeCategories(1, 5, Explain.SbrEnvelopeCount, Topics.Sbr);

    // The first category is no envelopes: the parameters of the block before are kept.
    public static readonly Category[] PsEnvelopes = [new("No envelopes", Explain.PsEnvelopeCount) { Topic = Topics.ParametricStereo }, .. EnvelopeCategories(1, 4, Explain.PsEnvelopeCount, Topics.ParametricStereo)];

    // By the finer of iid_mode and icc_mode where they are enabled: 10, 20 or 34 bands.
    public static readonly Category[] PsBands =
    [
        new("No parameters", Explain.PsBandCount) { Topic = Topics.ParametricStereo },
        new("10 bands", Explain.PsBandCount) { Topic = Topics.ParametricStereo },
        new("20 bands", Explain.PsBandCount) { Topic = Topics.ParametricStereo },
        new("34 bands", Explain.PsBandCount) { Topic = Topics.ParametricStereo },
    ];

    // By the coherence mode (Table 8.27): 0 to 2 mix with procedure Ra, 3 to 5 with Rb.
    public static readonly Category[] PsMixing =
    [
        new("Procedure Ra", Explain.PsMixingProcedure) { Topic = Topics.ParametricStereo },
        new("Procedure Rb", Explain.PsMixingProcedure) { Topic = Topics.ParametricStereo },
    ];

    // Whether the extension of the block's parametric stereo data carries IPD and OPD (enable_ipdopd).
    public static readonly Category[] PsPhase =
    [
        new("No phase differences", Explain.PsPhaseDifferences) { Topic = Topics.ParametricStereo },
        new("Phase differences", Explain.PsPhaseDifferences) { Topic = Topics.ParametricStereo },
    ];

    private static Category[] EnvelopeCategories(int first, int last, string description, string topic) =>
        [.. Enumerable.Range(first, last - first + 1).Select(n => new Category(n == 1 ? "1 envelope" : $"{n} envelopes", description) { Topic = topic })];

    public static readonly Category[] StereoCodings =
    [
        new("Left/right stereo", Explain.CodingLeftRight) { Topic = Topics.MidSide },
        new("Mid/side stereo", Explain.CodingMidSide) { Topic = Topics.MidSide },
        new("Intensity stereo", Explain.CodingIntensity) { Topic = Topics.Intensity },
        new("Mid/side and intensity stereo", Explain.CodingMidSideAndIntensity) { Topic = Topics.Intensity },
        new("Reserved", Explain.CodingReserved) { Topic = Topics.MidSide, IsReserved = true },
    ];
}

// The statistics nodes the coding series are anchored to.
internal sealed record CodingNodes(Node? WindowSequences, Node? BlockSizes, Node? StereoCoding, Node? CodedBandwidth, Node? IntensityBound = null)
{
    public Node? Shapes { get; init; }

    public Node? MidSideBands { get; init; }

    public Node? Noise { get; init; }

    public Node? Tns { get; init; }

    public Node? Pulses { get; init; }

    public Node? Sbr { get; init; }

    public Node? ParametricStereo { get; init; }

    public Node? SbrFrameClasses { get; init; }

    public Node? SbrEnvelopes { get; init; }

    public Node? PsEnvelopes { get; init; }

    public Node? PsBands { get; init; }

    public Node? PsMixing { get; init; }

    public Node? PsPhase { get; init; }

    public Node? LastCoefficient { get; init; }

    public Node? Escapes { get; init; }
}

// Records the coding series of a stream while its raw data blocks are read: the window sequence of every channel stream, the
// stereo coding of every channel pair and the coded bandwidth of every unit (an access unit, an ADTS frame or an ADIF block).
// The block reader calls Stream and Pair from the places that count the same things for the tally, so a series equals its
// tally; the caller ends each unit with BlockLog.EndUnit. Nothing here depends on the analysis it serves apart from the
// stream's setup, and a recorder belongs to one stream (docs/design/coding-graphs.md, "When the codes are made").
internal sealed class CodingRecorder(StreamSetup setup)
{
    private readonly List<byte> _windows = [];
    private readonly List<byte> _shapes = [];
    private readonly List<byte> _tns = [];
    private readonly List<byte> _pulses = [];
    private readonly List<byte> _noise = [];
    private readonly List<byte> _sbr = [];
    private readonly List<byte> _ps = [];
    private readonly CategorySeriesBuilder _shapeSeries = new("Window Shapes", CodingCategories.WindowShapes, "channel stream");
    private readonly CategorySeriesBuilder _tnsSeries = new("TNS", CodingCategories.WithTns, "channel stream");
    private readonly CategorySeriesBuilder _pulseSeries = new("Pulse Data", CodingCategories.WithPulses, "channel stream");
    private readonly CategorySeriesBuilder _noiseSeries = new("Noise Substitution", CodingCategories.WithNoise, "channel stream");

    // The SBR and parametric stereo series exist from the first raw data block with SBR data, so that a stream without it keeps nothing for them but
    // the number of raw data blocks of each unit, which the units before that block get as values without SBR.
    private CategorySeriesBuilder? _sbrSeries;
    private readonly LazySeries _sbrFrameClasses = new("SBR Frame Classes", CodingCategories.SbrFrameClasses, "SBR channel");
    private readonly LazySeries _sbrEnvelopes = new("SBR Envelopes", CodingCategories.SbrEnvelopes, "SBR channel");
    private readonly LazySeries _psEnvelopes = new("Parametric Stereo Envelopes", CodingCategories.PsEnvelopes, "raw data block");
    private readonly LazySeries _psBands = new("Parametric Stereo Bands", CodingCategories.PsBands, "raw data block");
    private readonly LazySeries _psMixing = new("Parametric Stereo Mixing", CodingCategories.PsMixing, "raw data block");
    private readonly LazySeries _psPhase = new("Parametric Stereo Phase", CodingCategories.PsPhase, "raw data block");
    private CategorySeriesBuilder? _psSeries;
    private ChunkedList<byte>? _blockCounts = new();

    // The mid/side bands and the bands of common window pairs of the unit being read, and per unit the share of them in tenths of a percent, -1 for a unit without such bands.
    private int _unitMidSide;
    private int _unitPairBands;
    private readonly ChunkedList<short> _midSideShare = new();
    private int _shareUnits;
    private readonly List<byte> _pairs = [];
    private readonly CategorySeriesBuilder _windowSeries = new("Block Usage", CodingCategories.WindowSequences, "channel stream", concept: GraphConcept.BlockSwitching);
    private CategorySeriesBuilder? _pairSeries;
    private readonly ChunkedList<int> _hertz = new();
    private int _unitHertz = -1;

    // The highest nonzero line of the unit's channel streams in Hz (-1 where all their values are zero), and the share of its nonzero values that are escape-coded
    // in tenths of a percent (-1 where it has none).
    private readonly ChunkedList<int> _valueHertz = new();
    private int _unitValueHertz = -1;
    private readonly ChunkedList<short> _escapeShare = new();
    private long _unitNonZero;
    private long _unitEscapes;
    private int _escapeUnits;
    private readonly ChunkedList<int> _boundHertz = new();
    private int _unitBound = -1;
    private int _pairUnits;

    // Units ended so far.
    public int Count { get; private set; }

    // Units by coded bandwidth in whole hertz, for the statistics node: the highest over the unit's channel streams.
    public SortedDictionary<int, long> Bandwidths { get; } = [];

    // The same by the highest nonzero line of the unit's channel streams.
    public SortedDictionary<int, long> ValueBandwidths { get; } = [];

    // A channel stream of window_sequence windowSequence whose max_sfb bands end at line top of its window.
    // Its highest nonzero line ends at line last of its window (0 where every value is zero); it has nonZero nonzero values, escapes of them escape-coded.
    public void Stream(int windowSequence, int top, int last, long nonZero, long escapes)
    {
        _windows.Add((byte)windowSequence);
        int hertz = Hertz(windowSequence == 2, top);
        _unitHertz = Math.Max(_unitHertz, hertz);
        if (last > 0)
        {
            _unitValueHertz = Math.Max(_unitValueHertz, Hertz(windowSequence == 2, last));
        }

        _unitNonZero += nonZero;
        _unitEscapes += escapes;
    }

    public void Pair(byte code) => _pairs.Add(code);

    // What the channel stream uses besides its window: the window shape, temporal noise shaping, pulse data and noise substitution.
    public void Tools(int windowShape, bool hasTns, bool hasPulses, bool hasNoise)
    {
        _shapes.Add((byte)windowShape);
        _tns.Add(hasTns ? (byte)0 : (byte)1);
        _pulses.Add(hasPulses ? (byte)0 : (byte)1);
        _noise.Add(hasNoise ? (byte)0 : (byte)1);
    }

    // An SBR payload read to its end, with the figures Sbr.ReadData states of it: the frame class and the envelope count of each channel, and the parametric stereo data's envelopes and modes.
    public void SbrPayload(ReadOnlySpan<int> figures)
    {
        for (int c = 0; c < 2; c++)
        {
            if (figures[Sbr.FigureFrameClass + (2 * c)] >= 0)
            {
                _sbrFrameClasses.Values.Add((byte)figures[Sbr.FigureFrameClass + (2 * c)]);
                _sbrEnvelopes.Values.Add((byte)(figures[Sbr.FigureEnvelopes + (2 * c)] - 1));
            }
        }

        if (figures[Sbr.FigurePsEnvelopes] >= 0)
        {
            _psEnvelopes.Values.Add((byte)figures[Sbr.FigurePsEnvelopes]);
            _psBands.Values.Add((byte)CodingTally.PsBandClass(figures));
            _psMixing.Values.Add((byte)figures[Sbr.FigurePsMixing]);
            _psPhase.Values.Add((byte)figures[Sbr.FigurePsPhase]);
        }
    }

    // A raw data block: whether it holds SBR data and whether that carries parametric stereo; a block that could not be read has neither.
    public void Block(bool hasSbr, bool hasPs)
    {
        _sbr.Add(hasSbr ? (byte)0 : (byte)1);
        _ps.Add(hasPs ? (byte)0 : (byte)1);
    }

    // The bands of a pair with a common window, and those of them coded as mid/side (not intensity stereo bands).
    public void PairBands(int midSide, int bands)
    {
        _unitMidSide += midSide;
        _unitPairBands += bands;
    }

    // Units that have an intensity stereo bound (the lowest band of codebook 14 or 15 in any pair of the unit), and the lowest and highest of them in Hz.
    public int IntensityUnits { get; private set; }

    public int LowestBound { get; private set; } = int.MaxValue;

    public int HighestBound { get; private set; } = -1;

    // An intensity stereo band of a pair's second channel, whose lower edge is line of its window.
    public void Intensity(bool isShort, int line)
    {
        int hertz = Hertz(isShort, line);
        _unitBound = _unitBound < 0 ? hertz : Math.Min(_unitBound, hertz);
    }

    // The top of the line-th line of a window in Hz at the core's sample rate: lines are spaced by half the sample rate over the
    // lines of the window, 1024 or 960 for a long window and an eighth of that for a short one.
    private int Hertz(bool isShort, int line)
    {
        long lines = isShort ? setup.LongLines / 8 : setup.LongLines;
        return (int)Math.Min(int.MaxValue, CodingStatistics.RoundHalfEven((long)line * setup.SampleRate, 2 * lines));
    }

    private void AddSbr()
    {
        if (_sbrSeries is null && _sbr.Contains(0))
        {
            _sbrSeries = new("SBR", CodingCategories.WithSbr, "raw data block");
            _psSeries = new("Parametric Stereo", CodingCategories.WithParametricStereo, "raw data block");
            byte[] without = new byte[CategorySeries.MaxValuesPerUnit];
            Array.Fill(without, (byte)1);
            for (int unit = 0; unit < _blockCounts!.Count; unit++)
            {
                _sbrSeries.Add(without.AsSpan(0, _blockCounts[unit]));
                _psSeries.Add(without.AsSpan(0, _blockCounts[unit]));
            }

            _blockCounts = null;
        }

        if (_sbrSeries is null)
        {
            _blockCounts!.Add((byte)Math.Min(_sbr.Count, CategorySeries.MaxValuesPerUnit));
            return;
        }

        _sbrSeries.Add(CollectionsMarshal.AsSpan(_sbr));
        _psSeries!.Add(CollectionsMarshal.AsSpan(_ps));
    }

    public void EndUnit()
    {
        _windowSeries.Add(CollectionsMarshal.AsSpan(_windows));
        _shapeSeries.Add(CollectionsMarshal.AsSpan(_shapes));
        _tnsSeries.Add(CollectionsMarshal.AsSpan(_tns));
        _pulseSeries.Add(CollectionsMarshal.AsSpan(_pulses));
        _noiseSeries.Add(CollectionsMarshal.AsSpan(_noise));
        AddSbr();
        _sbrFrameClasses.End(Count);
        _sbrEnvelopes.End(Count);
        _psEnvelopes.End(Count);
        _psBands.End(Count);
        _psMixing.End(Count);
        _psPhase.End(Count);
        _midSideShare.Add(_unitPairBands == 0 ? (short)-1 : (short)Math.Round(_unitMidSide * 1000.0 / _unitPairBands, MidpointRounding.ToEven));
        _shareUnits += _unitPairBands == 0 ? 0 : 1;
        if (_pairs.Count > 0 && _pairSeries is null)
        {
            // The first pair: the units before it have no pair and get empty units, so that a stream without pairs keeps nothing.
            _pairSeries = new("Stereo Coding", CodingCategories.StereoCodings, "channel pair", concept: GraphConcept.StereoCoding);
            for (; _pairUnits < Count; _pairUnits++)
            {
                _pairSeries.Add([]);
            }
        }

        if (_pairSeries is not null)
        {
            _pairSeries.Add(CollectionsMarshal.AsSpan(_pairs));
            _pairUnits++;
        }

        _hertz.Add(_unitHertz);
        _valueHertz.Add(_unitValueHertz);
        if (_unitValueHertz >= 0)
        {
            ValueBandwidths[_unitValueHertz] = ValueBandwidths.GetValueOrDefault(_unitValueHertz) + 1;
        }

        _escapeShare.Add(_unitNonZero == 0 ? (short)-1 : (short)CodingStatistics.RoundHalfEven(_unitEscapes * 1000, _unitNonZero));
        _escapeUnits += _unitNonZero == 0 ? 0 : 1;
        _boundHertz.Add(_unitBound);
        if (_unitBound >= 0)
        {
            IntensityUnits++;
            LowestBound = Math.Min(LowestBound, _unitBound);
            HighestBound = Math.Max(HighestBound, _unitBound);
        }

        if (_unitHertz >= 0)
        {
            Bandwidths[_unitHertz] = Bandwidths.GetValueOrDefault(_unitHertz) + 1;
        }

        _windows.Clear();
        _shapes.Clear();
        _tns.Clear();
        _pulses.Clear();
        _noise.Clear();
        _sbr.Clear();
        _ps.Clear();
        _unitMidSide = 0;
        _unitPairBands = 0;
        _pairs.Clear();
        _unitHertz = -1;
        _unitValueHertz = -1;
        _unitNonZero = 0;
        _unitEscapes = 0;
        _unitBound = -1;
        Count++;
    }

    // The series of the stream, once all its units are ended: offsets are the units' x values, which are those of the bitrate series
    // (shared) where every unit has a point, else the units' own; time is the partition the per-second series use. The numeric series
    // are added to series.
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

        if (nodes.WindowSequences is { } windowAnchor)
        {
            CategorySeries windows = _windowSeries.Build(offsets, shared, windowAnchor, Explain.WindowSequencesSeries, Topics.Windows, time);
            categories.Add(windows);
            categories.Add(windows.Group("Block Sizes", CodingCategories.Sizes, CodingCategories.SizeOfWindow, nodes.BlockSizes, Explain.BlockSizesSeries, Topics.Windows));
        }

        if (_pairSeries is not null && nodes.StereoCoding is { } pairAnchor)
        {
            categories.Add(_pairSeries.Build(offsets, shared, pairAnchor, Explain.StereoCodingSeries, Topics.MidSide, time));
        }

        void Add(CategorySeriesBuilder? builder, Node? anchor, string description, string topic)
        {
            if (builder is not null && anchor is not null)
            {
                categories.Add(builder.Build(offsets, shared, anchor, description, topic, time));
            }
        }

        Add(_shapeSeries, nodes.Shapes, Explain.WindowShapesSeries, Topics.Windows);
        Add(_tnsSeries, nodes.Tns, Explain.TnsSeries, Topics.Tns);
        Add(_pulseSeries, nodes.Pulses, Explain.PulseSeries, Topics.PulseData);
        Add(_noiseSeries, nodes.Noise, Explain.NoiseSeries, Topics.Pns);
        Add(_sbrFrameClasses.Builder, nodes.SbrFrameClasses, Explain.SbrFrameClassesSeries, Topics.Sbr);
        Add(_sbrEnvelopes.Builder, nodes.SbrEnvelopes, Explain.SbrEnvelopesSeries, Topics.Sbr);
        Add(_psEnvelopes.Builder, nodes.PsEnvelopes, Explain.PsEnvelopesSeries, Topics.ParametricStereo);
        Add(_psBands.Builder, nodes.PsBands, Explain.PsBandsSeries, Topics.ParametricStereo);
        Add(_psMixing.Builder, nodes.PsMixing, Explain.PsMixingSeries, Topics.ParametricStereo);
        Add(_psPhase.Builder, nodes.PsPhase, Explain.PsPhaseSeries, Topics.ParametricStereo);
        if (_sbrSeries is not null)
        {
            Add(_sbrSeries, nodes.Sbr, Explain.SbrSeries, Topics.Sbr);
            Add(_psSeries!, nodes.ParametricStereo, Explain.PsSeries, Topics.ParametricStereo);
        }
        AddMidSideBands(offsets, nodes.MidSideBands, series, cancellation);
        AddBound(offsets, nodes.IntensityBound, series, cancellation);
        AddBandwidth(offsets, nodes.CodedBandwidth, time, series, cancellation);
        AddHertz(_valueHertz, "Last Non-Zero Coefficient", Explain.LastCoefficientSeries, Explain.LastCoefficientOverTimeSeries, offsets, nodes.LastCoefficient, time, series, cancellation);
        AddEscapes(offsets, nodes.Escapes, series, cancellation);
        return categories;
    }

    // "Mid/Side Bands" per unit: the share of the bands of its common window pairs that are coded as mid/side, in percent, at the units that have such bands.
    private void AddMidSideBands(ReadOnlyMemory<double> offsets, Node? anchor, List<Series> series, CancellationToken cancellation)
    {
        if (_shareUnits == 0 || anchor is null)
        {
            return;
        }

        var values = new double[_shareUnits];
        double[]? own = _shareUnits == Count ? null : new double[_shareUnits];
        for (int i = 0, n = 0; i < Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            if (_midSideShare[i] >= 0)
            {
                values[n] = _midSideShare[i] / 10.0;
                if (own is not null)
                {
                    own[n] = offsets.Span[i];
                }

                n++;
            }
        }

        series.Add(new Series("Mid/Side Bands", "%", SeriesAxis.FrameOffset, own is null ? offsets : own, values, anchor)
        {
            Description = Explain.MidSideBandsSeries,
            Concept = GraphConcept.StereoCoding,
            Quantity = "Mid/Side Bands",
        });
    }

    // "Escape-Coded Coefficients" per unit: the share of its non-zero coefficients that are escape-coded, in percent, at the units that have a non-zero coefficient.
    private void AddEscapes(ReadOnlyMemory<double> offsets, Node? anchor, List<Series> series, CancellationToken cancellation)
    {
        if (_escapeUnits == 0 || anchor is null)
        {
            return;
        }

        var values = new double[_escapeUnits];
        double[]? own = _escapeUnits == Count ? null : new double[_escapeUnits];
        for (int i = 0, n = 0; i < Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            if (_escapeShare[i] >= 0)
            {
                values[n] = _escapeShare[i] / 10.0;
                if (own is not null)
                {
                    own[n] = offsets.Span[i];
                }

                n++;
            }
        }

        series.Add(new Series("Escape-Coded Coefficients", "%", SeriesAxis.FrameOffset, own is null ? offsets : own, values, anchor)
        {
            Description = Explain.EscapesSeries,
            Quantity = "Escape-Coded Coefficients",
        });
    }

    // "Intensity Stereo Bound" per unit, at the units that have one.
    private void AddBound(ReadOnlyMemory<double> offsets, Node? anchor, List<Series> series, CancellationToken cancellation)
    {
        if (IntensityUnits == 0)
        {
            return;
        }

        var x = new double[IntensityUnits];
        var values = new double[IntensityUnits];
        for (int i = 0, n = 0; i < Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            if (_boundHertz[i] >= 0)
            {
                x[n] = offsets.Span[i];
                values[n++] = _boundHertz[i];
            }
        }

        series.Add(new Series("Intensity Stereo Bound", "Hz", SeriesAxis.FrameOffset, x, values, anchor)
        {
            Description = Explain.IntensityStereoBoundSeries,
            Concept = GraphConcept.StereoCoding,
            Quantity = SeriesQuantities.IntensityStereoBound,
        });
    }

    // "Coded Bandwidth" per unit, at the units that have a value, and "Coded Bandwidth Over Time", each interval's highest; the same for "Last Non-Zero Coefficient".
    private void AddBandwidth(ReadOnlyMemory<double> offsets, Node? anchor, TimeIndex? time, List<Series> series, CancellationToken cancellation) =>
        AddHertz(_hertz, "Coded Bandwidth", Explain.CodedBandwidthSeries, Explain.CodedBandwidthOverTimeSeries, offsets, anchor, time, series, cancellation);

    private void AddHertz(ChunkedList<int> hertz, string name, string description, string overTimeDescription, ReadOnlyMemory<double> offsets, Node? anchor, TimeIndex? time, List<Series> series,
        CancellationToken cancellation)
    {
        int valued = 0;
        for (int i = 0; i < Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            valued += hertz[i] >= 0 ? 1 : 0;
        }

        if (valued == 0)
        {
            return;
        }

        var values = new double[valued];
        ReadOnlyMemory<double> x = offsets;
        double[]? own = valued == Count ? null : new double[valued];
        for (int i = 0, n = 0; i < Count; i++)
        {
            if (i % 4096 == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            if (hertz[i] >= 0)
            {
                values[n] = hertz[i];
                if (own is not null)
                {
                    own[n] = offsets.Span[i];
                }

                n++;
            }
        }

        if (own is not null)
        {
            x = own;
        }

        series.Add(new Series(name, "Hz", SeriesAxis.FrameOffset, x, values)
        {
            Description = description,
            Concept = GraphConcept.CodedBandwidth,
            Quantity = name,
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
                max = Math.Max(max, hertz[unit]);
            }

            if (max >= 0)
            {
                starts.Add(time.Starts.Span[interval]);
                highest.Add(max);
            }
        }

        if (starts.Count > 0)
        {
            series.Add(new Series($"{name} Over Time", "Hz", SeriesAxis.Seconds, starts.ToArray(), highest.ToArray(), anchor)
            {
                Description = overTimeDescription,
                Concept = GraphConcept.CodedBandwidth,
                Quantity = name,
            });
        }
    }
}

// A category series that exists from the first unit with a value, so that a stream without any keeps nothing: the units before it get empty units.
internal sealed class LazySeries(string name, IReadOnlyList<Category> categories, string valueLabel)
{
    private int _units;

    public List<byte> Values { get; } = [];

    public CategorySeriesBuilder? Builder { get; private set; }

    // Ends the unit with index count.
    public void End(int count)
    {
        if (Builder is null && Values.Count > 0)
        {
            Builder = new CategorySeriesBuilder(name, categories, valueLabel);
            for (; _units < count; _units++)
            {
                Builder.Add([]);
            }
        }

        if (Builder is not null)
        {
            Builder.Add(CollectionsMarshal.AsSpan(Values));
            _units++;
        }

        Values.Clear();
    }
}
