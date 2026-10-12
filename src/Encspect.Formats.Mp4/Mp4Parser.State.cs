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

        public int MoovCount { get; set; }

        public long? FirstMdat { get; set; }

        public List<(long Start, long End, Node Node)> Mdats { get; } = [];

        public List<Track> Tracks { get; } = [];

        public Track? Track { get; set; }

        public uint MovieTimescale { get; set; }

        public ulong? MovieDuration { get; set; }

        public Node? MovieHeader { get; set; }

        // The moof box being read, the number of them, and the track fragment being read.
        public (long Offset, Node Node)? Moof { get; set; }

        public int Fragments { get; set; }

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

        public Node? FirstFragment { get; set; }
    }

    // One sample description of a track's stsd box: its format, the fields of an audio sample entry
    // and, for MPEG-4 audio, what its esds box says.
    private sealed class SampleEntry(string format, Node node)
    {
        public string Format => format;

        public Node Node => node;

        public int Channels { get; set; }

        public int SampleSize { get; set; }

        public double SampleRate { get; set; }

        public int? ObjectType { get; set; }

        public MappedBytes? DecoderSpecificInfo { get; set; }

        public Node? DecoderSpecificInfoNode { get; set; }

        public uint? MaxBitrate { get; set; }

        public uint? AverageBitrate { get; set; }
    }

    private sealed record Edit(ulong SegmentDuration, long MediaTime, double Rate, Node Node);

    private readonly record struct FragmentSample(long Offset, int Size);

    private sealed record TrackDefaults(uint Description, uint Duration, uint Size, uint Flags);

    // A track fragment as its boxes are read, and the moof it belongs to.
    private sealed class FragmentTrack(Node node, (long Offset, Node Node)? moof)
    {
        public Node Node => node;

        public (long Offset, Node Node)? Moof => moof;

        public uint TrackId { get; set; }

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
