using Encspect.Core;

namespace Encspect.Formats.Wav;

// The WAVEFORMATEX of the fmt chunk as far as it was read, and what the stream's figures are taken from.
internal sealed record WaveFormat(int Tag, int Channels, long SampleRate, long BytesPerSecond, int BlockAlign, int Bits, int ExtraSize, int FormatSize)
{
    // WAVEFORMATEXTENSIBLE's valid bits per sample (or samples per block for some compressed codings), channel mask and sub-format.
    public int? ValidBits { get; init; }

    public uint? ChannelMask { get; init; }

    public Guid? SubFormat { get; init; }

    // The codec-specific bytes after the structure, where the format has any, and the node that holds them.
    public Node? CodecData { get; init; }

    public bool IsExtensible => Tag == FormatTags.Extensible;

    // The coding: the tag, or, for the extensible form, the format tag in its sub-format GUID.
    public int CodingTag => IsExtensible && SubFormat is { } guid && FormatTags.TagOfSubFormat(guid) is { } sub ? sub : Tag;

    // The sub-format GUID when it is no format tag but one Microsoft's headers name (E-AC-3, and AC-3 and MPEG-2 audio under their DirectShow subtypes).
    public (string Name, string CodecId)? NamedSubFormat => IsExtensible && SubFormat is { } guid ? FormatTags.NamedSubFormat(guid) : null;

    // The codec parser the audio goes to, or null where Encspect has none for the coding.
    public string? CodecId => FormatTags.CodecId(CodingTag) ?? NamedSubFormat?.CodecId;

    // The name of the coding: the tag's, or the named sub-format's, or null for a tag the table does not list.
    public string? CodingName => NamedSubFormat?.Name ?? FormatTags.NameOf(CodingTag);

    // The bytes one sample of one channel takes in the data chunk, rounded up to whole bytes (the container size).
    public int BytesPerSample => (Bits + 7) / 8;

    // The bits of each sample that hold audio: the valid bits of an extensible format that state a number up to the container's, else the container's.
    public int AudioBits => IsExtensible && ValidBits is > 0 && ValidBits <= Bits ? ValidBits.Value : Bits;
}

// The ds64 chunk of an RF64 file (EBU Tech 3306-2009 section 3.4): the three sizes whose 32-bit fields hold 0xFFFFFFFF, and the sizes of other
// chunks that need more than 32 bits.
internal sealed record Ds64(ulong RiffSize, ulong DataSize, ulong SampleCount, Dictionary<string, ulong> Table);

// The data chunk the stream is taken from: where its bytes are, how many the file holds, and what the header states.
internal sealed record DataChunk(Node Node, long Start, long Available, ulong Stated, bool IsSizeUnknown);
