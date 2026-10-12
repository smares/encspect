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

    // Null for a lossy coding, which has no sample size.
    public int? BitsPerSample { get; init; }

    public long Samples { get; init; }

    // In seconds.
    public double Duration { get; init; }

    // In kbps; null when the stream has no duration.
    public double? AverageBitrate { get; init; }

    public bool IsVbr { get; init; }

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
}
