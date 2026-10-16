using Encspect.Core;

namespace Encspect.Formats.Matroska;

// What the EBML Header says about a document (RFC 8794 section 11.2).
internal sealed class DocumentInfo
{
    public Node? Node { get; set; }

    public string? DocType { get; set; }

    public ulong? Version { get; set; }

    public ulong? ReadVersion { get; set; }

    public ulong? DocTypeVersion { get; set; }

    public ulong? DocTypeReadVersion { get; set; }

    public int MaxIdLength { get; set; } = 4;

    public int MaxSizeLength { get; set; } = 8;

    public bool IsWebM => DocType == "webm";

    public string Name => IsWebM ? "WebM" : "Matroska";
}

// A ContentEncoding of a track (RFC 9559 section 5.1.4.1.31).
internal sealed class EncodingInfo
{
    public Node? Node { get; set; }

    public ulong Order { get; set; }

    public ulong Scope { get; set; } = 1;

    public ulong Type { get; set; }

    public ulong CompressionAlgorithm { get; set; }

    public bool HasCompression { get; set; }

    public long SettingsOffset { get; set; } = -1;

    public long SettingsLength { get; set; }

    public bool HasEncryption { get; set; }

    public ulong EncryptionAlgorithm { get; set; }

    public ulong? CipherMode { get; set; }

    public bool AppliesToFrames => (Scope & 1) != 0;

    public bool AppliesToCodecPrivate => (Scope & 2) != 0;
}

// The totals the blocks of a track add up to.
internal sealed class TrackStatistics
{
    public long Blocks { get; set; }

    public long Frames { get; set; }

    public long Bytes { get; set; }

    public long Keyframes { get; set; }

    // The blocks' timestamps in Segment Ticks (the cluster's plus the block's), the first and the last in file order and the extremes.
    public bool HasTimes { get; set; }

    public long First { get; set; }

    public long Last { get; set; }

    public long Smallest { get; set; } = long.MaxValue;

    public long Largest { get; set; } = long.MinValue;

    public long Backwards { get; set; }

    public Node? FirstBackwards { get; set; }

    public Node? FirstBlock { get; set; }

    public long FirstOffset { get; set; } = -1;

    public long LastEnd { get; set; }

    public long EncodedBlocks { get; set; }

    public bool Laced { get; set; }
}

// A TrackEntry (RFC 9559 section 5.1.4.1), as far as the analysis needs it.
internal sealed class TrackInfo
{
    public required Node Node { get; init; }

    public ulong? Number { get; set; }

    public ulong? Uid { get; set; }

    public ulong? Type { get; set; }

    public string? CodecId { get; set; }

    public bool IsEnabled { get; set; } = true;

    public bool AllowsLacing { get; set; } = true;

    public Node? CodecIdNode { get; set; }

    public long CodecPrivateOffset { get; set; } = -1;

    public long CodecPrivateLength { get; set; }

    public Node? CodecPrivateNode { get; set; }

    public ulong CodecDelay { get; set; }

    public Node? CodecDelayNode { get; set; }

    public ulong SeekPreRoll { get; set; }

    public ulong? DefaultDuration { get; set; }

    public double TimestampScale { get; set; } = 1.0;

    public string? Name { get; set; }

    public string? Language { get; set; }

    public bool HasAudio { get; set; }

    public Node? AudioNode { get; set; }

    public double? SamplingFrequency { get; set; }

    public double? OutputSamplingFrequency { get; set; }

    public ulong? Channels { get; set; }

    public ulong? BitDepth { get; set; }

    public Node? SamplingFrequencyNode { get; set; }

    public Node? ChannelsNode { get; set; }

    public bool HasVideo { get; set; }

    public ulong? PixelWidth { get; set; }

    public ulong? PixelHeight { get; set; }

    public List<EncodingInfo> Encodings { get; } = [];

    public bool IsAudio => Type == 2;

    public TrackStatistics Statistics { get; set; } = new();
}

// A Segment and what the passes over it share.
internal sealed class SegmentState
{
    public required int Number { get; init; }

    public required Records Records { get; init; }

    public DocumentInfo? Document { get; set; }

    public long Offset { get; set; }

    public long DataStart { get; set; }

    public long End { get; set; }

    public bool IsSizeUnknown { get; set; }

    public bool IsHeaderMissing { get; set; }

    public List<TrackInfo> Tracks { get; } = [];

    public Dictionary<ulong, TrackInfo> TracksByNumber { get; } = [];

    // The totals of the blocks by the track number they name, whether or not a TrackEntry has it.
    public Dictionary<ulong, TrackStatistics> Statistics { get; } = [];

    public ulong TimestampScale { get; set; } = 1_000_000;

    public double? Duration { get; set; }

    public string? Title { get; set; }

    public string? MuxingApp { get; set; }

    public Node? MuxingAppNode { get; set; }

    public string? WritingApp { get; set; }

    public Node? WritingAppNode { get; set; }

    public Node? InfoNode { get; set; }

    // The claims the Segment's MuxingApp and WritingApp make, WritingApp first (see Route.cs): what the codec parsers weigh for the Segment's
    // streams, and what the container weighs itself, which also holds a library MuxingApp as evidence.
    public List<EncoderClaim> StreamClaims { get; } = [];

    public List<EncoderClaim> OwnClaims { get; } = [];

    // The Writer node of the Segment's streams the container resolved, made once.
    public Node? WriterNode { get; set; }

    // The writer findings already reported for the Segment, so that its streams do not repeat them.
    public HashSet<string> Reported { get; } = [];

    public int InfoCount { get; set; }

    public int TracksCount { get; set; }

    public int ClusterCount { get; set; }

    public long? FirstClusterOffset { get; set; }

    public Node? Node { get; set; }

    // The positions, from the start of the Segment's data, where each Cluster starts, and where the Cues and Seek entries point.
    public HashSet<long> ClusterPositions { get; } = [];

    public List<(long Position, Node Node)> CuePositions { get; } = [];

    public List<(ulong Id, long Position, Node Node)> Seeks { get; } = [];

    public Dictionary<ulong, Node> UnknownTrackFirst { get; } = [];
}
