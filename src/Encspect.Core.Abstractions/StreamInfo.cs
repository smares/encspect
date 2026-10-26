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

    // Streams of one group play at the same time, such as the tracks of a movie, of a Matroska Segment or the logical streams of one Ogg link: a summary of the file counts the
    // longest of them once instead of adding their durations and samples up. Streams in different groups, and streams without a group,
    // follow one another (the regions of an MPEG file) and add up. The number only tells groups apart.
    public int? PlaybackGroup { get; init; }

    // True where the stream's data carry object-based audio: for E-AC-3, joint object coding (JOC)
    // side information in an EMDF container (ETSI TS 103 420), the layer Dolby Atmos adds to a
    // channel-based stream. A stream that only signals it, as a container's configuration may, is not; nor is DTS:X, whose word after the band data is reported through the profile.
    public bool HasObjectAudio { get; init; }

    // The number of objects the object audio reconstructs (E-AC-3: joc_num_objects of the JOC header,
    // not the larger object_count of the OAMD payload, which also counts an LFE channel), or null
    // where it is not known or the stream has no object audio.
    public int? JocObjectCount { get; init; }

    // How the container protects the stream, or null where it does not. The container sets it on the
    // stream a codec parser reported (AnalysisContext.UpdateStream), or on the one it reports itself.
    public StreamProtection? Protection { get; init; }

    // How far the checksums the stream stores vouch for it; set for the phase 3 codings and for WavPack in every mode,
    // null for the others.
    public StreamVerification? Verification { get; init; }

    // What the host measured of the stream's decoded audio; set by the host, for every session of an analysis that asked
    // for measurements, null otherwise and for a stream that has no session.
    public StreamMeasurements? Measurements { get; init; }

    // What the stream carries; a stream that is not audio has no sample rate or channels.
    public StreamKind Kind { get; init; } = StreamKind.Audio;

    public bool IsProtected => Protection is not null;

    // The software that turned the audio into the coded stream, from the stream's own evidence or a tag that
    // names an encoder, and how sure the analysis is; null where nothing names one.
    public string? Encoder { get; init; }

    public Confidence? EncoderConfidence { get; init; }

    // The software that put the stream into the file (the muxer, the container writer, a tagger), from the
    // file's layout and the tags that name a writer, and how sure the analysis is; null where nothing names
    // one. It is resolved apart from the encoder and need not be the same software: a remuxed file has the
    // encoder of its audio and the writer of its container.
    public string? Writer { get; init; }

    public Confidence? WriterConfidence { get; init; }

    public IReadOnlyList<Series> Series { get; init; } = [];

    // Categories per frame for graphs and for the summary over the stream, such as the Layer III block types.
    public IReadOnlyList<CategorySeries> CategorySeries { get; init; } = [];
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

    // The quantity the series shows, such as "Bitrate", where the series of a stream that show it in other forms (per frame, per
    // second of audio) carry the same text: front ends offer them as one graph with the forms to choose from, and never pair
    // series by name. Null for a series that is a quantity of its own.
    public string? Quantity { get; init; }

    // The shared concept the series shows, which front ends group by; None for a series that belongs to none.
    public GraphConcept Concept { get; init; }
}
