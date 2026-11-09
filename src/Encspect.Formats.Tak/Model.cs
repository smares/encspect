using Encspect.Core;

namespace Encspect.Formats.Tak;

// The names of the checksums the stream declares, as the decoder's results would find them.
internal static class ChecksumNames
{
    public const string ObjectCrc24 = "header CRC-24";
    public const string FrameHeaderCrc24 = "frame header CRC-24";
    public const string FrameDataCrc24 = "frame data CRC-24";
    public const string Md5 = "MD5";
}

// The background articles the TAK nodes point to, by the IDs the host's topic library serves.
internal static class Topics
{
    public const string Metadata = "tak-metadata";
    public const string Presets = "tak-presets";
    public const string Crc = "crc";
    public const string Channels = "channel-layouts";
    public const string RiffChunks = "riff-chunks";
    public const string WaveFormat = "waveformatex";
    public const string WaveFormatExtensible = "wave-format-extensible";
    public const string Verification = "verification";
}

// One metadata object as the walk read it.
internal sealed class ObjectRecord
{
    public int Id { get; init; }

    public bool IsReserved { get; init; }

    public long Offset { get; init; }

    // The size the header states, and the bytes of data after the header that the input holds.
    public long Size { get; init; }

    public long Held { get; init; }

    public bool IsCut => Held < Size;

    public long DataOffset => Offset + Layout.ObjectHeaderLength;

    public long End => Offset + Layout.ObjectHeaderLength + Held;

    // The CRC-24 in the object's last 3 bytes and the one computed over the data before it; set where the object has one and the input holds it whole.
    public bool IsCompared { get; set; }

    public uint StoredCrc { get; set; }

    public uint ComputedCrc { get; set; }

    public bool IsCrcValid => IsCompared && StoredCrc == ComputedCrc;

    public bool IsKnown => Id <= Layout.LastFrameInfo;

    // Whether the size is the one the object's layout calls for.
    public bool IsSizeValid { get; set; } = true;

    // For a wave data object: the sizes it states.
    public long HeaderSize { get; set; }

    public long FooterSize { get; set; }

    public HeaderInfo? WavHeader { get; set; }

    public HeaderInfo? WavFooter { get; set; }
}

internal sealed class Records
{
    public const int MaxObjects = 4096;

    // The offset of the stream ID, the end of the input, and where the audio starts (-1 where the walk found no frame or End object).
    public long Start { get; set; }

    public long DataEnd { get; set; }

    public long AudioStart { get; set; } = -1;

    public bool IsSignaturePresent { get; set; }

    // The walk ended at a frame without an End object before it.
    public bool IsEndMissing { get; set; }

    // The data ended inside the metadata: before an End object or a frame, or inside an object.
    public bool IsMetadataCut { get; set; }

    public bool IsLimitReached { get; set; }

    // Where bytes the walk did not read begin: after the last object when fewer than an object header remain, or at the object the limit stopped before.
    public long LeftoverOffset { get; set; } = -1;

    public List<ObjectRecord> Objects { get; } = [];

    public ObjectRecord? StreamInfoObject { get; set; }

    public StreamInfoFields? Stream { get; set; }

    public ObjectRecord? EncoderInfoObject { get; set; }

    public int EncoderVersion { get; set; }

    public int Preset { get; set; }

    public int Evaluation { get; set; }

    public int EncoderReserved { get; set; }

    public bool HasEncoderInfo => EncoderInfoObject is not null;

    public ObjectRecord? WaveDataObject { get; set; }

    public ObjectRecord? Md5Object { get; set; }

    public byte[]? Md5 { get; set; }

    public ObjectRecord? LastFrameObject { get; set; }

    // The last frame's position counted from the first frame, and its size.
    public long LastFramePosition { get; set; }

    public long LastFrameSize { get; set; }

    // The length of the first frame's header, with its CRC-24 (0 where the audio starts with something that is no frame).
    public int FirstFrameHeaderLength { get; set; }

    public bool IsStreamInfoFirst => Objects.Count > 0 && Objects[0].Id == Layout.StreamInfo;

    // The end of the last frame by the last frame info object, when its CRC-24 holds; null otherwise.
    public long? DeclaredEnd => LastFrameObject is { IsCrcValid: true } && AudioStart >= 0 ? AudioStart + LastFramePosition + LastFrameSize : null;
}

internal sealed class State
{
    public State(ParseContext context)
    {
        Context = context;
    }

    public ParseContext Context { get; }

    public FindingCollector Findings { get; } = new();

    public Records Records { get; } = new();

    public Node Stream { get; set; } = null!;

    // The nodes in front of the stream ID: the statistics and the claims.
    public List<Node> Leading { get; } = [];

    public List<Node> Body { get; } = [];
}
