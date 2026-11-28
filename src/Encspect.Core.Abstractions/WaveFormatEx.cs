using System.Buffers.Binary;

namespace Encspect.Core;

// A WAVEFORMATEX, or the WAVEFORMATEXTENSIBLE that extends it, stored as bytes outside a WAV or ASF file's own chunk and object (Matroska's CodecPrivate of A_MS/ACM).
// The fields are read as far as the bytes go: a structure shorter than 16 bytes is none, and a cbSize that states more bytes than follow is read as far as they go.
public sealed record WaveFormatEx
{
    // The size of the fixed fields, up to and including wBitsPerSample, and of the cbSize after them.
    public const int FixedSize = 16;

    public const int CbSizeEnd = 18;

    // The extension of the extensible format: the valid bits, the channel mask and the sub-format GUID.
    public const int ExtensionSize = 22;

    public int Tag { get; init; }

    public int Channels { get; init; }

    public long SampleRate { get; init; }

    public long BytesPerSecond { get; init; }

    public int BlockAlign { get; init; }

    public int Bits { get; init; }

    // The cbSize the structure states, or null where it ends before the field.
    public int? StatedExtraSize { get; init; }

    public int? ValidBits { get; init; }

    public uint? ChannelMask { get; init; }

    public Guid? SubFormat { get; init; }

    // Where the codec-specific data starts in the bytes, and how many of the stated bytes the structure holds.
    public int DataStart { get; init; }

    public int DataLength { get; init; }

    public bool IsExtensible => Tag == WaveFormatRoutes.Extensible && SubFormat is not null;

    // The coding the structure names: the format tag, or for the extensible format the tag in its sub-format GUID (a GUID that is no tag leaves 0xFFFE).
    public int CodingTag => IsExtensible && WaveFormatRoutes.TagOfSubFormat(SubFormat!.Value) is { } sub ? sub : Tag;

    // The codec parser of the coding, or null: the tag's, or a named sub-format GUID's.
    public string? CodecId => WaveFormatRoutes.RouteOf(CodingTag)
        ?? (IsExtensible ? WaveFormatRoutes.NamedSubFormat(SubFormat!.Value)?.CodecId : null);

    // The name of the coding in the form every container uses for it, or null for a tag no table lists.
    public string? CodingName => (IsExtensible ? WaveFormatRoutes.NamedSubFormat(SubFormat!.Value)?.Name : null) ?? WaveFormatNames.NameOf(CodingTag);

    public AudioFormat ToAudioFormat() => new(CodingTag, Channels, (int)Math.Min(SampleRate, int.MaxValue), (int)Math.Min(BytesPerSecond, int.MaxValue), BlockAlign, Bits)
    {
        ChannelMask = ChannelMask,
        ValidBitsPerSample = ValidBits,
    };

    // How many of the bytes the cbSize states follow it (the extension of the extensible format included).
    public int HeldExtraSize { get; init; }

    // True where the cbSize states more bytes than follow it.
    public bool IsDataCut => StatedExtraSize is { } stated && stated > HeldExtraSize;

    // Reads the structure from the bytes, or null where there are fewer than 16.
    public static WaveFormatEx? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < FixedSize)
        {
            return null;
        }

        var format = new WaveFormatEx
        {
            Tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            Channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]),
            SampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
            BytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            BlockAlign = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]),
            Bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]),
            DataStart = bytes.Length,
        };
        if (bytes.Length < CbSizeEnd)
        {
            return format;
        }

        int stated = BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]);
        int held = Math.Min(stated, bytes.Length - CbSizeEnd);
        format = format with { StatedExtraSize = stated, HeldExtraSize = held, DataStart = CbSizeEnd, DataLength = held };
        if (format.Tag == WaveFormatRoutes.Extensible && held >= ExtensionSize)
        {
            format = format with
            {
                ValidBits = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]),
                ChannelMask = BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]),
                SubFormat = new Guid(bytes.Slice(24, 16)),
                DataStart = CbSizeEnd + ExtensionSize,
                DataLength = held - ExtensionSize,
            };
        }

        return format;
    }

    // Puts the fields under a node named "WAVEFORMATEX" that is added to the parent, at the file bytes of the data they were read from; returns the node of the
    // codec-specific data (null where there is none), whose bytes are what the codec parser is configured with. Bytes after the stated data are one more node.
    public Node AddNodes(Node parent, MappedBytes data, out Node? codecSpecific)
    {
        var node = Node.FromMapped("WAVEFORMATEX", data, 0, data.Length, NodeValue.Integer(data.Length, "bytes", "byte"), description: Descriptions.Structure, topic: Topic);
        parent.Add(node);
        node.Add(Node.FromMapped("Format Tag", data, 0, 2, NodeValue.Text(WaveFormatNames.Show(Tag)), description: Descriptions.FormatTag, topic: Topic));
        node.Add(Node.FromMapped("Channels", data, 2, 4, NodeValue.Integer(Channels), description: Descriptions.Channels));
        node.Add(Node.FromMapped("Samples Per Second", data, 4, 8, NodeValue.Integer(SampleRate, "Hz"), description: Descriptions.SamplesPerSecond));
        node.Add(Node.FromMapped("Average Bytes Per Second", data, 8, 12, NodeValue.Integer(BytesPerSecond, "bytes/s"), description: Descriptions.AverageBytesPerSecond));
        node.Add(Node.FromMapped("Block Alignment", data, 12, 14, NodeValue.Integer(BlockAlign, "bytes", "byte"), description: Descriptions.BlockAlignment));
        node.Add(Node.FromMapped("Bits Per Sample", data, 14, 16, NodeValue.Integer(Bits, "bits", "bit"), description: Descriptions.BitsPerSample));
        codecSpecific = null;
        if (StatedExtraSize is not { } stated)
        {
            if (data.Length > FixedSize)
            {
                node.Add(Node.FromMapped("Extra Data", data, FixedSize, data.Length, NodeValue.Integer(data.Length - FixedSize, "bytes", "byte"), description: Descriptions.ExtraData));
            }

            return node;
        }

        node.Add(Node.FromMapped("Codec Specific Data Size", data, 16, 18, NodeValue.Integer(stated, "bytes", "byte"), description: Descriptions.CodecSpecificDataSize, topic: Topic));
        if (ValidBits is { } valid && ChannelMask is { } mask && SubFormat is { } sub)
        {
            node.Add(Node.FromMapped("Valid Bits Per Sample", data, 18, 20, NodeValue.Integer(valid, "bits", "bit"), description: Descriptions.ValidBitsPerSample));
            node.Add(Node.FromMapped("Channel Mask", data, 20, 24, NodeValue.Hex(mask, 8), description: Descriptions.ChannelMask, topic: "channel-layouts"));
            string? known = WaveFormatRoutes.TagOfSubFormat(sub) is { } tag ? WaveFormatNames.NameOf(tag) ?? "unlisted coding" : WaveFormatRoutes.NamedSubFormat(sub)?.Name;
            node.Add(Node.FromMapped("Sub-Format", data, 24, 40, NodeValue.Text(known is null ? sub.ToString() : $"{sub} ({known})"), description: Descriptions.SubFormat));
        }

        if (DataLength > 0)
        {
            codecSpecific = Node.FromMapped("Codec Specific Data", data, DataStart, DataStart + DataLength, NodeValue.Integer(DataLength, "bytes", "byte"), description: Descriptions.CodecSpecificData);
            node.Add(codecSpecific);
        }

        int after = DataStart + DataLength;
        if (after < data.Length)
        {
            node.Add(Node.FromMapped("Extra Data", data, after, data.Length, NodeValue.Integer(data.Length - after, "bytes", "byte"), description: Descriptions.ExtraData));
        }

        return node;
    }

    // The id of the background article the nodes point to, which the host serves.
    public const string Topic = "waveformatex";

    // What each node is, from Microsoft's WAVEFORMATEX documentation and mmreg.h.
    public static class Descriptions
    {
        public const string Structure = "A WAVEFORMATEX: the structure that describes the coding of audio and the figures a decoder is configured with, as WAV files, AVI and ASF store it (Microsoft's WAVEFORMATEX documentation).";
        public const string FormatTag = "The format tag, wFormatTag: the coding of the audio. 1 is PCM, 3 IEEE floating point, 0xFFFE the extensible format whose sub-format names the coding (mmreg.h).";
        public const string Channels = "The number of audio channels, nChannels of the WAVEFORMATEX.";
        public const string SamplesPerSecond = "The sampling rate in Hz, nSamplesPerSec of the WAVEFORMATEX.";
        public const string AverageBytesPerSecond = "The average number of bytes per second of the audio, nAvgBytesPerSec of the WAVEFORMATEX.";
        public const string BlockAlignment = "The block alignment in bytes, nBlockAlign of the WAVEFORMATEX: the size of the smallest unit of the data, a sample frame of linear coding or a block of a block-coded one.";
        public const string BitsPerSample = "The bits per sample of one channel, wBitsPerSample of the WAVEFORMATEX; 0 where the coding has no such figure.";
        public const string CodecSpecificDataSize = "The size of the codec-specific data after the structure, cbSize of the WAVEFORMATEX; for the extensible format it counts the 22 bytes of the extension too.";
        public const string ValidBitsPerSample = "The number of bits of each sample that hold audio, wValidBitsPerSample of the WAVEFORMATEXTENSIBLE; the rest of the container is zero (ksmedia.h documentation).";
        public const string ChannelMask = "Which speakers the channels feed: a bit for each speaker position, dwChannelMask of the WAVEFORMATEXTENSIBLE. The channels are in the order of the bits.";
        public const string SubFormat = "A GUID whose first 32 bits are the format tag of the coding when the other twelve bytes are Microsoft's base GUID, SubFormat of the WAVEFORMATEXTENSIBLE; AC-3, E-AC-3 and MPEG-2 audio have GUIDs of their own.";
        public const string CodecSpecificData = "Bytes the coding defines, which follow the WAVEFORMATEX; the codec parser of the coding reads them.";
        public const string ExtraData = "Bytes after the codec-specific data the structure states, which the specification does not use.";
    }
}
