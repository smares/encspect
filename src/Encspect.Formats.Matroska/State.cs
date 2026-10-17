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

    // The two limits as the header states them, for the checks and their messages.
    public int MaxIdLength { get; set; } = 4;

    public int MaxSizeLength { get; set; } = 8;

    // The limits the Body is read with. RFC 9559 (section 4.4) gives the Segment and the Top-Level Elements IDs of four octets whatever
    // EBMLMaxIDLength says, and an ID or a size is never longer than eight octets.
    public int ReadIdLength => Math.Clamp(MaxIdLength, 4, 8);

    public int ReadSizeLength => Math.Min(MaxSizeLength, 8);

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

    public bool HasAesSettings { get; set; }

    public ulong? CipherMode { get; set; }

    public bool AppliesToFrames => (Scope & 1) != 0;

    public bool AppliesToCodecPrivate => (Scope & 2) != 0;
}

// Hider: the outermost encoding of the run of bit 4 encodings just before this one, whose settings nothing hides. HasHiddenEncryption: an encryption
// (type 1) sits in that run below the hider.
internal readonly record struct OrderedEncoding(EncodingInfo Encoding, EncodingInfo? Hider, bool HasHiddenEncryption, bool HasNext)
{
    // Whether the outermost hider is an encryption (its ContentEncAlgo is not 0).
    public bool HiderEncrypts => Hider is { Type: 1 } hider && !(hider.HasEncryption && hider.EncryptionAlgorithm == 0);

    // A hidden encoding counts as encrypted when the outermost hider encrypts, or when an encryption is hidden in the chain (its algorithm is unreadable),
    // or when it is an encryption itself, whatever its settings state.
    public bool IsHiddenEncrypted => Hider is not null && (HiderEncrypts || HasHiddenEncryption || Encoding.Type == 1);
}

// The totals the blocks of a track add up to.
internal sealed class TrackStatistics
{
    public long Blocks { get; set; }

    public long Frames { get; set; }

    public long Bytes { get; set; }

    public long Keyframes { get; set; }

    // The blocks' timestamps in Segment Ticks: the Cluster's Timestamp plus the block's, which is in Track Ticks, times the TrackTimestampScale
    // (RFC 9559, section 11.2); Scale is the track's, 1 where the Tracks element has not named the track yet. The first and the last in file
    // order and the extremes.
    public double Scale { get; set; } = 1.0;

    public bool HasTimes { get; set; }

    public double First { get; set; }

    public double Last { get; set; }

    public double Smallest { get; set; } = double.MaxValue;

    public double Largest { get; set; } = double.MinValue;

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

    // RFC 9559 (section 5.1.4.1.31.2) requires the ContentEncodingOrders of a track to be unique. With two the same, the encodings cannot be ordered
    // (the order of the elements in the file would decide), so none of them is trusted.
    public bool HasDuplicateOrders => Encodings.GroupBy(e => e.Order).Any(g => g.Count() > 1);

    // The encodings in the order a reader undoes them (the highest ContentEncodingOrder first), for a track whose orders are unique. Bit 4 of the
    // scope of any encoding makes the settings of the next one unreadable (section 5.1.4.1.31.3). That holds for a hidden encoding as well, so the
    // encodings that hide one are all the encodings of the run of bit 4 encodings just before it, the outermost first. The runs are told apart by
    // their start and a running count of encryptions, so the work is linear in the number of encodings. HasNext tells whether an encoding follows.
    public List<OrderedEncoding> OrderedEncodings()
    {
        List<EncodingInfo> ordered = [.. Encodings.OrderByDescending(e => e.Order)];
        var result = new List<OrderedEncoding>(ordered.Count);
        var encryptionsBefore = new int[ordered.Count + 1];
        for (int i = 0; i < ordered.Count; i++)
        {
            encryptionsBefore[i + 1] = encryptionsBefore[i] + (ordered[i].Type == 1 ? 1 : 0);
        }

        int start = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            if (i == 0 || (ordered[i - 1].Scope & 4) == 0)
            {
                start = i;
            }

            // The run is [start, i); its first element is the hider, and the encryptions below it are those in (start, i).
            bool hasHidden = start < i && encryptionsBefore[i] - encryptionsBefore[start + 1] > 0;
            result.Add(new OrderedEncoding(ordered[i], start < i ? ordered[start] : null, hasHidden, i + 1 < ordered.Count));
        }

        return result;
    }

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

    // Where the Segment ends by its own size, past End when the file is cut short.
    public long DeclaredEnd { get; set; }

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

    // Top-Level Elements past the node budget were skipped, so what the Segment lacks may be among them.
    public bool HasSkippedElements { get; set; }

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

    // The block records of each audio track by its number, made in one pass over the Segment's blocks while its tracks are routed, so that routing a
    // track costs its own blocks and not all of them.
    public Dictionary<ulong, List<int>>? BlocksByTrack { get; set; }
}
