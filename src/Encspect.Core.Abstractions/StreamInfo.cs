namespace Encspect.Core;

// One audio stream as its format defines it, in typed figures for file lists, batch summaries and
// graphs. A stream may span several nodes (an MPEG stream that an embedded tag splits into two
// regions), and one node may hold several streams (an MPEG region whose sample rate changes).
public sealed record StreamInfo
{
    // The nodes that hold the stream's data, in file order.
    public required IReadOnlyList<Node> Nodes { get; init; }

    // Where the host found Nodes in the tree: one child-index path per node, null where none was
    // found.
    public IReadOnlyList<IReadOnlyList<int>?> NodePaths { get; init; } = [];

    // The format's name for the coding, such as "MPEG-1 Layer III".
    public required string Codec { get; init; }

    public int SampleRate { get; init; }

    public int Channels { get; init; }

    // The format's name for how the channels are coded, such as "Joint stereo", or null.
    public string? ChannelMode { get; init; }

    // The speaker each channel feeds, one per channel in the stream's channel order, which is the
    // format's (AAC's 5.1 starts with the center), or null where the format states no positions. A
    // channel without a position, such as either program of MPEG dual channel, is
    // Speaker.Discrete. Where it is set, Channels is its count.
    public IReadOnlyList<Speaker>? ChannelLayout { get; init; }

    // The layout's common name, such as "5.1" or "stereo", from the speakers it holds; null without a
    // layout or where it has no common name.
    public string? ChannelLayoutName => ChannelLayout is { } layout ? ChannelLayouts.CommonName(layout) : null;

    // Channels that carry other programs next to the layout, such as the multilingual channels of
    // MPEG-2 multichannel audio (ISO/IEC 13818-3), which a decoder plays in place of a speaker
    // channel rather than through a speaker of their own. Neither Channels nor ChannelLayout counts
    // them.
    public int MultilingualChannels { get; init; }

    // Null for a lossy coding, which has no sample size.
    public int? BitsPerSample { get; init; }

    public long Samples { get; init; }

    // In seconds.
    public double Duration { get; init; }

    // In kbps; null when the stream has no duration.
    public double? AverageBitrate { get; init; }

    public bool IsVbr { get; init; }

    // Streams of one group play at the same time, such as the tracks of a movie or of a Matroska Segment: a summary of the file counts the
    // longest of them once instead of adding their durations and samples up. Streams in different groups, and streams without a group,
    // follow one another (chained Ogg streams, the regions of an MPEG file) and add up. The number only tells groups apart.
    public int? PlaybackGroup { get; init; }

    // True where the stream's data carry object-based audio: for E-AC-3, joint object coding (JOC)
    // side information in an EMDF container (ETSI TS 103 420), the layer Dolby Atmos adds to a
    // channel-based stream. A stream that only signals it, as a container's configuration may, is not; nor is DTS:X, whose word after the band data is reported through the profile.
    public bool HasObjectAudio { get; init; }

    // The number of objects the object audio reconstructs (E-AC-3: joc_num_objects of the JOC header,
    // not the larger object_count of the OAMD payload, which also counts an LFE channel), or null
    // where it is not known or the stream has no object audio.
    public int? JocObjectCount { get; init; }

    public string? Encoder { get; init; }

    public Confidence? EncoderConfidence { get; init; }

    public IReadOnlyList<Series> Series { get; init; } = [];
}

public enum SeriesAxis
{
    // X holds the byte offset of each frame.
    FrameOffset,

    // X holds the start of each interval in seconds.
    Seconds,
}

// Numbers over a stream for graphs, such as the bitrate of every frame. X and Values have the same
// length.
public sealed class Series
{
    public Series(string name, string unit, SeriesAxis axis, ReadOnlyMemory<double> x, ReadOnlyMemory<double> values, Node? anchor = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(unit);
        if (x.Length != values.Length)
        {
            throw new ArgumentException("A series needs one x value per value.", nameof(values));
        }

        Name = name;
        Unit = unit;
        Axis = axis;
        X = x;
        Values = values;
        Anchor = anchor;
    }

    public string Name { get; }

    public string Unit { get; }

    public SeriesAxis Axis { get; }

    public ReadOnlyMemory<double> X { get; }

    public ReadOnlyMemory<double> Values { get; }

    // The node the series belongs with, such as a statistics figure; the text tree prints the
    // series after it. Null for a series only graphs show.
    public Node? Anchor { get; }

    // What the series shows, as a node's Description says what its field is.
    public string? Description { get; init; }
}
