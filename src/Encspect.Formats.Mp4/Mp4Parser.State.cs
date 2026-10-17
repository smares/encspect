using Encspect.Core;

namespace Encspect.Formats.Mp4;

// What reading the boxes collects for the tracks, the layout checks and the codecs.
public sealed partial class Mp4Parser
{
    private sealed class State(ParseContext context, ByteReader reader)
    {
        public ParseContext Context => context;

        public ByteReader Reader => reader;

        public FindingCollector Findings { get; } = new();

        public Node File { get; set; } = null!;

        public string? MajorBrand { get; set; }

        // QuickTime lays out sound sample descriptions of version 1 and 2 differently from ISO/IEC
        // 14496-12; a file without an ftyp box is QuickTime's.
        public bool IsQuickTime { get; set; } = true;

        public bool HasQuickTimeBrand { get; set; }

        public long? Moov { get; set; }

        public Node? MoovNode { get; set; }

        public int MoovCount { get; set; }

        // Whether the boxes being read lie in a moov box that is not the movie (a second one, or one inside another box): its tracks are shown
        // and not routed.
        public bool IsMovieIgnored { get; set; }

        // The boxes read so far, against MaxBoxes, and whether the limit stopped the reading.
        public int Boxes { get; set; }

        public bool IsBoxLimitReached { get; set; }

        // What the file has left to place samples in, in bytes: samples of a valid file do not overlap, so they cannot add up to more than the
        // file holds. Fragments draw on it as they are read, and a track's sample tables when the track is routed.
        public long SampleBytesLeft { get; set; }

        // The samples all track fragments of the file have stored, against MaxFragmentSamples.
        public int FragmentSampleCount { get; set; }

        // The mdat boxes' ranges sorted by start, once reading ends, for the lookup of the box a sample lies in.
        public (long Start, long End, Node Node)[] MdatIndex { get; set; } = [];

        // The first track of each ID and how many tracks have it, for the track fragments' lookups.
        public Dictionary<uint, Track> TracksById { get; } = [];

        public Dictionary<uint, int> TrackIdCounts { get; } = [];

        public long? FirstMdat { get; set; }

        public List<(long Start, long End, Node Node)> Mdats { get; } = [];

        // Where the last top-level free box ends, and whether an mdat box follows one with its data starting
        // on a 4,096-byte boundary: how Apple's file writer pads the audio data to a page.
        public long FreeEnd { get; set; } = -1;

        public bool HasPageAlignedMdat { get; set; }

        public List<Track> Tracks { get; } = [];

        public Track? Track { get; set; }

        public uint MovieTimescale { get; set; }

        public ulong? MovieDuration { get; set; }

        public Node? MovieHeader { get; set; }

        // The moof box being read, the number of them, and the track fragment being read.
        public (long Offset, Node Node)? Moof { get; set; }

        public int Fragments { get; set; }

        // How many moof boxes are open around the box being read.
        public int OpenMoofs { get; set; }

        public FragmentTrack? Fragment { get; set; }

        // Where the last track fragment's data ended, by the moof it belongs to, for a fragment that
        // neither states its base offset nor takes the moof's.
        public long? LastFragmentEnd { get; set; }

        public long? LastFragmentMoof { get; set; }

        public Dictionary<uint, TrackDefaults> TrackDefaults { get; } = [];

        // The metadata handler of the meta box being read, and the keys of an mdta one.
        public string? MetaHandler { get; set; }

        public List<string> Keys { get; } = [];

        public GaplessInfo? Smpb { get; set; }

        public EncodingParams? EncodingParams { get; set; }

        // The sample entry whose boxes are being read, which its esds box describes.
        public SampleEntry? CurrentEntry { get; set; }

        // Whether the box being read lies in a sinf box (a frma box in QuickTime's wave box is no protection).
        public bool InSinf { get; set; }

        // The type of the box whose children are being read.
        public string? ParentType { get; set; }

        // The DRM systems the file's pssh boxes name, in file order; the tracks whose track fragments hold something that says their samples are protected;
        // whether a box outside any track and track fragment does (it is taken to apply to every track); and whether a track fragment has no tfhd box.
        public List<string> Systems { get; } = [];

        public HashSet<string> SystemSet { get; } = [];

        public HashSet<uint> ProtectedTracks { get; } = [];

        public bool DoesEveryTrackHaveProtectingEvidence { get; set; }

        public bool IsFragmentHeaderMissing { get; set; }

        // Whether a sinf or schm box lies outside any track, sample entry and track fragment (it is taken to apply to every track).
        public bool DoesEveryTrackHaveProtectionScheme { get; set; }

        // The sample descriptions the track fragments of each track use (tfhd, else trex).
        public Dictionary<uint, HashSet<uint>> FragmentDescriptions { get; } = [];
    }

    private sealed class Track(Node box)
    {
        public Node Box => box;

        public uint? Id { get; set; }

        public string? Handler { get; set; }

        public uint Timescale { get; set; }

        public ulong? MediaDuration { get; set; }

        public ulong? TrackDuration { get; set; }

        public Node? MediaHeader { get; set; }

        public Node? TrackHeader { get; set; }

        public List<SampleEntry> Entries { get; } = [];

        public List<(uint Count, uint Delta)>? TimeToSample { get; set; }

        public Node? TimeToSampleNode { get; set; }

        public List<(uint FirstChunk, uint Samples, uint Description)>? SampleToChunk { get; set; }

        public Node? SampleToChunkNode { get; set; }

        public uint SampleSize { get; set; }

        public uint SampleCount { get; set; }

        public int[]? Sizes { get; set; }

        public Node? SampleSizeNode { get; set; }

        public long[]? ChunkOffsets { get; set; }

        public Node? ChunkOffsetNode { get; set; }

        public List<Edit>? Edits { get; set; }

        public Node? EditListNode { get; set; }

        public List<(string Type, int Distance, Node Node)> Rolls { get; } = [];

        public ChunkedList<FragmentSample> FragmentSamples { get; } = new();

        // The fragment samples of no bytes, counted and not stored.
        public long EmptyFragmentSamples { get; set; }

        // What the budget of sample bytes allowed this track's sample tables, fixed when its ranges are collected so that its packets stop at the same sample.
        public long SampleBudget { get; set; }

        public Node? FirstFragment { get; set; }

        // Whether anything inside the track says that its samples are protected: a seig sample group description that protects, cannot be read or is of
        // a version the project does not know (wherever in the track it lies), or a sample encryption box.
        public bool HasProtectingEvidence { get; set; }

        // Whether a sinf or schm box lies in the track outside any sample entry, which says that its samples are protected by a scheme.
        public bool HasProtectionScheme { get; set; }

        // The sample entry the track is read through, once routed.
        public SampleEntry? ReadEntry { get; set; }
    }

    // One sample description of a track's stsd box: its format, the fields of an audio sample entry
    // and, for MPEG-4 audio, what its esds box says.
    private sealed class SampleEntry(string format, Node node)
    {
        public string Format => format;

        public Node Node => node;

        // The protection scheme information of a protected entry.
        public EntryProtection? Protection { get; set; }

        // The coding the samples have once decrypted: the original format a protected entry names, and for Audible's
        // aavd, whose layout is that of mp4a (GStreamer's qtdemux reads it so), mp4a.
        public string EffectiveFormat => Protection?.OriginalFormat ?? (format == "aavd" ? "mp4a" : format);

        public int Channels { get; set; }

        public int SampleSize { get; set; }

        public double SampleRate { get; set; }

        public int? ObjectType { get; set; }

        public MappedBytes? DecoderSpecificInfo { get; set; }

        public Node? DecoderSpecificInfoNode { get; set; }

        public uint? MaxBitrate { get; set; }

        public uint? AverageBitrate { get; set; }

        // The DecoderConfigDescriptor's byte of stream type, upstream flag and reserved bit, and the sbtd box.
        public int? StreamTypeByte { get; set; }

        public Node? SourceBitDepthNode { get; set; }

        // The speakers a chan or chnl box in the entry states, the box's node and its type.
        public IReadOnlyList<Speaker>? Layout { get; set; }

        public Node? LayoutNode { get; set; }

        public string? LayoutBox { get; set; }
    }

    // What a sinf box says: its node, the original format, the scheme and the track encryption defaults.
    private sealed class EntryProtection(Node sinf)
    {
        public Node SinfNode => sinf;

        public string? OriginalFormat { get; set; }

        public string? SchemeType { get; set; }

        public Node? SchemeNode { get; set; }

        public TrackEncryptionInfo? Encryption { get; set; }

        // Everything the entry's sinf boxes state (an entry may hold several, and a sinf box several of each): every original format, scheme type
        // and track encryption default. The entry is clear only if they all agree that it is.
        public List<string> OriginalFormats { get; } = [];

        public List<string> SchemeTypes { get; } = [];

        public List<(int IsProtected, int IvSize)> TrackEncryptions { get; } = [];
    }

    private sealed record TrackEncryptionInfo(int IsProtected, Node Node);

    private readonly record struct Edit(ulong SegmentDuration, long MediaTime, double Rate);

    private readonly record struct FragmentSample(long Offset, int Size);

    private sealed record TrackDefaults(uint Description, uint Duration, uint Size, uint Flags);

    // A track fragment as its boxes are read, and the moof it belongs to.
    private sealed class FragmentTrack(Node node, (long Offset, Node Node)? moof)
    {
        public Node Node => node;

        public (long Offset, Node Node)? Moof => moof;

        public uint TrackId { get; set; }

        // Whether the fragment has a tfhd box (without one its track is not known), and whether anything in it says its samples are protected.
        public bool HasHeader { get; set; }

        // How many tfhd boxes the fragment holds (a fragment has one), and whether it lies inside a trak box.
        public int HeaderCount { get; set; }

        public bool IsInTrack { get; set; }

        // Whether the fragment lies in another track fragment, where ISO/IEC 14496-12 8.8.6 does not place it.
        public bool IsMisplaced { get; set; }

        public bool HasProtectingEvidence { get; set; }

        public ulong? BaseDataOffset { get; set; }

        public bool BaseIsMoof { get; set; }

        public uint? DefaultSize { get; set; }

        // Each run's data offset, sample count, and the samples' sizes where the run states them.
        public List<(long? DataOffset, uint Count, List<uint>? Sizes, Node Node)> Runs { get; } = [];
    }

    // iTunSMPB: the encoder delay, padding and the samples between them, in the hexadecimal words
    // Apple's encoder writes.
    private sealed record GaplessInfo(long Delay, long Padding, long Samples, Node Node);

    // Apple's Encoding Params: the encoder's bitrate mode (acbf) and bitrate (brat).
    private sealed record EncodingParams(int? Mode, long? Bitrate, Node Node);
}
