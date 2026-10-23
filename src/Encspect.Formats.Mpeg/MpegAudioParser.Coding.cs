using Encspect.Core;
using static Encspect.Formats.Mpeg.CodingCategories;

namespace Encspect.Formats.Mpeg;

// The categories of the coding series, in a class of their own: static fields of a partial class initialize in an order that
// depends on the file, and these are read by the tally in one file and the series in another.
internal static class CodingCategories
{
    // By StereoCode: the codes of the "Stereo Coding" series. The first four are the Layer III stereo codings in the order of
    // the charts' colors, which the "Joint Stereo Modes" series keeps.
    public const byte CodeLeftRight = 0;
    public const byte CodeMidSide = 1;
    public const byte CodeIntensity = 2;
    public const byte CodeMidSideAndIntensity = 3;
    public const byte CodeDual = 4;
    public const byte CodeMono = 5;

    public static readonly Category[] StereoCodings =
    [
        new("Left/right stereo", Explain.CodingLeftRight) { Topic = Topics.JointStereo },
        new("Mid/side stereo", Explain.CodingMidSide) { Topic = Topics.MidSideStereo },
        new("Intensity stereo", Explain.CodingIntensity) { Topic = Topics.IntensityStereo },
        new("Mid/side and intensity stereo", Explain.CodingMidSideAndIntensity) { Topic = Topics.JointStereo },
        new("Dual channel", Explain.CodingDual) { Topic = Topics.ChannelModes },
        new("Mono", Explain.CodingMono) { Topic = Topics.ChannelModes },
    ];

    // The categories of "Joint Stereo Modes": the joint stereo frames of Layer III, in the order of StereoCodings.
    public static readonly Category[] JointCodings =
    [
        new("Left/right stereo", Explain.JointLeftRight) { Topic = Topics.JointStereo },
        new("Mid/side stereo", Explain.JointMidSide) { Topic = Topics.MidSideStereo },
        new("Intensity stereo", Explain.JointIntensity) { Topic = Topics.IntensityStereo },
        new("Mid/side and intensity stereo", Explain.JointMidSideAndIntensity) { Topic = Topics.JointStereo },
    ];

    // The tally lists the joint stereo frames by mode extension; the series by the order above.
    public static readonly byte[] JointCodeOfExtension = [CodeLeftRight, CodeIntensity, CodeMidSide, CodeMidSideAndIntensity];

    public static readonly Category[] ChannelModeCategories =
    [
        new(MpegHeader.ChannelModes[0], Explain.ChannelStereo) { Topic = Topics.ChannelModes },
        new(MpegHeader.ChannelModes[1], Explain.ChannelJoint) { Topic = Topics.JointStereo },
        new(MpegHeader.ChannelModes[2], Explain.ChannelDual) { Topic = Topics.ChannelModes },
        new(MpegHeader.ChannelModes[3], Explain.ChannelMono) { Topic = Topics.ChannelModes },
    ];

    public static readonly Category[] BoundCategories =
    [
        .. Enumerable.Range(0, 4).Select(e => new Category($"Bands {(e + 1) * 4} to 31", Explain.IntensityBound) { Topic = Topics.IntensityStereo }),
    ];

    // By block kind: long, start, short, mixed, stop, reserved, as the tally lists them.
    public static readonly string[] BlockKindNames = ["Long", "Start", "Short", "Mixed", "Stop", "Reserved"];

    public static readonly Category[] BlockCategories =
    [
        new(BlockKindNames[0], Explain.BlockNormal) { Topic = Topics.BlockSwitching },
        new(BlockKindNames[1], Explain.BlockStart) { Topic = Topics.BlockSwitching },
        new(BlockKindNames[2], Explain.BlockShort) { Topic = Topics.BlockSwitching },
        new(BlockKindNames[3], Explain.IsMixedBlock) { Topic = Topics.BlockSwitching },
        new(BlockKindNames[4], Explain.BlockStop) { Topic = Topics.BlockSwitching },
        new(BlockKindNames[5], Explain.BlockReserved) { Topic = Topics.BlockSwitching, IsReserved = true },
    ];

    // By block kind: EncSpot counts start and stop blocks as long ones.
    public static readonly string[] SizeNames = ["Long, start and stop", "Short", "Mixed", "Reserved"];

    public static readonly byte[] SizeOfKind = [0, 0, 1, 2, 0, 3];

    public static readonly Category[] SizeCategories =
    [
        new(SizeNames[0], Explain.SizeLong) { Topic = Topics.BlockSwitching },
        new(SizeNames[1], Explain.SizeShort) { Topic = Topics.BlockSwitching },
        new(SizeNames[2], Explain.SizeMixed) { Topic = Topics.BlockSwitching },
        new(SizeNames[3], Explain.SizeReserved) { Topic = Topics.BlockSwitching, IsReserved = true },
    ];
}

// The coding choices of a stream's frames as category series and numeric series (docs/design/coding-graphs.md): the channel
// mode, the stereo coding of all frames, the Layer III joint stereo modes and block usage, the Layer I and II intensity
// stereo bounds and coded bandwidth, and how the frames divide their bits between the two channels. They are derived at the
// end from the frame records and the kept side information and audio data (the Series pass), so a stream that a tag or a
// change of header splits is built from its own frames and nothing is kept per frame beyond the codes.
public sealed partial class MpegAudioParser
{
    // How a frame codes its channels: its channel mode, and for joint stereo the tools its mode extension selects. In
    // Layers I and II a joint stereo frame is intensity stereo above its bound, whatever the bound.
    private static byte StereoCode(MpegHeader header) => header.ChannelModeBits switch
    {
        0 => CodeLeftRight,
        2 => CodeDual,
        3 => CodeMono,
        _ when header.Layer != 3 => CodeIntensity,
        _ => JointCodeOfExtension[header.ModeExtension],
    };

    // The tally nodes the series of a stream are anchored to, per region.
    private sealed class TallyNodes
    {
        public Node? ChannelModes { get; set; }

        public Node? StereoCoding { get; set; }

        public Node? JointStereoModes { get; set; }

        public Node? IntensityStereoBounds { get; set; }

        public Node? BlockUsage { get; set; }

        public Node? BlockSizes { get; set; }

        public Node? CodedBandwidth { get; set; }

        public Node? ChannelBitShare { get; set; }
    }

    // The figure of an audio frame (FrameFigures) from the bytes its record keeps; empty where it keeps none or the layer has
    // no such figure.
    private static FrameFigures.Figure FigureOf(BuildContext build, in Entry frame, MpegHeader header)
    {
        switch (frame.DataKind)
        {
            case FrameData.SideInfo:
                return FrameFigures.Layer3(build.Store.Span(frame.Data, frame.DataLength), header);
            case FrameData.Layer1:
                return FrameFigures.Layer1(build.Store.Span(frame.Data, frame.DataLength), header);
            case FrameData.Layer2:
                return FrameFigures.Layer2(build.Store.Span(frame.Data, frame.DataLength), header);
            default:
                return FrameFigures.Figure.Empty;
        }
    }

    // The share of the first channel in tenths of a percent, rounded half to even.
    private static int ShareTenths(long first, long both) => (int)RoundHalfEven(first * 1000, both);

    // The series of a stream's coding, filled frame by frame in the Series pass and built at its end.
    private sealed class StreamCoding
    {
        private readonly BuildContext _build;
        private readonly double[] _offsets;
        private readonly IReadOnlyList<RegionState> _regions;
        private readonly bool _hasTwoChannels;
        private readonly int _layer;
        private readonly CategorySeriesBuilder _modes = new("Channel Modes", ChannelModeCategories, "frame", 1);
        private readonly CategorySeriesBuilder _stereo = new("Stereo Coding", StereoCodings, "frame", 1, concept: GraphConcept.StereoCoding);
        private readonly CategorySeriesBuilder? _joint;
        private readonly CategorySeriesBuilder? _bounds;
        private readonly CategorySeriesBuilder? _blocks;
        private readonly byte[] _blockCodes = [];
        private readonly int _channels;
        private readonly Numeric? _bandwidth;
        private readonly Numeric? _share;
        private int _unit;

        public StreamCoding(BuildContext build, double[] offsets, int count, IReadOnlyList<RegionState> regions, MpegHeader header, bool hasTwoChannels, bool hasJointStereo)
        {
            (_build, _offsets, _regions, _hasTwoChannels, _layer) = (build, offsets, regions, hasTwoChannels, header.Layer);
            _channels = hasTwoChannels ? 2 : 1;
            if (_layer == 3)
            {
                _joint = hasJointStereo && Has(r => r.Nodes.JointStereoModes) ? new("Joint Stereo Modes", JointCodings, "frame", 1, concept: GraphConcept.StereoCoding) : null;
                if (Has(r => r.Nodes.BlockUsage))
                {
                    int granules = FrameFigures.Granules(header);
                    string[] names = [.. Enumerable.Range(1, granules).SelectMany(gr => Enumerable.Range(1, _channels).Select(ch => $"granule {gr}, channel {ch}"))];
                    _blocks = new("Block Usage", BlockCategories, "granule and channel", granules * _channels, names, concept: GraphConcept.BlockSwitching);
                    _blockCodes = new byte[granules * _channels];
                }
            }
            else
            {
                _bounds = hasJointStereo && Has(r => r.Nodes.IntensityStereoBounds) ? new("Intensity Stereo Bounds", BoundCategories, "frame", 1, isOrdered: true, concept: GraphConcept.StereoCoding) : null;
                _bandwidth = new Numeric(count, offsets);
            }

            _share = hasTwoChannels ? new Numeric(count, offsets) : null;
        }

        private bool Has(Func<RegionState, Node?> node) => _regions.Any(r => node(r) is not null);

        // One audio frame, the unit-th of the stream, starting in the given second of audio.
        public void Add(in Entry frame, MpegHeader header, int second)
        {
            _modes.Add((byte)header.ChannelModeBits);
            _stereo.Add(StereoCode(header));
            bool isJoint = header.ChannelModeBits == 1;
            _joint?.Add(isJoint ? StereoCode(header) : CategorySeries.None);
            _bounds?.Add(isJoint ? (byte)header.ModeExtension : CategorySeries.None);
            if (_blocks is not null)
            {
                AddBlocks(frame, header);
            }

            if (_bandwidth is not null || _share is not null)
            {
                FrameFigures.Figure figure = FigureOf(_build, frame, header);
                if (_bandwidth is not null)
                {
                    _bandwidth.Add(_unit, second, figure.HasBandwidth ? figure.Bandwidth : null, 0);
                }

                if (_share is not null)
                {
                    _share.Add(_unit, second, figure.HasShare ? ShareTenths(figure.FirstBits, figure.FirstBits + figure.SecondBits) / 10.0 : null,
                        figure.HasShare ? figure.FirstBits : 0, figure.HasShare ? figure.FirstBits + figure.SecondBits : 0);
                }
            }

            _unit++;
        }

        private void AddBlocks(in Entry frame, MpegHeader header)
        {
            Array.Fill(_blockCodes, CategorySeries.None);
            if (frame.HasSideInfo)
            {
                ReadOnlySpan<byte> sideInfo = _build.Store.Span(frame.Data, frame.DataLength);
                int channels = header.IsMono ? 1 : 2;
                for (int gr = 0; gr < FrameFigures.Granules(header); gr++)
                {
                    for (int ch = 0; ch < channels; ch++)
                    {
                        _blockCodes[(gr * _channels) + ch] = FrameFigures.BlockKind(sideInfo, header, gr, ch);
                    }
                }
            }

            _blocks!.Add(_blockCodes);
        }

        public void Build(Series bitrate, TimeIndex time, double[] secondStarts, List<Series> series, List<CategorySeries> categories)
        {
            ReadOnlyMemory<double> x = bitrate.X;
            categories.Add(_modes.Build(x, bitrate, Anchor(r => r.Nodes.ChannelModes), SeriesText.ChannelModesSeries, Topics.ChannelModes, time));
            categories.Add(_stereo.Build(x, bitrate, Anchor(r => r.Nodes.StereoCoding), SeriesText.StereoCodingSeries, Topics.JointStereo, time));
            if (_joint is not null)
            {
                categories.Add(_joint.Build(x, bitrate, Anchor(r => r.Nodes.JointStereoModes), SeriesText.JointStereoModesSeries, Topics.JointStereo, time));
            }

            if (_bounds is not null)
            {
                categories.Add(_bounds.Build(x, bitrate, Anchor(r => r.Nodes.IntensityStereoBounds), SeriesText.IntensityStereoBoundsSeries, Topics.IntensityStereo, time));
            }

            if (_blocks is not null)
            {
                CategorySeries usage = _blocks.Build(x, bitrate, Anchor(r => r.Nodes.BlockUsage), SeriesText.BlockUsageSeries, Topics.BlockSwitching, time);
                categories.Add(usage);
                categories.Add(usage.Group("Block Sizes", SizeCategories, SizeOfKind, Anchor(r => r.Nodes.BlockSizes), SeriesText.BlockSizesSeries, Topics.BlockSwitching));
            }

            Series? bandwidthSeries = null;
            if (_bandwidth?.Count > 0)
            {
                Node? anchor = Anchor(r => r.Nodes.CodedBandwidth);
                bandwidthSeries = _bandwidth.PerFrame("Coded Bandwidth", "Hz", SeriesText.CodedBandwidth, GraphConcept.CodedBandwidth, "Coded Bandwidth", null, _bandwidth.X);
                series.Add(bandwidthSeries);
                series.Add(_bandwidth.PerSecond("Coded Bandwidth Over Time", "Hz", SeriesText.CodedBandwidthOverTime, GraphConcept.CodedBandwidth, "Coded Bandwidth", secondStarts,
                    anchor, share: false));
            }

            if (_share?.Count > 0)
            {
                Node? anchor = Anchor(r => r.Nodes.ChannelBitShare);
                // Where both series lack the same frames (a Layer I or II frame whose allocation does not fit), they have the same x values and keep one array.
                ReadOnlyMemory<double> shareX = _share.X;
                if (bandwidthSeries is not null && bandwidthSeries.X.Span.SequenceEqual(shareX.Span))
                {
                    shareX = bandwidthSeries.X;
                }

                series.Add(_share.PerFrame("Channel Bit Share", "%", SeriesText.ChannelBitShare, GraphConcept.BitDistribution, "Channel Bit Share", null, shareX));
                series.Add(_share.PerSecond("Channel Bit Share Over Time", "%", SeriesText.ChannelBitShareOverTime, GraphConcept.BitDistribution, "Channel Bit Share", secondStarts,
                    anchor, share: true));
            }
        }

        // The tally node of the first of the stream's regions that has one.
        private Node? Anchor(Func<RegionState, Node?> node) => _regions.Select(node).FirstOrDefault(n => n is not null);
    }

    // A per-frame figure and its per-second form, for the frames that have a value: the x values are the stream's frame offsets
    // while every frame has one, and the frames' own once one has none.
    private sealed class Numeric
    {
        private readonly double[] _offsets;
        private readonly double[] _values;
        private double[]? _own;
        private readonly List<(double Max, long First, long Both)> _seconds = [];

        public Numeric(int count, double[] offsets)
        {
            _offsets = offsets;
            _values = new double[count];
        }

        public int Count { get; private set; }

        // The unit-th frame has the value, or none; first and both are the bits of a share.
        public void Add(int unit, int second, double? value, long first, long both = 0)
        {
            while (_seconds.Count <= second)
            {
                _seconds.Add((double.NaN, 0, 0));
            }

            if (value is not { } v)
            {
                if (_own is null && Count == unit)
                {
                    _own = new double[_values.Length];
                    _offsets.AsSpan(0, Count).CopyTo(_own);
                }

                return;
            }

            if (_own is not null)
            {
                _own[Count] = _offsets[unit];
            }

            _values[Count++] = v;
            (double max, long f, long b) = _seconds[second];
            _seconds[second] = (double.IsNaN(max) || v > max ? v : max, f + first, b + both);
        }

        public ReadOnlyMemory<double> X => _own is null && Count == _values.Length ? _offsets : (_own ?? _offsets).AsSpan(0, Count).ToArray();

        private ReadOnlyMemory<double> Values => Count == _values.Length ? _values : _values.AsSpan(0, Count).ToArray();

        public Series PerFrame(string name, string unit, string description, GraphConcept concept, string quantity, Node? anchor, ReadOnlyMemory<double> x) =>
            new(name, unit, SeriesAxis.FrameOffset, x, Values, anchor) { Description = description, Concept = concept, Quantity = quantity };

        // Each second's highest value, or its share over the bits of the frames that start in it; seconds without a value have
        // no point.
        public Series PerSecond(string name, string unit, string description, GraphConcept concept, string quantity, double[] secondStarts, Node? anchor, bool share)
        {
            var x = new List<double>();
            var values = new List<double>();
            for (int second = 0; second < _seconds.Count; second++)
            {
                (double max, long first, long both) = _seconds[second];
                if (share ? both > 0 : !double.IsNaN(max))
                {
                    x.Add(secondStarts[second]);
                    values.Add(share ? ShareTenths(first, both) / 10.0 : max);
                }
            }

            return new Series(name, unit, SeriesAxis.Seconds, x.ToArray(), values.ToArray(), anchor) { Description = description, Concept = concept, Quantity = quantity };
        }
    }
}
