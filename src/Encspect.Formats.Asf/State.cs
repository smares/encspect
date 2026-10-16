using Encspect.Core;

namespace Encspect.Formats.Asf;

// A stream as the header describes it: its Stream Properties Object (free-standing or inside an
// Extended Stream Properties Object), that object's extension, and the bitrate record.
internal sealed class StreamEntry
{
    public int Number { get; set; }

    public Guid Type { get; set; }

    public Guid ErrorCorrectionType { get; set; }

    public ulong TimeOffset { get; set; }

    public ushort Flags { get; set; }

    public bool IsEncrypted => (Flags & 0x8000) != 0;

    // The Stream Properties Object, and the Extended Stream Properties Object that holds it or
    // extends it.
    public Node? Properties { get; set; }

    public Node? Extension { get; set; }

    public bool IsInExtension { get; set; }

    public Node? TypeSpecific { get; set; }

    public Node? CodecSpecific { get; set; }

    // The WAVEFORMATEX of an audio stream (section 9.1), and where its codec-specific data lies.
    public WaveFormat? Format { get; set; }

    public long CodecSpecificOffset { get; set; }

    public int CodecSpecificLength { get; set; }

    // Section 9.1.1: the spread audio error correction data.
    public SpreadAudio? Spread { get; set; }

    public uint? AverageBitrate { get; set; }

    public uint? DataBitrate { get; set; }

    public bool IsAudio => Type == AsfGuids.AudioMedia;

    public string TypeName => AsfGuids.StreamTypeName(Type) ?? "Unknown";

    // The payload extension systems of the Extended Stream Properties Object, in the order their data
    // follows the media object's size and time in the replicated data (section 7.3.1).
    public List<(Guid Id, ushort Size, uint InfoLength)> Extensions { get; } = [];

    public string? CodecListName { get; set; }
}

internal sealed record WaveFormat(
    int Tag,
    int Channels,
    int SamplesPerSecond,
    int AverageBytesPerSecond,
    int BlockAlign,
    int BitsPerSample,
    int ExtraSize,
    int? ValidBits,
    uint? ChannelMask,
    Guid? SubFormat)
{
    // The format tag the coding is known by: an extensible format's real tag is the first 32 bits of
    // its sub-format when that is the base GUID of the WAVE formats (mmreg.h).
    public int EffectiveTag => Tag == 0xFFFE && SubFormat is { } sub && IsWaveBase(sub) ? (int)BitConverter.ToUInt32(sub.ToByteArray(), 0) : Tag;

    public static bool IsWaveBase(Guid sub)
    {
        byte[] bytes = sub.ToByteArray();
        return bytes.AsSpan(4).SequenceEqual(new byte[] { 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
    }
}

internal sealed record SpreadAudio(byte Span, ushort VirtualPacketLength, ushort VirtualChunkLength, ushort SilenceLength)
{
    // FFmpeg turns descrambling off, and decodes the media objects as they are, where the chunk size is 0, the chunks
    // do not divide the virtual packet or the packet holds one chunk or less (asfdec_f.c, asf_read_stream_properties).
    public bool Scrambles => Span > 1 && VirtualChunkLength > 0 && VirtualPacketLength / VirtualChunkLength > 1 && VirtualPacketLength % VirtualChunkLength == 0;
}

// Everything the passes over the file share.
internal sealed class State(ParseContext context, ByteReader reader)
{
    public ParseContext Context => context;

    public ByteReader Reader => reader;

    public FindingCollector Findings { get; } = new();

    public Node File { get; set; } = null!;

    public IReadOnlyDictionary<int, string> Routes { get; init; } = new Dictionary<int, string>();

    public Node? HeaderNode { get; set; }

    public Node? DataNode { get; set; }

    public int HeaderObjects { get; set; }

    // The GUIDs of the objects of the header and its extension, and what the header says about its
    // writer, for the writer's layout (AddWriterEvidence).
    public HashSet<Guid> HeaderGuids { get; } = [];

    public string? SdkVersion { get; set; }

    public Node? SdkVersionNode { get; set; }

    public string? MediaFoundationVersion { get; set; }

    public Node? MediaFoundationVersionNode { get; set; }

    public List<string> ExtendedContentNames { get; } = [];

    // Objects no specification defines, by where they lie: the first of each place gets the finding, with the count
    // (a file of thousands of them gets one finding, not thousands).
    private readonly Dictionary<string, (ObjectHeaderInfo First, Node Node, string Place, string Section, int Count)> _unknown = [];

    public void NoteUnknownObject(string key, AsfParser.ObjectHeader header, Node node, string place, string section)
    {
        _unknown[key] = _unknown.TryGetValue(key, out var existing)
            ? existing with { Count = existing.Count + 1 }
            : (new ObjectHeaderInfo(header.Id, header.Offset), node, place, section, 1);
    }

    public void FlushUnknownObjects()
    {
        foreach ((string key, var entry) in _unknown)
        {
            string text = AsfGuids.Text(entry.First.Id);
            string message = entry.Count == 1
                ? $"The {(key == "top-level" ? "top-level object" : "object")} {text} at 0x{entry.First.Offset:X} in {entry.Place} is none of the objects the specification defines; readers ignore it ({entry.Section})."
                : $"{entry.Count} objects in {entry.Place} are none of the objects the specification defines, the first {text} at 0x{entry.First.Offset:X}; readers ignore them ({entry.Section}).";
            Findings.AddNow(Severity.Info, "asf.unknown-object", message, entry.Node, entry.Count);
        }

        _unknown.Clear();
    }

    internal readonly record struct ObjectHeaderInfo(Guid Id, long Offset);

    // The IsVBR attribute both Windows writers stamp into the file: what the writer says about its mode.
    public bool? IsVbr { get; set; }

    public Node? HeaderExtensionNode { get; set; }

    public int DataObjects { get; set; }

    // The File Properties Object (section 3.2).
    public Node? FilePropertiesNode { get; set; }

    public int FilePropertiesCount { get; set; }

    public Guid FileId { get; set; }

    public ulong FileSize { get; set; }

    public ulong CreationDate { get; set; }

    public ulong PacketCount { get; set; }

    public ulong PlayDuration { get; set; }

    public ulong SendDuration { get; set; }

    public ulong Preroll { get; set; }

    public uint Flags { get; set; }

    public uint MinPacketSize { get; set; }

    public uint MaxPacketSize { get; set; }

    public uint MaxBitrate { get; set; }

    public bool IsBroadcast => (Flags & 1) != 0;

    public Node? FileSizeNode { get; set; }

    public Node? PacketCountNode { get; set; }

    public Node? PacketSizeNode { get; set; }

    public Node? FileIdNode { get; set; }

    public List<StreamEntry> Streams { get; } = [];

    public Dictionary<int, StreamEntry> StreamsByNumber { get; } = [];

    // The Header Extension Object, which the objects inside it are counted as one object for.
    public int HeaderExtensionCount { get; set; }

    public int StreamPropertiesCount { get; set; }

    public int ChildCount { get; set; }

    public Node? CodecListNode { get; set; }

    public List<(string Name, string Codec, int Type)> Codecs { get; } = [];

    public Node? IndexParametersNode { get; set; }

    public List<(ushort Stream, ushort Type)> IndexSpecifiers { get; } = [];

    public int IndexObjects { get; set; }

    public int SimpleIndexObjects { get; set; }

    public List<string> Languages { get; } = [];

    public List<(int Number, uint Bitrate, Node Node)> PendingBitrates { get; } = [];

    public List<ExtensionRecord> ExtensionRecords { get; } = [];

    // The data object: where its packets start and end, the size every packet has, and the figures
    // the scan of the packets gathered.
    public long PacketsStart { get; set; }

    public long PacketsEnd { get; set; }

    public int PacketSize { get; set; }

    // The File Properties Object gives one packet size as minimum and maximum, which fixes the size of every
    // packet (section 8.2.13): a packet is stepped over by it, whatever its own length field says.
    public bool PacketSizeIsFixed { get; set; }

    public DataFigures? Figures { get; set; }

    // The protection the header's encryption objects describe: the Content Encryption Object and the Extended Content Encryption Object
    // cover the whole file, each record of the Advanced Content Encryption Object the streams it names (0 is the whole file). The Digital
    // Signature Object protects nothing.
    public List<ProtectionObject> Protections { get; } = [];

    public Node? TagNode { get; set; }

    public StreamEntry? Stream(int number) => StreamsByNumber.GetValueOrDefault(number);
}

// What an encryption object (or one record of the Advanced Content Encryption Object) says: the scheme, the object's node, the names of the
// systems it lists and the streams it covers (IsWholeFile for the objects that name none).
internal sealed record ProtectionObject(string Scheme, Node Node, IReadOnlyList<string> Systems, IReadOnlyList<(int Number, Node Node)> Streams, bool IsWholeFile = false)
{
    public bool Covers(int stream) => IsWholeFile || Streams.Any(s => s.Number == 0 || s.Number == stream);
}

// The schemes of the protection objects, in the project's wording.
internal static class ProtectionSchemes
{
    public const string DrmVersion1 = "Microsoft Digital Rights Manager version 1";
    public const string RightsManager7 = "Windows Media Rights Manager 7";
    public const string AdvancedEncryption = "Windows Media DRM for network devices";
    public const string StreamFlag = "Encrypted Content Flag (scheme not stated)";
}
